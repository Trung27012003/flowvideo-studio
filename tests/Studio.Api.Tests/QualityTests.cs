using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public class QualityTests
{
    static async Task<(Guid Project, Asset Video)> Setup(AudioApp app, HttpClient c)
    {
        var p = (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "QA" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;
        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(app.Video)),
            "file",
            "video.mp4"
        );
        var video = (
            await (
                await c.PostAsync($"/api/projects/{p}/assets", form)
            ).Content.ReadFromJsonAsync<Asset>()
        )!;
        return (p, video);
    }

    static async Task<Guid> Render(HttpClient c, Guid p, RenderRequest req)
    {
        var r = await c.PostAsJsonAsync($"/api/projects/{p}/renders", req);
        r.EnsureSuccessStatusCode();
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        for (int i = 0; i < 200; i++)
        {
            var row = (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!.Single(
                j => j.GetProperty("id").GetGuid() == id
            );
            Assert.NotEqual("Failed", row.GetProperty("status").GetString());
            if (row.GetProperty("status").GetString() == "Succeeded")
                return id;
            await Task.Delay(100);
        }
        throw new Exception("Render timeout");
    }

    [Fact]
    public async Task EverySuccessfulRenderHasAutomaticQaReportAndCanRecheckWithoutAi()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, v) = await Setup(app, c);
        var id = await Render(c, p, new([v.Id], Mode: "loop", Duration: 2));
        var report = await c.GetFromJsonAsync<QualityReport>($"/api/jobs/{id}/qa");
        Assert.Equal("Passed", report!.Result);
        Assert.False(report.FullDecode);
        Assert.Contains(report.Checks, x => x.Code == "video-codec" && x.Severity == "pass");
        Assert.Contains(report.Checks, x => x.Code == "audio-stream" && x.Severity == "pass");
        Assert.All(report.Checks, x => Assert.NotEqual("error", x.Severity));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        var job = await db.Jobs.SingleAsync(x => x.Id == id);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.RequestJson))),
            report.RecipeSha256
        );
        var recheck = await c.PostAsJsonAsync($"/api/jobs/{id}/qa", new { });
        recheck.EnsureSuccessStatusCode();
        Assert.Equal("Passed", (await recheck.Content.ReadFromJsonAsync<QualityReport>())!.Result);
        Assert.Equal(1, await db.RenderQaReports.CountAsync());
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task ManualQaDetectsMissingCorruptAndChangedOutputWithoutPretendingToRepairIt()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, v) = await Setup(app, c);
        var id = await Render(c, p, new([v.Id], Mode: "loop", Duration: 2));
        var file = Path.Combine(app.Data, "exports", id + ".mp4");
        await File.WriteAllBytesAsync(file, await File.ReadAllBytesAsync(app.Video));
        var changed = await (
            await c.PostAsJsonAsync($"/api/jobs/{id}/qa", new { })
        ).Content.ReadFromJsonAsync<QualityReport>();
        Assert.Equal("Failed", changed!.Result);
        Assert.Contains(changed.Checks, x => x.Code == "dimensions" && x.Severity == "error");
        Assert.Contains(changed.Checks, x => x.Code == "duration" && x.Severity == "error");
        await File.WriteAllTextAsync(file, "corrupt");
        var corrupt = await (
            await c.PostAsJsonAsync($"/api/jobs/{id}/qa", new { })
        ).Content.ReadFromJsonAsync<QualityReport>();
        Assert.Contains(corrupt!.Checks, x => x.Code == "probe" && x.Severity == "error");
        File.Delete(file);
        var missing = await (
            await c.PostAsJsonAsync($"/api/jobs/{id}/qa", new { })
        ).Content.ReadFromJsonAsync<QualityReport>();
        Assert.Equal("Failed", missing!.Result);
        Assert.Contains(missing.Checks, x => x.Code == "output-file" && x.Severity == "error");
        var stored = await c.GetFromJsonAsync<QualityReport>($"/api/jobs/{id}/qa");
        Assert.Equal(missing.CheckedAt, stored!.CheckedAt);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task SubtitleBeyondOutputIsWarningAndAudioMetadataIsVerified()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, v) = await Setup(app, c);
        using var aform = new MultipartFormDataContent();
        aform.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(app.Audio)),
            "file",
            "voice.mp3"
        );
        var audio = (
            await (
                await c.PostAsync($"/api/projects/{p}/assets", aform)
            ).Content.ReadFromJsonAsync<Asset>()
        )!;
        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(
                Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:05,000\nXin chào\n")
            ),
            "file",
            "captions.srt"
        );
        var sub = (
            await (
                await c.PostAsync($"/api/projects/{p}/assets", form)
            ).Content.ReadFromJsonAsync<Asset>()
        )!;
        var id = await Render(
            c,
            p,
            new(
                [v.Id],
                Mode: "loop",
                Duration: 2,
                VoiceId: audio.Id,
                SubtitleId: sub.Id,
                Effects: new(Mono: true)
            )
        );
        var report = await c.GetFromJsonAsync<QualityReport>($"/api/jobs/{id}/qa");
        Assert.Equal("Warnings", report!.Result);
        Assert.Contains(report.Checks, x => x.Code == "subtitle-tail" && x.Severity == "warning");
        Assert.Contains(report.Checks, x => x.Code == "mono" && x.Severity == "pass");
        Assert.Equal(1, report.Media!.Channels);
        await File.WriteAllBytesAsync(
            Path.Combine(app.Data, "exports", id + ".srt"),
            new byte[1048577]
        );
        var large = await (
            await c.PostAsJsonAsync($"/api/jobs/{id}/qa", new { })
        ).Content.ReadFromJsonAsync<QualityReport>();
        Assert.Equal("Warnings", large!.Result);
        Assert.Contains(large.Checks, x => x.Code == "subtitle-size" && x.Severity == "warning");
    }

    [Fact]
    public async Task QaRejectsNonterminalJobsAndValidatesDirectionOnDocumentSave()
    {
        using var app = new AudioApp(false) { DisabledWorkers = [typeof(RenderWorker)] };
        using var c = app.CreateClient();
        var (p, v) = await Setup(app, c);
        var r = await c.PostAsJsonAsync($"/api/projects/{p}/renders", new RenderRequest([v.Id]));
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync($"/api/jobs/{id}/qa", new { })).StatusCode
        );
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/jobs/{id}/qa")).StatusCode);
        var scene = new StoryScene(
            Guid.NewGuid(),
            "S",
            "Hi",
            "Prompt",
            v.Id,
            1,
            Direction: new(Shot: "invalid")
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.PutAsJsonAsync(
                    $"/api/projects/{p}/document",
                    new SaveDocument(0, new(Scenes: [scene]))
                )
            ).StatusCode
        );
        var valid = scene with
        {
            Direction = new("wide", "pan", "soft", "studio", "person", "intro"),
        };
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/document",
                new SaveDocument(0, new(Scenes: [valid]))
            )
        ).EnsureSuccessStatusCode();
        var doc = (
            await c.GetFromJsonAsync<JsonElement>($"/api/projects/{p}/document")
        ).GetProperty("document");
        Assert.Equal(
            "pan",
            doc.GetProperty("scenes")[0].GetProperty("direction").GetProperty("camera").GetString()
        );
    }
}
