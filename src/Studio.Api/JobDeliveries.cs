using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public record RenderMediaInfo(
    long Bytes,
    double Duration,
    string? VideoCodec,
    int? Width,
    int? Height,
    string? FrameRate,
    string? AudioCodec,
    int? Channels,
    int? SampleRate
);

public static class JobDeliveries
{
    static readonly SemaphoreSlim Gate = new(2, 2);

    static string N(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);

    static string? Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            ? field.ToString()
            : null;

    static int? Integer(JsonElement value, string key) =>
        int.TryParse(Text(value, key), out var n) ? n : null;

    public static async Task<RenderMediaInfo> Probe(
        string file,
        Storage store,
        CancellationToken ct
    )
    {
        var raw = await MediaProcess.Run(
            "ffprobe",
            [
                "-v",
                "error",
                "-show_entries",
                "format=duration:stream=codec_type,codec_name,width,height,r_frame_rate,channels,sample_rate,duration",
                "-of",
                "json",
                file,
            ],
            store.Root,
            ct
        );
        using var json = JsonDocument.Parse(raw);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var v = streams.FirstOrDefault(s => Text(s, "codec_type") == "video");
        var a = streams.FirstOrDefault(s => Text(s, "codec_type") == "audio");
        if (
            !double.TryParse(
                Text(json.RootElement.GetProperty("format"), "duration"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration
            )
            || !double.IsFinite(duration)
            || duration <= 0
        )
            throw new IOException("Invalid duration.");
        if (
            double.TryParse(
                Text(v, "duration"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var videoDuration
            )
            && double.IsFinite(videoDuration)
            && videoDuration > 0
        )
            duration = videoDuration;
        return new(
            new FileInfo(file).Length,
            duration,
            Text(v, "codec_name"),
            Integer(v, "width"),
            Integer(v, "height"),
            Text(v, "r_frame_rate"),
            Text(a, "codec_name"),
            Integer(a, "channels"),
            Integer(a, "sample_rate")
        );
    }

    public static void MapJobDeliveries(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<Storage>();
        foreach (
            var directory in Directory.EnumerateDirectories(
                Path.Combine(store.Root, "temp"),
                "delivery-*"
            )
        )
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
        }
        app.MapGet(
            "/api/jobs/{id:guid}/media-info",
            async (Guid id, StudioDb db, Storage storage, CancellationToken ct) =>
            {
                if (
                    !await db.Jobs.AnyAsync(j => j.Id == id && j.Status == "Succeeded", ct)
                    || !File.Exists(storage.Output(id))
                )
                    return Results.NotFound();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    return Results.Ok(await Probe(storage.Output(id), storage, timeout.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem("Đọc thông số quá 15 giây.", statusCode: 504);
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    return Results.Problem("Không đọc được bản xuất.", statusCode: 422);
                }
            }
        );
        app.MapGet(
            "/api/jobs/{id:guid}/deliver/{kind}",
            async (
                Guid id,
                string kind,
                double? at,
                double? duration,
                StudioDb db,
                Storage storage,
                HttpContext context,
                CancellationToken ct
            ) =>
            {
                if (
                    kind
                    is not ("wav" or "flac" or "mute" or "gif" or "poster" or "contact" or "bundle")
                )
                    return Results.BadRequest(new { error = "Định dạng xuất chưa hỗ trợ." });
                var start = at ?? 0;
                var length = duration ?? 3;
                if (
                    !double.IsFinite(start)
                    || start < 0
                    || start > 600
                    || !double.IsFinite(length)
                    || length < .1
                    || length > 10
                )
                    return Results.BadRequest(new { error = "Mốc 0–600 giây; GIF 0,1–10 giây." });
                var job = await db
                    .Jobs.AsNoTracking()
                    .FirstOrDefaultAsync(j => j.Id == id && j.Status == "Succeeded", ct);
                if (job is null || !File.Exists(storage.Output(id)))
                    return Results.NotFound();
                if (!await Gate.WaitAsync(0, ct))
                    return Results.Problem(
                        "Đang xử lý hai bản xuất. Thử lại sau.",
                        statusCode: 429
                    );
                var folder = Path.Combine(
                    storage.Root,
                    "temp",
                    "delivery-" + Guid.NewGuid().ToString("N")
                );
                bool staged = false;
                try
                {
                    Directory.CreateDirectory(folder);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var token = timeout.Token;
                    var source = storage.Output(id);
                    var info = await Probe(source, storage, token);
                    if (kind is "wav" or "flac" && info.AudioCodec is null)
                        return Results.Problem(
                            "Bản xuất không có track âm thanh.",
                            statusCode: 422
                        );
                    if (kind is "gif" or "poster" && start >= info.Duration)
                        return Results.BadRequest(
                            new { error = "Mốc lấy ảnh/GIF phải nằm trong video." }
                        );
                    if (kind is "mute" or "bundle" && info.Bytes > 104857600)
                        return Results.Problem(
                            "MP4 nguồn tối đa 100 MiB cho công cụ này.",
                            statusCode: 413
                        );
                    var ext = kind switch
                    {
                        "wav" => "wav",
                        "flac" => "flac",
                        "mute" => "mp4",
                        "gif" => "gif",
                        "bundle" => "zip",
                        _ => "png",
                    };
                    var path = Path.Combine(folder, "result." + ext);
                    if (kind == "bundle")
                    {
                        var recipe = Path.Combine(folder, "recipe.json");
                        var request = JsonSerializer.Deserialize<RenderRequest>(job.RequestJson)!;
                        await File.WriteAllBytesAsync(
                            recipe,
                            JsonSerializer.SerializeToUtf8Bytes(
                                new
                                {
                                    format = "flowvideo.render-recipe",
                                    schemaVersion = 1,
                                    job.Id,
                                    job.ProjectId,
                                    job.CreatedAt,
                                    job.Status,
                                    request,
                                },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                                {
                                    WriteIndented = true,
                                }
                            ),
                            token
                        );
                        var payload = new List<(string Name, string Path)>
                        {
                            ("video.mp4", source),
                            ("recipe.json", recipe),
                        };
                        if (File.Exists(storage.Subtitles(id)))
                            payload.Add(("subtitles.srt", storage.Subtitles(id)));
                        var checks = new List<object>();
                        foreach (var item in payload)
                        {
                            await using var input = File.OpenRead(item.Path);
                            checks.Add(
                                new
                                {
                                    name = item.Name,
                                    bytes = input.Length,
                                    sha256 = Convert.ToHexString(
                                        await SHA256.HashDataAsync(input, token)
                                    ),
                                }
                            );
                        }
                        var manifest = Path.Combine(folder, "manifest.json");
                        await File.WriteAllBytesAsync(
                            manifest,
                            JsonSerializer.SerializeToUtf8Bytes(
                                new
                                {
                                    format = "flowvideo.delivery",
                                    schemaVersion = 1,
                                    jobId = id,
                                    files = checks,
                                },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                                {
                                    WriteIndented = true,
                                }
                            ),
                            token
                        );
                        payload.Add(("manifest.json", manifest));
                        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
                            foreach (var item in payload)
                            {
                                var entry = zip.CreateEntry(
                                    item.Name,
                                    CompressionLevel.NoCompression
                                );
                                await using var output = entry.Open();
                                await using var input = File.OpenRead(item.Path);
                                await input.CopyToAsync(output, token);
                            }
                    }
                    else
                    {
                        var args = new List<string>
                        {
                            "-y",
                            "-nostdin",
                            "-hide_banner",
                            "-loglevel",
                            "error",
                        };
                        if (kind is "gif" or "poster" && start > 0)
                            args.AddRange(["-ss", N(start)]);
                        args.AddRange(["-i", source]);
                        if (kind is "wav" or "flac")
                            args.AddRange([
                                "-map",
                                "0:a:0",
                                "-vn",
                                "-ar",
                                "48000",
                                "-ac",
                                "2",
                                "-c:a",
                                kind == "wav" ? "pcm_s16le" : "flac",
                            ]);
                        else if (kind == "mute")
                            args.AddRange([
                                "-map",
                                "0:v:0",
                                "-an",
                                "-c:v",
                                "copy",
                                "-movflags",
                                "+faststart",
                            ]);
                        else if (kind == "gif")
                            args.AddRange([
                                "-t",
                                N(Math.Min(length, info.Duration - start)),
                                "-an",
                                "-vf",
                                "fps=10,scale=640:-2",
                                "-loop",
                                "0",
                            ]);
                        else if (kind == "poster")
                            args.AddRange([
                                "-map",
                                "0:v:0",
                                "-frames:v",
                                "1",
                                "-vf",
                                "scale=640:640:force_original_aspect_ratio=decrease",
                            ]);
                        else if (kind == "contact")
                            args.AddRange([
                                "-map",
                                "0:v:0",
                                "-vf",
                                $"fps={N(6 / info.Duration)},scale=320:-2,tile=3x2:nb_frames=6:padding=4:margin=4:color=black",
                                "-frames:v",
                                "1",
                            ]);
                        args.AddRange(["-threads", "2", path]);
                        await MediaProcess.Run("ffmpeg", args, folder, token);
                    }
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                        throw new IOException("No delivery output.");
                    if (new FileInfo(path).Length > 157286400)
                        return Results.Problem("File kết quả vượt 150 MiB.", statusCode: 413);
                    var mime = kind switch
                    {
                        "wav" => "audio/wav",
                        "flac" => "audio/flac",
                        "mute" => "video/mp4",
                        "gif" => "image/gif",
                        "bundle" => "application/zip",
                        _ => "image/png",
                    };
                    context.Response.RegisterForDispose(new TempDirectory(folder));
                    staged = true;
                    return Results.File(path, mime, $"flowvideo-{kind}-{id}.{ext}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem("Xuất file quá 60 giây.", statusCode: 504);
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    return Results.Problem(
                        "Không tạo được file. Kiểm tra video và track âm thanh.",
                        statusCode: 422
                    );
                }
                finally
                {
                    if (!staged)
                        Delete(folder);
                    Gate.Release();
                }
            }
        );
    }

    static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        catch (IOException) { }
    }

    sealed class TempDirectory(string folder) : IDisposable
    {
        public void Dispose() => Delete(folder);
    }
}
