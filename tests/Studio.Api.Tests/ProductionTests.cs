using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Studio;
using Xunit;

public class ProductionTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static async Task<(Guid Project, StoryDocument Document)> Setup(
        AudioApp app,
        HttpClient c,
        bool media = true,
        string script = ""
    )
    {
        var id = (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Production" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;
        Guid? asset = null;
        if (media)
        {
            using var form = new MultipartFormDataContent();
            form.Add(
                new ByteArrayContent(await File.ReadAllBytesAsync(app.Video)),
                "file",
                "source.mp4"
            );
            asset = (
                await (
                    await c.PostAsync($"/api/projects/{id}/assets", form)
                ).Content.ReadFromJsonAsync<Asset>()
            )!.Id;
        }
        var doc = new StoryDocument(
            Script: script,
            Scenes:
            [
                new(Guid.NewGuid(), "First", "Xin chào", "A teal room", asset, .4),
                new(Guid.NewGuid(), "Second", "Hẹn gặp lại", "A blue room", asset, .6),
            ],
            Resolution: 720
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{id}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        return (id, doc);
    }

    static async Task<JsonElement> Wait(HttpClient c, Guid p, Guid id, string? expected = null)
    {
        for (int i = 0; i < 300; i++)
        {
            var list = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/workflows");
            var run = list.GetProperty("items")
                .EnumerateArray()
                .Single(x => x.GetProperty("id").GetGuid() == id);
            var status = run.GetProperty("status").GetString();
            if (
                expected is null
                    ? status is "Succeeded" or "Failed" or "Unknown" or "Canceled"
                    : status == expected
            )
                return run.Clone();
            await Task.Delay(100);
        }
        throw new Exception("Production timeout");
    }

    static async Task<StoryDocument> Result(HttpClient c, Guid p, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/workflows/{id}/snapshot"))
            .GetProperty("result")
            .Deserialize<StoryDocument>(Json)!;

    [Fact]
    public async Task OfflineOneClickSnapshotsRendersAndDoesNotOverwriteCurrentDocument()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, doc) = await Setup(app, c);
        var request = new ProductionRequest(Guid.NewGuid(), 1);
        var url = $"/api/projects/{p}/workflows";
        var plan = (
            await (
                await c.PostAsJsonAsync(url + "/plan", request)
            ).Content.ReadFromJsonAsync<JsonElement>()
        );
        Assert.Equal(2, plan.GetProperty("existingMedia").GetInt32());
        Assert.Equal(0, plan.GetProperty("newSpeech").GetInt32());
        Assert.Equal(
            0,
            (await c.GetFromJsonAsync<JsonElement>(url)).GetProperty("items").GetArrayLength()
        );
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await c.PostAsJsonAsync(url + "/start", request)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await c.PostAsJsonAsync(url + "/start", request)).StatusCode
        );
        var changed = doc with { Scenes = [doc.Scenes![0] with { Narration = "Changed later" }] };
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(1, changed))
        ).EnsureSuccessStatusCode();
        var run = await Wait(c, p, request.RequestId);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        var result = await Result(c, p, request.RequestId);
        Assert.Equal(2, result.Scenes!.Length);
        Assert.Equal("Xin chào", result.Scenes[0].Narration);
        var current = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.Equal("Changed later", current.Scenes![0].Narration);
        Assert.Equal(
            1,
            (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/jobs")).GetArrayLength()
        );
        var rid = run.GetProperty("renderId").GetGuid();
        Assert.True((await c.GetAsync($"/api/jobs/{rid}/download")).IsSuccessStatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url + "/start", request with { Speech = true })).StatusCode
        );
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task SpeechPerSceneAlignsDurationAndAssemblesRealVoiceAndCaptions()
    {
        using var app = new AudioApp();
        using var c = app.CreateClient();
        var (p, doc) = await Setup(app, c);
        doc = doc with
        {
            Scenes =
            [
                doc.Scenes![0],
                new(Guid.NewGuid(), "Silence", "", "", doc.Scenes[0].AssetId, .5),
                doc.Scenes[1],
            ],
        };
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(1, doc))
        ).EnsureSuccessStatusCode();
        var req = new ProductionRequest(Guid.NewGuid(), 2, Speech: true, Padding: .2);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        var run = await Wait(c, p, req.RequestId);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(2, app.Calls);
        var result = await Result(c, p, req.RequestId);
        Assert.InRange(result.Scenes![0].Duration, 1.2, 1.4);
        Assert.Equal(.5, result.Scenes[1].Duration);
        Assert.Equal(result.Scenes[0].Duration, result.Scenes[2].Duration);
        Assert.NotNull(result.VoiceId);
        Assert.Equal(0, result.Audio!.VoiceStart);
        Assert.Equal(1, result.Effects!.VoiceSpeed);
        var rid = run.GetProperty("renderId").GetGuid();
        var srt = await c.GetStringAsync($"/api/jobs/{rid}/subtitles");
        var cues = Subtitles.Parse(srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(result.Scenes[0].Duration + .5, cues[1].Start, 2);
        var metadata = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{rid}/media-info");
        Assert.Equal("aac", metadata.GetProperty("audioCodec").GetString());
        Assert.InRange(metadata.GetProperty("duration").GetDouble(), 3, 3.5);
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp"), "production-*"));
        Assert.Equal(
            "alloy",
            JsonDocument.Parse(app.SpeechBody).RootElement.GetProperty("voice").GetString()
        );
    }

    [Fact]
    public async Task ImageGenerationPipelineUsesSharedCacheAndPreservesDraftMediaBindings()
    {
        using var app = new AudioApp { ImageLimit = 2 };
        var fake = new FakeImage(app.Data);
        app.ImageHandler = fake;
        using var c = app.CreateClient();
        var (p, doc) = await Setup(app, c, false);
        var req = new ProductionRequest(Guid.NewGuid(), 1, Media: "images");
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        var run = await Wait(c, p, req.RequestId);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(2, fake.Calls);
        var result = await Result(c, p, req.RequestId);
        Assert.All(result.Scenes!, s => Assert.NotNull(s.AssetId));
        var current = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(Json)!;
        Assert.All(current.Scenes!, s => Assert.Null(s.AssetId));
        var second = req with { RequestId = Guid.NewGuid() };
        var plan = await (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/plan", second)
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, plan.GetProperty("reusedMedia").GetInt32());
        Assert.Equal(0, plan.GetProperty("newImages").GetInt32());
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", second)
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Succeeded",
            (await Wait(c, p, second.RequestId)).GetProperty("status").GetString()
        );
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task ScriptToVideoGeneratesScenesMediaAndRendersFromImmutableScript()
    {
        using var app = new AudioApp(false) { VideoKey = true, VideoLimit = 2 };
        var fake = new VideoFixture(app.Video);
        app.VideoHandler = fake;
        using var c = app.CreateClient();
        var (p, _) = await Setup(app, c, false, "First shot\n\nSecond shot");
        var req = new ProductionRequest(
            Guid.NewGuid(),
            1,
            Media: "videos",
            FromScript: true,
            VideoSeconds: 3,
            VideoResolution: "360p"
        );
        var plan = await (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/plan", req)
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, plan.GetProperty("newVideos").GetInt32());
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        var run = await Wait(c, p, req.RequestId);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(2, fake.Calls);
        var result = await Result(c, p, req.RequestId);
        Assert.Equal("First shot", result.Scenes![0].Narration);
        Assert.Equal("Second shot", result.Scenes[1].Prompt);
        Assert.All(result.Scenes, s => Assert.NotNull(s.AssetId));
        Assert.Equal(10, result.Scenes.Sum(s => s.Duration));
        var repeat = req with { RequestId = Guid.NewGuid() };
        var cachedPlan = await (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/plan", repeat)
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, cachedPlan.GetProperty("newVideos").GetInt32());
        Assert.Equal(2, cachedPlan.GetProperty("reusedMedia").GetInt32());
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", repeat)
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Succeeded",
            (await Wait(c, p, repeat.RequestId)).GetProperty("status").GetString()
        );
        Assert.Equal(
            result.Scenes.Select(x => x.Id),
            (await Result(c, p, repeat.RequestId)).Scenes!.Select(x => x.Id)
        );
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task InvalidMissingKeyStaleVersionAndWholeBatchQuotaDoNotReservePartialWork()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, _) = await Setup(app, c, false);
        var url = $"/api/projects/{p}/workflows/start";
        var req = new ProductionRequest(Guid.NewGuid(), 1);
        foreach (
            var bad in new[]
            {
                req,
                req with
                {
                    Media = "bad",
                },
                req with
                {
                    Speed = 0,
                },
                req with
                {
                    VideoSeconds = 1,
                },
                req with
                {
                    Padding = 3,
                },
                req with
                {
                    FromScript = true,
                },
                req with
                {
                    RequestId = Guid.Empty,
                },
            }
        )
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, bad)).StatusCode);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await c.PostAsJsonAsync(url, req with { Media = "images" })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await c.PostAsJsonAsync(url, req with { Media = "videos" })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url, req with { Version = 99 })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await c.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/workflows/start", req)
            ).StatusCode
        );
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        Assert.Empty(await db.ProductionRuns.ToListAsync());
        Assert.Empty(await db.ImageGenerations.ToListAsync());
        using var limited = new AudioApp { ImageLimit = 1, WorkflowSpeechLimit = 1 };
        using var lc = limited.CreateClient();
        var (lp, _) = await Setup(limited, lc, false);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (
                await lc.PostAsJsonAsync(
                    $"/api/projects/{lp}/workflows/start",
                    new ProductionRequest(Guid.NewGuid(), 1, Media: "images")
                )
            ).StatusCode
        );
        var (ep, _) = await Setup(limited, lc, true);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (
                await lc.PostAsJsonAsync(
                    $"/api/projects/{ep}/workflows/start",
                    new ProductionRequest(Guid.NewGuid(), 1, Speech: true)
                )
            ).StatusCode
        );
        using var ls = limited.Services.CreateScope();
        var ld = ls.ServiceProvider.GetRequiredService<StudioDb>();
        Assert.Empty(await ld.ProductionRuns.ToListAsync());
        Assert.Empty(await ld.ImageGenerations.ToListAsync());
        Assert.Equal(0, limited.Calls);
    }

    [Fact]
    public async Task SpeechUnknownDoesNotReplayAndStartupNeverResendsInterruptedSpeech()
    {
        using var app = new AudioApp { Mode = "speech-status" };
        using var c = app.CreateClient();
        var (p, doc) = await Setup(app, c);
        var req = new ProductionRequest(Guid.NewGuid(), 1, Speech: true);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Unknown",
            (await Wait(c, p, req.RequestId)).GetProperty("status").GetString()
        );
        Assert.Equal(1, app.Calls);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        Assert.Equal(1, app.Calls);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        var interrupted = new ProductionRun
        {
            Id = Guid.NewGuid(),
            ProjectId = p,
            DocumentVersion = 1,
            Status = "Speaking",
            RequestJson = JsonSerializer.Serialize(req),
            SnapshotJson = JsonSerializer.Serialize(doc),
            StateJson = JsonSerializer.Serialize(
                new ProductionState([new(doc.Scenes![0].Id, "existing", null, false, "Running")])
            ),
        };
        db.ProductionRuns.Add(interrupted);
        await db.SaveChangesAsync();
        await ProductionWorkflow.Upgrade(db);
        Assert.Equal(
            "Unknown",
            (await db.ProductionRuns.AsNoTracking().SingleAsync(x => x.Id == interrupted.Id)).Status
        );
        Assert.Equal(1, app.Calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp"), "production-*"));
    }

    [Fact]
    public async Task QueuedCancelStopsChildrenAndCapacityAndScopeAreEnforced()
    {
        using var app = new AudioApp
        {
            ImageLimit = 10,
            DisabledWorkers = [typeof(ProductionWorker), typeof(ImageWorker)],
        };
        using var c = app.CreateClient();
        var (p, _) = await Setup(app, c, false);
        var req = new ProductionRequest(Guid.NewGuid(), 1, Media: "images");
        var url = $"/api/projects/{p}/workflows";
        (await c.PostAsJsonAsync(url + "/start", req)).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{Guid.NewGuid()}/workflows/{req.RequestId}/cancel",
                    new { }
                )
            ).StatusCode
        );
        (
            await c.PostAsJsonAsync(url + "/" + req.RequestId + "/cancel", new { })
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Canceled",
            (await Wait(c, p, req.RequestId)).GetProperty("status").GetString()
        );
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        Assert.All(
            await db.ImageGenerations.ToListAsync(),
            x => Assert.Equal("Canceled", x.Status)
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(url + "/" + req.RequestId + "/retry-render", new { })
            ).StatusCode
        );
        var (ep, _) = await Setup(app, c, true);
        for (int i = 0; i < 3; i++)
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{ep}/workflows/start",
                    new ProductionRequest(Guid.NewGuid(), 1)
                )
            ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{ep}/workflows/start",
                    new ProductionRequest(Guid.NewGuid(), 1)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task RenderFailureCanRetryWithSameSnapshotAndNoAdditionalAi()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, doc) = await Setup(app, c);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        var asset = await db.Assets.SingleAsync(a => a.Id == doc.Scenes![0].AssetId);
        var store = scope.ServiceProvider.GetRequiredService<Storage>();
        var bytes = await File.ReadAllBytesAsync(store.AssetPath(asset));
        await File.WriteAllTextAsync(store.AssetPath(asset), "broken");
        var req = new ProductionRequest(Guid.NewGuid(), 1);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        var failed = await Wait(c, p, req.RequestId);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, failed.GetProperty("renderId").ValueKind);
        await File.WriteAllBytesAsync(store.AssetPath(asset), bytes);
        (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/workflows/{req.RequestId}/retry-render",
                new { }
            )
        ).EnsureSuccessStatusCode();
        var done = await Wait(c, p, req.RequestId);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.NotEqual(
            failed.GetProperty("renderId").GetGuid(),
            done.GetProperty("renderId").GetGuid()
        );
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task FailedMediaCancelsUnsentSiblingsBeforeAnySpeechOrRender()
    {
        using var app = new AudioApp { ImageLimit = 2, DisabledWorkers = [typeof(ImageWorker)] };
        using var c = app.CreateClient();
        var (p, _) = await Setup(app, c, false);
        var request = new ProductionRequest(Guid.NewGuid(), 1, Media: "images", Speech: true);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", request)
        ).EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        var first = (
            await db.ImageGenerations.AsNoTracking().OrderBy(x => x.CreatedAt).ToArrayAsync()
        )[0];
        await db
            .ImageGenerations.Where(x => x.Id == first.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Failed"));
        Assert.Equal(
            "Failed",
            (await Wait(c, p, request.RequestId)).GetProperty("status").GetString()
        );
        Assert.Equal(1, await db.ImageGenerations.CountAsync(x => x.Status == "Canceled"));
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task CompletedSpeechStepsResumeLocallyAndFixedTimingPadsOrTrimsEachScene()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, doc) = await Setup(app, c);
        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(app.Audio)),
            "file",
            "completed-speech.mp3"
        );
        var speech = (
            await (
                await c.PostAsync($"/api/projects/{p}/assets", form)
            ).Content.ReadFromJsonAsync<Asset>()
        )!;
        var gate = app.Services.GetRequiredService<ProductionGate>();
        await gate.Gate.WaitAsync();
        var id = Guid.NewGuid();
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            var req = new ProductionRequest(id, 1, Speech: true, AutoTiming: false);
            var state = new ProductionState(
                doc.Scenes!.Select(s => new ProductionStep(
                        s.Id,
                        "existing",
                        null,
                        false,
                        "Succeeded",
                        speech.Id,
                        1.04
                    ))
                    .ToArray()
            );
            db.ProductionRuns.Add(
                new()
                {
                    Id = id,
                    ProjectId = p,
                    DocumentVersion = 1,
                    Status = "Preparing",
                    RequestJson = JsonSerializer.Serialize(req),
                    SnapshotJson = JsonSerializer.Serialize(doc),
                    StateJson = JsonSerializer.Serialize(state),
                    SpeechReservations = 2,
                }
            );
            await db.SaveChangesAsync();
            await ProductionWorkflow.Upgrade(db);
        }
        finally
        {
            gate.Gate.Release();
        }
        var run = await Wait(c, p, id);
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(0, app.Calls);
        var result = await Result(c, p, id);
        Assert.Equal(.4, result.Scenes![0].Duration);
        Assert.Equal(.6, result.Scenes[1].Duration);
        var info = await c.GetFromJsonAsync<JsonElement>(
            $"/api/jobs/{run.GetProperty("renderId").GetGuid()}/media-info"
        );
        Assert.InRange(info.GetProperty("duration").GetDouble(), .98, 1.1);
    }

    [Fact]
    public async Task CancelDuringPaidSpeechNeverRevivesWorkflowAndRetainsCompletedAudio()
    {
        using var app = new AudioApp();
        var fake = new BlockingSpeech(app.Audio);
        app.AudioHandler = fake;
        using var c = app.CreateClient();
        var (p, _) = await Setup(app, c);
        var req = new ProductionRequest(Guid.NewGuid(), 1, Speech: true);
        (
            await c.PostAsJsonAsync($"/api/projects/{p}/workflows/start", req)
        ).EnsureSuccessStatusCode();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/workflows/{req.RequestId}/cancel",
                    new { }
                )
            ).EnsureSuccessStatusCode();
        }
        finally
        {
            fake.Release.TrySetResult();
        }
        for (int i = 0; i < 100; i++)
        {
            var assets = (await c.GetFromJsonAsync<Asset[]>($"/api/projects/{p}/assets"))!;
            if (assets.Any(a => a.Kind == "audio"))
                break;
            Assert.True(i < 99);
            await Task.Delay(100);
        }
        Assert.Equal(
            "Canceled",
            (await Wait(c, p, req.RequestId)).GetProperty("status").GetString()
        );
        Assert.Equal(1, fake.Calls);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!);
    }

    sealed class BlockingSpeech(string file) : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource Release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(await File.ReadAllBytesAsync(file, ct)),
            };
        }
    }

    sealed class VideoFixture(string file) : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        id = "fixture",
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
                                            await File.ReadAllBytesAsync(file, ct)
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
