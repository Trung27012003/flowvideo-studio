using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class ImageTests
{
    private static async Task<(Guid Project, Guid Scene)> Setup(
        HttpClient c,
        string prompt = "Căn phòng yên tĩnh"
    )
    {
        var id = (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Image test" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;
        var scene = Guid.NewGuid();
        var doc = new StoryDocument(Scenes: [new(scene, "Cảnh ảnh", "Xin chào", prompt, null, 1)]);
        (
            await c.PutAsJsonAsync($"/api/projects/{id}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        return (id, scene);
    }

    private static async Task<JsonElement> Wait(HttpClient c, Guid project, Guid request)
    {
        for (var i = 0; i < 150; i++)
        {
            var list = await c.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{project}/image-generations"
            );
            var row = list.GetProperty("items")
                .EnumerateArray()
                .First(x => x.GetProperty("generation").GetProperty("id").GetGuid() == request);
            if (
                row.GetProperty("generation").GetProperty("status").GetString()
                is "Succeeded"
                    or "Failed"
                    or "Unknown"
            )
                return row.Clone();
            await Task.Delay(100);
        }
        throw new Exception("Image worker timeout");
    }

    [Fact]
    public async Task GeneratesImageOnceCachesItAndRendersAfterExplicitAttachment()
    {
        using var app = new AudioApp { ImageLimit = 1 };
        var fake = new FakeImage(app.Data);
        app.ImageHandler = fake;
        fake.Block = true;
        using var client = app.CreateClient();
        var (project, scene) = await Setup(client);
        var request = Guid.NewGuid();
        var body = new ImageRequest(request, scene, 1);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (
                await client.PostAsJsonAsync($"/api/projects/{project}/image-generations", body)
            ).StatusCode
        );
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync($"/api/projects/{project}/image-generations", body)
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project}/image-generations",
                    body with
                    {
                        RequestId = Guid.NewGuid(),
                    }
                )
            ).StatusCode
        );
        fake.Release.TrySetResult();
        var row = await Wait(client, project, request);
        Assert.Equal("Succeeded", row.GetProperty("generation").GetProperty("status").GetString());
        Assert.Equal(
            "Căn phòng yên tĩnh",
            row.GetProperty("generation").GetProperty("prompt").GetString()
        );
        var asset = row.GetProperty("asset")
            .Deserialize<Asset>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("image", asset.Kind);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/assets/{asset.Id}/file")).StatusCode
        );
        var document = (
            await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/document")
        )
            .GetProperty("document")
            .Deserialize<StoryDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(document.Scenes![0].AssetId); // No background overwrite.
        document = document with { Scenes = [document.Scenes[0] with { AssetId = asset.Id }] };
        (
            await client.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(1, document)
            )
        ).EnsureSuccessStatusCode();
        var cached = await client.PostAsJsonAsync(
            $"/api/projects/{project}/image-generations",
            new ImageRequest(Guid.NewGuid(), scene, 2)
        );
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.True(
            (await cached.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("reused")
                .GetBoolean()
        );
        var render = await client.PostAsJsonAsync(
            $"/api/projects/{project}/storyboard/render",
            new RenderDocument(2)
        );
        Assert.Equal(HttpStatusCode.Accepted, render.StatusCode);
        var job = (await render.Content.ReadFromJsonAsync<JsonElement>())
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
            if (status is "Succeeded" or "Failed")
                break;
            await Task.Delay(100);
        }
        Assert.Equal("Succeeded", status);
        document = document with
        {
            Scenes = [document.Scenes[0] with { Prompt = "Một cảnh khác" }],
        };
        (
            await client.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(2, document)
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project}/image-generations",
                    new ImageRequest(Guid.NewGuid(), scene, 3)
                )
            ).StatusCode
        );
        // A reused result older than the history window must become visible again,
        // without refreshing CreatedAt (which would incorrectly consume quota).
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            await db
                .ImageGenerations.Where(x => x.Id == request)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-3))
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-3))
                );
            for (var i = 0; i < 50; i++)
                db.ImageGenerations.Add(
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = project,
                        SceneId = Guid.NewGuid(),
                        DocumentVersion = 1,
                        Prompt = "Old failure",
                        Fingerprint = Guid.NewGuid().ToString(),
                        Model = "gpt-image-1",
                        Size = "1024x1536",
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow.AddDays(-2),
                        UpdatedAt = DateTime.UtcNow.AddDays(-2),
                    }
                );
            await db.SaveChangesAsync();
        }
        document = document with
        {
            Scenes = [document.Scenes![0] with { Prompt = "Căn phòng yên tĩnh" }],
        };
        (
            await client.PutAsJsonAsync(
                $"/api/projects/{project}/document",
                new SaveDocument(3, document)
            )
        ).EnsureSuccessStatusCode();
        var reusedOld = await client.PostAsJsonAsync(
            $"/api/projects/{project}/image-generations",
            new ImageRequest(Guid.NewGuid(), scene, 4)
        );
        Assert.Equal(HttpStatusCode.OK, reusedOld.StatusCode);
        var visible = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/image-generations"
        );
        Assert.Equal(
            request,
            visible.GetProperty("items")[0].GetProperty("generation").GetProperty("id").GetGuid()
        );
        Assert.Equal(0, visible.GetProperty("used").GetInt32());
        Assert.Equal(1, fake.Calls);
        Assert.Contains("\"model\":\"gpt-image-1\"", fake.Body);
        Assert.Contains("\"size\":\"1024x1536\"", fake.Body);
        Assert.Contains("\"quality\":\"low\"", fake.Body);
        Assert.Contains("\"n\":1", fake.Body);
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp")));
    }

    [Theory]
    [InlineData("http", "Failed")]
    [InlineData("json", "Failed")]
    [InlineData("png", "Failed")]
    [InlineData("dimensions", "Failed")]
    [InlineData("network", "Unknown")]
    [InlineData("timeout", "Unknown")]
    public async Task ProviderFailuresAreRecordedAndNeverAutomaticallyRetried(
        string mode,
        string expected
    )
    {
        using var app = new AudioApp();
        var fake = new FakeImage(app.Data) { Mode = mode };
        app.ImageHandler = fake;
        using var client = app.CreateClient();
        var (project, scene) = await Setup(client);
        var id = Guid.NewGuid();
        var body = new ImageRequest(id, scene, 1);
        (
            await client.PostAsJsonAsync($"/api/projects/{project}/image-generations", body)
        ).EnsureSuccessStatusCode();
        var row = await Wait(client, project, id);
        Assert.Equal(expected, row.GetProperty("generation").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("asset").ValueKind);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync($"/api/projects/{project}/image-generations", body)
            ).StatusCode
        );
        Assert.Equal(1, fake.Calls);
        Assert.Empty(Directory.GetFiles(Path.Combine(app.Data, "assets")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp")));
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/image-generations"
        );
        Assert.Equal(1, list.GetProperty("used").GetInt32());
    }

    [Fact]
    public async Task LocalPreparationFailureDoesNotCallProviderOrStayRunning()
    {
        using var app = new AudioApp();
        var fake = new FakeImage(app.Data);
        app.ImageHandler = fake;
        using var client = app.CreateClient();
        var (project, scene) = await Setup(client);
        var request = Guid.NewGuid();
        await File.WriteAllTextAsync(
            Path.Combine(app.Data, "temp", "image-" + request.ToString("N")),
            "File blocking temporary directory"
        );
        (
            await client.PostAsJsonAsync(
                $"/api/projects/{project}/image-generations",
                new ImageRequest(request, scene, 1)
            )
        ).EnsureSuccessStatusCode();
        var result = await Wait(client, project, request);
        Assert.Equal("Failed", result.GetProperty("generation").GetProperty("status").GetString());
        Assert.Contains(
            "Chưa gửi",
            result.GetProperty("generation").GetProperty("message").GetString()
        );
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task MissingKeyStaleVersionAndBlankPromptDoNotEnqueue()
    {
        using var app = new AudioApp(false);
        using var client = app.CreateClient();
        var (project, scene) = await Setup(client);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project}/image-generations",
                    new ImageRequest(Guid.NewGuid(), scene, 1)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project}/image-generations",
                    new ImageRequest(Guid.NewGuid(), scene, 2)
                )
            ).StatusCode
        );
        var (empty, emptyScene) = await Setup(client, "");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{empty}/image-generations",
                    new ImageRequest(Guid.NewGuid(), emptyScene, 1)
                )
            ).StatusCode
        );
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/image-generations"
        );
        Assert.Empty(list.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task RestartMarksRunningUnknownAndResumesOnlyQueued()
    {
        using var app = new AudioApp();
        var fake = new FakeImage(app.Data);
        app.ImageHandler = fake;
        var project = Guid.NewGuid();
        var running = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<StudioDb>()
            .UseSqlite(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(app.Data, "studio.db"),
                }.ToString()
            )
            .Options;
        await using (var db = new StudioDb(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Projects.Add(new() { Id = project, Name = "Restart test" });
            foreach (var (id, status) in new[] { (running, "Running"), (queued, "Queued") })
                db.ImageGenerations.Add(
                    new()
                    {
                        Id = id,
                        ProjectId = project,
                        SceneId = Guid.NewGuid(),
                        DocumentVersion = 1,
                        Prompt = "Ảnh thử",
                        Fingerprint = id.ToString(),
                        Model = "gpt-image-1",
                        Size = "1024x1536",
                        Status = status,
                    }
                );
            await db.SaveChangesAsync();
        }
        using var client = app.CreateClient();
        Assert.Equal(
            "Unknown",
            (await Wait(client, project, running))
                .GetProperty("generation")
                .GetProperty("status")
                .GetString()
        );
        Assert.Equal(
            "Succeeded",
            (await Wait(client, project, queued))
                .GetProperty("generation")
                .GetProperty("status")
                .GetString()
        );
        Assert.Equal(1, fake.Calls);
    }
}

