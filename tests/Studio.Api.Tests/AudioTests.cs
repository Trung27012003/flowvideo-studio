using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class AudioTests
{
    [Fact]
    public async Task SpeechTranscriptionAndEditedSubtitleRenderTogether()
    {
        using var app = new AudioApp();
        using var client = app.CreateClient();
        var project = await CreateProject(client);
        var speech = await client.PostAsJsonAsync(
            $"/api/projects/{project}/speech",
            new
            {
                text = "Xin chào",
                voice = "nova",
                speed = 1.25,
                name = "Lời đọc",
            }
        );
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        var audio = (await speech.Content.ReadFromJsonAsync<Asset>())!;
        Assert.Equal("audio", audio.Kind);
        Assert.Equal("Lời đọc.mp3", audio.Name);
        var file = await client.GetAsync($"/api/assets/{audio.Id}/file");
        Assert.Equal("audio/mpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(await file.Content.ReadAsByteArrayAsync());
        var transcript = await client.PostAsJsonAsync(
            $"/api/projects/{project}/transcriptions",
            new { assetId = audio.Id, language = "vi" }
        );
        Assert.Equal(HttpStatusCode.OK, transcript.StatusCode);
        var payload = (await transcript.Content.ReadFromJsonAsync<JsonElement>());
        var sub = payload
            .GetProperty("asset")
            .Deserialize<Asset>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var original = await client.GetStringAsync($"/api/assets/{sub.Id}/file");
        Assert.Contains("Xin chào", original);
        Assert.Contains("00:00:00,500", original);
        var edit = await client.PostAsJsonAsync(
            $"/api/projects/{project}/subtitles",
            new { name = "Đã sửa", content = original.Replace("Xin chào", "Chào bạn") }
        );
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await edit.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("asset")
            .GetProperty("id")
            .GetGuid();
        Assert.NotEqual(sub.Id, edited);
        Assert.Equal(original, await client.GetStringAsync($"/api/assets/{sub.Id}/file"));
        var video = await Upload(client, project, app.Video, "clip.mp4");
        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project}/renders",
            new
            {
                assetIds = new[] { video.Id },
                duration = 1,
                voiceId = audio.Id,
                subtitleId = edited,
            }
        );
        var job = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        string? status = null;
        for (var i = 0; i < 200; i++)
        {
            var jobs = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
            status = jobs.EnumerateArray()
                .First(x => x.GetProperty("id").GetGuid() == job)
                .GetProperty("status")
                .GetString();
            if (status == "Succeeded" || status == "Failed")
                break;
            await Task.Delay(100);
        }
        Assert.Equal("Succeeded", status);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/jobs/{job}/download")).StatusCode
        );
        Assert.Equal(2, app.Calls);
        Assert.Contains("\"voice\":\"nova\"", app.SpeechBody);
        Assert.Contains("\"speed\":1.25", app.SpeechBody);
        Assert.Contains("whisper-1", app.TranscriptionBody);
        Assert.Contains("verbose_json", app.TranscriptionBody);
        Assert.Contains("vi", app.TranscriptionBody);
    }

    [Fact]
    public async Task MissingKeyAndValidationDoNotCallProvider()
    {
        using var app = new AudioApp(false);
        using var client = app.CreateClient();
        var project = await CreateProject(client);
        var invalid = await client.PostAsJsonAsync(
            $"/api/projects/{project}/speech",
            new { text = "", voice = "nova" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var missing = await client.PostAsJsonAsync(
            $"/api/projects/{project}/speech",
            new { text = "Xin chào" }
        );
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        var invalidVoice = await client.PostAsJsonAsync(
            $"/api/projects/{project}/speech",
            new { text = "Xin chào", voice = "unknown" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalidVoice.StatusCode);
        var invalidSpeed = await client.PostAsJsonAsync(
            $"/api/projects/{project}/speech",
            new { text = "Xin chào", speed = 0 }
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalidSpeed.StatusCode);
        var audio = await Upload(client, project, app.Audio, "source.mp3");
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project}/transcriptions",
                    new { assetId = audio.Id }
                )
            ).StatusCode
        );
        var other = await CreateProject(client);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{other}/transcriptions",
                    new { assetId = audio.Id }
                )
            ).StatusCode
        );
        Assert.Equal(0, app.Calls);
    }

    [Theory]
    [InlineData("speech-status")]
    [InlineData("speech-invalid-audio")]
    [InlineData("transcript-invalid-json")]
    [InlineData("transcript-no-segments")]
    [InlineData("transcript-invalid-timing")]
    [InlineData("transcript-null-segment")]
    [InlineData("transcript-root-array")]
    public async Task ProviderFailuresDoNotLeaveNewAssetsOrTemporaryFiles(string mode)
    {
        using var app = new AudioApp { Mode = mode };
        using var client = app.CreateClient();
        var project = await CreateProject(client);
        HttpResponseMessage response;
        int expected;
        if (mode.StartsWith("speech"))
        {
            response = await client.PostAsJsonAsync(
                $"/api/projects/{project}/speech",
                new { text = "Xin chào" }
            );
            expected = 0;
        }
        else
        {
            var audio = await Upload(client, project, app.Audio, "source.mp3");
            response = await client.PostAsJsonAsync(
                $"/api/projects/{project}/transcriptions",
                new { assetId = audio.Id }
            );
            expected = 1;
        }
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var assets = await client.GetFromJsonAsync<Asset[]>($"/api/projects/{project}/assets");
        Assert.Equal(expected, assets!.Length);
        Assert.Equal(expected, Directory.GetFiles(Path.Combine(app.Data, "assets")).Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp")));
        Assert.Equal(1, app.Calls);
    }

    [Fact]
    public async Task VideoWithoutAudioIsRejectedBeforePaidRequest()
    {
        using var app = new AudioApp();
        using var client = app.CreateClient();
        var project = await CreateProject(client);
        var video = await Upload(client, project, app.Video, "silent.mp4");
        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project}/transcriptions",
            new { assetId = video.Id }
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task SourceOverTenMinutesIsRejectedBeforePaidRequest()
    {
        using var app = new AudioApp();
        using var client = app.CreateClient();
        var project = await CreateProject(client);
        var path = Path.Combine(app.Data, "long.wav");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (
            var arg in new[]
            {
                "-y",
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "lavfi",
                "-i",
                "anullsrc=r=8000:cl=mono",
                "-t",
                "601",
                path,
            }
        )
            info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        var source = await Upload(client, project, path, "long.wav");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project}/transcriptions",
                    new { assetId = source.Id }
                )
            ).StatusCode
        );
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task ManualSrtWorksWithoutKeyAndNormalizesUtf8NewlinesAndNumbering()
    {
        using var app = new AudioApp(false);
        using var client = app.CreateClient();
        var project = await CreateProject(client);
        foreach (
            var content in new string?[]
            {
                null,
                "broken",
                "1\n00:00:03,000 --> 00:00:01,000\nSai",
                "1\n00:61:00,000 --> 00:62:00,000\nSai",
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.PostAsJsonAsync(
                        $"/api/projects/{project}/subtitles",
                        new { name = "Test", content }
                    )
                ).StatusCode
            );
        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project}/subtitles",
            new
            {
                name = "Tiếng Việt",
                content = "\uFEFF7\r\n00:00:00,000 --> 00:00:01,000\r\nXin chào!\r\n",
            }
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("asset")
            .GetProperty("id")
            .GetGuid();
        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:01,000\nXin chào!\n",
            await client.GetStringAsync($"/api/assets/{id}/file")
        );
        Assert.Equal(0, app.Calls);
    }

    private static async Task<Guid> CreateProject(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Audio test" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    private static async Task<Asset> Upload(HttpClient c, Guid project, string path, string name)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", name);
        var response = await c.PostAsync($"/api/projects/{project}/assets", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Asset>())!;
    }
}

