using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class StoryPlan
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int DocumentVersion { get; set; }
    public string RequestJson { get; set; } = "";
    public string SourceText { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Origin { get; set; } = "ai";
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "Đang chờ phân cảnh";
    public string? OriginalJson { get; set; }
    public string? DraftJson { get; set; }
    public int Revision { get; set; }
    public string? ProviderRequestId { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record StoryPlanRequest(
    Guid RequestId,
    int Version,
    string Source = "idea",
    int SceneCount = 5,
    double Duration = 30,
    string Genre = "education",
    string Structure = "hook",
    string Language = "vi",
    string Style = "",
    int Variant = 0
);

public record PlannedScene(
    string Title,
    string Narration,
    string Prompt,
    double Duration,
    SceneDirection Direction
);

public record StoryProposal(
    string Title,
    string Audience,
    string Message,
    string Hook,
    string Ending,
    PlannedScene[] Scenes
);

public record ImportStoryPlan(string Format, int SchemaVersion, StoryProposal Proposal);

public record EditStoryPlan(int Revision, StoryProposal Proposal);

public record ApplyStoryPlan(int Revision, int Version);

public sealed class StoryPlanGate
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public static class StoryPlans
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static int Limit(IConfiguration c) =>
        int.TryParse(c["STORY_DAILY_LIMIT"], out var n) ? Math.Clamp(n, 1, 100) : 10;

    public static async Task Upgrade(StudioDb db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "StoryPlans" (
            "Id" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"DocumentVersion" INTEGER NOT NULL,
            "RequestJson" TEXT NOT NULL,"SourceText" TEXT NOT NULL,"Fingerprint" TEXT NOT NULL,"Model" TEXT NOT NULL,
            "Origin" TEXT NOT NULL DEFAULT 'ai',"Status" TEXT NOT NULL,"Message" TEXT NOT NULL,"OriginalJson" TEXT NULL,"DraftJson" TEXT NULL,"Revision" INTEGER NOT NULL,
            "ProviderRequestId" TEXT NULL,"InputTokens" INTEGER NULL,"OutputTokens" INTEGER NULL,
            "CreatedAt" TEXT NOT NULL,"UpdatedAt" TEXT NOT NULL,
            FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_StoryPlans_ProjectId_CreatedAt" ON "StoryPlans"("ProjectId","CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_StoryPlans_Status_CreatedAt" ON "StoryPlans"("Status","CreatedAt");
            """
        );
        await db
            .StoryPlans.Where(x => x.Status == "Running")
            .ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.Status, "Unknown")
                    .SetProperty(
                        x => x.Message,
                        "Ứng dụng dừng khi phân cảnh. Dịch vụ có thể đã tính phí; không tự gửi lại."
                    )
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow)
            );
    }

    public static string? Validate(StoryProposal? p)
    {
        if (
            p is null
            || string.IsNullOrWhiteSpace(p.Title)
            || p.Title.Length is < 1 or > 120
            || new[] { p.Audience, p.Message, p.Hook, p.Ending }.Any(s =>
                s is null || s.Length > 1000 || s.Contains('\0')
            )
            || p.Scenes is null
            || p.Scenes.Length is < 1 or > 10
        )
            return "Bản phân cảnh cần tiêu đề 1–120 ký tự, mô tả tối đa 1.000 ký tự và 1–10 cảnh.";
        if (
            p.Title.Contains('\0')
            || p.Scenes.Any(s =>
                s is null
                || string.IsNullOrWhiteSpace(s.Title)
                || s.Title.Length > 120
                || s.Narration is null
                || s.Narration.Length > 2000
                || string.IsNullOrWhiteSpace(s.Prompt)
                || s.Prompt.Length > 4000
                || s.Narration.Contains('\0')
                || s.Prompt.Contains('\0')
                || !double.IsFinite(s.Duration)
                || s.Duration is < .1 or > 600
                || s.Direction is null
                || !s.Direction.Valid()
                || SceneDirection
                    .VisualPrompt(
                        new(
                            Guid.Empty,
                            s.Title,
                            s.Narration,
                            s.Prompt,
                            null,
                            s.Duration,
                            Direction: s.Direction
                        )
                    )
                    .Length > 4000
            )
            || p.Scenes.Sum(s => s.Duration) > 600
        )
            return "Cảnh có dữ liệu không hợp lệ, prompt cuối vượt 4.000 ký tự hoặc tổng thời lượng vượt 600 giây.";
        return null;
    }

    public static object View(StoryPlan row) =>
        new
        {
            row.Id,
            row.ProjectId,
            row.DocumentVersion,
            row.Model,
            row.Origin,
            row.Status,
            row.Message,
            row.Revision,
            row.InputTokens,
            row.OutputTokens,
            row.CreatedAt,
            row.UpdatedAt,
            options = JsonSerializer.Deserialize<StoryPlanRequest>(row.RequestJson),
            proposal = row.DraftJson is null
                ? null
                : JsonSerializer.Deserialize<StoryProposal>(row.DraftJson, Web),
        };

    public static void MapStoryPlans(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/story-plans",
            async (Guid id, StudioDb db, IConfiguration c, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var rows = await db
                    .StoryPlans.AsNoTracking()
                    .Where(x => x.ProjectId == id)
                    .OrderByDescending(x => x.UpdatedAt)
                    .Take(20)
                    .ToListAsync(ct);
                var since = DateTime.UtcNow.AddDays(-1);
                return Results.Ok(
                    new
                    {
                        items = rows.Select(View),
                        dailyLimit = Limit(c),
                        used = await db.StoryPlans.CountAsync(
                            x => x.CreatedAt >= since && x.Origin == "ai",
                            ct
                        ),
                    }
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/story-plans",
            async (
                Guid id,
                StoryPlanRequest req,
                StudioDb db,
                StoryPlanGate gate,
                IConfiguration c,
                CancellationToken ct
            ) =>
            {
                if (
                    req.RequestId == Guid.Empty
                    || req.Version < 1
                    || req.Source is not ("idea" or "script")
                    || req.SceneCount is < 1 or > 10
                    || !double.IsFinite(req.Duration)
                    || req.Duration is < 1 or > 600
                    || req.Genre is not ("education" or "story" or "ad" or "documentary")
                    || req.Structure is not ("hook" or "three-act" or "aida")
                    || req.Language is not ("vi" or "en")
                    || req.Style is null
                    || req.Style.Length > 300
                    || req.Style.Contains('\0')
                    || req.Variant is < 0 or > 100
                )
                    return Results.BadRequest(new { error = "Cấu hình phân cảnh không hợp lệ." });
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var old = await db
                        .StoryPlans.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == req.RequestId, ct);
                    if (old is not null)
                        return
                            old.ProjectId == id && old.RequestJson == JsonSerializer.Serialize(req)
                            ? Results.Ok(new { plan = View(old), reused = true })
                            : Results.Conflict(
                                new { error = "Mã yêu cầu đã dùng cho cấu hình khác." }
                            );
                    var saved = await db
                        .ProjectDocuments.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.ProjectId == id, ct);
                    if (saved is null || saved.Version != req.Version)
                        return Results.Conflict(
                            new { error = "Lưu hoặc tải lại storyboard mới trước khi phân cảnh." }
                        );
                    var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var source = (req.Source == "idea" ? doc.Idea : doc.Script).Trim();
                    if (source.Length is < 1 or > 12000 || source.Contains('\0'))
                        return Results.BadRequest(
                            new { error = "Nội dung nguồn cần 1–12.000 ký tự." }
                        );
                    var model = c["OPENAI_STORY_MODEL"] ?? c["OPENAI_MODEL"] ?? "gpt-4o-mini";
                    var fingerprint = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        sourceText = source,
                                        model,
                                        sourceKind = req.Source,
                                        req.SceneCount,
                                        req.Duration,
                                        req.Genre,
                                        req.Structure,
                                        req.Language,
                                        req.Style,
                                        req.Variant,
                                    }
                                )
                            )
                        )
                    );
                    var cached = await db
                        .StoryPlans.AsNoTracking()
                        .Where(x =>
                            x.ProjectId == id
                            && x.Fingerprint == fingerprint
                            && x.Status == "Succeeded"
                        )
                        .OrderByDescending(x => x.UpdatedAt)
                        .FirstOrDefaultAsync(ct);
                    if (cached is not null)
                        return Results.Ok(new { plan = View(cached), reused = true });
                    if (
                        await db.StoryPlans.AnyAsync(
                            x =>
                                x.ProjectId == id
                                && (x.Status == "Queued" || x.Status == "Running"),
                            ct
                        )
                    )
                        return Results.Conflict(
                            new { error = "Dự án đã có yêu cầu phân cảnh đang xử lý." }
                        );
                    if (string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]))
                        return Results.Json(
                            new { error = "Cần OPENAI_API_KEY ở backend." },
                            statusCode: 503
                        );
                    var since = DateTime.UtcNow.AddDays(-1);
                    if (
                        await db.StoryPlans.CountAsync(
                            x => x.CreatedAt >= since && x.Origin == "ai",
                            ct
                        ) >= Limit(c)
                    )
                        return Results.Json(
                            new
                            {
                                error = "Đã hết hạn mức phân cảnh trong 24 giờ. Không gửi thêm AI.",
                            },
                            statusCode: 429
                        );
                    var row = new StoryPlan
                    {
                        Id = req.RequestId,
                        ProjectId = id,
                        DocumentVersion = req.Version,
                        RequestJson = JsonSerializer.Serialize(req),
                        SourceText = source,
                        Fingerprint = fingerprint,
                        Model = model,
                    };
                    db.StoryPlans.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted(
                        $"/api/projects/{id}/story-plans",
                        new { plan = View(row), reused = false }
                    );
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/story-plans/import",
            async (
                Guid id,
                ImportStoryPlan req,
                StudioDb db,
                StoryPlanGate gate,
                CancellationToken ct
            ) =>
            {
                if (req.Format != "flowvideo.story-plan" || req.SchemaVersion != 1)
                    return Results.BadRequest(
                        new { error = "Định dạng phân cảnh không được hỗ trợ." }
                    );
                var error = Validate(req.Proposal);
                if (error is not null)
                    return Results.BadRequest(new { error });
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var version = await db
                        .ProjectDocuments.Where(x => x.ProjectId == id)
                        .Select(x => x.Version)
                        .FirstOrDefaultAsync(ct);
                    var key = Guid.NewGuid();
                    var json = JsonSerializer.Serialize(req.Proposal, Web);
                    var row = new StoryPlan
                    {
                        Id = key,
                        ProjectId = id,
                        DocumentVersion = version,
                        Origin = "import",
                        Model = "manual-import",
                        RequestJson = JsonSerializer.Serialize(
                            new StoryPlanRequest(
                                key,
                                version,
                                Source: "script",
                                SceneCount: req.Proposal.Scenes.Length,
                                Duration: req.Proposal.Scenes.Sum(s => s.Duration)
                            )
                        ),
                        Status = "Succeeded",
                        Message = "Đã nhập phân cảnh JSON; chưa áp dụng hoặc gọi AI.",
                        OriginalJson = json,
                        DraftJson = json,
                        Revision = 1,
                    };
                    db.StoryPlans.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Created(
                        $"/api/projects/{id}/story-plans/{key}",
                        new { plan = View(row) }
                    );
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/story-plans/{planId:guid}",
            async (Guid id, Guid planId, StudioDb db, CancellationToken ct) =>
            {
                var row = await db
                    .StoryPlans.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id && x.Id == planId, ct);
                return row is null
                    ? Results.NotFound()
                    : Results.Ok(
                        new
                        {
                            format = "flowvideo.story-plan",
                            schemaVersion = 1,
                            plan = View(row),
                            original = row.OriginalJson is null
                                ? null
                                : JsonSerializer.Deserialize<StoryProposal>(row.OriginalJson, Web),
                            source = row.SourceText,
                        }
                    );
            }
        );
        app.MapPut(
            "/api/projects/{id:guid}/story-plans/{planId:guid}",
            async (
                Guid id,
                Guid planId,
                EditStoryPlan req,
                StudioDb db,
                StoryPlanGate gate,
                CancellationToken ct
            ) =>
            {
                var error = Validate(req.Proposal);
                if (error is not null || req.Revision < 1 || req.Revision == int.MaxValue)
                    return Results.BadRequest(new { error = error ?? "Phiên bản không hợp lệ." });
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var row = await db.StoryPlans.FirstOrDefaultAsync(
                        x => x.ProjectId == id && x.Id == planId,
                        ct
                    );
                    if (row is null)
                        return Results.NotFound();
                    if (row.Status != "Succeeded" || row.Revision != req.Revision)
                        return Results.Conflict(
                            new
                            {
                                error = "Bản phân cảnh đã đổi hoặc chưa hoàn tất. Tải lại trước khi lưu.",
                            }
                        );
                    row.DraftJson = JsonSerializer.Serialize(req.Proposal, Web);
                    row.Revision++;
                    row.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { plan = View(row) });
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/story-plans/{planId:guid}/apply",
            async (
                Guid id,
                Guid planId,
                ApplyStoryPlan req,
                StudioDb db,
                StoryPlanGate gate,
                CancellationToken ct
            ) =>
            {
                if (req.Version < 1 || req.Version == int.MaxValue)
                    return Results.BadRequest(new { error = "Phiên bản storyboard không hợp lệ." });
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var row = await db
                        .StoryPlans.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.ProjectId == id && x.Id == planId, ct);
                    if (row is null)
                        return Results.NotFound();
                    if (row.Status != "Succeeded" || row.Revision != req.Revision)
                        return Results.Conflict(
                            new { error = "Bản phân cảnh đã thay đổi; tải lại trước khi áp dụng." }
                        );
                    var saved = await db
                        .ProjectDocuments.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.ProjectId == id, ct);
                    if (saved is null || saved.Version != req.Version)
                        return Results.Conflict(
                            new
                            {
                                error = "Storyboard đã thay đổi. Tải bản mới nhất trước khi áp dụng.",
                            }
                        );
                    var proposal = JsonSerializer.Deserialize<StoryProposal>(row.DraftJson!, Web)!;
                    var error = Validate(proposal);
                    if (error is not null)
                        return Results.BadRequest(new { error });
                    var current = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var next = current with
                    {
                        Script = string.Join("\n\n", proposal.Scenes.Select(s => s.Narration)),
                        Scenes = proposal
                            .Scenes.Select(s => new StoryScene(
                                Guid.NewGuid(),
                                s.Title,
                                s.Narration,
                                s.Prompt,
                                null,
                                s.Duration,
                                Direction: s.Direction
                            ))
                            .ToArray(),
                    };
                    error = await Storyboard.Validate(id, next, db, false, ct);
                    if (error is not null)
                        return Results.BadRequest(new { error });
                    var changed = await db
                        .ProjectDocuments.Where(x => x.ProjectId == id && x.Version == req.Version)
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.ContentJson, JsonSerializer.Serialize(next))
                                    .SetProperty(x => x.Version, req.Version + 1)
                                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                            ct
                        );
                    return changed == 0
                        ? Results.Conflict(new { error = "Storyboard vừa thay đổi; chưa áp dụng." })
                        : Results.Ok(new { version = req.Version + 1, document = next });
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/story-plans/{planId:guid}/cancel",
            async (Guid id, Guid planId, StudioDb db, StoryPlanGate gate, CancellationToken ct) =>
            {
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var row = await db
                        .StoryPlans.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == planId && x.ProjectId == id, ct);
                    if (row is null)
                        return Results.NotFound();
                    var count = await db
                        .StoryPlans.Where(x => x.Id == planId && x.Status == "Queued")
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.Status, "Canceled")
                                    .SetProperty(x => x.Message, "Đã hủy trước khi gửi AI")
                                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                            ct
                        );
                    return count > 0
                        ? Results.Ok(new { canceled = true })
                        : Results.Conflict(new { error = "Chỉ hủy được yêu cầu chưa gửi." });
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
    }
}