public sealed class FakeImage : HttpMessageHandler
{
    public int Calls;
    public string Body = "";
    public string Mode = "";
    public bool Block;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] png;
    private readonly byte[] wrong;

    public FakeImage(string folder)
    {
        byte[] Make(string name, string size)
        {
            var path = Path.Combine(folder, name);
            var info = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
            };
            foreach (
                var a in new[]
                {
                    "-y",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "color=c=teal:s=" + size,
                    "-frames:v",
                    "1",
                    "-threads",
                    "1",
                    path,
                }
            )
                info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new Exception(error);
            return File.ReadAllBytes(path);
        }
        png = Make("image-fixture.png", "1024x1536");
        wrong = Make("wrong-fixture.png", "32x32");
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        Interlocked.Increment(ref Calls);
        Body = await request.Content!.ReadAsStringAsync(ct);
        Started.TrySetResult();
        if (Block)
            await Release.Task.WaitAsync(ct);
        Assert.Equal(
            "https://api.openai.com/v1/images/generations",
            request.RequestUri!.AbsoluteUri
        );
        Assert.Equal("test-key-not-real", request.Headers.Authorization?.Parameter);
        if (Mode == "network")
            throw new HttpRequestException("Simulated network failure");
        if (Mode == "timeout")
            throw new TaskCanceledException("Simulated timeout");
        if (Mode == "http")
            return new(HttpStatusCode.TooManyRequests);
        var payload = Mode switch
        {
            "json" => "[]",
            "png" => "{\"data\":[{\"b64_json\":\"aW52YWxpZA==\"}]}",
            _ => JsonSerializer.Serialize(
                new
                {
                    data = new[]
                    {
                        new
                        {
                            b64_json = Convert.ToBase64String(Mode == "dimensions" ? wrong : png),
                        },
                    },
                }
            ),
        };
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("x-request-id", "test-provider-id");
        return response;
    }
}
