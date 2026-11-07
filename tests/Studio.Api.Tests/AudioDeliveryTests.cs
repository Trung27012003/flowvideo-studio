using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Studio;
using Xunit;

public sealed class AudioDeliveryTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static async Task<Guid> Project(HttpClient c) =>
        (
            await (
                await c.PostAsJsonAsync("/api/projects", new { name = "Audio v11" })
            ).Content.ReadFromJsonAsync<Project>()
        )!.Id;

    static async Task<Asset> Upload(HttpClient c, Guid p, string path, string name)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", name);
        var r = await c.PostAsync($"/api/projects/{p}/assets", form);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Asset>())!;
    }

    static async Task<Guid> Wait(
        HttpClient c,
        Guid p,
        HttpResponseMessage response,
        bool failed = false
    )
    {
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        for (int n = 0; n < 400; n++)
        {
            var jobs = (await c.GetFromJsonAsync<JsonElement[]>($"/api/projects/{p}/jobs"))!;
            var job = jobs.Single(j => j.GetProperty("id").GetGuid() == id);
            var status = job.GetProperty("status").GetString();
            if (status == (failed ? "Failed" : "Succeeded"))
                return id;
            if (!failed)
                Assert.NotEqual("Failed", status);
            await Task.Delay(100);
        }
        throw new Exception("Job timed out");
    }

    static async Task<JsonElement> Probe(string path) =>
        JsonSerializer.Deserialize<JsonElement>(
            await MediaProcess.Run(
                "ffprobe",
                ["-v", "error", "-show_streams", "-show_format", "-of", "json", path],
                Path.GetDirectoryName(path)!,
                CancellationToken.None
            )
        );

    [Fact]
    public async Task TuningValidationRejectsUnsupportedValuesInDocumentAndStudio()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var v = await Upload(c, p, app.Video, "v.mp4");
        foreach (
            var tuning in new[]
            {
                new AudioTuning(VoiceHighpass: 1),
                new AudioTuning(VoiceLowpass: 999),
                new AudioTuning(VoiceBass: 13),
                new AudioTuning(VoiceTreble: -13),
                new AudioTuning(VoicePan: 2),
                new AudioTuning(VoiceStart: 601),
                new AudioTuning(VoiceFadeIn: 6),
                new AudioTuning(VoiceFadeOut: -1),
                new AudioTuning(MusicSpeed: .4),
                new AudioTuning(MusicHighpass: 501),
                new AudioTuning(MusicLowpass: 19000),
                new AudioTuning(MusicPan: -2),
                new AudioTuning(MusicFadeIn: 6),
                new AudioTuning(MusicFadeOut: -1),
            }
        )
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PostAsJsonAsync(
                        $"/api/projects/{p}/renders",
                        new RenderRequest([v.Id], Audio: tuning)
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.PutAsJsonAsync(
                        $"/api/projects/{p}/document",
                        new SaveDocument(0, new(Scenes: [], Audio: tuning))
                    )
                ).StatusCode
            );
        }
        Assert.Null(JsonSerializer.Deserialize<RenderRequest>("{\"AssetIds\":[]}")!.Audio);
        Assert.True(new AudioTuning().Valid());
    }

    [Fact]
    public async Task TuningPersistsAcrossSnapshotsAndPortablePresets()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var audio = new AudioTuning(
            VoiceHighpass: 80,
            VoiceLowpass: 12000,
            DenoiseVoice: true,
            CompressVoice: true,
            VoiceBass: 3,
            VoiceTreble: -2,
            VoicePan: .2,
            VoiceStart: .1,
            VoiceFadeIn: .2,
            VoiceFadeOut: .3,
            MusicSpeed: 1.2,
            MusicHighpass: 100,
            MusicLowpass: 10000,
            MusicPan: -.3,
            MusicLoop: false,
            MusicFadeIn: .2,
            MusicFadeOut: .4
        );
        var doc = new StoryDocument(Scenes: [], Audio: audio);
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        var package = (
            await c.GetFromJsonAsync<StoryboardPackage>($"/api/projects/{p}/storyboard/export")
        )!;
        Assert.Equal(audio, package.Document.Audio);
        var imported = await (
            await c.PostAsJsonAsync("/api/storyboards/import", package)
        ).Content.ReadFromJsonAsync<JsonElement>();
        var clone = await (
            await c.PostAsync($"/api/projects/{p}/duplicate", null)
        ).Content.ReadFromJsonAsync<JsonElement>();
        foreach (var result in new[] { imported, clone })
        {
            var id = result.GetProperty("project").GetProperty("id").GetGuid();
            var saved = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/document");
            Assert.Equal(
                audio,
                saved.GetProperty("document").GetProperty("audio").Deserialize<AudioTuning>(Web)
            );
        }
        foreach (var kind in new[] { "checkpoint", "preset" })
        {
            var item = await (
                await c.PostAsJsonAsync(
                    $"/api/projects/{p}/saved-items",
                    new CaptureItem(kind, "Audio", 1)
                )
            ).Content.ReadFromJsonAsync<JsonElement>();
            var result = await c.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{p}/saved-items/{item.GetProperty("id").GetGuid()}"
            );
            Assert.Equal(
                audio,
                result.GetProperty("document").GetProperty("audio").Deserialize<AudioTuning>(Web)
            );
        }
    }

    [Fact]
    public async Task AllAudioFiltersRenderWithDuckingAndPreserveSnapshot()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var v = await Upload(c, p, app.Video, "v.mp4");
        var a = await Upload(c, p, app.Audio, "a.mp3");
        var tuning = new AudioTuning(
            VoiceHighpass: 80,
            VoiceLowpass: 12000,
            DenoiseVoice: true,
            CompressVoice: true,
            VoiceBass: 3,
            VoiceTreble: -2,
            VoicePan: .5,
            VoiceStart: .1,
            VoiceFadeIn: .2,
            VoiceFadeOut: .2,
            MusicSpeed: 1.2,
            MusicHighpass: 100,
            MusicLowpass: 10000,
            MusicPan: -.5,
            MusicLoop: false,
            MusicFadeIn: .2,
            MusicFadeOut: .2
        );
        var job = await Wait(
            c,
            p,
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/renders",
                new RenderRequest(
                    [v.Id],
                    Mode: "loop",
                    Duration: 3,
                    VoiceId: a.Id,
                    MusicId: a.Id,
                    DuckMusic: true,
                    Effects: new(VoiceDelay: .1, NormalizeVoice: true),
                    Audio: tuning
                )
            )
        );
        var info = await c.GetFromJsonAsync<RenderMediaInfo>($"/api/jobs/{job}/media-info");
        Assert.InRange(info!.Duration, 2.9, 3.1);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(2, info.Channels);
        var recipe = await c.GetFromJsonAsync<JsonElement>($"/api/jobs/{job}/recipe");
        Assert.Equal(
            tuning,
            recipe.GetProperty("request").GetProperty("audio").Deserialize<AudioTuning>(Web)
        );
    }

    [Fact]
    public async Task AllDeliveryFormatsContainRealMediaAndBundleHashesMatchPayload()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var v = await Upload(c, p, app.Video, "v.mp4");
        var a = await Upload(c, p, app.Audio, "a.mp3");
        var doc = new StoryDocument(
            Scenes: [new(Guid.NewGuid(), "A", "Xin chào", "", v.Id, 1)],
            VoiceId: a.Id
        );
        (
            await c.PutAsJsonAsync($"/api/projects/{p}/document", new SaveDocument(0, doc))
        ).EnsureSuccessStatusCode();
        var job = await Wait(
            c,
            p,
            await c.PostAsJsonAsync($"/api/projects/{p}/storyboard/render", new RenderDocument(1))
        );
        foreach (var kind in new[] { "wav", "flac", "mute", "gif", "poster", "contact", "bundle" })
        {
            var response = await c.GetAsync($"/api/jobs/{job}/deliver/{kind}?at=0.1&duration=0.5");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 100);
            if (kind == "bundle")
            {
                using var zip = new ZipArchive(new MemoryStream(bytes));
                Assert.Contains(zip.Entries, e => e.FullName == "video.mp4");
                Assert.Contains(zip.Entries, e => e.FullName == "subtitles.srt");
                Assert.Contains(zip.Entries, e => e.FullName == "recipe.json");
                using var stream = zip.GetEntry("manifest.json")!.Open();
                using var manifest = await JsonDocument.ParseAsync(stream);
                foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
                {
                    using var input = zip.GetEntry(entry.GetProperty("name").GetString()!)!.Open();
                    Assert.Equal(
                        entry.GetProperty("sha256").GetString(),
                        Convert.ToHexString(await SHA256.HashDataAsync(input))
                    );
                }
                continue;
            }
            var extension =
                kind == "mute" ? "mp4"
                : kind is "poster" or "contact" ? "png"
                : kind;
            var file = Path.Combine(app.Data, "delivery-" + kind + "." + extension);
            await File.WriteAllBytesAsync(file, bytes);
            var info = await Probe(file);
            var streams = info.GetProperty("streams").EnumerateArray().ToArray();
            if (kind is "wav" or "flac")
            {
                var audio = Assert.Single(streams);
                Assert.Equal(
                    kind == "wav" ? "pcm_s16le" : "flac",
                    audio.GetProperty("codec_name").GetString()
                );
                Assert.Equal("48000", audio.GetProperty("sample_rate").GetString());
                Assert.Equal(2, audio.GetProperty("channels").GetInt32());
            }
            if (kind == "mute")
                Assert.DoesNotContain(
                    streams,
                    s => s.GetProperty("codec_type").GetString() == "audio"
                );
            if (kind == "gif")
                Assert.Equal("gif", streams[0].GetProperty("codec_name").GetString());
            if (kind == "poster")
                Assert.Equal("png", streams[0].GetProperty("codec_name").GetString());
            if (kind == "contact")
                Assert.True(streams[0].GetProperty("width").GetInt32() >= 960);
        }
        for (
            int i = 0;
            i < 50
                && Directory.GetDirectories(Path.Combine(app.Data, "temp"), "delivery-*").Length
                    > 0;
            i++
        )
            await Task.Delay(20);
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp"), "delivery-*"));
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task DeliveryRejectsInvalidRangesStatesAndMissingAudioAndBoundsLargeFiles()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var v = await Upload(c, p, app.Video, "v.mp4");
        var job = await Wait(
            c,
            p,
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/renders",
                new RenderRequest([v.Id], Duration: 1)
            )
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/jobs/{Guid.NewGuid()}/deliver/poster")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await c.GetAsync($"/api/jobs/{job}/deliver/exe")).StatusCode
        );
        foreach (var query in new[] { "at=-1", "at=601", "duration=11", "duration=0", "at=1.5" })
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await c.GetAsync($"/api/jobs/{job}/deliver/gif?{query}")).StatusCode
            );
        foreach (var kind in new[] { "wav", "flac" })
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await c.GetAsync($"/api/jobs/{job}/deliver/{kind}")).StatusCode
            );
        var output = app.Services.GetRequiredService<Storage>().Output(job);
        using (var stream = File.Open(output, FileMode.Open, FileAccess.Write))
            stream.SetLength(104857601);
        foreach (var kind in new[] { "mute", "bundle" })
            Assert.Equal(
                HttpStatusCode.RequestEntityTooLarge,
                (await c.GetAsync($"/api/jobs/{job}/deliver/{kind}")).StatusCode
            );
        Assert.Empty(Directory.GetDirectories(Path.Combine(app.Data, "temp"), "delivery-*"));
    }

    [Fact]
    public async Task VoiceTrimOutsideSourceFailsAndNonloopMusicDoesNotShortenVideo()
    {
        using var app = new AudioApp(false);
        using var c = app.CreateClient();
        var p = await Project(c);
        var v = await Upload(c, p, app.Video, "v.mp4");
        var a = await Upload(c, p, app.Audio, "a.mp3");
        var job = await Wait(
            c,
            p,
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/renders",
                new RenderRequest(
                    [v.Id],
                    Mode: "loop",
                    Duration: 3,
                    MusicId: a.Id,
                    Audio: new(MusicLoop: false, MusicSpeed: 1.5)
                )
            )
        );
        var info = (await c.GetFromJsonAsync<RenderMediaInfo>($"/api/jobs/{job}/media-info"))!;
        Assert.InRange(info.Duration, 2.9, 3.1);
        var failed = await Wait(
            c,
            p,
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/renders",
                new RenderRequest([v.Id], VoiceId: a.Id, Audio: new(VoiceStart: 10))
            ),
            true
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.GetAsync($"/api/jobs/{failed}/download")).StatusCode
        );
        (
            await c.PutAsJsonAsync(
                $"/api/projects/{p}/document",
                new SaveDocument(
                    0,
                    new(
                        Scenes: [new(Guid.NewGuid(), "A", "", "", v.Id, 1)],
                        VoiceId: a.Id,
                        Audio: new(VoiceStart: 10)
                    )
                )
            )
        ).EnsureSuccessStatusCode();
        var report = await (
            await c.PostAsJsonAsync(
                $"/api/projects/{p}/storyboard/preflight",
                new RenderDocument(1)
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("ready").GetBoolean());
    }
}
