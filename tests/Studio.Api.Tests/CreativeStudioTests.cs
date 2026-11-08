using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public class CreativeStudioTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Creative production" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    static async Task<Asset> Upload(HttpClient c, Guid p, string file)
    {
        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(file)),
            "file",
            Path.GetFileName(file)
        );
        var r = await c.PostAsync($"/api/projects/{p}/assets", form);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Asset>())!;
    }

    static async Task Save(HttpClient c, Guid p, StoryDocument doc, int version = 0) =>
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(version, doc))
        ).EnsureSuccessStatusCode();

    static async Task<JsonElement> Budget(HttpClient c, Guid p) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/budget");

    static async Task SetBudget(HttpClient c, Guid p, BudgetConfig cfg, int version = 0) =>
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/budget", new BudgetWrite(version, cfg))
        ).EnsureSuccessStatusCode();

    static byte[] Ffmpeg(string[] args)
    {
        using var p = new Process
        {
            StartInfo = new("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var a in args)
            p.StartInfo.ArgumentList.Add(a);
        p.Start();
        var err = p.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(memory);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.GetAwaiter().GetResult());
        return memory.ToArray();
    }

    static byte[] Frame(string file, double at, string crop = "scale=1:1") =>
        Ffmpeg([
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            at.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i",
            file,
            "-vf",
            crop,
            "-frames:v",
            "1",
            "-pix_fmt",
            "rgb24",
            "-threads",
            "1",
            "-f",
            "rawvideo",
            "-",
        ]);

    static async Task<(Guid Id, string File)> Render(
        AudioApp app,
        HttpClient c,
        Guid project,
        RenderRequest req
    )
    {
        var res = await c.PostAsJsonAsync($"/api/projects/{project}/renders", req);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        for (var i = 0; i < 300; i++)
        {
            var rows = (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{project}/jobs"))!;
            var row = rows.Single(j => j.GetProperty("id").GetGuid() == id);
            var status = row.GetProperty("status").GetString();
            Assert.True(status != "Failed", row.ToString());
            if (status == "Succeeded")
                return (id, Path.Combine(app.Data, "exports", id + ".mp4"));
            await Task.Delay(100);
        }
        throw new Exception("Render timeout");
    }

    static async Task<ProfileSnapshot> Profile(
        HttpClient c,
        Guid p,
        string name = "An",
        string kind = "character",
        Guid? image = null
    )
    {
        var r = await c.PostAsJsonAsync(
            $"/api/projects/{p}/profiles",
            new ProfileWrite(
                kind,
                new(name, "Căn phòng màu xanh", Appearance: "Áo đỏ", ReferenceId: image)
            )
        );
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProfileSnapshot>())!;
    }

    [Fact]
    public async Task ProfilesSnapshotRemainsStableAfterEditDeleteAndScopesReferences()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var other = await Project(c);
        var profile = await Profile(c, p);
        var response = await c.PostAsJsonAsync(
            $"/api/projects/{p}/profiles/resolve",
            new ResolveProfiles([profile.Id], CharacterState: "Cầm cốc")
        );
        var snapshot = (await response.Content.ReadFromJsonAsync<SceneContinuity>())!;
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{other}/profiles/resolve",
                    new ResolveProfiles([profile.Id])
                )
            ).StatusCode
        );
        var edited = profile.Content with { Name = "Bình" };
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/profiles/{profile.Id}",
                new ProfileWrite("character", edited, 1)
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PutAsJsonAsync(
                    $"/api/projects/{p}/profiles/{profile.Id}",
                    new ProfileWrite("character", edited, 1)
                )
            ).StatusCode
        );
        (await c.DeleteAsync($"/api/projects/{p}/profiles/{profile.Id}")).EnsureSuccessStatusCode();
        var scene = new StoryScene(
            Guid.NewGuid(),
            "Cảnh",
            "Xin chào",
            "A room",
            null,
            Continuity: snapshot
        );
        await Save(c, p, new(Scenes: [scene]));
        var saved = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.Equal("An", saved.Scenes![0].Continuity!.Characters![0].Content.Name);
        Assert.Contains("Cầm cốc", SceneDirection.VisualPrompt(saved.Scenes[0]));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/profiles",
                    new ProfileWrite(
                        "character",
                        profile.Content with
                        {
                            ReferenceId = Guid.NewGuid(),
                        }
                    )
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ProfilePackDetachesImagesAndImportIsAtomic()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        await Profile(c, p);
        var pack = (
            await c.GetFromJsonAsync<ProfilePackage>($"/api/projects/{p}/profiles/export")
        )!;
        var other = await Project(c);
        (
            await c.PostAsJsonAsync($"/api/projects/{other}/profiles/import", pack)
        ).EnsureSuccessStatusCode();
        var rows = (
            await c.GetFromJsonAsync<ProfileSnapshot[]>($"/api/projects/{other}/profiles")
        )!;
        Assert.Single(rows);
        Assert.Null(rows[0].Content.ReferenceId);
        var invalid = pack with
        {
            Profiles = [pack.Profiles[0], new("wrong", pack.Profiles[0].Content)],
        };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/projects/{other}/profiles/import", invalid)).StatusCode
        );
        Assert.Single(
            (await c.GetFromJsonAsync<ProfileSnapshot[]>($"/api/projects/{other}/profiles"))!
        );
    }

    [Fact]
    public async Task TextLogoAndAudioRenderChangesActualFramesOnlyInsideTimeWindow()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        var voice = await Upload(c, p, app.Audio);
        var png = Path.Combine(app.Data, "logo.png");
        await File.WriteAllBytesAsync(
            png,
            Ffmpeg([
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "lavfi",
                "-i",
                "color=c=red:s=80x40",
                "-frames:v",
                "1",
                "-threads",
                "1",
                "-f",
                "image2pipe",
                "-vcodec",
                "png",
                "-",
            ])
        );
        var logo = await Upload(c, p, png);
        var result = await Render(
            app,
            c,
            p,
            new(
                [video.Id],
                Duration: 2,
                VoiceId: voice.Id,
                MusicId: voice.Id,
                Ratio: "16:9",
                Scenes: [new(video.Id, 2)],
                Branding: new(
                    [new("Xin chào: 100% [x] ' ; %{n}", .4, 1.4, "top-left", 24)],
                    new(logo.Id, .4, 1.4, "bottom-right", .2, 1)
                )
            )
        );
        var before = Frame(result.File, .1, "crop=2:2:1100:680");
        var during = Frame(result.File, .8, "crop=2:2:1100:680");
        var after = Frame(result.File, 1.8, "crop=2:2:1100:680");
        Assert.True(during[0] > 180 && during[1] < 60, "During: " + string.Join(',', during));
        Assert.True(before[1] > 70 && before[0] < 80, "Before: " + string.Join(',', before));
        Assert.True(after[1] > 70 && after[0] < 80, "After: " + string.Join(',', after));
        var letters = Frame(result.File, .8, "crop=640:100:20:20");
        Assert.True(
            Enumerable
                .Range(0, letters.Length / 3)
                .Count(i =>
                    letters[3 * i] > 220 && letters[3 * i + 1] > 220 && letters[3 * i + 2] > 220
                ) > 50,
            "The text must appear in actual decoded pixels."
        );
        var qa = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{result.Id}/qa");
        Assert.DoesNotContain("Failed", qa.ToString());
        Assert.True(await MediaProcess.AudioDuration(result.File, CancellationToken.None) > 1.8);
    }

    [Theory]
    [InlineData("fade", 24)]
    [InlineData("wipeleft", 30)]
    [InlineData("smoothleft", 60)]
    public async Task TransitionsKeepSceneTimingAndActuallyBlendFrames(string style, int fps)
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var first = await Upload(c, p, app.Video);
        var red = Path.Combine(app.Data, "red.mp4");
        Ffmpeg([
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            "color=c=red:s=320x240:d=1",
            "-c:v",
            "libx264",
            "-threads",
            "1",
            "-pix_fmt",
            "yuv420p",
            red,
        ]);
        var second = await Upload(c, p, red);
        var voice = await Upload(c, p, app.Audio);
        var result = await Render(
            app,
            c,
            p,
            new(
                [first.Id, second.Id],
                Duration: 2,
                Ratio: "16:9",
                VoiceId: voice.Id,
                Scenes: [new(first.Id, 1, Caption: "Một"), new(second.Id, 1, Caption: "Hai")],
                AutoCaptions: true,
                Effects: new(Fps: fps),
                Transitions: new(style, .4)
            )
        );
        var duration = await MediaProcess.AudioDuration(result.File, CancellationToken.None);
        Assert.InRange(duration, 1.95, 2.15);
        var early = Frame(result.File, .5);
        var late = Frame(result.File, 1.8);
        Assert.True(early[0] < 40 && early[1] > 50);
        Assert.True(late[0] > 170 && late[1] < 50);
        if (style == "fade")
        {
            var middle = Frame(result.File, 1.2);
            Assert.True(middle[0] > 40 && middle[1] > 25, "Expected blended transition");
        }
        Assert.Contains(
            "00:00:01,000 --> 00:00:02,000",
            await File.ReadAllTextAsync(Path.Combine(app.Data, "exports", result.Id + ".srt"))
        );
    }

    [Fact]
    public async Task GraphicsRejectInvalidInputAndCrossProjectLogo()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var a = await Upload(c, p, app.Video);
        var other = await Project(c);
        var png = Path.Combine(app.Data, "foreign.png");
        await File.WriteAllBytesAsync(
            png,
            Ffmpeg([
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "lavfi",
                "-i",
                "color=c=red:s=80x40",
                "-frames:v",
                "1",
                "-threads",
                "1",
                "-f",
                "image2pipe",
                "-vcodec",
                "png",
                "-",
            ])
        );
        var foreign = await Upload(c, other, png);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/profiles",
                    new ProfileWrite("character", new("Foreign", "", ReferenceId: foreign.Id))
                )
            ).StatusCode
        );
        var snapshot = new ProfileSnapshot(
            Guid.NewGuid(),
            1,
            "character",
            new("Foreign", "", ReferenceId: foreign.Id)
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PutAsJsonAsync(
                    $"/api/projects/{p}/document",
                    new SaveDocument(
                        0,
                        new(
                            Scenes:
                            [
                                new(
                                    Guid.NewGuid(),
                                    "One",
                                    "",
                                    "",
                                    a.Id,
                                    1,
                                    Continuity: new([snapshot])
                                ),
                            ]
                        )
                    )
                )
            ).StatusCode
        );
        foreach (
            var req in new RenderRequest[]
            {
                new([a.Id], Branding: new([new("bad", End: 0)])),
                new([a.Id], Branding: new([new("bad", Color: "red;movie=evil")])),
                new([a.Id], Branding: new(Logo: new(Guid.NewGuid()))),
                new([a.Id], Branding: new(Logo: new(foreign.Id))),
                new([a.Id], Mode: "loop", Transitions: new("fade")),
                new(null!, Transitions: new("fade")),
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await c.PostAsJsonAsync($"/api/projects/{p}/renders", req)).StatusCode
            );
    }

    [Fact]
    public async Task BudgetReservesAtomicallyAcrossConcurrentRequestsAndCacheIsFree()
    {
        using var app = new AudioApp { DisabledWorkers = [typeof(ImageWorker)] };
        using var c = app.CreateClient();
        var p = await Project(c);
        var scenes = new[]
        {
            new StoryScene(Guid.NewGuid(), "One", "", "Teal room", null),
            new StoryScene(Guid.NewGuid(), "Two", "", "Red room", null),
        };
        await Save(c, p, new(Scenes: scenes));
        await SetBudget(c, p, new(true, 1, ImageUsdPerCall: .6m));
        var requests = scenes.Select(s => new ImageRequest(Guid.NewGuid(), s.Id, 1)).ToArray();
        var responses = await Task.WhenAll(
            requests.Select(r => c.PostAsJsonAsync($"/api/projects/{p}/image-generations", r))
        );
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var accepted = Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/image-generations", requests[accepted])
        ).EnsureSuccessStatusCode();
        var budget = await Budget(c, p);
        Assert.Equal(.6m, budget.GetProperty("summary").GetProperty("reservedUsd").GetDecimal());
        Assert.Single(budget.GetProperty("entries").EnumerateArray());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        Assert.Equal(1, await db.ImageGenerations.CountAsync());
    }

    [Fact]
    public async Task UnknownCostsBlockUntilExplicitlyPricedAndReconciliationDoesNotEraseHistory()
    {
        using var app = new AudioApp();
        using var c = app.CreateClient();
        var p = await Project(c);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/speech", new SpeechRequest("Hi", "alloy"))
        ).EnsureSuccessStatusCode();
        var b = await Budget(c, p);
        Assert.Equal(1, b.GetProperty("summary").GetProperty("unknownEntries").GetInt32());
        await SetBudget(c, p, new(true, 1, SpeechUsdPer1000Chars: 1));
        var calls = app.Calls;
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/speech",
                    new SpeechRequest("Hello", "alloy")
                )
            ).StatusCode
        );
        Assert.Equal(calls, app.Calls);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/budget/reprice", new { })
        ).EnsureSuccessStatusCode();
        (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/speech",
                new SpeechRequest("Hello", "alloy")
            )
        ).EnsureSuccessStatusCode();
        b = await Budget(c, p);
        Assert.Equal(.007m, b.GetProperty("summary").GetProperty("reservedUsd").GetDecimal());
        var entry = b.GetProperty("entries")[0].GetProperty("id").GetGuid();
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/budget/entries/{entry}",
                new ReconcileBudget(0, "Provider refunded")
            )
        ).EnsureSuccessStatusCode();
        b = await Budget(c, p);
        Assert.Equal(2, b.GetProperty("entries").GetArrayLength());
        Assert.Equal(.002m, b.GetProperty("summary").GetProperty("reservedUsd").GetDecimal());
    }

    [Fact]
    public async Task MissingKeysNeverReserveAndBudgetConfigUsesVersionChecks()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        await SetBudget(c, p, new(true, 1));
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/speech",
                    new SpeechRequest("Hi", "alloy")
                )
            ).StatusCode
        );
        Assert.Empty((await Budget(c, p)).GetProperty("entries").EnumerateArray());
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PutAsJsonAsync($"/api/projects/{p}/budget", new BudgetWrite(0, new()))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PutAsJsonAsync(
                    $"/api/projects/{p}/budget",
                    new BudgetWrite(1, new(LimitUsd: -1))
                )
            ).StatusCode
        );
    }

    static async Task<HttpResponseMessage> Restore(HttpClient c, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "backup.zip");
        return await c.PostAsync("/api/backups/restore", form);
    }

    static byte[] ModifyArchive(
        byte[] bytes,
        Func<string, byte[], byte[]> change,
        string? extra = null
    )
    {
        using var original = new MemoryStream(bytes);
        using var source = new ZipArchive(original, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var data = new MemoryStream();
                input.CopyTo(data);
                using var target = zip.CreateEntry(entry.FullName).Open();
                target.Write(change(entry.FullName, data.ToArray()));
            }
            if (extra is not null)
            {
                using var s = zip.CreateEntry(extra).Open();
                s.WriteByte(1);
            }
        }
        return output.ToArray();
    }

    [Fact]
    public async Task ProjectArchiveRestoresIndependentFilesProfilesPreferencesAndRenderableDocument()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var a = await Upload(c, p, app.Video);
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/asset-preferences/{a.Id}",
                new PreferenceRequest(true, ["source"], "Original clip")
            )
        ).EnsureSuccessStatusCode();
        var profile = await Profile(c, p);
        var scene = new StoryScene(
            Guid.NewGuid(),
            "One",
            "Hello",
            "Room",
            a.Id,
            1,
            Continuity: new([profile])
        );
        await Save(
            c,
            p,
            new(Scenes: [scene], Branding: new([new("CTA", End: .8)]), Transitions: new("fade"))
        );
        (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/saved-items",
                new CaptureItem("checkpoint", "Before", 1)
            )
        ).EnsureSuccessStatusCode();
        var backup = await c.GetByteArrayAsync($"/api/projects/{p}/backup");
        var response = await Restore(c, backup);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var restored = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project")
            .GetProperty("id")
            .GetGuid();
        var doc = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{restored}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.NotEqual(a.Id, doc.Scenes![0].AssetId);
        Assert.NotEqual(scene.Id, doc.Scenes[0].Id);
        Assert.NotEqual(profile.Id, doc.Scenes[0].Continuity!.Characters![0].Id);
        Assert.Equal("CTA", doc.Branding!.Texts![0].Text);
        var preferences = (
            await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{restored}/asset-preferences")
        )!;
        Assert.Single(preferences);
        Assert.True(preferences[0].GetProperty("favorite").GetBoolean());
        Assert.Equal("Original clip", preferences[0].GetProperty("note").GetString());
        Assert.Equal(doc.Scenes![0].AssetId, preferences[0].GetProperty("assetId").GetGuid());
        Assert.Single(
            (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{restored}/saved-items"))!
        );
        Assert.Empty((await Budget(c, restored)).GetProperty("entries").EnumerateArray());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            var source = await db.Assets.SingleAsync(x => x.Id == a.Id);
            File.Delete(Path.Combine(app.Data, "assets", source.StoredName));
        }
        var req = new RenderRequest(
            [doc.Scenes[0].AssetId!.Value],
            Duration: 1,
            Scenes: [new(doc.Scenes[0].AssetId!.Value, 1)],
            Branding: doc.Branding
        );
        await Render(app, c, restored, req);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("traversal")]
    [InlineData("null-scene")]
    [InlineData("missing-ref")]
    public async Task CorruptBackupsNeverCreatePartialProjectsOrFiles(string mode)
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var a = await Upload(c, p, app.Video);
        await Save(c, p, new(Scenes: [new(Guid.NewGuid(), "One", "", "", a.Id, 1)]));
        var bytes = await c.GetByteArrayAsync($"/api/projects/{p}/backup");
        bytes = ModifyArchive(
            bytes,
            (name, data) =>
            {
                if (mode == "corrupt" && name.StartsWith("media/"))
                {
                    data[0] ^= 255;
                    return data;
                }
                if (name == "manifest.json" && mode is "null-scene" or "missing-ref")
                {
                    var manifest = JsonSerializer.Deserialize<ProjectArchive>(data, Json)!;
                    manifest = manifest with
                    {
                        Document = manifest.Document! with
                        {
                            Scenes =
                                mode == "null-scene"
                                    ? [null!]
                                    :
                                    [
                                        manifest.Document.Scenes![0] with
                                        {
                                            AssetId = Guid.NewGuid(),
                                        },
                                    ],
                        },
                    };
                    return JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                }
                return data;
            },
            mode == "traversal" ? "../outside.txt" : null
        );
        var before = Directory.GetFiles(Path.Combine(app.Data, "assets")).Length;
        var response = await Restore(c, bytes);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await c.GetFromJsonAsync<Project[]>("/api/projects"))!);
        Assert.Equal(before, Directory.GetFiles(Path.Combine(app.Data, "assets")).Length);
    }

    [Fact]
    public async Task VideoStorySpeechAndTranscriptionRejectBeforeCallingProvider()
    {
        using var app = new AudioApp
        {
            VideoKey = true,
            DisabledWorkers = [typeof(VideoWorker), typeof(StoryPlanWorker)],
        };
        using var c = app.CreateClient();
        var p = await Project(c);
        var scene = new StoryScene(Guid.NewGuid(), "Scene", "Hello", "A room", null, 1);
        await Save(c, p, new(Idea: "Test", Scenes: [scene]));
        await SetBudget(
            c,
            p,
            new(
                true,
                .001m,
                VideoUsdPerSecond: .2m,
                SpeechUsdPer1000Chars: 1,
                StoryUsdPerCall: .1m,
                TranscriptionUsdPerMinute: 1
            )
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/video-generations",
                    new VideoRequest(Guid.NewGuid(), scene.Id, 1)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/story-plans",
                    new StoryPlanRequest(Guid.NewGuid(), 1, SceneCount: 1, Duration: 1)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync($"/api/projects/{p}/speech", new SpeechRequest("Hello"))
            ).StatusCode
        );
        var audio = await Upload(c, p, app.Audio);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/transcriptions",
                    new TranscriptionRequest(audio.Id)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync("/api/ai/script", new ScriptRequest("Hello", p))).StatusCode
        );
        Assert.Equal(0, app.Calls);
        Assert.Empty((await Budget(c, p)).GetProperty("entries").EnumerateArray());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        Assert.Equal(0, await db.VideoGenerations.CountAsync());
        Assert.Equal(0, await db.StoryPlans.CountAsync());
    }

    [Fact]
    public async Task WorkflowBudgetQuotesAndReservesWholeBatchWithoutPartialChildren()
    {
        using var app = new AudioApp
        {
            DisabledWorkers = [typeof(ProductionWorker), typeof(ImageWorker)],
        };
        using var c = app.CreateClient();
        var p = await Project(c);
        await Save(
            c,
            p,
            new(
                Scenes:
                [
                    new(Guid.NewGuid(), "One", "Hello", "Blue room", null, 1),
                    new(Guid.NewGuid(), "Two", "World", "Red room", null, 1),
                ]
            )
        );
        await SetBudget(c, p, new(true, 1, ImageUsdPerCall: .6m, SpeechUsdPer1000Chars: 1));
        var req = new ProductionRequest(Guid.NewGuid(), 1, Media: "images", Speech: true);
        var response = await c.PostAsJsonAsync($"/api/projects/{p}/workflows/plan", req);
        response.EnsureSuccessStatusCode();
        var quote = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("budget");
        Assert.False(quote.GetProperty("allowed").GetBoolean());
        Assert.Equal(1.21m, quote.GetProperty("estimatedUsd").GetDecimal());
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)).StatusCode
        );
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            Assert.Equal(0, await db.ImageGenerations.CountAsync());
            Assert.Equal(0, await db.ProductionRuns.CountAsync());
        }
        await SetBudget(c, p, new(true, 2, ImageUsdPerCall: .6m, SpeechUsdPer1000Chars: 1), 1);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        var b = await Budget(c, p);
        Assert.Equal(1.21m, b.GetProperty("summary").GetProperty("reservedUsd").GetDecimal());
        Assert.Equal(2, b.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task CloneAndPortableJsonRemapOrDetachAllProfileAndLogoReferences()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var png = Path.Combine(app.Data, "reference.png");
        await File.WriteAllBytesAsync(
            png,
            Ffmpeg([
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "lavfi",
                "-i",
                "color=c=red:s=80x40",
                "-frames:v",
                "1",
                "-threads",
                "1",
                "-f",
                "image2pipe",
                "-vcodec",
                "png",
                "-",
            ])
        );
        var image = await Upload(c, p, png);
        var profile = await Profile(c, p, image: image.Id);
        var doc = new StoryDocument(
            Scenes:
            [
                new(Guid.NewGuid(), "One", "", "Room", image.Id, 1, Continuity: new([profile])),
            ],
            Branding: new(Logo: new(image.Id))
        );
        await Save(c, p, doc);
        var cloneResponse = await c.PostAsync($"/api/projects/{p}/duplicate", null);
        cloneResponse.EnsureSuccessStatusCode();
        var clone = (await cloneResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project")
            .GetProperty("id")
            .GetGuid();
        var cloned = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{clone}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.NotEqual(image.Id, cloned.Branding!.Logo!.AssetId);
        Assert.Equal(cloned.Scenes![0].AssetId, cloned.Branding.Logo.AssetId);
        Assert.Equal(
            cloned.Scenes[0].AssetId,
            cloned.Scenes[0].Continuity!.Characters![0].Content.ReferenceId
        );
        Assert.NotEqual(profile.Id, cloned.Scenes[0].Continuity!.Characters![0].Id);
        var profiles = (
            await c.GetFromJsonAsync<ProfileSnapshot[]>($"/api/projects/{clone}/profiles")
        )!;
        Assert.Equal(profiles[0].Id, cloned.Scenes[0].Continuity!.Characters![0].Id);
        var package = (
            await c.GetFromJsonAsync<StoryboardPackage>($"/api/projects/{p}/storyboard/export")
        )!;
        var importedResponse = await c.PostAsJsonAsync("/api/storyboards/import", package);
        Assert.True(
            importedResponse.IsSuccessStatusCode,
            await importedResponse.Content.ReadAsStringAsync()
        );
        var imported = (await importedResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project")
            .GetProperty("id")
            .GetGuid();
        var detached = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{imported}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.Null(detached.Branding!.Logo);
        Assert.Null(detached.Scenes![0].Continuity!.Characters![0].Content.ReferenceId);
        Assert.Equal("An", detached.Scenes[0].Continuity!.Characters![0].Content.Name);
        var archive = await c.GetByteArrayAsync($"/api/projects/{p}/backup");
        var restoredResponse = await Restore(c, archive);
        restoredResponse.EnsureSuccessStatusCode();
        var restored = (await restoredResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project")
            .GetProperty("id")
            .GetGuid();
        var restoredDoc = (
            await c.GetFromJsonAsync<JsonElement>($"/api/projects/{restored}/document")
        )
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.Equal(
            restoredDoc.Branding!.Logo!.AssetId,
            restoredDoc.Scenes![0].Continuity!.Characters![0].Content.ReferenceId
        );
        Assert.NotEqual(image.Id, restoredDoc.Branding.Logo.AssetId);
    }

    [Fact]
    public async Task ManifestInflationIsBoundedEvenWithFalseZipSizeHeader()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var target = zip.CreateEntry("manifest.json", CompressionLevel.SmallestSize)
                .Open();
            target.Write(new byte[3 * 1024 * 1024]);
        }
        var bytes = stream.ToArray();
        for (var i = 0; i < bytes.Length - 30; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2)
            {
                BitConverter.GetBytes(1).CopyTo(bytes, i + 24);
                break;
            }
        var response = await Restore(c, bytes);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
        Assert.Empty((await c.GetFromJsonAsync<Project[]>("/api/projects"))!);
    }

    [Fact]
    public async Task BudgetQuoteGroupsAndRoundsLikeReservationsAndExportContainsAllEntries()
    {
        using var app = new AudioApp();
        using var c = app.CreateClient();
        var p = await Project(c);
        await SetBudget(c, p, new(true, .000001m, SpeechUsdPer1000Chars: .0001m));
        var quote = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/budget/quote",
                new BudgetLine[] { new("speech", .001m), new("speech", .001m) }
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(quote.GetProperty("allowed").GetBoolean());
        Assert.Equal(.000001m, quote.GetProperty("estimatedUsd").GetDecimal());
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/speech", new SpeechRequest("Hi"))
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync($"/api/projects/{p}/speech", new SpeechRequest("Hi"))
            ).StatusCode
        );
        var export = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/budget/export");
        Assert.Equal("flowvideo.budget", export.GetProperty("format").GetString());
        Assert.Single(export.GetProperty("entries").EnumerateArray());
    }
}
