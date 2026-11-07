using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class ProductionToolsTests
{
    private static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "v0.8" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    private static async Task<Asset> Upload(HttpClient c, Guid project, string file, string name)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(file)), "file", name);
        var response = await c.PostAsync($"/api/projects/{project}/assets", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Asset>())!;
    }

    private static async Task<Guid> Render(HttpClient c, Guid project, RenderRequest request)
    {
        var response = await c.PostAsJsonAsync($"/api/projects/{project}/renders", request);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        for (int n = 0; n < 300; n++)
        {
            var rows = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
            var row = rows.EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == id);
            var status = row.GetProperty("status").GetString();
            Assert.NotEqual("Failed", status);
            if (status == "Succeeded")
                return id;
            await Task.Delay(100);
        }
        throw new Exception("Render timed out.");
    }

    private static async Task<string> Mux(AudioApp app)
    {
        var path = Path.Combine(app.Data, "source.mp4");
        await MediaProcess.Run(
            "ffmpeg",
            [
                "-y",
                "-loglevel",
                "error",
                "-i",
                app.Video,
                "-i",
                app.Audio,
                "-c:v",
                "copy",
                "-c:a",
                "aac",
                "-shortest",
                path,
            ],
            app.Data,
            CancellationToken.None
        );
        return path;
    }

    private static async Task<JsonElement> Probe(string path)
    {
        var raw = await MediaProcess.Run(
            "ffprobe",
            ["-v", "error", "-show_streams", "-of", "json", path],
            Path.GetDirectoryName(path)!,
            CancellationToken.None
        );
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    [Fact]
    public async Task InvalidEffectsAreRejectedByBothIntakePaths()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var defaults = new RenderEffects();
        foreach (
            var effects in new[]
            {
                defaults with
                {
                    Brightness = .51,
                },
                defaults with
                {
                    Contrast = .49,
                },
                defaults with
                {
                    Saturation = 2.1,
                },
                defaults with
                {
                    Rotation = 45,
                },
                defaults with
                {
                    Sharpen = 1.6,
                },
                defaults with
                {
                    Background = "red;movie=x",
                },
                defaults with
                {
                    Background = null!,
                },
                defaults with
                {
                    Fps = 120,
                },
                defaults with
                {
                    Quality = "ultra",
                },
                defaults with
                {
                    VoiceDelay = 61,
                },
                defaults with
                {
                    MusicStart = -1,
                },
                defaults with
                {
                    VoiceSpeed = .5,
                },
            }
        )
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{project}/renders",
                        new RenderRequest([video.Id], Effects: effects)
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PutAsJsonAsync(
                        $"/api/projects/{project}/document",
                        new SaveDocument(0, new StoryDocument(Scenes: [], Effects: effects))
                    )
                ).StatusCode
            );
        }
    }

    [Fact]
    public async Task EffectsSurviveSaveExportImportAndClone()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var effects = new RenderEffects(
            Brightness: .2,
            Contrast: 1.4,
            Saturation: .3,
            FlipHorizontal: true,
            FlipVertical: true,
            Rotation: 270,
            Vignette: true,
            Sharpen: .7,
            Background: "#123abc",
            Fps: 60,
            Quality: "high",
            Mono: true,
            MusicStart: .2,
            VoiceDelay: .5,
            VoiceSpeed: 1.25,
            NormalizeVoice: true
        );
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(0, new StoryDocument(Scenes: [], Effects: effects))
            )
        ).EnsureSuccessStatusCode();
        var package = (
            await c.GetFromJsonAsync<StoryboardPackage>(
                $"/api/projects/{project}/storyboard/export"
            )
        )!;
        Assert.Equal(effects, package.Document.Effects);
        var imported = await (
            await c.PostAsJsonAsync("/api/storyboards/import", package)
        ).Content.ReadFromJsonAsync<JsonElement>();
        var cloned = await (
            await c.PostAsync($"/api/projects/{project}/duplicate", null)
        ).Content.ReadFromJsonAsync<JsonElement>();
        foreach (var result in new[] { imported, cloned })
        {
            var id = result.GetProperty("project").GetProperty("id").GetGuid();
            var saved = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/document");
            Assert.Equal(
                effects,
                saved
                    .GetProperty("document")
                    .GetProperty("effects")
                    .Deserialize<RenderEffects>(
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    )
            );
        }
        Assert.Null(JsonSerializer.Deserialize<RenderRequest>("{\"AssetIds\":[]}")!.Effects);
    }

    [Fact]
    public async Task MediaToolsProduceRealPngWaveformAndIndependentLibraryAssets()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var source = await Mux(app);
        var video = await Upload(c, project, source, "Nguồn.mp4");
        foreach (var endpoint in new[] { "thumbnail?at=0.2", "waveform" })
        {
            var response = await c.GetAsync($"/api/assets/{video.Id}/{endpoint}");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8).ToArray());
            if (endpoint == "waveform")
            {
                Assert.Equal(
                    1000,
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4))
                );
                Assert.Equal(
                    200,
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4))
                );
            }
        }
        var audioResponse = await c.PostAsync($"/api/assets/{video.Id}/extract-audio", null);
        audioResponse.EnsureSuccessStatusCode();
        var audio = (await audioResponse.Content.ReadFromJsonAsync<Asset>())!;
        var frameResponse = await c.PostAsJsonAsync(
            $"/api/assets/{video.Id}/frame",
            new FrameRequest(.3)
        );
        frameResponse.EnsureSuccessStatusCode();
        var frame = (await frameResponse.Content.ReadFromJsonAsync<Asset>())!;
        Assert.Equal("audio", audio.Kind);
        Assert.Equal("image", frame.Kind);
        Assert.Equal(project, audio.ProjectId);
        Assert.NotEqual(video.Id, audio.Id);
        var store = app.Services.GetRequiredService<Storage>();
        var info = await Probe(store.AssetPath(audio));
        Assert.Equal("mp3", info.GetProperty("streams")[0].GetProperty("codec_name").GetString());
        File.Delete(store.AssetPath(video));
        Assert.True((await c.GetByteArrayAsync($"/api/assets/{frame.Id}/file")).Length > 100);
        Assert.True(File.Exists(store.AssetPath(audio)));
        Assert.Equal(
            3,
            (await c.GetFromJsonAsync<Asset[]>($"/api/projects/{project}/assets"))!.Length
        );
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp")));
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task MediaToolsRejectWrongTypesBadTimesAndSourcesWithoutAudioWithoutLeakingFiles()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "Silent.mp4");
        var audio = await Upload(c, project, app.Audio, "Tone.mp3");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await c.GetAsync($"/api/assets/{audio.Id}/thumbnail")).StatusCode
        );
        foreach (var at in new[] { -1, 601, 1.1 })
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync($"/api/assets/{video.Id}/frame", new FrameRequest(at))
                ).StatusCode
            );
        foreach (var action in new[] { "extract-audio", "frame" })
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync($"/api/assets/{audio.Id}/{action}", new FrameRequest())
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await c.GetAsync($"/api/assets/{video.Id}/waveform")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await c.PostAsync($"/api/assets/{video.Id}/extract-audio", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/jobs/{Guid.NewGuid()}/audio")).StatusCode
        );
        Assert.Equal(
            2,
            (await c.GetFromJsonAsync<Asset[]>($"/api/projects/{project}/assets"))!.Length
        );
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp")));
    }

    [Fact]
    public async Task CombinedEffectsRenderAndJobExportsWorkWithActualFpsAndMonoAudio()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var audio = await Upload(c, project, app.Audio, "voice.mp3");
        var effects = new RenderEffects(
            Brightness: .1,
            Contrast: 1.2,
            Saturation: .5,
            FlipHorizontal: true,
            FlipVertical: true,
            Rotation: 90,
            Vignette: true,
            Sharpen: .4,
            Background: "#ee00ee",
            Fps: 24,
            Quality: "compact",
            Mono: true,
            MusicStart: .1,
            VoiceDelay: .1,
            VoiceSpeed: 1.25,
            NormalizeVoice: true
        );
        var job = await Render(
            c,
            project,
            new RenderRequest(
                [video.Id],
                Mode: "loop",
                Duration: 2,
                VoiceId: audio.Id,
                MusicId: audio.Id,
                Effects: effects,
                FadeSeconds: .1,
                DuckMusic: true
            )
        );
        var info = await Probe(Path.Combine(app.Data, "exports", job + ".mp4"));
        var streams = info.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Equal(
            "24/1",
            streams
                .Single(s => s.GetProperty("codec_type").GetString() == "video")
                .GetProperty("r_frame_rate")
                .GetString()
        );
        Assert.Equal(
            1,
            streams
                .Single(s => s.GetProperty("codec_type").GetString() == "audio")
                .GetProperty("channels")
                .GetInt32()
        );
        Assert.Equal(
            "48000",
            streams
                .Single(s => s.GetProperty("codec_type").GetString() == "audio")
                .GetProperty("sample_rate")
                .GetString()
        );
        var recipeResponse = await c.GetAsync($"/api/jobs/{job}/recipe");
        recipeResponse.EnsureSuccessStatusCode();
        var text = await recipeResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(app.Data, text);
        Assert.DoesNotContain(video.StoredName, text);
        var recipe = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("flowvideo.render-recipe", recipe.GetProperty("format").GetString());
        Assert.Equal(
            effects,
            recipe
                .GetProperty("request")
                .GetProperty("effects")
                .Deserialize<RenderEffects>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        var download = await c.GetAsync($"/api/jobs/{job}/audio");
        download.EnsureSuccessStatusCode();
        Assert.Equal("audio/mpeg", download.Content.Headers.ContentType!.MediaType);
        Assert.True((await download.Content.ReadAsByteArrayAsync()).Length > 1000);
        var jobs = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
        Assert.True(jobs[0].GetProperty("hasAudio").GetBoolean());
        Assert.False(jobs[0].TryGetProperty("requestJson", out _));
    }

    [Fact]
    public async Task SilentJobDoesNotAdvertiseAudioAndMusicOffsetBeyondSourceFailsCleanly()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var project = await Project(c);
        var video = await Upload(c, project, app.Video, "video.mp4");
        var job = await Render(c, project, new RenderRequest([video.Id], Duration: 1));
        var jobs = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
        Assert.False(jobs[0].GetProperty("hasAudio").GetBoolean());
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await c.GetAsync($"/api/jobs/{job}/audio")).StatusCode
        );
        var audio = await Upload(c, project, app.Audio, "music.mp3");
        var response = await c.PostAsJsonAsync(
            $"/api/projects/{project}/renders",
            new RenderRequest([video.Id], MusicId: audio.Id, Effects: new(MusicStart: 500))
        );
        response.EnsureSuccessStatusCode();
        var failedId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        for (int n = 0; n < 200; n++)
        {
            jobs = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
            if (
                jobs.EnumerateArray()
                    .Single(j => j.GetProperty("id").GetGuid() == failedId)
                    .GetProperty("status")
                    .GetString() == "Failed"
            )
            {
                Assert.False(File.Exists(Path.Combine(app.Data, "exports", failedId + ".mp4")));
                return;
            }
            await Task.Delay(100);
        }
        throw new Exception("Invalid offset did not fail.");
    }
}
