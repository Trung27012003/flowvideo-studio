using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class WorkspaceToolTests
{
    private static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Công cụ" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    private static async Task<Asset> Upload(
        HttpClient c,
        Guid id,
        string path,
        string name = "clip.mp4"
    )
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", name);
        var response = await c.PostAsync($"/api/projects/{id}/assets", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Asset>())!;
    }

    [Fact]
    public async Task RenamePersistsAndPreservesAssetFileAndExtension()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var id = await Project(c);
        var asset = await Upload(c, id, app.Video);
        (
            await c.PatchAsJsonAsync($"/api/projects/{id}", new { name = "Tên mới" })
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Tên mới",
            (await c.GetFromJsonAsync<Project[]>("/api/projects"))!.Single().Name
        );
        var renamed = await c.PatchAsJsonAsync(
            $"/api/assets/{asset.Id}",
            new { name = "Tư liệu mới" }
        );
        renamed.EnsureSuccessStatusCode();
        Assert.Equal("Tư liệu mới.mp4", (await renamed.Content.ReadFromJsonAsync<Asset>())!.Name);
        Assert.Equal(
            await File.ReadAllBytesAsync(app.Video),
            await c.GetByteArrayAsync($"/api/assets/{asset.Id}/file")
        );
        foreach (
            var name in new[] { "../escape.mp4", "C:\\escape.mp4", "", "bad\nname", "wrong.mp3" }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await c.PatchAsJsonAsync($"/api/assets/{asset.Id}", new { name })).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await c.PatchAsJsonAsync(
                    $"/api/projects/{Guid.NewGuid()}",
                    new { name = "Missing" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task CloneRemapsScenesAndAudioAndCopiesMediaIndependently()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var id = await Project(c);
        var asset = await Upload(c, id, app.Video);
        var audio = await Upload(c, id, app.Audio, "voice.mp3");
        var scene = new StoryScene(Guid.NewGuid(), "Mở đầu", "Xin chào", "Prompt", asset.Id, 1);
        var doc = new StoryDocument(
            Scenes: [scene],
            VoiceId: audio.Id,
            MusicId: audio.Id,
            Fit: "cover",
            FadeSeconds: .2,
            DuckMusic: true
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{id}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        var response = await c.PostAsync($"/api/projects/{id}/duplicate", null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cloneId = result.GetProperty("project").GetProperty("id").GetGuid();
        Assert.Equal(2, result.GetProperty("copiedAssets").GetInt32());
        var copies = (await c.GetFromJsonAsync<Asset[]>($"/api/projects/{cloneId}/assets"))!;
        var copied = copies.Single(a => a.Kind == "video");
        Assert.NotEqual(asset.Id, copied.Id);
        Assert.NotEqual(asset.StoredName, copied.StoredName);
        var saved = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{cloneId}/document");
        var document = saved
            .GetProperty("document")
            .Deserialize<StoryDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        Assert.Equal(copied.Id, document.Scenes![0].AssetId);
        Assert.NotEqual(scene.Id, document.Scenes[0].Id);
        Assert.Equal(copies.Single(a => a.Kind == "audio").Id, document.VoiceId);
        Assert.Equal(document.MusicId, document.VoiceId);
        Assert.Equal("cover", document.Fit);
        Assert.Equal(.2, document.FadeSeconds);
        Assert.True(document.DuckMusic);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{cloneId}/jobs"))!);
        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<Storage>();
            File.Delete(store.AssetPath(asset));
        }
        Assert.Equal(
            await File.ReadAllBytesAsync(app.Video),
            await c.GetByteArrayAsync($"/api/assets/{copied.Id}/file")
        );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await c.PostAsync($"/api/projects/{id}/duplicate", null)).StatusCode
        );
        Assert.Equal(2, (await c.GetFromJsonAsync<Project[]>("/api/projects"))!.Length);
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task ArchiveRejectsForeignDuplicateAndTooManyIdsAndSanitizesLegacyNames()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var id = await Project(c);
        var other = await Project(c);
        var asset = await Upload(c, id, app.Video);
        foreach (
            var ids in new[]
            {
                Array.Empty<Guid>(),
                new[] { asset.Id, asset.Id },
                Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(),
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{id}/assets/archive",
                        new ArchiveRequest(ids)
                    )
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{other}/assets/archive",
                    new ArchiveRequest([asset.Id])
                )
            ).StatusCode
        );
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            await db
                .Assets.Where(a => a.Id == asset.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "../../C:\\escape.mp4"));
        }
        var response = await c.PostAsJsonAsync(
            $"/api/projects/{id}/assets/archive",
            new ArchiveRequest([asset.Id])
        );
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(
            new MemoryStream(await response.Content.ReadAsByteArrayAsync())
        );
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("01-escape.mp4", entry.FullName);
        using var stream = entry.Open();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(await File.ReadAllBytesAsync(app.Video), output.ToArray());
    }

    [Fact]
    public async Task CsvAndTextExportsPreserveVietnameseAndEscapeFormulaFields()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var id = await Project(c);
        var asset = await Upload(c, id, app.Video);
        (
            await c.PatchAsJsonAsync($"/api/assets/{asset.Id}", new { name = "=SUM(1,2).mp4" })
        ).EnsureSuccessStatusCode();
        var doc = new StoryDocument(
            Scenes:
            [
                new(
                    Guid.NewGuid(),
                    "=SUM(1,2)",
                    "Tiếng Việt, \"xin chào\"",
                    "@formula",
                    asset.Id,
                    1
                ),
                new(Guid.NewGuid(), "B", "Lời thứ hai", "", null, 2),
            ]
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{id}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        var manifest = await c.GetStringAsync($"/api/projects/{id}/assets/manifest");
        Assert.Contains("\"'=SUM(1,2).mp4\"", manifest);
        var shotlist = await c.GetStringAsync($"/api/projects/{id}/storyboard/shotlist");
        Assert.Contains("\"'=SUM(1,2)\"", shotlist);
        Assert.Contains("\"'@formula\"", shotlist);
        Assert.Contains("\"\"xin chào\"\"", shotlist);
        Assert.Equal(
            "Tiếng Việt, \"xin chào\"\n\nLời thứ hai",
            await c.GetStringAsync($"/api/projects/{id}/storyboard/narration")
        );
    }

    [Fact]
    public async Task MetadataReportsVideoAndHandlesInvalidSources()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var id = await Project(c);
        var asset = await Upload(c, id, app.Video);
        var metadata = await c.GetFromJsonAsync<JsonElement>($"/api/assets/{asset.Id}/metadata");
        Assert.True(metadata.GetProperty("width").GetInt32() > 0);
        Assert.True(metadata.GetProperty("height").GetInt32() > 0);
        Assert.InRange(metadata.GetProperty("duration").GetDouble(), .9, 1.2);
        Assert.Equal(new FileInfo(app.Video).Length, metadata.GetProperty("bytes").GetInt64());
        using (var scope = app.Services.CreateScope())
            await File.WriteAllTextAsync(
                scope.ServiceProvider.GetRequiredService<Storage>().AssetPath(asset),
                "invalid"
            );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await c.GetAsync($"/api/assets/{asset.Id}/metadata")).StatusCode
        );
    }

    [Theory]
    [InlineData("bad", 0)]
    [InlineData("cover", -1)]
    [InlineData("contain", 2.1)]
    public async Task RejectsInvalidCompositionOptionsForDocumentsAndRenders(
        string fit,
        double fade
    )
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var id = await Project(c);
        var asset = await Upload(c, id, app.Video);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{id}/renders",
                    new RenderRequest([asset.Id], Fit: fit, FadeSeconds: fade)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(0, new StoryDocument(Scenes: [], Fit: fit, FadeSeconds: fade))
                )
            ).StatusCode
        );
    }

    [Fact]
    public void DefaultsKeepOldDocumentsAndJobsPlayable()
    {
        var doc = JsonSerializer.Deserialize<StoryDocument>("{\"Scenes\":[]}")!;
        var job = JsonSerializer.Deserialize<RenderRequest>("{\"AssetIds\":[]}")!;
        Assert.Equal("contain", doc.Fit);
        Assert.Equal(0, doc.FadeSeconds);
        Assert.False(doc.DuckMusic);
        Assert.Equal("contain", job.Fit);
        Assert.Equal(0, job.FadeSeconds);
        Assert.False(job.DuckMusic);
    }
}
