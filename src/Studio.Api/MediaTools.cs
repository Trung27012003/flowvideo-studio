using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public record FrameRequest(double At = 0);

public static class MediaTools
{
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private static string N(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);

    public static void MapMediaTools(this WebApplication app)
    {
        app.MapGet(
            "/api/assets/{id:guid}/thumbnail",
            async (Guid id, double? at, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
                if (asset is null || !File.Exists(store.AssetPath(asset)))
                    return Results.NotFound();
                if (asset.Kind is not ("image" or "video"))
                    return Results.BadRequest(new { error = "Chọn ảnh hoặc video." });
                return await Process(asset, "thumbnail", at ?? 0, db, store, ct);
            }
        );
        app.MapGet(
            "/api/assets/{id:guid}/waveform",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
                if (asset is null || !File.Exists(store.AssetPath(asset)))
                    return Results.NotFound();
                if (asset.Kind is not ("audio" or "video"))
                    return Results.BadRequest(new { error = "Chọn audio/video có âm thanh." });
                return await Process(asset, "waveform", 0, db, store, ct);
            }
        );
        app.MapPost(
            "/api/assets/{id:guid}/extract-audio",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
                if (asset is null || !File.Exists(store.AssetPath(asset)))
                    return Results.NotFound();
                if (asset.Kind != "video")
                    return Results.BadRequest(new { error = "Chọn video có âm thanh." });
                return await Process(asset, "audio", 0, db, store, ct);
            }
        );
        app.MapPost(
            "/api/assets/{id:guid}/frame",
            async (
                Guid id,
                FrameRequest request,
                StudioDb db,
                Storage store,
                CancellationToken ct
            ) =>
            {
                var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
                if (asset is null || !File.Exists(store.AssetPath(asset)))
                    return Results.NotFound();
                if (asset.Kind != "video")
                    return Results.BadRequest(new { error = "Chọn video để lấy khung hình." });
                return await Process(asset, "frame", request.At, db, store, ct);
            }
        );
        app.MapGet(
            "/api/jobs/{id:guid}/recipe",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null)
                    return Results.NotFound();
                var request = JsonSerializer.Deserialize<RenderRequest>(job.RequestJson)!;
                return Results.File(
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
                    "application/json",
                    $"render-{id}.json"
                );
            }
        );
        app.MapGet(
            "/api/jobs/{id:guid}/audio",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var job = await db
                    .Jobs.AsNoTracking()
                    .FirstOrDefaultAsync(j => j.Id == id && j.Status == "Succeeded", ct);
                if (job is null || !File.Exists(store.Output(id)))
                    return Results.NotFound();
                return await Process(
                    new Asset
                    {
                        Id = id,
                        ProjectId = job.ProjectId,
                        Name = "Bản xuất",
                        Kind = "video",
                        StoredName = "",
                    },
                    "download-audio",
                    0,
                    db,
                    store,
                    ct,
                    store.Output(id)
                );
            }
        );
    }

    private static async Task<IResult> Process(
        Asset asset,
        string action,
        double at,
        StudioDb db,
        Storage store,
        CancellationToken ct,
        string? source = null
    )
    {
        if (!double.IsFinite(at) || at < 0 || at > 600)
            return Results.BadRequest(new { error = "Mốc lấy ảnh từ 0–600 giây." });
        if (!await Gate.WaitAsync(0, ct))
            return Results.Problem("Đang xử lý hai công cụ media. Thử lại sau.", statusCode: 429);
        var folder = Path.Combine(store.Root, "temp", "media-" + Guid.NewGuid().ToString("N"));
        string? committedPath = null;
        bool committed = false;
        try
        {
            Directory.CreateDirectory(folder);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var token = timeout.Token;
            source ??= store.AssetPath(asset);
            var audio = action is "audio" or "download-audio" or "waveform";
            var duration =
                asset.Kind == "image" ? 0
                : audio ? await MediaProcess.AudioDuration(source, token)
                : await MediaProcess.Duration(source, token);
            if (duration > 600.1)
                return Results.BadRequest(
                    new { error = "Công cụ media hỗ trợ nguồn tối đa 10 phút." }
                );
            if (asset.Kind != "image" && !audio && at >= duration)
                return Results.BadRequest(new { error = "Mốc lấy ảnh phải nằm trong video." });
            var args = new List<string> { "-y", "-nostdin", "-hide_banner", "-loglevel", "error" };
            if (!audio && at > 0)
                args.AddRange(["-ss", N(at)]);
            args.AddRange(["-i", source]);
            var ext = action is "audio" or "download-audio" ? ".mp3" : ".png";
            var path = Path.Combine(folder, "result" + ext);
            if (action == "waveform")
                args.AddRange([
                    "-filter_complex",
                    "[0:a:0]aformat=channel_layouts=mono,showwavespic=s=1000x200:colors=0x4be0c0[v]",
                    "-map",
                    "[v]",
                    "-frames:v",
                    "1",
                ]);
            else if (audio)
                args.AddRange([
                    "-map",
                    "0:a:0",
                    "-vn",
                    "-t",
                    "600",
                    "-ac",
                    "2",
                    "-ar",
                    "48000",
                    "-c:a",
                    "libmp3lame",
                    "-b:a",
                    "128k",
                ]);
            else
                args.AddRange([
                    "-map",
                    "0:v:0",
                    "-frames:v",
                    "1",
                    "-vf",
                    "scale=640:640:force_original_aspect_ratio=decrease",
                ]);
            args.AddRange(["-threads", "2", path]);
            await MediaProcess.Run("ffmpeg", args, folder, token);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("No output.");
            if (action is "audio" or "frame")
            {
                var derived = new Asset
                {
                    ProjectId = asset.ProjectId,
                    Kind = audio ? "audio" : "image",
                    Name =
                        Path.GetFileNameWithoutExtension(asset.Name)[
                            ..Math.Min(Path.GetFileNameWithoutExtension(asset.Name).Length, 100)
                        ]
                        + (audio ? " · audio" : " · khung " + N(at))
                        + ext,
                    StoredName = Guid.NewGuid().ToString("N") + ext,
                };
                committedPath = store.AssetPath(derived);
                File.Move(path, committedPath);
                db.Assets.Add(derived);
                await db.SaveChangesAsync(token);
                committed = true;
                return Results.Ok(derived);
            }
            var bytes = await File.ReadAllBytesAsync(path, token);
            return Results.File(
                bytes,
                ext == ".mp3" ? "audio/mpeg" : "image/png",
                $"flowvideo-{action}-{asset.Id}{ext}"
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem("Xử lý media quá thời gian chờ.", statusCode: 504);
        }
        catch (IOException)
        {
            return Results.Problem(
                "Không xử lý được nguồn. Kiểm tra file/track âm thanh và mốc thời gian.",
                statusCode: 422
            );
        }
        finally
        {
            if (!committed && committedPath is not null)
                File.Delete(committedPath);
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (IOException) { }
            Gate.Release();
        }
    }
}
