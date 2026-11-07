using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class VideoGeneration
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    public int DocumentVersion { get; set; }
    public string Prompt { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Size { get; set; } = "";
    public string Quality { get; set; } = "720p";
    public int Seconds { get; set; } = 5;
    public Guid? ReferenceId { get; set; }
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "Đang chờ tạo video";
    public Guid? AssetId { get; set; }
    public string? ProviderRequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record VideoRequest(
    Guid RequestId,
    Guid SceneId,
    int Version,
    int Seconds = 5,
    string Resolution = "720p",
    Guid? ReferenceId = null
);

public sealed class VideoIntake
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public static class VideoFeatures
{
    public static int DailyLimit(IConfiguration c) =>
        int.TryParse(c["VIDEO_DAILY_LIMIT"], out var value) ? Math.Clamp(value, 1, 100) : 5;

    public static async Task Upgrade(StudioDb db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "VideoGenerations" (
            "Id" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"SceneId" TEXT NOT NULL,"DocumentVersion" INTEGER NOT NULL,
            "Prompt" TEXT NOT NULL,"Fingerprint" TEXT NOT NULL,"Model" TEXT NOT NULL,"Size" TEXT NOT NULL,"Quality" TEXT NOT NULL,
            "Status" TEXT NOT NULL,"Message" TEXT NOT NULL,"AssetId" TEXT NULL,"ProviderRequestId" TEXT NULL,
            "Seconds" INTEGER NOT NULL DEFAULT 5,"ReferenceId" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,"UpdatedAt" TEXT NOT NULL,
            FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_VideoGenerations_ProjectId_CreatedAt" ON "VideoGenerations" ("ProjectId","CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_VideoGenerations_Status_CreatedAt" ON "VideoGenerations" ("Status","CreatedAt");
            """
        );
        await db
            .VideoGenerations.Where(x => x.Status == "Running")
            .ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.Status, "Unknown")
                    .SetProperty(
                        x => x.Message,
                        "Ứng dụng bị dừng khi đang tạo video. Dịch vụ có thể đã tính phí; yêu cầu không được tự gửi lại."
                    )
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow)
            );
    }

    public static void MapVideos(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/video-generations",
            async (Guid id, StudioDb db, IConfiguration config, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var rows = await db
                    .VideoGenerations.AsNoTracking()
                    .Where(x => x.ProjectId == id)
                    .OrderByDescending(x => x.UpdatedAt)
                    .ThenByDescending(x => x.CreatedAt)
                    .Take(50)
                    .ToListAsync(ct);
                var assetIds = rows.Where(r => r.AssetId.HasValue)
                    .Select(r => r.AssetId!.Value)
                    .ToArray();
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id && assetIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, ct);
                var since = DateTime.UtcNow.AddDays(-1);
                var used = await db.VideoGenerations.CountAsync(x => x.CreatedAt >= since, ct);
                return Results.Ok(
                    new
                    {
                        dailyLimit = DailyLimit(config),
                        used,
                        items = rows.Select(row => new
                        {
                            generation = View(row),
                            asset = row.AssetId is Guid aid ? assets.GetValueOrDefault(aid) : null,
                        }),
                    }
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/video-generations",
            async (
                Guid id,
                VideoRequest req,
                StudioDb db,
                VideoIntake intake,
                Storage store,
                IConfiguration config,
                CancellationToken ct
            ) =>
            {
                if (
                    req.RequestId == Guid.Empty
                    || req.SceneId == Guid.Empty
                    || req.Version < 1
                    || req.Seconds is < 3 or > 15
                    || req.Resolution is not ("360p" or "720p" or "1080p")
                )
                    return Results.BadRequest(
                        new { error = "Mã yêu cầu, cảnh hoặc phiên bản không hợp lệ." }
                    );
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                await intake.Gate.WaitAsync(ct);
                try
                {
                    var old = await db
                        .VideoGenerations.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == req.RequestId, ct);
                    if (old is not null)
                    {
                        if (
                            old.ProjectId != id
                            || old.SceneId != req.SceneId
                            || old.DocumentVersion != req.Version
                            || old.Seconds != req.Seconds
                            || old.Quality != req.Resolution
                            || old.ReferenceId != req.ReferenceId
                        )
                            return Results.Conflict(
                                new { error = "Mã yêu cầu đã dùng cho nội dung khác." }
                            );
                        await Touch(old, db, ct);
                        return Results.Ok(new { generation = View(old), reused = true });
                    }
                    var saved = await db
                        .ProjectDocuments.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.ProjectId == id, ct);
                    if (saved is null || saved.Version != req.Version)
                        return Results.Conflict(
                            new
                            {
                                error = "Storyboard đã thay đổi. Lưu hoặc tải lại bản mới trước khi tạo video.",
                            }
                        );
                    var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var scene = doc.Scenes?.FirstOrDefault(s => s.Id == req.SceneId);
                    if (scene is null)
                        return Results.NotFound();
                    if (string.IsNullOrWhiteSpace(scene.Prompt) || scene.Prompt.Length > 4000)
                        return Results.BadRequest(
                            new { error = "Nhập prompt video từ 1–4.000 ký tự cho cảnh rồi lưu." }
                        );
                    var model = config["GEMINI_VIDEO_MODEL"] ?? "gemini-omni-1.2-flash";
                    if (doc.Ratio is not ("9:16" or "16:9"))
                        return Results.BadRequest(
                            new { error = "Tạo video hỗ trợ tỉ lệ 9:16 hoặc 16:9." }
                        );
                    if (req.ReferenceId is Guid referenceId)
                    {
                        var reference = await db
                            .Assets.AsNoTracking()
                            .FirstOrDefaultAsync(
                                a => a.Id == referenceId && a.ProjectId == id && a.Kind == "image",
                                ct
                            );
                        if (reference is null || !File.Exists(store.AssetPath(reference)))
                            return Results.BadRequest(
                                new { error = "Ảnh tham chiếu phải thuộc dự án và còn file." }
                            );
                        if (new FileInfo(store.AssetPath(reference)).Length > 10 * 1024 * 1024)
                            return Results.BadRequest(
                                new { error = "Ảnh tham chiếu tối đa 10 MiB." }
                            );
                    }
                    var size = doc.Ratio;
                    var fingerprint = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        sceneId = req.SceneId,
                                        prompt = scene.Prompt.Trim(),
                                        model,
                                        size,
                                        quality = req.Resolution,
                                        seconds = req.Seconds,
                                        referenceId = req.ReferenceId,
                                    }
                                )
                            )
                        )
                    );
                    var cached = await db
                        .VideoGenerations.AsNoTracking()
                        .Where(x =>
                            x.ProjectId == id
                            && x.Fingerprint == fingerprint
                            && x.Status == "Succeeded"
                        )
                        .OrderByDescending(x => x.CreatedAt)
                        .FirstOrDefaultAsync(ct);
                    if (cached?.AssetId is Guid cachedId)
                    {
                        var cachedAsset = await db
                            .Assets.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == cachedId && x.ProjectId == id, ct);
                        if (cachedAsset is not null && File.Exists(store.AssetPath(cachedAsset)))
                        {
                            await Touch(cached, db, ct);
                            return Results.Ok(new { generation = View(cached), reused = true });
                        }
                    }
                    if (
                        await db.VideoGenerations.AnyAsync(
                            x =>
                                x.ProjectId == id
                                && x.SceneId == req.SceneId
                                && (x.Status == "Queued" || x.Status == "Running"),
                            ct
                        )
                    )
                        return Results.Conflict(
                            new
                            {
                                error = "Cảnh này đã có yêu cầu đang xử lý. Theo dõi kết quả trước khi tạo thêm.",
                            }
                        );
                    if (string.IsNullOrWhiteSpace(config["GEMINI_API_KEY"]))
                        return Results.Problem(
                            "Chưa cấu hình GEMINI_API_KEY ở backend.",
                            statusCode: 503
                        );
                    var since = DateTime.UtcNow.AddDays(-1);
                    if (
                        await db.VideoGenerations.CountAsync(x => x.CreatedAt >= since, ct)
                        >= DailyLimit(config)
                    )
                        return Results.Problem(
                            "Đã đạt giới hạn yêu cầu video trong 24 giờ. Các lượt lỗi/không rõ kết quả cũng được tính.",
                            statusCode: 429
                        );
                    var row = new VideoGeneration
                    {
                        Id = req.RequestId,
                        ProjectId = id,
                        SceneId = req.SceneId,
                        DocumentVersion = req.Version,
                        Prompt = scene.Prompt.Trim(),
                        Fingerprint = fingerprint,
                        Model = model,
                        Size = size,
                        Quality = req.Resolution,
                        Seconds = req.Seconds,
                        ReferenceId = req.ReferenceId,
                    };
                    db.VideoGenerations.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted(
                        $"/api/projects/{id}/video-generations",
                        new { generation = View(row), reused = false }
                    );
                }
                finally
                {
                    intake.Gate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/video-generations/{generationId:guid}/cancel",
            async (
                Guid id,
                Guid generationId,
                StudioDb db,
                VideoIntake intake,
                CancellationToken ct
            ) =>
            {
                await intake.Gate.WaitAsync(ct);
                try
                {
                    var row = await db
                        .VideoGenerations.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == generationId && x.ProjectId == id, ct);
                    if (row is null)
                        return Results.NotFound();
                    var changed = await db
                        .VideoGenerations.Where(x => x.Id == generationId && x.Status == "Queued")
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.Status, "Canceled")
                                    .SetProperty(x => x.Message, "Đã hủy trước khi gửi tới dịch vụ")
                                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                            ct
                        );
                    return changed == 1
                        ? Results.Ok(new { canceled = true })
                        : Results.Conflict(
                            new { error = "Chỉ hủy được lượt chưa gửi tới dịch vụ." }
                        );
                }
                finally
                {
                    intake.Gate.Release();
                }
            }
        );
    }

    private static async Task Touch(VideoGeneration row, StudioDb db, CancellationToken ct)
    {
        row.UpdatedAt = DateTime.UtcNow;
        await db
            .VideoGenerations.Where(x => x.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, row.UpdatedAt), ct);
    }

    private static object View(VideoGeneration row) =>
        new
        {
            row.Id,
            row.Prompt,
            row.SceneId,
            row.DocumentVersion,
            row.Status,
            row.Message,
            row.AssetId,
            row.Model,
            row.Size,
            row.Quality,
            row.Seconds,
            row.ReferenceId,
            row.CreatedAt,
            row.UpdatedAt,
        };
}
