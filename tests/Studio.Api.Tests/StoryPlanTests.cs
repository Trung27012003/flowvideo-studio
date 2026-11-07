using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public class StoryPlanTests
{
    public static StoryProposal Proposal() =>
        new(
            "Video title",
            "Developers",
            "A useful message",
            "Opening hook",
            "Ending CTA",
            [
                new(
                    "First",
                    "Xin chào",
                    "A teal room",
                    1,
                    new("wide", "pan", "Soft light", "Studio", "A developer", "Establish the scene")
                ),
                new(
                    "Second",
                    "Hẹn gặp lại",
                    "A blue room",
                    1,
                    new("close", "static", "Warm light", "Studio", "A developer", "Ending")
                ),
            ]
        );

    static async Task<(Guid Id, StoryDocument Doc)> Setup(HttpClient c)
    {
        var p = (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Story architect" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;
        var doc = new StoryDocument(
            Idea: "A helpful C# video",
            Script: "Existing script",
            Scenes: [new(Guid.NewGuid(), "Original scene", "Old narration", "old prompt", null)]
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        return (p, doc);
    }

    static async Task<JsonElement> Wait(HttpClient c, Guid p, Guid plan)
    {
        for (int i = 0; i < 150; i++)
        {
            var data = await c.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{p}/story-plans/{plan}"
            );
            var row = data.GetProperty("plan");
            if (
                row.GetProperty("status").GetString()
                is "Succeeded"
                    or "Failed"
                    or "Unknown"
                    or "Canceled"
            )
                return row.Clone();
            await Task.Delay(100);
        }
        throw new Exception("Story planning timeout");
    }

    [Fact]
    public async Task StructuredContractCacheQuotaAndImmutableDocumentAreEnforced()
    {
        using var app = new AudioApp { StoryLimit = 1 };
        var fake = new FakeStory { Block = true };
        app.StoryHandler = fake;
        using var c = app.CreateClient();
        var (p, doc) = await Setup(c);
        var url = $"/api/projects/{p}/story-plans";
        var req = new StoryPlanRequest(Guid.NewGuid(), 1, SceneCount: 2, Duration: 2);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync(url, req)).StatusCode);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync(url, req)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Conflict,
                (await c.PostAsJsonAsync(url, req with { RequestId = Guid.NewGuid() })).StatusCode
            );
        }
        finally
        {
            fake.Release.TrySetResult();
        }
        var row = await Wait(c, p, req.RequestId);
        Assert.Equal("Succeeded", row.GetProperty("status").GetString());
        Assert.Equal(40, row.GetProperty("inputTokens").GetInt32());
        Assert.Equal(60, row.GetProperty("outputTokens").GetInt32());
        var payload = JsonDocument.Parse(fake.Body).RootElement;
        Assert.Equal(
            "json_schema",
            payload.GetProperty("response_format").GetProperty("type").GetString()
        );
        Assert.True(
            payload
                .GetProperty("response_format")
                .GetProperty("json_schema")
                .GetProperty("strict")
                .GetBoolean()
        );
        Assert.Equal(7000, payload.GetProperty("max_completion_tokens").GetInt32());
        var schema = payload
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(
            "scenes",
            schema.GetProperty("required").EnumerateArray().Select(x => x.GetString())
        );
        var current = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document"))
            .GetProperty("document")
            .Deserialize<StoryDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(doc.Scenes![0].Id, current.Scenes![0].Id);
        Assert.Equal("Existing script", current.Script);
        var reused = await c.PostAsJsonAsync(url, req with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.True(
            (await reused.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("reused")
                .GetBoolean()
        );
        Assert.Equal(1, fake.Calls);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (
                await c.PostAsJsonAsync(url, req with { RequestId = Guid.NewGuid(), Variant = 1 })
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url, req with { Style = "changed" })).StatusCode
        );
    }

    [Fact]
    public async Task ReviewRevisionApplyAndDirectionPersistWithoutAutomaticMediaGeneration()
    {
        using var app = new AudioApp();
        app.StoryHandler = new FakeStory();
        using var c = app.CreateClient();
        var (p, doc) = await Setup(c);
        var req = new StoryPlanRequest(Guid.NewGuid(), 1, SceneCount: 2, Duration: 2);
        var url = $"/api/projects/{p}/story-plans/{req.RequestId}";
        (await c.PostAsJsonAsync($"/api/projects/{p}/story-plans", req)).EnsureSuccessStatusCode();
        await Wait(c, p, req.RequestId);
        var proposal = Proposal() with
        {
            Title = "Reviewed title",
            Scenes =
            [
                Proposal().Scenes[0] with
                {
                    Narration = "Đã sửa lời",
                    Duration = 1.2,
                },
                Proposal().Scenes[1],
            ],
        };
        var edited = await c.PutAsJsonAsync(url, new EditStoryPlan(1, proposal));
        edited.EnsureSuccessStatusCode();
        Assert.Equal(
            2,
            (await edited.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("plan")
                .GetProperty("revision")
                .GetInt32()
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PutAsJsonAsync(url, new EditStoryPlan(1, proposal))).StatusCode
        );
        var exported = await c.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(
            "Video title",
            exported.GetProperty("original").GetProperty("title").GetString()
        );
        Assert.Equal(
            "Reviewed title",
            exported.GetProperty("plan").GetProperty("proposal").GetProperty("title").GetString()
        );
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/document",
                new SaveDocument(1, doc with { Idea = "Edited while AI ran" })
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url + "/apply", new ApplyStoryPlan(2, 1))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url + "/apply", new ApplyStoryPlan(1, 2))).StatusCode
        );
        var applied = await c.PostAsJsonAsync(url + "/apply", new ApplyStoryPlan(2, 2));
        applied.EnsureSuccessStatusCode();
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, result.GetProperty("version").GetInt32());
        var document = result
            .GetProperty("document")
            .Deserialize<StoryDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Edited while AI ran", document.Idea);
        Assert.Equal("Đã sửa lời", document.Scenes![0].Narration);
        Assert.Equal("wide", document.Scenes[0].Direction!.Shot);
        Assert.Equal("pan", document.Scenes[0].Direction!.Camera);
        Assert.All(document.Scenes, s => Assert.Null(s.AssetId));
        Assert.DoesNotContain(document.Scenes, s => s.Id == doc.Scenes![0].Id);
        Assert.Contains("Shot: wide", SceneDirection.VisualPrompt(document.Scenes[0]));
        var export = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/storyboard/export");
        Assert.Contains("Establish the scene", export.ToString());
        Assert.Empty((await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!);
        var (other, _) = await Setup(c);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/projects/{other}/story-plans/{req.RequestId}")).StatusCode
        );
    }

    [Theory]
    [InlineData("malformed", "Failed")]
    [InlineData("missing", "Failed")]
    [InlineData("extra", "Failed")]
    [InlineData("refusal", "Failed")]
    [InlineData("length", "Failed")]
    [InlineData("timing", "Failed")]
    [InlineData("network", "Unknown")]
    [InlineData("server", "Unknown")]
    [InlineData("denied", "Failed")]
    public async Task InvalidOrAmbiguousProviderResponsesNeverOverwriteOrReplay(
        string mode,
        string status
    )
    {
        using var app = new AudioApp();
        var fake = new FakeStory { Mode = mode };
        app.StoryHandler = fake;
        using var c = app.CreateClient();
        var (p, doc) = await Setup(c);
        var req = new StoryPlanRequest(Guid.NewGuid(), 1, SceneCount: 2, Duration: 2);
        var url = $"/api/projects/{p}/story-plans";
        (await c.PostAsJsonAsync(url, req)).EnsureSuccessStatusCode();
        var row = await Wait(c, p, req.RequestId);
        Assert.Equal(status, row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("proposal").ValueKind);
        (await c.PostAsJsonAsync(url, req)).EnsureSuccessStatusCode();
        Assert.Equal(1, fake.Calls);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.PostAsJsonAsync(
                    url + "/" + req.RequestId + "/apply",
                    new ApplyStoryPlan(0, 1)
                )
            ).StatusCode
        );
        var current = (
            await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document")
        ).GetProperty("document");
        Assert.Equal(
            "Original scene",
            current.GetProperty("scenes")[0].GetProperty("title").GetString()
        );
    }

    [Fact]
    public async Task MissingKeyInvalidOptionsAndBadReviewDoNotCreateUnwantedCalls()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, _) = await Setup(c);
        var req = new StoryPlanRequest(Guid.NewGuid(), 1);
        var url = $"/api/projects/{p}/story-plans";
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await c.PostAsJsonAsync(url, req)).StatusCode
        );
        foreach (
            var bad in new[]
            {
                req with
                {
                    SceneCount = 11,
                },
                req with
                {
                    Source = "url",
                },
                req with
                {
                    Duration = 0,
                },
                req with
                {
                    Genre = "bad",
                },
                req with
                {
                    Structure = "bad",
                },
                req with
                {
                    Language = "bad",
                },
                req with
                {
                    Style = new string('x', 301),
                },
                req with
                {
                    RequestId = Guid.Empty,
                },
            }
        )
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, bad)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url, req with { Version = 10 })).StatusCode
        );
        var badProposal = Proposal() with
        {
            Scenes = [Proposal().Scenes[0] with { Direction = new(Shot: "bad") }],
        };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PutAsJsonAsync(
                    url + "/" + Guid.NewGuid(),
                    new EditStoryPlan(1, badProposal)
                )
            ).StatusCode
        );
        using var scope = app.Services.CreateScope();
        Assert.Empty(
            await scope.ServiceProvider.GetRequiredService<StudioDb>().StoryPlans.ToListAsync()
        );
    }

    [Fact]
    public async Task ImportedProposalIsEditableOfflineAndDoesNotPretendToConsumeAiQuota()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, _) = await Setup(c);
        var url = $"/api/projects/{p}/story-plans";
        var result = await c.PostAsJsonAsync(
            url + "/import",
            new ImportStoryPlan("flowvideo.story-plan", 1, Proposal())
        );
        result.EnsureSuccessStatusCode();
        var row = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan");
        Assert.Equal("import", row.GetProperty("origin").GetString());
        Assert.Equal("manual-import", row.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("inputTokens").ValueKind);
        Assert.Equal(
            0,
            (await c.GetFromJsonAsync<JsonElement>(url)).GetProperty("used").GetInt32()
        );
        var id = row.GetProperty("id").GetGuid();
        (
            await c.PostAsJsonAsync(url + "/" + id + "/apply", new ApplyStoryPlan(1, 1))
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PostAsJsonAsync(
                    url + "/import",
                    new ImportStoryPlan("wrong", 1, Proposal())
                )
            ).StatusCode
        );
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task QueuedCancelAndInterruptedRestartAreDurable()
    {
        using var app = new AudioApp { DisabledWorkers = [typeof(StoryPlanWorker)] };
        using var c = app.CreateClient();
        var (p, _) = await Setup(c);
        var req = new StoryPlanRequest(Guid.NewGuid(), 1, SceneCount: 2, Duration: 2);
        var url = $"/api/projects/{p}/story-plans";
        (await c.PostAsJsonAsync(url, req)).EnsureSuccessStatusCode();
        (
            await c.PostAsJsonAsync(url + "/" + req.RequestId + "/cancel", new { })
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            "Canceled",
            (await Wait(c, p, req.RequestId)).GetProperty("status").GetString()
        );
        var second = req with { RequestId = Guid.NewGuid() };
        (await c.PostAsJsonAsync(url, second)).EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        await db
            .StoryPlans.Where(x => x.Id == second.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Running"));
        await StoryPlans.Upgrade(db);
        Assert.Equal(
            "Unknown",
            (await Wait(c, p, second.RequestId)).GetProperty("status").GetString()
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync(url + "/" + second.RequestId + "/cancel", new { })).StatusCode
        );
    }
}

