using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class ProjectBudget
{
    public Guid ProjectId { get; set; }
    public string ConfigJson { get; set; } = "";
    public int Version { get; set; } = 1;
}

public class BudgetEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid OperationId { get; set; }
    public string Kind { get; set; } = "";
    public string Units { get; set; } = "";
    public long? EstimatedMicros { get; set; }
    public long? ActualMicros { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record BudgetConfig(
    bool Enabled = false,
    decimal LimitUsd = 10,
    decimal? ImageUsdPerCall = null,
    decimal? VideoUsdPerSecond = null,
    decimal? SpeechUsdPer1000Chars = null,
    decimal? StoryUsdPerCall = null,
    decimal? TranscriptionUsdPerMinute = null
)
{
    public bool Valid() =>
        LimitUsd is > 0 and <= 10000
        && new[]
        {
            ImageUsdPerCall,
            VideoUsdPerSecond,
            SpeechUsdPer1000Chars,
            StoryUsdPerCall,
            TranscriptionUsdPerMinute,
        }.All(x => x is null || x is >= 0 and <= 100);

    public decimal? Rate(string kind) =>
        kind switch
        {
            "image" => ImageUsdPerCall,
            "video" => VideoUsdPerSecond,
            "speech" => SpeechUsdPer1000Chars,
            "story" => StoryUsdPerCall,
            "transcription" => TranscriptionUsdPerMinute,
            _ => null,
        };
}

public record BudgetWrite(int Version, BudgetConfig Config);

public record BudgetLine(string Kind, decimal Units);

public record ReconcileBudget(decimal ActualUsd, string Note);

public sealed class BudgetException(string message) : Exception(message);

public static class AiBudget
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string[] Kinds = ["image", "video", "speech", "story", "transcription"];

    public static long Micros(decimal value) => checked((long)decimal.Ceiling(value * 1000000));

    public static Task Upgrade(StudioDb db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ProjectBudgets" ("ProjectId" TEXT NOT NULL PRIMARY KEY,"ConfigJson" TEXT NOT NULL,"Version" INTEGER NOT NULL,FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS "BudgetEntries" ("Id" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"OperationId" TEXT NOT NULL,"Kind" TEXT NOT NULL,"Units" TEXT NOT NULL,"EstimatedMicros" INTEGER NULL,"ActualMicros" INTEGER NULL,"Note" TEXT NOT NULL,"CreatedAt" TEXT NOT NULL,FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_BudgetEntries_ProjectId_CreatedAt" ON "BudgetEntries"("ProjectId","CreatedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetEntries_OperationId_Kind" ON "BudgetEntries"("OperationId","Kind");
            """
        );

    static BudgetConfig Config(ProjectBudget? row) =>
        row is null ? new() : JsonSerializer.Deserialize<BudgetConfig>(row.ConfigJson, Json)!;

    public static async Task<object> Quote(
        StudioDb db,
        Guid project,
        BudgetLine[] lines,
        CancellationToken ct
    )
    {
        var config = Config(
            await db
                .ProjectBudgets.AsNoTracking()
                .FirstOrDefaultAsync(b => b.ProjectId == project, ct)
        );
        var entries = await db
            .BudgetEntries.AsNoTracking()
            .Where(e => e.ProjectId == project)
            .ToListAsync(ct);
        var known = entries.Sum(e => e.ActualMicros ?? e.EstimatedMicros ?? 0);
        var unknown = entries.Count(e => e.ActualMicros is null && e.EstimatedMicros is null);
        var quotes = lines
            .Where(l => l.Units > 0)
            .GroupBy(l => l.Kind)
            .Select(g => new BudgetLine(g.Key, g.Sum(l => l.Units)))
            .Select(l => new
            {
                l.Kind,
                l.Units,
                estimatedUsd = config.Rate(l.Kind) is decimal rate
                    ? Micros(rate * l.Units) / 1000000m
                    : (decimal?)null,
            })
            .ToArray();
        var total = quotes.All(q => q.estimatedUsd.HasValue)
            ? quotes.Sum(q => q.estimatedUsd!.Value)
            : (decimal?)null;
        return new
        {
            enabled = config.Enabled,
            limitUsd = config.LimitUsd,
            reservedUsd = known / 1000000m,
            unknownEntries = unknown,
            estimatedUsd = total,
            lines = quotes,
            allowed = !config.Enabled
                || quotes.Length == 0
                || (
                    unknown == 0
                    && total.HasValue
                    && known + Micros(total.Value) <= Micros(config.LimitUsd)
                ),
            basis = "manual-unit-prices; reservations, not provider invoices",
        };
    }

    // Add operation rows BEFORE calling this method: the reservation and operation are saved atomically.
    public static async Task Reserve(
        StudioDb db,
        Guid project,
        Guid operation,
        BudgetLine[] lines,
        CancellationToken ct
    )
    {
        lines = lines
            .Where(l => l.Units > 0)
            .GroupBy(l => l.Kind)
            .Select(g => new BudgetLine(g.Key, g.Sum(l => l.Units)))
            .ToArray();
        if (lines.Length == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        if (lines.Any(l => !Kinds.Contains(l.Kind) || l.Units > 100000))
            throw new BudgetException("Đơn vị dự toán AI không hợp lệ.");
        await Gate.WaitAsync(ct);
        try
        {
            var config = Config(
                await db
                    .ProjectBudgets.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.ProjectId == project, ct)
            );
            var entries = await db
                .BudgetEntries.AsNoTracking()
                .Where(e => e.ProjectId == project)
                .ToListAsync(ct);
            var newEntries = lines
                .Select(l => new BudgetEntry
                {
                    ProjectId = project,
                    OperationId = operation,
                    Kind = l.Kind,
                    Units = l.Units.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    EstimatedMicros = config.Rate(l.Kind) is decimal rate
                        ? Micros(rate * l.Units)
                        : null,
                })
                .ToArray();
            if (config.Enabled)
            {
                if (
                    entries.Any(e => e.ActualMicros is null && e.EstimatedMicros is null)
                    || newEntries.Any(e => e.EstimatedMicros is null)
                )
                    throw new BudgetException(
                        "Chưa biết đơn giá/chi phí. Điền đơn giá và định giá khoản chưa rõ trước khi gọi AI."
                    );
                if (
                    entries.Sum(e => e.ActualMicros ?? e.EstimatedMicros ?? 0)
                        + newEntries.Sum(e => e.EstimatedMicros!.Value)
                    > Micros(config.LimitUsd)
                )
                    throw new BudgetException(
                        "Yêu cầu vượt ngân sách AI ước tính của dự án. Chưa gửi nhà cung cấp."
                    );
            }
            db.BudgetEntries.AddRange(newEntries);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void MapAiBudget(this WebApplication app)
    {
        app.Use(
            async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (BudgetException ex)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsJsonAsync(
                        new { error = ex.Message },
                        context.RequestAborted
                    );
                }
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/budget/export",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                await Gate.WaitAsync(ct);
                try
                {
                    var row = await db
                        .ProjectBudgets.AsNoTracking()
                        .FirstOrDefaultAsync(b => b.ProjectId == id, ct);
                    if (await db.BudgetEntries.CountAsync(e => e.ProjectId == id, ct) > 50000)
                        return Results.Json(
                            new { error = "Xuất tối đa 50.000 khoản." },
                            statusCode: 413
                        );
                    return Results.Ok(
                        new
                        {
                            format = "flowvideo.budget",
                            schemaVersion = 1,
                            projectId = id,
                            version = row?.Version ?? 0,
                            config = Config(row),
                            summary = await Quote(db, id, [], ct),
                            entries = await db
                                .BudgetEntries.AsNoTracking()
                                .Where(e => e.ProjectId == id)
                                .OrderBy(e => e.CreatedAt)
                                .ToListAsync(ct),
                        }
                    );
                }
                finally
                {
                    Gate.Release();
                }
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/budget",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var row = await db
                    .ProjectBudgets.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.ProjectId == id, ct);
                return Results.Ok(
                    new
                    {
                        version = row?.Version ?? 0,
                        config = Config(row),
                        summary = await Quote(db, id, [], ct),
                        entries = await db
                            .BudgetEntries.AsNoTracking()
                            .Where(e => e.ProjectId == id)
                            .OrderByDescending(e => e.CreatedAt)
                            .Take(200)
                            .ToListAsync(ct),
                    }
                );
            }
        );
        app.MapPut(
            "/api/projects/{id:guid}/budget",
            async (Guid id, BudgetWrite req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    req.Config is null
                    || !req.Config.Valid()
                    || req.Version < 0
                    || req.Version == int.MaxValue
                )
                    return Results.BadRequest(
                        new { error = "Ngân sách (0–10.000 USD], đơn giá [0–100] hoặc để trống." }
                    );
                await Gate.WaitAsync(ct);
                try
                {
                    if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                        return Results.NotFound();
                    var row = await db.ProjectBudgets.FirstOrDefaultAsync(
                        b => b.ProjectId == id,
                        ct
                    );
                    if ((row?.Version ?? 0) != req.Version)
                        return Results.Conflict(new { error = "Ngân sách đã đổi; tải lại." });
                    if (row is null)
                    {
                        row = new() { ProjectId = id, Version = 0 };
                        db.ProjectBudgets.Add(row);
                    }
                    row.ConfigJson = JsonSerializer.Serialize(req.Config, Json);
                    row.Version++;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { row.Version });
                }
                finally
                {
                    Gate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/budget/quote",
            async (Guid id, BudgetLine[] lines, StudioDb db, CancellationToken ct) =>
            {
                if (
                    lines is null
                    || lines.Length > 10
                    || lines.Any(l =>
                        l is null || !Kinds.Contains(l.Kind) || l.Units < 0 || l.Units > 100000
                    )
                )
                    return Results.BadRequest(new { error = "Dự toán không hợp lệ." });
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                return Results.Ok(await Quote(db, id, lines, ct));
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/budget/reprice",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                await Gate.WaitAsync(ct);
                try
                {
                    var config = Config(
                        await db
                            .ProjectBudgets.AsNoTracking()
                            .FirstOrDefaultAsync(b => b.ProjectId == id, ct)
                    );
                    var entries = await db
                        .BudgetEntries.Where(e =>
                            e.ProjectId == id && e.EstimatedMicros == null && e.ActualMicros == null
                        )
                        .ToListAsync(ct);
                    foreach (var e in entries)
                        if (config.Rate(e.Kind) is decimal rate)
                            e.EstimatedMicros = Micros(
                                rate
                                    * decimal.Parse(
                                        e.Units,
                                        System.Globalization.CultureInfo.InvariantCulture
                                    )
                            );
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(
                        new { updated = entries.Count(e => e.EstimatedMicros.HasValue) }
                    );
                }
                finally
                {
                    Gate.Release();
                }
            }
        );
        app.MapPut(
            "/api/projects/{id:guid}/budget/entries/{entryId:guid}",
            async (Guid id, Guid entryId, ReconcileBudget req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    req.ActualUsd is < 0 or > 10000
                    || string.IsNullOrWhiteSpace(req.Note)
                    || req.Note.Length > 300
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Điền số tiền 0–10.000 USD và ghi chú đối soát tối đa 300 ký tự.",
                        }
                    );
                await Gate.WaitAsync(ct);
                try
                {
                    var e = await db.BudgetEntries.FirstOrDefaultAsync(
                        e => e.ProjectId == id && e.Id == entryId,
                        ct
                    );
                    if (e is null)
                        return Results.NotFound();
                    e.ActualMicros = Micros(req.ActualUsd);
                    e.Note = req.Note;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(e);
                }
                finally
                {
                    Gate.Release();
                }
            }
        );
    }
}