public sealed class AudioApp : WebApplicationFactory<Program>
{
    public string Data { get; } =
        Path.Combine(Path.GetTempPath(), "flowvideo-audio-test-" + Guid.NewGuid().ToString("N"));
    public string Audio => Path.Combine(Data, "fixture.mp3");
    public string Video => Path.Combine(Data, "fixture.mp4");
    public Type[] DisabledWorkers = [];
    public HttpMessageHandler? ImageHandler;
    public HttpMessageHandler? AudioHandler;
    public int ImageLimit = 10;
    public HttpMessageHandler? VideoHandler;
    public bool VideoKey;
    public int VideoLimit = 5;
    public int WorkflowSpeechLimit = 20;
    public int Calls;
    public string SpeechBody = "";
    public string TranscriptionBody = "";
    public string Mode = "";
    private readonly bool key;

    public AudioApp(bool withKey = true)
    {
        key = withKey;
        Directory.CreateDirectory(Data);
        Generate([
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=1",
            "-c:a",
            "libmp3lame",
            Audio,
        ]);
        Generate([
            "-f",
            "lavfi",
            "-i",
            "color=c=teal:s=320x240:d=1",
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            Video,
        ]);
    }

    private static void Generate(IEnumerable<string> args)
    {
        var p = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error" }.Concat(args))
            p.ArgumentList.Add(a);
        using var process = Process.Start(p)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception(errors);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(
            (_, c) =>
                c.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        { "STUDIO_DATA", Data },
                        { "OPENAI_API_KEY", key ? "test-key-not-real" : "" },
                        { "IMAGE_DAILY_LIMIT", ImageLimit.ToString() },
                        { "GEMINI_API_KEY", VideoKey ? "gemini-test-key" : "" },
                        { "VIDEO_DAILY_LIMIT", VideoLimit.ToString() },
                        { "WORKFLOW_TTS_DAILY_LIMIT", WorkflowSpeechLimit.ToString() },
                    }
                )
        );
        builder.ConfigureServices(s =>
        {
            foreach (
                var descriptor in s.Where(d =>
                        d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationType is Type type
                        && DisabledWorkers.Contains(type)
                    )
                    .ToArray()
            )
                s.Remove(descriptor);
            s.AddHttpClient("OpenAiAudio")
                .ConfigurePrimaryHttpMessageHandler(() => AudioHandler ?? new FakeAudio(this));
            s.AddHttpClient("GeminiVideo")
                .ConfigurePrimaryHttpMessageHandler(() => VideoHandler ?? new RejectImage());
            s.AddHttpClient("OpenAiImage")
                .ConfigurePrimaryHttpMessageHandler(() => ImageHandler ?? new RejectImage());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(Data))
            Directory.Delete(Data, true);
    }

    private class RejectImage : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => throw new InvalidOperationException("Unexpected AI image request in test.");
    }

    private class FakeAudio(AudioApp app) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref app.Calls);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key-not-real", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith("/speech"))
            {
                app.SpeechBody = await request.Content!.ReadAsStringAsync(ct);
                if (app.Mode == "speech-status")
                    return new(HttpStatusCode.TooManyRequests);
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        app.Mode == "speech-invalid-audio"
                            ? Encoding.UTF8.GetBytes("invalid")
                            : await File.ReadAllBytesAsync(app.Audio, ct)
                    ),
                };
            }
            app.TranscriptionBody = await request.Content!.ReadAsStringAsync(ct);
            var json = app.Mode switch
            {
                "transcript-invalid-json" => "invalid",
                "transcript-no-segments" => "{}",
                "transcript-null-segment" => "{\"segments\":[null]}",
                "transcript-root-array" => "[]",
                "transcript-invalid-timing" =>
                    "{\"segments\":[{\"start\":\"bad\",\"end\":1,\"text\":\"x\"}]}",
                _ =>
                    "{\"segments\":[{\"start\":0,\"end\":0.5,\"text\":\"Xin chào\"},{\"start\":0.5,\"end\":1,\"text\":\"FlowVideo\"}]}",
            };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
