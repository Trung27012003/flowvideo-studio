using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Studio;
using Xunit;

public sealed class BatchFeatureTests
{
    private static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Bộ kiểm thử" })
            ).Content.ReadFromJsonAsync<Studio.Project>()
        )!.Id;

    private static async Task<Asset> Upload(HttpClient c, Guid id, string path, string name)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", name);
        var response = await c.PostAsync($"/api/projects/{id}/assets", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Asset>())!;
    }

    private static async Task<(Guid Id, JsonElement Row)> Wait(
        HttpClient c,
        Guid project,
        HttpResponseMessage response
    )
    {
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        for (var i = 0; i < 200; i++)
        {
            var rows = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
            var row = rows.EnumerateArray().First(r => r.GetProperty("id").GetGuid() == id);
            var status = row.GetProperty("status").GetString();
            if (status == "Failed")
                throw new Exception(row.ToString());
            if (status == "Succeeded")
                return (id, row.Clone());
            await Task.Delay(100);
        }
        throw new Exception("Render timeout");
    }

    [Fact]
    public async Task JsonRoundTripPreservesContentAndSettingsButDetachesMediaIntoNewProject()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var audio = await Upload(c, project, app.Audio, "audio.mp3");
        var first = new StoryScene(Guid.NewGuid(), "Cảnh 1", "Xin chào", "Prompt", video.Id, 1, .2);
        var second = first with { Id = Guid.NewGuid(), Title = "Bản sao" };
        var doc = new StoryDocument(
            "Ý tưởng",
            "Kịch bản",
            [first, second],
            "16:9",
            audio.Id,
            audio.Id,
            true,
            1080,
            .7,
            .25
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{project}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        var exported = await c.GetAsync($"/api/projects/{project}/storyboard/export");
        Assert.Equal("application/json", exported.Content.Headers.ContentType!.MediaType);
        Assert.Contains(
            ".json",
            exported.Content.Headers.ContentDisposition!.FileNameStar
                ?? exported.Content.Headers.ContentDisposition.FileName!
        );
        var package = (await exported.Content.ReadFromJsonAsync<StoryboardPackage>())!;
        Assert.Equal("flowvideo.storyboard", package.Format);
        Assert.Equal(1080, package.Document.Resolution);
        var imported = await c.PostAsJsonAsync("/api/storyboards/import", package);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>();
        var newId = result.GetProperty("project").GetProperty("id").GetGuid();
        Assert.NotEqual(project, newId);
        Assert.Equal(4, result.GetProperty("detachedMediaCount").GetInt32());
        var loaded = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{newId}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Kịch bản", loaded.Script);
        Assert.Equal(1080, loaded.Resolution);
        Assert.Equal(.7, loaded.VoiceGain);
        Assert.Equal(.25, loaded.MusicGain);
        Assert.Null(loaded.VoiceId);
        Assert.Null(loaded.MusicId);
        Assert.Equal(2, loaded.Scenes!.Length);
        Assert.All(
            loaded.Scenes,
            s =>
            {
                Assert.Null(s.AssetId);
                Assert.Equal(0, s.Start);
            }
        );
        Assert.DoesNotContain(loaded.Scenes, s => s.Id == first.Id || s.Id == second.Id);
        Assert.Empty((await c.GetFromJsonAsync<Asset[]>($"/api/projects/{newId}/assets"))!);
        var original = (
            await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/document")
        ).GetProperty("document");
        Assert.Equal(video.Id, original.GetProperty("scenes")[0].GetProperty("assetId").GetGuid());
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task LargestUnicodeDocumentCanImportItsOwnExport()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var scenes = Enumerable
            .Range(0, 30)
            .Select(_ => new StoryScene(
                Guid.NewGuid(),
                new string('ắ', 120),
                new string('ắ', 2000),
                new string('ắ', 4000),
                null,
                1
            ))
            .ToArray();
        var document = new StoryDocument(new string('ắ', 4000), new string('ắ', 50000), scenes);
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(0, document)
            )
        ).EnsureSuccessStatusCode();
        var bytes = await c.GetByteArrayAsync($"/api/projects/{project}/storyboard/export");
        Assert.InRange(bytes.Length, 524289, 2097152);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json"
        );
        Assert.Equal(
            HttpStatusCode.Created,
            (await c.PostAsync("/api/storyboards/import", content)).StatusCode
        );
    }

    [Fact]
    public async Task InvalidOrOversizedImportsDoNotCreateProjects()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var good = new StoryboardPackage(
            "flowvideo.storyboard",
            1,
            "Import",
            new StoryDocument(Scenes: [])
        );
        foreach (
            var package in new[]
            {
                good with
                {
                    Format = "wrong",
                },
                good with
                {
                    SchemaVersion = 2,
                },
                good with
                {
                    Document = good.Document with { Scenes = [null!] },
                },
                good with
                {
                    Document = good.Document with { Script = new string('x', 50001) },
                },
                good with
                {
                    Document = good.Document with { Resolution = 1440 },
                },
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await c.PostAsJsonAsync("/api/storyboards/import", package)).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsync(
                    "/api/storyboards/import",
                    new StringContent("{invalid", Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (
                await c.PostAsync(
                    "/api/storyboards/import",
                    new StringContent(new string('x', 2097153), Encoding.UTF8, "application/json")
                )
            ).StatusCode
        );
        Assert.Empty((await c.GetFromJsonAsync<Studio.Project[]>("/api/projects"))!);
    }

    [Fact]
    public void OldSerializedDocumentsAndJobsKeepCompatibleDefaults()
    {
        var doc = JsonSerializer.Deserialize<StoryDocument>("{\"Scenes\":[]}")!;
        Assert.Equal(720, doc.Resolution);
        Assert.Equal(1, doc.VoiceGain);
        Assert.Equal(.18, doc.MusicGain);
        var job = JsonSerializer.Deserialize<RenderRequest>("{\"AssetIds\":[]}")!;
        Assert.Equal(720, job.Resolution);
        Assert.Equal(1, job.VoiceGain);
        Assert.Equal(.18, job.MusicGain);
    }

    [Theory]
    [InlineData(1440, 1, .18)]
    [InlineData(720, -1, .18)]
    [InlineData(1080, 1, 2.1)]
    public async Task RejectsUnsupportedResolutionOrGain(int resolution, double voice, double music)
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "source.mp4");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{project}/renders",
                    new RenderRequest(
                        [video.Id],
                        Resolution: resolution,
                        VoiceGain: voice,
                        MusicGain: music
                    )
                )
            ).StatusCode
        );
        var doc = new StoryDocument(
            Scenes: [],
            Resolution: resolution,
            VoiceGain: voice,
            MusicGain: music
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PutAsJsonAsync(
                    $"/api/projects/{project}/document",
                    new SaveDocument(0, doc)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task RendersFullHdAndDownloadsActualSceneCaptions()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var audio = await Upload(c, project, app.Audio, "voice.mp3");
        var doc = new StoryDocument(
            Scenes:
            [
                new(Guid.NewGuid(), "A", "Mở đầu", "", video.Id, .5),
                new(Guid.NewGuid(), "B", "Kết thúc", "", video.Id, .5),
            ],
            Ratio: "16:9",
            VoiceId: audio.Id,
            Resolution: 1080,
            VoiceGain: .7
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{project}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        var (job, row) = await Wait(
            c,
            project,
            await c.PostAsJsonAsync(
                $"/api/projects/{project}/storyboard/render",
                new RenderDocument(1)
            )
        );
        Assert.True(row.GetProperty("hasSubtitles").GetBoolean());
        var subtitles = await c.GetAsync($"/api/jobs/{job}/subtitles");
        subtitles.EnsureSuccessStatusCode();
        var cues = Subtitles.Parse(await subtitles.Content.ReadAsStringAsync());
        Assert.Equal(2, cues.Count);
        Assert.Equal("Mở đầu", cues[0].Text);
        Assert.InRange(cues[1].Start, .49, .54);
        Assert.InRange(cues[1].End, .99, 1.05);
        var info = await MediaProcess.Run(
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
                Path.Combine(app.Data, "exports", job + ".mp4"),
            ],
            app.Data,
            CancellationToken.None
        );
        using var json = JsonDocument.Parse(info);
        var stream = json.RootElement.GetProperty("streams")[0];
        Assert.Equal(1920, stream.GetProperty("width").GetInt32());
        Assert.Equal(1080, stream.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task CaptionlessRenderDoesNotExposeSubtitleDownload()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var (job, row) = await Wait(
            c,
            project,
            await c.PostAsJsonAsync(
                $"/api/projects/{project}/renders",
                new RenderRequest([video.Id], Duration: 1)
            )
        );
        Assert.False(row.GetProperty("hasSubtitles").GetBoolean());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/jobs/{job}/subtitles")).StatusCode
        );
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("music")]
    [InlineData("mix")]
    public async Task RenderedAudioRespondsToGainControls(string kind)
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var audio = await Upload(c, project, app.Audio, "tone.mp3");
        var levels = new List<double>();
        foreach (var gain in new[] { 1.0, .25 })
        {
            var request = new RenderRequest(
                [video.Id],
                Duration: 1,
                VoiceId: kind != "music" ? audio.Id : null,
                MusicId: kind != "voice" ? audio.Id : null,
                VoiceGain: gain,
                MusicGain: gain
            );
            var (job, _) = await Wait(
                c,
                project,
                await c.PostAsJsonAsync($"/api/projects/{project}/renders", request)
            );
            levels.Add(await Rms(Path.Combine(app.Data, "exports", job + ".mp4")));
        }
        Assert.True(levels[0] > .01);
        Assert.InRange(levels[1] / levels[0], .20, .30);
    }

    private static async Task<double> Rms(string path)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            var arg in new[]
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-i",
                path,
                "-map",
                "0:a:0",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-f",
                "f32le",
                "pipe:1",
            }
        )
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        var data = buffer.ToArray();
        double sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            var sample = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                data.AsSpan(i, 4)
            );
            sum += sample * sample;
        }
        return Math.Sqrt(sum / (data.Length / 4));
    }
}
