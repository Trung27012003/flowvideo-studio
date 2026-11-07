using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Studio;
using Xunit;

public sealed class StoryboardTests
{
    private static async Task<Guid> Project(HttpClient client) =>
        (
            await (
                await client.PostAsJsonAsync("/api/projects", new { name = "Storyboard test" })
            ).Content.ReadFromJsonAsync<Studio.Project>()
        )!.Id;

    private static async Task<Asset> Upload(HttpClient client, Guid id, string path, string name)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", name);
        var response = await client.PostAsync($"/api/projects/{id}/assets", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Asset>())!;
    }

    private static StoryDocument Document(params StoryScene[] scenes) =>
        new(Idea: "Ý tưởng", Script: "Kịch bản tiếng Việt", Scenes: scenes, Ratio: "16:9");

    private static StoryScene Scene(
        Guid? asset = null,
        double duration = 1,
        string text = "Xin chào"
    ) => new(Guid.NewGuid(), "Cảnh", text, "Ghi chú", asset, duration);

    [Fact]
    public async Task SavesScriptAndScenesAndRejectsStaleUpdates()
    {
        using var app = new AudioApp();
        using var client = app.CreateClient();
        var id = await Project(client);
        var initial = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/document");
        Assert.Equal(0, initial.GetProperty("version").GetInt32());
        var draft = Document(Scene());
        var saved = await client.PutAsJsonAsync(
            $"/api/projects/{id}/document",
            new SaveDocument(0, draft)
        );
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(
            1,
            (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32()
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(0, draft with { Script = "stale" })
                )
            ).StatusCode
        );
        var result = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/document");
        Assert.Equal(
            draft.Script,
            result.GetProperty("document").GetProperty("script").GetString()
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{id}/storyboard/render",
                    new RenderDocument(1)
                )
            ).StatusCode
        ); // Asset not selected yet.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{id}/storyboard/render",
                    new RenderDocument(0)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(1, draft with { Script = "Updated" })
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(1, draft)
                )
            ).StatusCode
        );
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task RejectsInvalidScenesAndCrossProjectAssets()
    {
        using var app = new AudioApp();
        using var client = app.CreateClient();
        var id = await Project(client);
        var other = await Project(client);
        var asset = await Upload(client, other, app.Video, "clip.mp4");
        var scene = Scene();
        foreach (
            var doc in new[]
            {
                Document(scene with { Duration = 0 }),
                Document(scene with { Start = -1 }),
                Document(scene, scene),
                Document(Scene(asset.Id)),
                Document(scene with { Duration = 601 }),
                Document(Scene(duration: 400), Scene(duration: 300)),
                Document(scene) with
                {
                    Ratio = "wrong",
                },
                Document(scene) with
                {
                    VoiceId = asset.Id,
                },
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await client.PutAsJsonAsync(
                        $"/api/projects/{id}/document",
                        new SaveDocument(0, doc)
                    )
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/document")).StatusCode
        );
    }

    [Fact]
    public async Task RendersRepeatedAssetWithSceneTimingAndImmutableSnapshot()
    {
        using var app = new AudioApp();
        using var client = app.CreateClient();
        var id = await Project(client);
        var asset = await Upload(client, id, app.Video, "clip.mp4");
        // Both scenes share one short source; the first must freeze its last frame
        // to reach two seconds, and captions must use the actual assembled timeline.
        var doc = Document(Scene(asset.Id, 2, "Cảnh thứ nhất"), Scene(asset.Id, 1, "Cảnh thứ hai"));
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(0, doc)
                )
            ).StatusCode
        );
        var response = await client.PostAsJsonAsync(
            $"/api/projects/{id}/storyboard/render",
            new RenderDocument(1)
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(1, doc with { Scenes = [Scene(asset.Id, 1, "Changed")] })
                )
            ).StatusCode
        );
        string? status = null;
        for (var i = 0; i < 200; i++)
        {
            var jobs = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/jobs");
            status = jobs.EnumerateArray()
                .First(x => x.GetProperty("id").GetGuid() == job)
                .GetProperty("status")
                .GetString();
            if (status is "Succeeded" or "Failed")
                break;
            await Task.Delay(100);
        }
        Assert.Equal("Succeeded", status);
        var output = Path.Combine(app.Data, "exports", job + ".mp4");
        Assert.InRange(await MediaProcess.Duration(output, CancellationToken.None), 2.95, 3.15);
        // Job JSON is a snapshot of v1 and is not rewritten by the v2 save.
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(app.Data, "studio.db"),
            }.ToString()
        );
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestJson FROM Jobs WHERE Id=$id";
        command.Parameters.AddWithValue("$id", job.ToString().ToUpperInvariant());
        var request = JsonSerializer.Deserialize<RenderRequest>(
            (string)(await command.ExecuteScalarAsync())!
        )!;
        Assert.Equal(2, request.Scenes!.Length);
        Assert.Equal("Cảnh thứ nhất", request.Scenes[0].Caption);
        Assert.True(request.AutoCaptions);
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task UpgradesExistingDatabaseWithoutLosingProjectsOrMedia()
    {
        using var app = new AudioApp();
        var id = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(app.Data, "assets"));
        File.Copy(app.Video, Path.Combine(app.Data, "assets", "existing.mp4"));
        await using (
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(app.Data, "studio.db"),
                }.ToString()
            )
        )
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Projects(Id TEXT NOT NULL PRIMARY KEY,Name TEXT NOT NULL,CreatedAt TEXT NOT NULL);
                CREATE TABLE Assets(Id TEXT NOT NULL PRIMARY KEY,ProjectId TEXT NOT NULL,Name TEXT NOT NULL,Kind TEXT NOT NULL,StoredName TEXT NOT NULL);
                CREATE TABLE Jobs(Id TEXT NOT NULL PRIMARY KEY,ProjectId TEXT NOT NULL,RequestJson TEXT NOT NULL,Status TEXT NOT NULL,Message TEXT NOT NULL,CreatedAt TEXT NOT NULL);
                INSERT INTO Projects VALUES($id,'Existing project','2026-11-07 00:00:00');
                INSERT INTO Assets VALUES($asset,$id,'Existing video','video','existing.mp4');
                """;
            command.Parameters.AddWithValue("$id", id.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$asset", assetId.ToString().ToUpperInvariant());
            await command.ExecuteNonQueryAsync();
        }
        using var client = app.CreateClient();
        Assert.Contains(
            (await client.GetFromJsonAsync<Studio.Project[]>("/api/projects"))!,
            p => p.Id == id
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/assets/{assetId}/file")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/document",
                    new SaveDocument(0, Document(Scene(assetId)))
                )
            ).StatusCode
        );
        Assert.Equal(
            1,
            (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/document"))
                .GetProperty("version")
                .GetInt32()
        );
    }
}
