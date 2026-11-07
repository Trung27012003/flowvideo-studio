using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Studio;
using Xunit;

public class LoopTests
{
    static async Task<(Guid Project, Guid Asset)> Setup(
        AudioApp app,
        HttpClient c,
        double duration = 2
    )
    {
        var fixture = Path.Combine(app.Data, "two-colors.mp4");
        Run([
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            "color=red:s=160x90:r=30:d="
                + (duration / 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f",
            "lavfi",
            "-i",
            "color=blue:s=160x90:r=30:d="
                + (duration / 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-filter_complex",
            "[0:v][1:v]concat=n=2:v=1:a=0[v]",
            "-map",
            "[v]",
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-threads",
            "1",
            fixture,
        ]);
        var p = (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Loops" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(fixture)), "file", "colors.mp4");
        var asset = (
            await (
                await c.PostAsync($"/api/projects/{p}/assets", form)
            ).Content.ReadFromJsonAsync<Asset>()
        )!;
        return (p, asset.Id);
    }

    static byte[] Run(string[] args, bool binary = false)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.GetAwaiter().GetResult());
        return output.ToArray();
    }

    static byte[] Pixel(string file, double at) =>
        Run(
            [
                "-hide_banner",
                "-loglevel",
                "error",
                "-ss",
                at.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-i",
                file,
                "-frames:v",
                "1",
                "-vf",
                "scale=1:1",
                "-pix_fmt",
                "rgb24",
                "-threads",
                "1",
                "-f",
                "rawvideo",
                "-",
            ],
            true
        );

    static async Task<JsonElement> Wait(HttpClient c, Guid p, Guid id)
    {
        for (int i = 0; i < 200; i++)
        {
            var rows = (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!;
            var row = rows.Single(x => x.GetProperty("id").GetGuid() == id);
            if (row.GetProperty("status").GetString() is "Succeeded" or "Failed")
                return row;
            await Task.Delay(100);
        }
        throw new Exception("Loop timeout");
    }

    static async Task<(Guid Id, JsonElement Row, string File)> Render(
        AudioApp app,
        HttpClient c,
        Guid p,
        RenderRequest req
    )
    {
        var response = await c.PostAsJsonAsync($"/api/projects/{p}/renders", req);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        return (id, await Wait(c, p, id), Path.Combine(app.Data, "exports", id + ".mp4"));
    }

    [Fact]
    public async Task ReversePingpongAndCrossfadeChangeActualFramesAndKeepRequestedOutputLength()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, a) = await Setup(app, c);
        foreach (var style in new[] { "direct", "reverse", "pingpong", "crossfade" })
        {
            var result = await Render(
                app,
                c,
                p,
                new([a], Mode: "loop", Duration: 4, Ratio: "16:9", Loop: new(style, .4))
            );
            Assert.True(
                result.Row.GetProperty("status").GetString() == "Succeeded",
                style + ": " + result.Row.ToString()
            );
            Assert.InRange(
                await MediaProcess.Duration(result.File, CancellationToken.None),
                3.95,
                4.1
            );
            var first = Pixel(result.File, .2);
            var second = Pixel(result.File, 1.0);
            var third = Pixel(result.File, 2.2);
            var fourth = Pixel(result.File, 3.2);
            if (style == "reverse")
            {
                Assert.True(first[2] > first[0] + 150);
                Assert.True(third[2] > third[0] + 150);
                Assert.True(fourth[0] > fourth[2] + 150);
            }
            if (style == "direct")
            {
                Assert.True(first[0] > first[2] + 150);
                Assert.True(third[0] > third[2] + 150);
                Assert.True(fourth[2] > fourth[0] + 150);
            }
            if (style == "pingpong")
            {
                Assert.True(first[0] > first[2] + 150);
                Assert.True(third[2] > third[0] + 150);
                Assert.True(fourth[0] > fourth[2] + 150);
            }
            if (style == "crossfade")
            {
                var blend = Pixel(result.File, 1.4);
                Assert.True(blend[0] > 50 && blend[2] > 50, string.Join(',', blend));
                var before = Pixel(result.File, 1.57);
                var after = Pixel(result.File, 1.67);
                Assert.True(Math.Abs(before[0] - after[0]) + Math.Abs(before[2] - after[2]) < 100);
            }
            var recipe = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{result.Id}/recipe");
            Assert.Equal(
                style,
                recipe.GetProperty("request").GetProperty("loop").GetProperty("style").GetString()
            );
        }
    }

    [Theory]
    [InlineData("reverse", 24)]
    [InlineData("reverse", 60)]
    [InlineData("pingpong", 24)]
    [InlineData("pingpong", 60)]
    [InlineData("crossfade", 24)]
    [InlineData("crossfade", 60)]
    public async Task AdvancedLoopsKeepConfiguredFrameRate(string style, int fps)
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, a) = await Setup(app, c);
        var result = await Render(
            app,
            c,
            p,
            new([a], Mode: "loop", Duration: 2, Effects: new(Fps: fps), Loop: new(style, .25))
        );
        Assert.Equal("Succeeded", result.Row.GetProperty("status").GetString());
        var info = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{result.Id}/media-info");
        Assert.Equal(fps + "/1", info.GetProperty("frameRate").GetString());
        Assert.InRange(info.GetProperty("duration").GetDouble(), 1.95, 2.1);
    }

    [Fact]
    public async Task RejectsBadModesResolutionAndUnsafeCycleBudgetsAndCleansFiles()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var (p, a) = await Setup(app, c, 10);
        foreach (
            var req in new[]
            {
                new RenderRequest([a], Loop: new("bad")),
                new([a], Mode: "concat", Loop: new("reverse")),
                new([a], Mode: "loop", Resolution: 1080, Loop: new("pingpong")),
                new([a], Mode: "loop", Loop: new("crossfade", 0)),
            }
        )
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await c.PostAsJsonAsync($"/api/projects/{p}/renders", req)).StatusCode
            );
        var over = await Render(
            app,
            c,
            p,
            new([a], Mode: "loop", Duration: 10, Loop: new("pingpong"))
        );
        Assert.Equal("Failed", over.Row.GetProperty("status").GetString());
        Assert.False(File.Exists(over.File));
        var badFade = await Render(
            app,
            c,
            p,
            new([a], Mode: "loop", Duration: 1, Loop: new("crossfade", .6))
        );
        Assert.Equal("Failed", badFade.Row.GetProperty("status").GetString());
        Assert.False(File.Exists(badFade.File));
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp")));
    }
}
