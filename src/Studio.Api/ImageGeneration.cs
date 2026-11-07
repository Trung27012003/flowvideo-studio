using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class ImageGeneration
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    public int DocumentVersion { get; set; }
    public string Prompt { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Size { get; set; } = "";
    public string Quality { get; set; } = "low";
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "Đang chờ tạo ảnh";
    public Guid? AssetId { get; set; }
    public string? ProviderRequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record ImageRequest(Guid RequestId, Guid SceneId, int Version);

public sealed class ImageIntake
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public static class ImageFeatures
{
    public static int DailyLimit(IConfiguration c) =>
        int.TryParse(c["IMAGE_DAILY_LIMIT"], out var value) ? Math.Clamp(value, 1, 100) : 10;

    public static async Task Upgrade(StudioDb db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ImageGenerations" (
            "Id" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"SceneId" TEXT NOT NULL,"DocumentVersion" INTEGER NOT NULL,
            "Prompt" TEXT NOT NULL,"Fingerprint" TEXT NOT NULL,"Model" TEXT NOT NULL,"Size" TEXT NOT NULL,"Quality" TEXT NOT NULL,
            "Status" TEXT NOT NULL,"Message" TEXT NOT NULL,"AssetId" TEXT NULL,"ProviderRequestId" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,"UpdatedAt" TEXT NOT NULL,
            FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_ImageGenerations_ProjectId_CreatedAt" ON "ImageGenerations" ("ProjectId","CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ImageGenerations_Status_CreatedAt" ON "ImageGenerations" ("Status","CreatedAt");
            """
        );
        await db
            .ImageGenerations.Where(x => x.Status == "Running")
            .ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.Status, "Unknown")
                    .SetProperty(
                        x => x.Message,
                        "Ứng dụng bị dừng khi đang tạo ảnh. Dịch vụ có thể đã tính phí; yêu cầu không được tự gửi lại."
                    )
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow)
            );
    }

    public static void MapImages(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/image-generations",
            async (Guid id, StudioDb db, IConfiguration config, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var rows = await db
                    .ImageGenerations.AsNoTracking()
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
                var used = await db.ImageGenerations.CountAsync(x => x.CreatedAt >= since, ct);
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
            "/api/projects/{id:guid}/image-generations",
            async (
                Guid id,
                ImageRequest req,
                StudioDb db,
                ImageIntake intake,
                Storage store,
                IConfiguration config,
                CancellationToken ct
            ) =>
            {
                if (req.RequestId == Guid.Empty || req.SceneId == Guid.Empty || req.Version < 1)
                    return Results.BadRequest(
                        new { error = "Mã yêu cầu, cảnh hoặc phiên bản không hợp lệ." }
                    );
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                await intake.Gate.WaitAsync(ct);
                try
                {
                    var old = await db
                        .ImageGenerations.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == req.RequestId, ct);
                    if (old is not null)
                    {
                        if (
                            old.ProjectId != id
                            || old.SceneId != req.SceneId
                            || old.DocumentVersion != req.Version
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
                                error = "Storyboard đã thay đổi. Lưu hoặc tải lại bản mới trước khi tạo ảnh.",
                            }
                        );
                    var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var scene = doc.Scenes?.FirstOrDefault(s => s.Id == req.SceneId);
                    if (scene is null)
                        return Results.NotFound();
                    if (string.IsNullOrWhiteSpace(scene.Prompt) || scene.Prompt.Length > 4000)
                        return Results.BadRequest(
                            new
                            {
                                error = "Nhập prompt hình ảnh từ 1–4.000 ký tự cho cảnh rồi lưu.",
                            }
                        );
                    var model = config["OPENAI_IMAGE_MODEL"] ?? "gpt-image-1";
                    var size = doc.Ratio == "16:9" ? "1536x1024" : "1024x1536";
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
                                        quality = "low",
                                    }
                                )
                            )
                        )
                    );
                    var cached = await db
                        .ImageGenerations.AsNoTracking()
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
                        await db.ImageGenerations.AnyAsync(
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
                    if (string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]))
                        return Results.Problem(
                            "Chưa cấu hình OPENAI_API_KEY ở backend.",
                            statusCode: 503
                        );
                    var since = DateTime.UtcNow.AddDays(-1);
                    if (
                        await db.ImageGenerations.CountAsync(x => x.CreatedAt >= since, ct)
                        >= DailyLimit(config)
                    )
                        return Results.Problem(
                            "Đã đạt giới hạn yêu cầu ảnh trong 24 giờ. Các lượt lỗi/không rõ kết quả cũng được tính.",
                            statusCode: 429
                        );
                    var row = new ImageGeneration
                    {
                        Id = req.RequestId,
                        ProjectId = id,
                        SceneId = req.SceneId,
                        DocumentVersion = req.Version,
                        Prompt = scene.Prompt.Trim(),
                        Fingerprint = fingerprint,
                        Model = model,
                        Size = size,
                    };
                    db.ImageGenerations.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted(
                        $"/api/projects/{id}/image-generations",
                        new { generation = View(row), reused = false }
                    );
                }
                finally
                {
                    intake.Gate.Release();
                }
            }
        );
    }

    private static async Task Touch(ImageGeneration row, StudioDb db, CancellationToken ct)
    {
        row.UpdatedAt = DateTime.UtcNow;
        await db
            .ImageGenerations.Where(x => x.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, row.UpdatedAt), ct);
    }

    private static object View(ImageGeneration row) =>
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
            row.CreatedAt,
            row.UpdatedAt,
        };
}
