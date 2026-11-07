using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class ProjectDocument
{
    public Guid ProjectId { get; set; }
    public int Version { get; set; }
    public string ContentJson { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public record StoryScene(
    Guid Id,
    string Title,
    string Narration,
    string Prompt,
    Guid? AssetId,
    double Duration = 5,
    double Start = 0
);

public record StoryDocument(
    string Idea = "",
    string Script = "",
    StoryScene[]? Scenes = null,
    string Ratio = "9:16",
    Guid? VoiceId = null,
    Guid? MusicId = null,
    bool AutoCaptions = true
);

public record SaveDocument(int Version, StoryDocument Document);

public record RenderDocument(int Version);

public static class Storyboard
{
    // Additive upgrade for databases created before v0.4; no existing table is rewritten.
    public static Task Upgrade(StudioDb db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ProjectDocuments" (
            "ProjectId" TEXT NOT NULL CONSTRAINT "PK_ProjectDocuments" PRIMARY KEY,
            "Version" INTEGER NOT NULL, "ContentJson" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_ProjectDocuments_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE);
            """
        );

    public static void MapStoryboard(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/document",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                var stored = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id, ct);
                return Results.Ok(
                    new
                    {
                        version = stored?.Version ?? 0,
                        document = stored is null
                            ? new StoryDocument(Scenes: [])
                            : JsonSerializer.Deserialize<StoryDocument>(stored.ContentJson),
                    }
                );
            }
        );
        app.MapPut(
            "/api/projects/{id:guid}/document",
            async (Guid id, SaveDocument req, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                if (req.Version < 0 || req.Version == int.MaxValue || req.Document is null)
                    return Results.BadRequest(
                        new { error = "Phiên bản hoặc nội dung không hợp lệ." }
                    );
                var error = await Validate(id, req.Document, db, false, ct);
                if (error is not null)
                    return Results.BadRequest(new { error });
                var json = JsonSerializer.Serialize(req.Document);
                var version = req.Version + 1;
                var now = DateTime.UtcNow;
                if (req.Version == 0)
                {
                    db.ProjectDocuments.Add(
                        new()
                        {
                            ProjectId = id,
                            Version = version,
                            ContentJson = json,
                            UpdatedAt = now,
                        }
                    );
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException e)
                        when (e.InnerException
                                is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
                        )
                    {
                        return Conflict();
                    }
                }
                else
                {
                    var updated = await db
                        .ProjectDocuments.Where(x => x.ProjectId == id && x.Version == req.Version)
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.ContentJson, json)
                                    .SetProperty(x => x.Version, version)
                                    .SetProperty(x => x.UpdatedAt, now),
                            ct
                        );
                    if (updated == 0)
                        return Conflict();
                }
                return Results.Ok(new { version, updatedAt = now });
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/storyboard/render",
            async (Guid id, RenderDocument req, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id, ct);
                if (saved is null || saved.Version != req.Version)
                    return Conflict();
                var document = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                var error = await Validate(id, document, db, true, ct);
                if (error is not null)
                    return Results.BadRequest(new { error });
                var clips = document
                    .Scenes!.Select(s => new SceneClip(
                        s.AssetId!.Value,
                        s.Duration,
                        s.Start,
                        s.Narration
                    ))
                    .ToArray();
                var render = new RenderRequest(
                    clips.Select(c => c.AssetId).ToArray(),
                    Duration: (int)Math.Ceiling(clips.Sum(s => s.Duration)),
                    VoiceId: document.VoiceId,
                    MusicId: document.MusicId,
                    Ratio: document.Ratio,
                    Scenes: clips,
                    AutoCaptions: document.AutoCaptions
                );
                var job = new RenderJob
                {
                    ProjectId = id,
                    RequestJson = JsonSerializer.Serialize(render),
                };
                db.Jobs.Add(job);
                await db.SaveChangesAsync(ct);
                return Results.Accepted(
                    $"/api/jobs/{job.Id}",
                    new
                    {
                        job.Id,
                        job.Status,
                        documentVersion = saved.Version,
                    }
                );
            }
        );
    }

    private static IResult Conflict() =>
        Results.Conflict(
            new
            {
                error = "Dự án đã thay đổi ở phiên khác. Sao chép nội dung đang sửa rồi tải lại bản đã lưu trước khi tiếp tục.",
            }
        );

    private static async Task<string?> Validate(
        Guid projectId,
        StoryDocument doc,
        StudioDb db,
        bool render,
        CancellationToken ct
    )
    {
        if (
            doc.Idea is null
            || doc.Idea.Length > 4000
            || doc.Script is null
            || doc.Script.Length > 50000
            || doc.Ratio is not ("9:16" or "16:9")
            || doc.Scenes is null
            || doc.Scenes.Length > 30
        )
            return "Kịch bản/ý tưởng quá dài, tỉ lệ sai hoặc quá 30 cảnh.";
        if (render && doc.Scenes.Length == 0)
            return "Thêm ít nhất một cảnh trước khi dựng.";
        if (
            doc.Scenes.Any(s =>
                s is null
                || s.Id == Guid.Empty
                || s.Title is null
                || s.Title.Length > 120
                || s.Narration is null
                || s.Narration.Length > 2000
                || s.Narration.Contains('\0')
                || s.Prompt is null
                || s.Prompt.Length > 4000
                || !double.IsFinite(s.Duration)
                || s.Duration < .1
                || s.Duration > 600
                || !double.IsFinite(s.Start)
                || s.Start < 0
            )
            || doc.Scenes.Select(s => s.Id).Distinct().Count() != doc.Scenes.Length
            || doc.Scenes.Sum(s => s.Duration) > 600
        )
            return "Cảnh không hợp lệ: tối đa 30 cảnh, tổng 600 giây, thời lượng mỗi cảnh từ 0,1 giây và điểm vào không âm.";
        if (render && doc.Scenes.Any(s => s.AssetId is null))
            return "Chọn ảnh/video cho tất cả các cảnh trước khi dựng.";
        var ids = doc
            .Scenes.Where(s => s.AssetId.HasValue)
            .Select(s => s.AssetId!.Value)
            .Concat(new[] { doc.VoiceId, doc.MusicId }.OfType<Guid>())
            .Distinct()
            .ToArray();
        var assets = await db
            .Assets.AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        if (
            doc.Scenes.Any(s =>
                s.AssetId is Guid id
                && (
                    !assets.TryGetValue(id, out var a)
                    || a.Kind is not ("image" or "video")
                    || (a.Kind == "image" && s.Start != 0)
                )
            )
            || new[] { doc.VoiceId, doc.MusicId }
                .OfType<Guid>()
                .Any(id => !assets.TryGetValue(id, out var a) || a.Kind != "audio")
        )
            return "Tư liệu không thuộc dự án, sai loại hoặc ảnh có điểm vào khác 0.";
        return null;
    }
}
