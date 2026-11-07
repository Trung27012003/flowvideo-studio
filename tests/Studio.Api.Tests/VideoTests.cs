using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Studio;
using Xunit;

public sealed class VideoTests
{
    static async Task<(Guid Project, Guid Scene)> Setup(HttpClient client, string ratio = "9:16")
    {
        var project = (
            await (
                await client.PostAsJsonAsync("/api/projects", new { name = "Video AI" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;
        var scene = Guid.NewGuid();
        (
            await client.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(
                    0,
                    new StoryDocument(
                        Ratio: ratio,
                        Scenes: [new(scene, "Cảnh", "Lời đọc", "A teal studio", null, 3)]
                    )
                )
            )
        ).EnsureSuccessStatusCode();
        return (project, scene);
    }

    static async Task<JsonElement> Wait(HttpClient client, Guid project, Guid id)
    {
        for (int i = 0; i < 200; i++)
        {
            var list = await client.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{project}/video-generations"
            );
            var row = list.GetProperty("items")
                .EnumerateArray()
                .First(x => x.GetProperty("generation").GetProperty("id").GetGuid() == id);
            if (
                row.GetProperty("generation").GetProperty("status").GetString()
                is "Succeeded"
                    or "Failed"
                    or "Unknown"
                    or "Canceled"
            )
                return row.Clone();
            await Task.Delay(100);
        }
        throw new Exception("Video worker timeout");
    }

    [Fact]
    public async Task GeneratesCachesValidatesContractAndRendersWithoutOverwritingDraft()
    {
        using var app = new AudioApp(false) { VideoKey = true, VideoLimit = 1 };
        var fake = new FakeVideo(app.Video);
        app.VideoHandler = fake;
        using var c = app.CreateClient();
        var (project, scene) = await Setup(c);
        var id = Guid.NewGuid();
        var body = new VideoRequest(id, scene, 1, 3, "360p");
        Assert.True(
            (await c.GetFromJsonAsync<JsonElement>("/api/capabilities"))
                .GetProperty("aiVideo")
                .GetBoolean()
        );
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await c.PostAsJsonAsync($"/api/projects/{project}/video-generations", body)).StatusCode
        );
        var result = await Wait(c, project, id);
        Assert.Equal(
            "Succeeded",
            result.GetProperty("generation").GetProperty("status").GetString()
        );
        Assert.Equal(1, fake.Calls);
        var payload = JsonDocument.Parse(fake.Body).RootElement;
        Assert.Equal("gemini-omni-1.2-flash", payload.GetProperty("model").GetString());
        Assert.Equal("A teal studio", payload.GetProperty("input").GetString());
        Assert.Equal(
            "3s",
            payload.GetProperty("response_format").GetProperty("duration").GetString()
        );
        Assert.Equal(
            "9:16",
            payload.GetProperty("response_format").GetProperty("aspect_ratio").GetString()
        );
        Assert.Equal(
            "360p",
            payload.GetProperty("response_format").GetProperty("resolution").GetString()
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await c.PostAsJsonAsync($"/api/projects/{project}/video-generations", body)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{project}/video-generations",
                    body with
                    {
                        RequestId = Guid.NewGuid(),
                    }
                )
            ).StatusCode
        );
        Assert.Equal(1, fake.Calls);
        var doc = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(doc.Scenes![0].AssetId);
        var asset = result
            .GetProperty("asset")
            .Deserialize<Asset>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("video", asset.Kind);
        Assert.Equal(
            await File.ReadAllBytesAsync(app.Video),
            await c.GetByteArrayAsync($"/api/assets/{asset.Id}/file")
        );
        doc = doc with { Scenes = [doc.Scenes[0] with { AssetId = asset.Id }] };
        (
            await c.PutAsJsonAsync($"/api/projects/{project}/document", new SaveDocument(1, doc))
        ).EnsureSuccessStatusCode();
        var render = await c.PostAsJsonAsync(
            $"/api/projects/{project}/storyboard/render",
            new { version = 2 }
        );
        render.EnsureSuccessStatusCode();
        for (int i = 0; i < 150; i++)
        {
            var jobs = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/jobs");
            var job = jobs[0];
            if (job.GetProperty("status").GetString() == "Succeeded")
                break;
            Assert.NotEqual("Failed", job.GetProperty("status").GetString());
            Assert.True(i < 149, "Render timeout");
            await Task.Delay(100);
        }
        var changed = doc with { Scenes = [doc.Scenes[0] with { Prompt = "Different" }] };
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(2, changed)
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{project}/video-generations",
                    new VideoRequest(Guid.NewGuid(), scene, 3)
                )
            ).StatusCode
        );
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp"), "video-*"));
    }

    [Fact]
    public async Task RejectsInvalidInputsMissingKeyStaleVersionAndForeignReference()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (project, scene) = await Setup(c);
        var url = $"/api/projects/{project}/video-generations";
        var request = new VideoRequest(Guid.NewGuid(), scene, 1);
        Assert.False(
            (await c.GetFromJsonAsync<JsonElement>("/api/capabilities"))
                .GetProperty("aiVideo")
                .GetBoolean()
        );
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await c.PostAsJsonAsync(url, request)).StatusCode
        );
        foreach (
            var bad in new[]
            {
                request with
                {
                    Seconds = 2,
                },
                request with
                {
                    Seconds = 16,
                },
                request with
                {
                    Resolution = "4k",
                },
                request with
                {
                    RequestId = Guid.Empty,
                },
                request with
                {
                    ReferenceId = Guid.NewGuid(),
                },
            }
        )
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, bad)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url, request with { Version = 2 })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.PostAsJsonAsync(url, request with { SceneId = Guid.NewGuid() })).StatusCode
        );
        var list = await c.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(0, list.GetProperty("used").GetInt32());
    }

    [Theory]
    [InlineData("malformed", "Failed")]
    [InlineData("invalid-video", "Failed")]
    [InlineData("network", "Unknown")]
    [InlineData("incomplete", "Unknown")]
    [InlineData("denied", "Failed")]
    [InlineData("corrupt-container", "Failed")]
    [InlineData("oversize", "Failed")]
    [InlineData("server-error", "Unknown")]
    [InlineData("io-error", "Unknown")]
    public async Task ProviderFailuresNeverReplayOrAttachAssets(string mode, string status)
    {
        using var app = new AudioApp(false) { VideoKey = true };
        var fake = new FakeVideo(app.Video) { Mode = mode };
        app.VideoHandler = fake;
        using var c = app.CreateClient();
        var (project, scene) = await Setup(c);
        var body = new VideoRequest(Guid.NewGuid(), scene, 1);
        (
            await c.PostAsJsonAsync($"/api/projects/{project}/video-generations", body)
        ).EnsureSuccessStatusCode();
        var result = await Wait(c, project, body.RequestId);
        Assert.Equal(status, result.GetProperty("generation").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("asset").ValueKind);
        Assert.Equal(
            HttpStatusCode.OK,
            (await c.PostAsJsonAsync($"/api/projects/{project}/video-generations", body)).StatusCode
        );
        Assert.Equal(1, fake.Calls);
        Assert.Empty(Directory.GetFiles(Path.Combine(app.Data, "assets")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp"), "video-*"));
    }

    [Fact]
    public async Task ImageReferenceIsScopedEncodedAndIdempotencyIncludesOptions()
    {
        using var app = new AudioApp(false) { VideoKey = true };
        var fake = new FakeVideo(app.Video);
        app.VideoHandler = fake;
        using var c = app.CreateClient();
        var (project, scene) = await Setup(c, "16:9");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZV8AAAAASUVORK5CYII="
        );
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(png), "file", "ref.png");
        var uploaded = (
            await (
                await c.PostAsync($"/api/projects/{project}/assets", form)
            ).Content.ReadFromJsonAsync<Asset>()
        )!;
        var body = new VideoRequest(Guid.NewGuid(), scene, 1, 4, "720p", uploaded.Id);
        (
            await c.PostAsJsonAsync($"/api/projects/{project}/video-generations", body)
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Succeeded",
            (await Wait(c, project, body.RequestId))
                .GetProperty("generation")
                .GetProperty("status")
                .GetString()
        );
        var input = JsonDocument.Parse(fake.Body).RootElement.GetProperty("input");
        Assert.Equal("image/png", input[0].GetProperty("mime_type").GetString());
        Assert.Equal(png, Convert.FromBase64String(input[0].GetProperty("data").GetString()!));
        Assert.Equal("A teal studio", input[1].GetProperty("text").GetString());
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{project}/video-generations",
                    body with
                    {
                        Seconds = 5,
                    }
                )
            ).StatusCode
        );
        var (other, otherScene) = await Setup(c);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{other}/video-generations",
                    new VideoRequest(Guid.NewGuid(), otherScene, 1, ReferenceId: uploaded.Id)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task StartupKeepsQueuedMarksRunningUnknownAndQueuedCanCancel()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (project, scene) = await Setup(c);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        await app
            .Services.GetServices<IHostedService>()
            .OfType<VideoWorker>()
            .Single()
            .StopAsync(CancellationToken.None);
        var running = new VideoGeneration
        {
            Id = Guid.NewGuid(),
            ProjectId = project,
            SceneId = scene,
            Status = "Running",
        };
        db.VideoGenerations.Add(running);
        await db.SaveChangesAsync();
        await VideoFeatures.Upgrade(db);
        Assert.Equal(
            "Unknown",
            (await db.VideoGenerations.AsNoTracking().SingleAsync(x => x.Id == running.Id)).Status
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{project}/video-generations/{running.Id}/cancel",
                    new { }
                )
            ).StatusCode
        );
        var queued = new VideoGeneration
        {
            Id = Guid.NewGuid(),
            ProjectId = project,
            SceneId = scene,
        };
        db.VideoGenerations.Add(queued);
        await db.SaveChangesAsync();
        await VideoFeatures.Upgrade(db);
        Assert.Equal(
            "Queued",
            (await db.VideoGenerations.AsNoTracking().SingleAsync(x => x.Id == queued.Id)).Status
        );
        var cancel = await c.PostAsJsonAsync(
            $"/api/projects/{project}/video-generations/{queued.Id}/cancel",
            new { }
        );
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(
            "Canceled",
            (await Wait(c, project, queued.Id))
                .GetProperty("generation")
                .GetProperty("status")
                .GetString()
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{Guid.NewGuid()}/video-generations/{running.Id}/cancel",
                    new { }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ActiveSceneIsDeduplicatedAndQueuedCancelNeverCallsProvider()
    {
        using var app = new AudioApp(false) { VideoKey = true, VideoLimit = 2 };
        var fake = new FakeVideo(app.Video) { Block = true };
        app.VideoHandler = fake;
        using var c = app.CreateClient();
        var (project, scene) = await Setup(c);
        var second = Guid.NewGuid();
        var document = new StoryDocument(
            Scenes:
            [
                new(scene, "First", "", "Prompt one", null, 3),
                new(second, "Second", "", "Prompt two", null, 3),
            ]
        );
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(1, document)
            )
        ).EnsureSuccessStatusCode();
        var first = new VideoRequest(Guid.NewGuid(), scene, 2);
        var url = $"/api/projects/{project}/video-generations";
        (await c.PostAsJsonAsync(url, first)).EnsureSuccessStatusCode();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync(url, first)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Conflict,
                (await c.PostAsJsonAsync(url, first with { RequestId = Guid.NewGuid() })).StatusCode
            );
            var queued = new VideoRequest(Guid.NewGuid(), second, 2);
            Assert.Equal(
                HttpStatusCode.Accepted,
                (await c.PostAsJsonAsync(url, queued)).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.OK,
                (
                    await c.PostAsJsonAsync(url + "/" + queued.RequestId + "/cancel", new { })
                ).StatusCode
            );
            Assert.Equal(
                "Canceled",
                (await Wait(c, project, queued.RequestId))
                    .GetProperty("generation")
                    .GetProperty("status")
                    .GetString()
            );
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await c.PostAsJsonAsync(url + "/" + first.RequestId + "/cancel", new { })
                ).StatusCode
            );
            var (other, otherScene) = await Setup(c);
            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{other}/video-generations",
                        new VideoRequest(Guid.NewGuid(), otherScene, 1)
                    )
                ).StatusCode
            );
            Assert.Equal(1, fake.Calls);
        }
        finally
        {
            fake.Release.TrySetResult();
        }
        Assert.Equal(
            "Succeeded",
            (await Wait(c, project, first.RequestId))
                .GetProperty("generation")
                .GetProperty("status")
                .GetString()
        );
        Assert.Equal(1, fake.Calls);
    }

    sealed class FakeVideo(string file) : HttpMessageHandler
    {
        public int Calls;
        public bool Block;
        public TaskCompletionSource Started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource Release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public string Body = "";
        public string Mode = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            if (Block)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            Assert.Equal(
                "https://generativelanguage.googleapis.com/v1beta/interactions",
                request.RequestUri!.ToString()
            );
            Assert.Equal("gemini-test-key", request.Headers.GetValues("x-goog-api-key").Single());
            Body = await request.Content!.ReadAsStringAsync(ct);
            if (Mode == "server-error")
                return new(HttpStatusCode.ServiceUnavailable);
            if (Mode == "io-error")
                throw new IOException("simulated interrupted response");
            if (Mode == "oversize")
            {
                var oversized = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([]),
                };
                oversized.Content.Headers.ContentLength = 150 * 1024 * 1024;
                return oversized;
            }
            if (Mode == "corrupt-container")
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
                        {
                            id = "corrupt",
                            status = "completed",
                            steps = new[]
                            {
                                new
                                {
                                    type = "model_output",
                                    content = new[]
                                    {
                                        new
                                        {
                                            type = "video",
                                            mime_type = "video/mp4",
                                            data = Convert.ToBase64String(
                                                new byte[]
                                                {
                                                    0,
                                                    0,
                                                    0,
                                                    12,
                                                    102,
                                                    116,
                                                    121,
                                                    112,
                                                    0,
                                                    0,
                                                    0,
                                                    0,
                                                }
                                            ),
                                        },
                                    },
                                },
                            },
                        }
                    ),
                };
            if (Mode == "network")
                throw new HttpRequestException("simulated");
            if (Mode == "denied")
                return new(HttpStatusCode.Forbidden);
            if (Mode == "malformed")
                return new(HttpStatusCode.OK) { Content = new StringContent("{broken") };
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        id = "test-interaction",
                        status = Mode == "incomplete" ? "in_progress" : "completed",
                        steps = new[]
                        {
                            new
                            {
                                type = "model_output",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "video",
                                        mime_type = "video/mp4",
                                        data = Convert.ToBase64String(
                                            Mode == "invalid-video"
                                                ? new byte[12]
                                                : await File.ReadAllBytesAsync(file, ct)
                                        ),
                                    },
                                },
                            },
                        },
                    }
                ),
            };
        }
    }
}
