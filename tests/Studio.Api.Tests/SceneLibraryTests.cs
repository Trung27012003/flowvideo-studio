using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class SceneLibraryTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "v0.10" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    static async Task<Asset> Upload(HttpClient c, Guid p, string file, string name = "video.mp4")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(file)), "file", name);
        var r = await c.PostAsync($"/api/projects/{p}/assets", form);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Asset>())!;
    }

    static async Task Save(HttpClient c, Guid p, StoryDocument doc, int version = 0) =>
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(version, doc))
        ).EnsureSuccessStatusCode();

    static async Task<Guid> Wait(HttpClient c, Guid p, HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        for (int i = 0; i < 400; i++)
        {
            var rows = (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!;
            var job = rows.Single(j => j.GetProperty("id").GetGuid() == id);
            Assert.NotEqual("Failed", job.GetProperty("status").GetString());
            if (job.GetProperty("status").GetString() == "Succeeded")
                return id;
            await Task.Delay(100);
        }
        throw new Exception("Render timed out");
    }

    [Fact]
    public async Task InvalidOptionsAndStylesAreRejectedAndLegacyDefaultsRemainCompatible()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        var scene = new StoryScene(Guid.NewGuid(), "A", "Lời", "", video.Id, 1);
        foreach (
            var options in new[]
            {
                new SceneOptions(Fit: "bad"),
                new SceneOptions(Brightness: 1),
                new SceneOptions(Rotation: 12),
                new SceneOptions(Speed: 0),
                new SceneOptions(Zoom: 3),
                new SceneOptions(FocusX: -1),
                new SceneOptions(FocusY: 2),
                new SceneOptions(Fade: 2),
                new SceneOptions(Note: new string('x', 1001)),
            }
        )
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PutAsJsonAsync(
                        $"/api/projects/{p}/document",
                        new SaveDocument(0, new(Scenes: [scene with { Options = options }]))
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{p}/renders",
                        new RenderRequest([video.Id], Scenes: [new(video.Id, 1, Options: options)])
                    )
                ).StatusCode
            );
        }
        foreach (
            var style in new[]
            {
                new CaptionStyle(Font: "x,Shadow=99"),
                new CaptionStyle(Size: 49),
                new CaptionStyle(Color: "red"),
                new CaptionStyle(OutlineColor: "#ffffff\n"),
                new CaptionStyle(Outline: 5),
                new CaptionStyle(Shadow: -1),
                new CaptionStyle(Alignment: 0),
                new CaptionStyle(Margin: 201),
                new CaptionStyle(BoxOpacity: 2),
                new CaptionStyle(BoxColor: null!),
            }
        )
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PutAsJsonAsync(
                        $"/api/projects/{p}/document",
                        new SaveDocument(0, new(Scenes: [scene], Captions: style))
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{p}/renders",
                        new RenderRequest([video.Id], Captions: style)
                    )
                ).StatusCode
            );
        }
        Assert.Null(
            JsonSerializer
                .Deserialize<StoryScene>(
                    "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Title\":\"A\",\"Narration\":\"\",\"Prompt\":\"\"}"
                )!
                .Options
        );
        Assert.Null(JsonSerializer.Deserialize<RenderRequest>("{\"AssetIds\":[]}")!.Captions);
        Assert.True(new SceneOptions().Valid());
        Assert.True(new CaptionStyle().Valid());
    }

    [Fact]
    public async Task SceneAndCaptionSettingsSurvivePortabilityAndDisabledScenesDoNotRender()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        var options = new SceneOptions(
            Fit: "cover",
            Brightness: .2,
            Rotation: 90,
            Speed: 2,
            Zoom: 1.2,
            FocusX: 0,
            FocusY: 1,
            Caption: false,
            Fade: .1,
            Note: "Chỉ ghi chú"
        );
        var style = new CaptionStyle(
            Font: "mono",
            Size: 24,
            Bold: true,
            Alignment: 8,
            Box: true,
            BoxColor: "#123456"
        );
        var doc = new StoryDocument(
            Scenes:
            [
                new(Guid.NewGuid(), "A", "Không hiện", "", video.Id, 1, Options: options),
                new(Guid.NewGuid(), "Tắt", "", "", null, 1, Options: new(Enabled: false)),
            ],
            Captions: style
        );
        await Save(c, p, doc);
        var package = (
            await c.GetFromJsonAsync<StoryboardPackage>($"/api/projects/{p}/storyboard/export")
        )!;
        Assert.Equal(options, package.Document.Scenes![0].Options);
        Assert.Equal(style, package.Document.Captions);
        var imported = await (
            await c.PostAsJsonAsync("/api/storyboards/import", package)
        ).Content.ReadFromJsonAsync<JsonElement>();
        var importedId = imported.GetProperty("project").GetProperty("id").GetGuid();
        var loaded = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{importedId}/document");
        Assert.Equal(
            options,
            loaded
                .GetProperty("document")
                .GetProperty("scenes")[0]
                .GetProperty("options")
                .Deserialize<SceneOptions>(Web)
        );
        var copy = await (
            await c.PostAsync($"/api/projects/{p}/duplicate", null)
        ).Content.ReadFromJsonAsync<JsonElement>();
        var copyId = copy.GetProperty("project").GetProperty("id").GetGuid();
        loaded = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{copyId}/document");
        Assert.Equal(
            style,
            loaded.GetProperty("document").GetProperty("captions").Deserialize<CaptionStyle>(Web)
        );
        var checkpoint = await c.PostAsJsonAsync(
            $"/api/projects/{p}/saved-items",
            new CaptureItem("checkpoint", "Mốc", 1)
        );
        checkpoint.EnsureSuccessStatusCode();
        var preset = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/saved-items",
                new CaptureItem("preset", "Style", 1)
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        var presetId = preset.GetProperty("id").GetGuid();
        var presetPackage = (
            await c.GetFromJsonAsync<RenderPresetPackage>(
                $"/api/projects/{p}/saved-items/{presetId}/export"
            )
        )!;
        Assert.Equal(style, presetPackage.Settings.Captions);
        var report = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/storyboard/preflight",
                new RenderDocument(1)
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(report.GetProperty("ready").GetBoolean());
        var job = await Wait(
            c,
            p,
            await c.PostAsJsonAsync($"/api/projects/{p}/storyboard/render", new RenderDocument(1))
        );
        var recipe = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{job}/recipe");
        Assert.Equal(1, recipe.GetProperty("request").GetProperty("scenes").GetArrayLength());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/jobs/{job}/subtitles")).StatusCode
        );
        await Save(
            c,
            p,
            doc with
            {
                Scenes = doc.Scenes!.Select(s =>
                        s with
                        {
                            Options = (s.Options ?? new()) with { Enabled = false },
                        }
                    )
                    .ToArray(),
            },
            1
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/storyboard/render",
                    new RenderDocument(2)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task RenderCacheSeparatesSameSourceWithDifferentOverridesAndCaptionsActuallyRender()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        var doc = new StoryDocument(
            Scenes:
            [
                new(
                    Guid.NewGuid(),
                    "A",
                    "Xin chào",
                    "",
                    video.Id,
                    1,
                    Options: new(Brightness: -.3)
                ),
                new(Guid.NewGuid(), "B", "Việt Nam", "", video.Id, 1, Options: new(Brightness: .3)),
            ],
            Captions: new(
                Font: "serif",
                Size: 28,
                Color: "#ff8800",
                OutlineColor: "#0000ff",
                Outline: 2,
                Shadow: 1,
                Bold: true,
                Alignment: 8,
                Margin: 10,
                Box: true,
                BoxColor: "#000000",
                BoxOpacity: .7
            )
        );
        await Save(c, p, doc);
        var job = await Wait(
            c,
            p,
            await c.PostAsJsonAsync($"/api/projects/{p}/storyboard/render", new RenderDocument(1))
        );
        var cues = Subtitles.Parse(await c.GetStringAsync($"/api/jobs/{job}/subtitles"));
        Assert.Equal(2, cues.Count);
        Assert.Equal("Xin chào", cues[0].Text);
        var file = Path.Combine(app.Data, "exports", job + ".mp4");
        var raw = await MediaProcess.Run(
            "ffmpeg",
            [
                "-hide_banner",
                "-i",
                file,
                "-vf",
                "signalstats,metadata=print:file=-",
                "-an",
                "-f",
                "null",
                "-",
            ],
            app.Data,
            CancellationToken.None
        );
        Assert.Contains("lavfi.signalstats.YAVG", raw);
        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<Storage>();
            Assert.True(new FileInfo(store.Output(job)).Length > 1000);
        }
    }

    [Fact]
    public async Task PreferencesAreScopedBoundedDurableAndUpgradeIsIdempotent()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var other = await Project(c);
        var asset = await Upload(c, p, app.Video);
        var pref = new PreferenceRequest(true, [" Bàn ", "bàn", "Video"], "Ghi chú\nTiếng Việt");
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/asset-preferences/{asset.Id}", pref)
        ).EnsureSuccessStatusCode();
        var rows = (
            await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/asset-preferences")
        )!;
        Assert.Single(rows);
        Assert.True(rows[0].GetProperty("favorite").GetBoolean());
        Assert.Equal(2, rows[0].GetProperty("tags").GetArrayLength());
        Assert.Equal(pref.Note, rows[0].GetProperty("note").GetString());
        Assert.Empty(
            (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{other}/asset-preferences"))!
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await c.PutAsJsonAsync($"/api/projects/{other}/asset-preferences/{asset.Id}", pref)
            ).StatusCode
        );
        foreach (
            var bad in new[]
            {
                pref with
                {
                    Tags = Enumerable.Repeat("x", 11).ToArray(),
                },
                pref with
                {
                    Tags = [""],
                },
                pref with
                {
                    Note = new string('x', 1001),
                },
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PutAsJsonAsync($"/api/projects/{p}/asset-preferences/{asset.Id}", bad)
                ).StatusCode
            );
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            await LibraryTools.Upgrade(db);
            await LibraryTools.Upgrade(db);
            Assert.Single(await db.AssetPreferences.ToListAsync());
        }
    }

    [Fact]
    public async Task CopyAndDuplicateHashUseRealIndependentBytesAndBulkRenameKeepsExtensions()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var target = await Project(c);
        var a = await Upload(c, p, app.Video, "a.mp4");
        var b = await Upload(c, p, app.Video, "b.mp4");
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/asset-preferences/{a.Id}",
                new PreferenceRequest(true, ["Tag"], "Note")
            )
        ).EnsureSuccessStatusCode();
        var report = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/assets/duplicates",
                new AssetSelection([a.Id, b.Id])
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, report.GetProperty("groups").GetArrayLength());
        Assert.Equal(2, report.GetProperty("groups")[0].GetProperty("assets").GetArrayLength());
        var renamed = await c.PatchAsJsonAsync(
            $"/api/projects/{p}/assets/bulk-name",
            new BulkAssetName([b.Id, a.Id], "Cảnh-", "-VN", 4)
        );
        renamed.EnsureSuccessStatusCode();
        var names = (await renamed.Content.ReadFromJsonAsync<Asset[]>())!;
        Assert.Equal("Cảnh-04-VN.mp4", names.Single(x => x.Id == b.Id).Name);
        Assert.Equal("Cảnh-05-VN.mp4", names.Single(x => x.Id == a.Id).Name);
        var copied = await c.PostAsJsonAsync(
            $"/api/projects/{p}/assets/copy",
            new CopyAssets([a.Id, b.Id], target)
        );
        copied.EnsureSuccessStatusCode();
        var result = await copied.Content.ReadFromJsonAsync<JsonElement>();
        var copies = result.GetProperty("assets").Deserialize<Asset[]>(Web)!;
        Assert.Equal(2, copies.Length);
        Assert.All(copies, x => Assert.Equal(target, x.ProjectId));
        Assert.DoesNotContain(copies, x => x.Id == a.Id || x.Id == b.Id);
        var preferences = (
            await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{target}/asset-preferences")
        )!;
        Assert.Single(preferences);
        Assert.Equal("Note", preferences[0].GetProperty("note").GetString());
        File.Delete(app.Services.GetRequiredService<Storage>().AssetPath(a));
        Assert.Equal(
            await File.ReadAllBytesAsync(app.Video),
            await c.GetByteArrayAsync($"/api/assets/{copies[0].Id}/file")
        );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/assets/copy",
                    new CopyAssets([a.Id], target)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task LibraryOperationsRejectForeignIdsAndUsageIncludesDocumentCheckpointAndJobs()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var other = await Project(c);
        var video = await Upload(c, p, app.Video);
        var ids = new[] { video.Id };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{other}/assets/copy",
                    new CopyAssets(ids, p)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync($"/api/projects/{p}/assets/copy", new CopyAssets(ids, p))
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{other}/assets/duplicates",
                    new AssetSelection(ids)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PatchAsJsonAsync(
                    $"/api/projects/{p}/assets/bulk-name",
                    new BulkAssetName(ids, "../")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PatchAsJsonAsync(
                    $"/api/projects/{other}/assets/bulk-name",
                    new BulkAssetName(ids)
                )
            ).StatusCode
        );
        foreach (
            var bad in new[]
            {
                Array.Empty<Guid>(),
                new[] { video.Id, video.Id },
                Enumerable.Repeat(Guid.NewGuid(), 21).ToArray(),
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{p}/assets/duplicates",
                        new AssetSelection(bad)
                    )
                ).StatusCode
            );
        await Save(c, p, new(Scenes: [new(Guid.NewGuid(), "Nguồn", "", "", video.Id, 1)]));
        (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/saved-items",
                new CaptureItem("checkpoint", "Mốc", 1)
            )
        ).EnsureSuccessStatusCode();
        await Wait(
            c,
            p,
            await c.PostAsJsonAsync($"/api/projects/{p}/storyboard/render", new RenderDocument(1))
        );
        var usage = await c.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{p}/assets/{video.Id}/usage"
        );
        Assert.Equal(1, usage.GetProperty("scenes").GetArrayLength());
        Assert.Equal(1, usage.GetProperty("checkpoints").GetArrayLength());
        Assert.Equal(1, usage.GetProperty("jobCount").GetInt32());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/projects/{other}/assets/{video.Id}/usage")).StatusCode
        );
        var sourcePath = app.Services.GetRequiredService<Storage>().AssetPath(video);
        using (var stream = File.Open(sourcePath, FileMode.Open, FileAccess.Write))
            stream.SetLength(104857601);
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/assets/copy",
                    new CopyAssets([video.Id], other)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/assets/duplicates",
                    new AssetSelection([video.Id])
                )
            ).StatusCode
        );
    }
}
