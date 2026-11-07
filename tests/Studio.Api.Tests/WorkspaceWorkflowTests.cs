using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class WorkspaceWorkflowTests
{
    static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Workflow" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    static async Task<Asset> Upload(HttpClient c, Guid p, string path, string name = "source.mp4")
    {
        using var f = new MultipartFormDataContent();
        f.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", name);
        var r = await c.PostAsync($"/api/projects/{p}/assets", f);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Asset>())!;
    }

    static async Task Save(HttpClient c, Guid p, StoryDocument doc, int version = 0) =>
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(version, doc))
        ).EnsureSuccessStatusCode();

    static async Task<Guid> Capture(HttpClient c, Guid p, string kind, int version = 1)
    {
        var r = await c.PostAsJsonAsync(
            $"/api/projects/{p}/saved-items",
            new CaptureItem(kind, "Mục thử", version)
        );
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task CheckpointsAreImmutableVersionedProjectScopedAndPortable()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var other = await Project(c);
        var video = await Upload(c, p, app.Video);
        var doc = new StoryDocument(
            "Ý tưởng",
            "Bản gốc",
            [new(Guid.NewGuid(), "A", "Lời gốc", "", video.Id, 1)],
            Effects: new(Brightness: .2)
        );
        await Save(c, p, doc);
        var item = await Capture(c, p, "checkpoint");
        await Save(c, p, doc with { Script = "Bản sửa" }, 1);
        var checkpoint = await c.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{p}/saved-items/{item}"
        );
        Assert.Equal(
            "Bản gốc",
            checkpoint.GetProperty("document").GetProperty("script").GetString()
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/saved-items",
                    new CaptureItem("checkpoint", "Stale", 1)
                )
            ).StatusCode
        );
        foreach (var method in new[] { "GET", "PATCH", "DELETE" })
        {
            using var req = new HttpRequestMessage(
                new HttpMethod(method),
                $"/api/projects/{other}/saved-items/{item}"
            );
            if (method == "PATCH")
                req.Content = JsonContent.Create(new NameRequest("Tên mới"));
            Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(req)).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PatchAsJsonAsync(
                    $"/api/projects/{p}/saved-items/{item}",
                    new NameRequest("\n")
                )
            ).StatusCode
        );
        (
            await c.PatchAsJsonAsync(
                $"/api/projects/{p}/saved-items/{item}",
                new NameRequest("Mốc đầu")
            )
        ).EnsureSuccessStatusCode();
        var package = (
            await c.GetFromJsonAsync<StoryboardPackage>(
                $"/api/projects/{p}/saved-items/{item}/export"
            )
        )!;
        Assert.Equal("Mốc đầu", package.Name);
        Assert.Equal(doc.Script, package.Document.Script);
        Assert.Equal(doc.Scenes, package.Document.Scenes);
        Assert.Equal(doc.Effects, package.Document.Effects);
        var imported = await c.PostAsJsonAsync("/api/storyboards/import", package);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        await Save(c, p, package.Document, 2);
        Assert.Equal(
            "Bản gốc",
            (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document"))
                .GetProperty("document")
                .GetProperty("script")
                .GetString()
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await c.DeleteAsync($"/api/projects/{p}/saved-items/{item}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/projects/{p}/saved-items/{item}")).StatusCode
        );
        Assert.Single((await c.GetFromJsonAsync<Asset[]>($"/api/projects/{p}/assets"))!);
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task PresetsDetachContentAndMediaAndImportRejectsInvalidOrOversizedData()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var audio = await Upload(c, p, app.Audio, "audio.mp3");
        var doc = new StoryDocument(
            "Private idea",
            "Private script",
            [],
            VoiceId: audio.Id,
            MusicId: audio.Id,
            Ratio: "16:9",
            Effects: new(Contrast: 1.4)
        );
        await Save(c, p, doc);
        var id = await Capture(c, p, "preset");
        var package = (
            await c.GetFromJsonAsync<RenderPresetPackage>(
                $"/api/projects/{p}/saved-items/{id}/export"
            )
        )!;
        Assert.Equal("flowvideo.render-preset", package.Format);
        Assert.Empty(package.Settings.Scenes!);
        Assert.Null(package.Settings.VoiceId);
        Assert.Null(package.Settings.MusicId);
        Assert.Equal("", package.Settings.Idea);
        Assert.Equal("", package.Settings.Script);
        Assert.Equal(1.4, package.Settings.Effects!.Contrast);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/saved-items/import-preset", package)
        ).EnsureSuccessStatusCode();
        foreach (
            var invalid in new[]
            {
                package with
                {
                    Format = "wrong",
                },
                package with
                {
                    SchemaVersion = 2,
                },
                package with
                {
                    Name = "",
                },
                package with
                {
                    Settings = package.Settings with { Effects = new(Fps: 300) },
                },
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync($"/api/projects/{p}/saved-items/import-preset", invalid)
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsync(
                    $"/api/projects/{p}/saved-items/import-preset",
                    new StringContent("invalid", Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
        using var chunked = new UnknownLengthJson(new string('x', 65537));
        chunked.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json"
        );
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await c.PostAsync($"/api/projects/{p}/saved-items/import-preset", chunked)).StatusCode
        );
        Assert.Equal(
            2,
            (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/saved-items"))!.Length
        );
    }

    [Fact]
    public async Task ItemLimitsAndUpgradeAreAdditiveAndIdempotent()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        await Save(c, p, new(Scenes: []));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE SavedWorkspaceItems");
            await WorkspaceWorkflow.Upgrade(db);
            await WorkspaceWorkflow.Upgrade(db);
        }
        for (int i = 0; i < 20; i++)
            await Capture(c, p, "checkpoint");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/saved-items",
                    new CaptureItem("checkpoint", "21", 1)
                )
            ).StatusCode
        );
        await Capture(c, p, "preset");
        Assert.Equal(
            21,
            (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/saved-items"))!.Length
        );
        Assert.Equal(
            await File.ReadAllBytesAsync(app.Video),
            await c.GetByteArrayAsync($"/api/assets/{video.Id}/file")
        );
        Assert.Single((await c.GetFromJsonAsync<Project[]>("/api/projects"))!);
    }

    [Fact]
    public async Task PreflightAndSummaryIdentifyPaddedBadAndMissingSourcesWithoutRendering()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        var doc = new StoryDocument(Scenes: [new(Guid.NewGuid(), "A", "Lời", "", video.Id, 2)]);
        await Save(c, p, doc);
        var response = await c.PostAsJsonAsync(
            $"/api/projects/{p}/storyboard/preflight",
            new RenderDocument(1)
        );
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(report.GetProperty("ready").GetBoolean());
        Assert.Contains(
            report.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("message").GetString()!.Contains("giữ khung")
        );
        await Save(c, p, doc with { Scenes = [doc.Scenes![0] with { Start = 10 }] }, 1);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/storyboard/preflight",
                    new RenderDocument(1)
                )
            ).StatusCode
        );
        report = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/storyboard/preflight",
                new RenderDocument(2)
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("ready").GetBoolean());
        File.Delete(app.Services.GetRequiredService<Storage>().AssetPath(video));
        var summary = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/summary");
        Assert.Equal(1, summary.GetProperty("missingFiles").GetInt32());
        Assert.Equal(2, summary.GetProperty("duration").GetDouble());
        Assert.Equal(0, summary.GetProperty("bytes").GetInt64());
        report = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/storyboard/preflight",
                new RenderDocument(2)
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("ready").GetBoolean());
        Assert.Empty((await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!);
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task PreflightFindsMissingBindingsAndBrokenAudio()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var invalid = Path.Combine(app.Data, "bad.wav");
        await File.WriteAllTextAsync(invalid, "invalid");
        var audio = await Upload(c, p, invalid, "bad.wav");
        await Save(
            c,
            p,
            new(Scenes: [new(Guid.NewGuid(), "A", "", "", null, 1)], VoiceId: audio.Id)
        );
        var response = await c.PostAsJsonAsync(
            $"/api/projects/{p}/storyboard/preflight",
            new RenderDocument(1)
        );
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("ready").GetBoolean());
        Assert.Equal(
            2,
            report
                .GetProperty("issues")
                .EnumerateArray()
                .Count(i => i.GetProperty("severity").GetString() == "error")
        );
    }

    [Fact]
    public async Task VariantRenderIsAtomicAndKeepsTheOriginalSnapshotAndBothAspectRatios()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var video = await Upload(c, p, app.Video);
        var doc = new StoryDocument(
            Scenes: [new(Guid.NewGuid(), "A", "Gốc", "", video.Id, 1)],
            Effects: new(Fps: 24, Brightness: .1)
        );
        await Save(c, p, doc);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/storyboard/render-variants",
                    new RenderDocument(0)
                )
            ).StatusCode
        );
        var response = await c.PostAsJsonAsync(
            $"/api/projects/{p}/storyboard/render-variants",
            new RenderDocument(1)
        );
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = result
            .GetProperty("jobs")
            .EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid())
            .ToArray();
        Assert.Equal(2, ids.Length);
        await Save(c, p, doc with { Scenes = [doc.Scenes![0] with { Narration = "Đã sửa" }] }, 1);
        for (int n = 0; n < 300; n++)
        {
            var rows = (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!;
            Assert.DoesNotContain(rows, j => j.GetProperty("status").GetString() == "Failed");
            if (rows.All(j => j.GetProperty("status").GetString() == "Succeeded"))
                break;
            if (n == 299)
                throw new Exception("Variants timed out");
            await Task.Delay(100);
        }
        var ratios = new List<string>();
        foreach (var id in ids)
        {
            var recipe = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}/recipe");
            var request = recipe.GetProperty("request");
            var ratio = request.GetProperty("ratio").GetString()!;
            ratios.Add(ratio);
            Assert.Equal(
                "Gốc",
                request.GetProperty("scenes")[0].GetProperty("caption").GetString()
            );
            Assert.Equal(24, request.GetProperty("effects").GetProperty("fps").GetInt32());
            var raw = await MediaProcess.Run(
                "ffprobe",
                [
                    "-v",
                    "error",
                    "-select_streams",
                    "v:0",
                    "-show_entries",
                    "stream=width,height",
                    "-of",
                    "json",
                    Path.Combine(app.Data, "exports", id + ".mp4"),
                ],
                app.Data,
                CancellationToken.None
            );
            using var info = JsonDocument.Parse(raw);
            var stream = info.RootElement.GetProperty("streams")[0];
            Assert.Equal(ratio == "9:16" ? 720 : 1280, stream.GetProperty("width").GetInt32());
            Assert.Equal(ratio == "9:16" ? 1280 : 720, stream.GetProperty("height").GetInt32());
        }
        Assert.Contains("9:16", ratios);
        Assert.Contains("16:9", ratios);
        var duplicate = await c.PostAsync($"/api/jobs/{ids[0]}/duplicate", null);
        duplicate.EnsureSuccessStatusCode();
        var newId = (await duplicate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        Assert.DoesNotContain(newId, ids);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            var source = await db.Jobs.FindAsync(ids[0]);
            var copy = await db.Jobs.FindAsync(newId);
            Assert.Equal(source!.RequestJson, copy!.RequestJson);
        }
        var cancelled = new RenderJob
        {
            ProjectId = p,
            Status = "Cancelled",
            RequestJson = "{}",
        };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            db.Jobs.Add(cancelled);
            await db.SaveChangesAsync();
        }
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsync($"/api/jobs/{cancelled.Id}/duplicate", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.PostAsync($"/api/jobs/{Guid.NewGuid()}/duplicate", null)).StatusCode
        );
    }

    private sealed class UnknownLengthJson(string text) : HttpContent
    {
        public UnknownLengthJson()
            : this("") { }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    }
}