public class FakeStory : HttpMessageHandler
{
    public string Mode = "";
    public int Calls;
    public string Body = "";
    public bool Block;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        Interlocked.Increment(ref Calls);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("test-key-not-real", request.Headers.Authorization?.Parameter);
        Body = await request.Content!.ReadAsStringAsync(ct);
        if (Block)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
        if (Mode == "network")
            throw new HttpRequestException();
        if (Mode == "server")
            return new(HttpStatusCode.ServiceUnavailable);
        if (Mode == "denied")
            return new(HttpStatusCode.Forbidden);
        if (Mode == "malformed")
            return new(HttpStatusCode.OK) { Content = new StringContent("bad json") };
        var proposal = StoryPlanTests.Proposal();
        if (Mode == "timing")
            proposal = proposal with
            {
                Scenes = [proposal.Scenes[0] with { Duration = 50 }, proposal.Scenes[1]],
            };
        var content = JsonSerializer.Serialize(
            proposal,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        if (Mode == "missing")
            content = content.Replace("\"camera\":\"pan\",", "");
        if (Mode == "extra")
            content = content.Replace("\"audience\":", "\"surprise\":true,\"audience\":");
        return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new
                {
                    choices = new[]
                    {
                        new
                        {
                            finish_reason = Mode == "length" ? "length" : "stop",
                            message = new
                            {
                                content,
                                refusal = Mode == "refusal" ? "Not available" : null,
                            },
                        },
                    },
                    usage = new { prompt_tokens = 40, completion_tokens = 60 },
                }
            ),
        };
    }
}
