using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class ProductionRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int DocumentVersion { get; set; }
    public string RequestJson { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public string StateJson { get; set; } = "";
    public string Status { get; set; } = "WaitingAssets";
    public string Message { get; set; } = "Đang chuẩn bị tư liệu";
    public int SpeechReservations { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record ProductionRequest(
    Guid RequestId,
    int Version,
    string Media = "existing",
    bool Speech = false,
    string Voice = "alloy",
    double Speed = 1,
    bool AutoTiming = true,
    double Padding = .15,
    int VideoSeconds = 5,
    string VideoResolution = "720p",
    bool FromScript = false
);

public record ProductionStep(
    Guid SceneId,
    string Provider,
    Guid? GenerationId,
    bool Owned,
    string SpeechStatus = "Skipped",
    Guid? SpeechId = null,
    double? SpeechDuration = null
);

public record ProductionEvent(DateTime At, string Message);

public record ProductionState(
    ProductionStep[] Steps,
    Guid? VoiceId = null,
    Guid? RenderId = null,
    StoryDocument? Document = null,
    ProductionEvent[]? Events = null
)
{
    public ProductionState Event(string message) =>
        this with
        {
            Events = (Events ?? []).Append(new(DateTime.UtcNow, message)).TakeLast(50).ToArray(),
        };
}

public sealed class ProductionGate
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class ProductionError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public record ProductionPlan(
    StoryDocument Document,
    ProductionStep[] Steps,
    ImageGeneration[] Images,
    VideoGeneration[] Videos,
    int SpeechCalls,
    int ImageUsed,
    int VideoUsed,
    int SpeechUsed
);

public static class ProductionWorkflow
{
    public static readonly string[] Active =
    [
        "WaitingAssets",
        "Preparing",
        "Speaking",
        "Rendering",
    ];

    public static int SpeechLimit(IConfiguration c) =>
        int.TryParse(c["WORKFLOW_TTS_DAILY_LIMIT"], out var n) ? Math.Clamp(n, 1, 100) : 20;

    public static async Task Upgrade(StudioDb db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ProductionRuns" (
            "Id" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"DocumentVersion" INTEGER NOT NULL,
            "RequestJson" TEXT NOT NULL,"SnapshotJson" TEXT NOT NULL,"StateJson" TEXT NOT NULL,
            "Status" TEXT NOT NULL,"Message" TEXT NOT NULL,"SpeechReservations" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,"UpdatedAt" TEXT NOT NULL,
            FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_ProductionRuns_ProjectId_CreatedAt" ON "ProductionRuns" ("ProjectId","CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ProductionRuns_Status_CreatedAt" ON "ProductionRuns" ("Status","CreatedAt");
            """
        );
        var interrupted = await db.ProductionRuns.Where(x => x.Status == "Speaking").ToListAsync();
        foreach (var row in interrupted)
        {
            row.Status = "Unknown";
            row.Message =
                "Ứng dụng dừng khi tạo giọng; không tự gửi lại để tránh tính phí hai lần.";
            row.UpdatedAt = DateTime.UtcNow;
            var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
            row.StateJson = JsonSerializer.Serialize(
                (
                    state with
                    {
                        Steps = state
                            .Steps.Select(s =>
                                s.SpeechStatus == "Running"
                                    ? s with
                                    {
                                        SpeechStatus = "Unknown",
                                    }
                                    : s
                            )
                            .ToArray(),
                    }
                ).Event(row.Message)
            );
        }
        await db.SaveChangesAsync();
    }

    public static object View(ProductionRun row)
    {
        var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
        return new
        {
            row.Id,
            row.ProjectId,
            row.DocumentVersion,
            row.Status,
            row.Message,
            row.SpeechReservations,
            row.CreatedAt,
            row.UpdatedAt,
            options = JsonSerializer.Deserialize<ProductionRequest>(row.RequestJson),
            state.Steps,
            state.VoiceId,
            state.RenderId,
            hasDocument = state.Document is not null,
            events = state.Events ?? [],
        };
    }

    static Guid ScriptSceneId(Guid project, int index, string text) =>
        new Guid(
            System
                .Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"flowvideo:production-scene:v1:{project:N}:{index}:{text}"
                    )
                )
                .AsSpan(0, 16)
        );

    public static async Task<ProductionPlan> Plan(
        Guid projectId,
        ProductionRequest req,
        StudioDb db,
        Storage store,
        IConfiguration config,
        CancellationToken ct
    )
    {
        if (
            req.RequestId == Guid.Empty
            || req.Version < 1
            || req.Media is not ("existing" or "images" or "videos")
            || !AudioProvider.Voices.Contains(req.Voice)
            || !double.IsFinite(req.Speed)
            || req.Speed is < .5 or > 1.5
            || !double.IsFinite(req.Padding)
            || req.Padding is < 0 or > 2
            || req.VideoSeconds is < 3 or > 15
            || req.VideoResolution is not ("360p" or "720p" or "1080p")
        )
            throw new ProductionError("Tùy chọn quy trình không hợp lệ.");
        if (!await db.Projects.AnyAsync(x => x.Id == projectId, ct))
            throw new ProductionError("Không có dự án.", 404);
        var saved = await db
            .ProjectDocuments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (saved is null || saved.Version != req.Version)
            throw new ProductionError(
                "Storyboard đã thay đổi. Lưu lại trước khi tạo quy trình.",
                409
            );
        var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
        if (req.FromScript)
        {
            var paragraphs = System
                .Text.RegularExpressions.Regex.Split(doc.Script.Trim(), @"\r?\n\s*\r?\n")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            if (paragraphs.Length is < 1 or > 10 || paragraphs.Any(s => s.Length > 2000))
                throw new ProductionError(
                    "Kịch bản cần 1–10 đoạn cách nhau dòng trống, tối đa 2.000 ký tự/đoạn."
                );
            doc = doc with
            {
                Scenes = paragraphs
                    .Select(
                        (p, i) =>
                            new StoryScene(
                                ScriptSceneId(projectId, i, p),
                                "Cảnh " + (i + 1),
                                p,
                                p,
                                null,
                                5
                            )
                    )
                    .ToArray(),
            };
        }
        var error = await Storyboard.Validate(projectId, doc, db, false, ct);
        if (error is not null)
            throw new ProductionError(error);
        var scenes = doc.Scenes!.Where(s => s.Options?.Enabled != false).ToArray();
        if (scenes.Length is < 1 or > 10)
            throw new ProductionError("Quy trình hỗ trợ 1–10 cảnh bật, tổng tối đa 600 giây.");
        var sceneAssets = await db
            .Assets.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .ToDictionaryAsync(a => a.Id, ct);
        foreach (
            var id in scenes
                .Select(s => s.AssetId)
                .Concat(new[] { doc.VoiceId, doc.MusicId })
                .OfType<Guid>()
        )
            if (!sceneAssets.TryGetValue(id, out var a) || !File.Exists(store.AssetPath(a)))
                throw new ProductionError(
                    "File tư liệu không còn; chọn lại hoặc bỏ liên kết để tạo mới.",
                    422
                );
        var images = new List<ImageGeneration>();
        var videos = new List<VideoGeneration>();
        var steps = new List<ProductionStep>();
        foreach (var scene in scenes)
        {
            string provider = "existing";
            Guid? generation = null;
            bool owned = false;
            if (scene.AssetId is null)
            {
                if (req.Media == "existing")
                    throw new ProductionError(
                        "Cảnh còn thiếu tư liệu. Chọn tạo ảnh/video AI hoặc gắn tư liệu trước."
                    );
                if (string.IsNullOrWhiteSpace(scene.Prompt))
                    throw new ProductionError("Cảnh cần prompt để tạo tư liệu AI.");
                provider = req.Media;
                var prompt = SceneDirection.VisualPrompt(scene);
                if (provider == "images")
                {
                    var model = config["OPENAI_IMAGE_MODEL"] ?? "gpt-image-1";
                    var size = doc.Ratio == "16:9" ? "1536x1024" : "1024x1536";
                    var key = GenerationKeys.Image(scene.Id, prompt, model, size, "low");
                    var cached = await db
                        .ImageGenerations.AsNoTracking()
                        .Where(x =>
                            x.ProjectId == projectId
                            && x.Fingerprint == key
                            && x.Status == "Succeeded"
                        )
                        .OrderByDescending(x => x.CreatedAt)
                        .FirstOrDefaultAsync(ct);
                    if (
                        cached?.AssetId is Guid aid
                        && sceneAssets.TryGetValue(aid, out var asset)
                        && File.Exists(store.AssetPath(asset))
                    )
                        generation = cached.Id;
                    else
                    {
                        if (
                            await db.ImageGenerations.AnyAsync(
                                x =>
                                    x.ProjectId == projectId
                                    && x.SceneId == scene.Id
                                    && (x.Status == "Queued" || x.Status == "Running"),
                                ct
                            )
                        )
                            throw new ProductionError(
                                "Cảnh đang tạo ảnh ở lượt khác; đợi kết quả trước.",
                                409
                            );
                        var child = new ImageGeneration
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = projectId,
                            SceneId = scene.Id,
                            DocumentVersion = req.Version,
                            Prompt = prompt,
                            Fingerprint = key,
                            Model = model,
                            Size = size,
                        };
                        images.Add(child);
                        generation = child.Id;
                        owned = true;
                    }
                }
                else
                {
                    var model = config["GEMINI_VIDEO_MODEL"] ?? "gemini-omni-1.2-flash";
                    var key = GenerationKeys.Video(
                        scene.Id,
                        prompt,
                        model,
                        doc.Ratio,
                        req.VideoResolution,
                        req.VideoSeconds,
                        null
                    );
                    var cached = await db
                        .VideoGenerations.AsNoTracking()
                        .Where(x =>
                            x.ProjectId == projectId
                            && x.Fingerprint == key
                            && x.Status == "Succeeded"
                        )
                        .OrderByDescending(x => x.CreatedAt)
                        .FirstOrDefaultAsync(ct);
                    if (
                        cached?.AssetId is Guid aid
                        && sceneAssets.TryGetValue(aid, out var asset)
                        && File.Exists(store.AssetPath(asset))
                    )
                        generation = cached.Id;
                    else
                    {
                        if (
                            await db.VideoGenerations.AnyAsync(
                                x =>
                                    x.ProjectId == projectId
                                    && x.SceneId == scene.Id
                                    && (x.Status == "Queued" || x.Status == "Running"),
                                ct
                            )
                        )
                            throw new ProductionError(
                                "Cảnh đang tạo video ở lượt khác; đợi kết quả trước.",
                                409
                            );
                        var child = new VideoGeneration
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = projectId,
                            SceneId = scene.Id,
                            DocumentVersion = req.Version,
                            Prompt = prompt,
                            Fingerprint = key,
                            Model = model,
                            Size = doc.Ratio,
                            Quality = req.VideoResolution,
                            Seconds = req.VideoSeconds,
                        };
                        videos.Add(child);
                        generation = child.Id;
                        owned = true;
                    }
                }
            }
            steps.Add(
                new(
                    scene.Id,
                    provider,
                    generation,
                    owned,
                    req.Speech && !string.IsNullOrWhiteSpace(scene.Narration) ? "Queued" : "Skipped"
                )
            );
        }
        var speech = steps.Count(x => x.SpeechStatus == "Queued");
        if (req.Speech && speech == 0)
            throw new ProductionError("Thêm lời vào ít nhất một cảnh để tạo giọng.");
        if ((images.Count > 0 || speech > 0) && string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]))
            throw new ProductionError("Cần OPENAI_API_KEY để tạo ảnh/giọng.", 503);
        if (videos.Count > 0 && string.IsNullOrWhiteSpace(config["GEMINI_API_KEY"]))
            throw new ProductionError("Cần GEMINI_API_KEY để tạo video.", 503);
        var since = DateTime.UtcNow.AddDays(-1);
        var imageUsed = await db.ImageGenerations.CountAsync(x => x.CreatedAt >= since, ct);
        var videoUsed = await db.VideoGenerations.CountAsync(x => x.CreatedAt >= since, ct);
        var speechUsed = await db
            .ProductionRuns.Where(x => x.CreatedAt >= since)
            .SumAsync(x => x.SpeechReservations, ct);
        if (
            images.Count > 0 && imageUsed + images.Count > ImageFeatures.DailyLimit(config)
            || videos.Count > 0 && videoUsed + videos.Count > VideoFeatures.DailyLimit(config)
            || speech > 0 && speechUsed + speech > SpeechLimit(config)
        )
            throw new ProductionError(
                "Không đủ hạn mức cho toàn quy trình. Chưa tạo lượt nào; giảm số cảnh hoặc dùng tư liệu có sẵn.",
                429
            );
        return new(
            doc,
            steps.ToArray(),
            images.ToArray(),
            videos.ToArray(),
            speech,
            imageUsed,
            videoUsed,
            speechUsed
        );
    }

    internal static async Task CancelPending(
        StudioDb db,
        ProductionState state,
        CancellationToken ct
    )
    {
        var imageIds = state
            .Steps.Where(x => x.Owned && x.Provider == "images")
            .Select(x => x.GenerationId)
            .OfType<Guid>()
            .ToArray();
        var videoIds = state
            .Steps.Where(x => x.Owned && x.Provider == "videos")
            .Select(x => x.GenerationId)
            .OfType<Guid>()
            .ToArray();
        await db
            .ImageGenerations.Where(x => imageIds.Contains(x.Id) && x.Status == "Queued")
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.Status, "Canceled")
                        .SetProperty(x => x.Message, "Quy trình dừng trước khi gửi")
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                ct
            );
        await db
            .VideoGenerations.Where(x => videoIds.Contains(x.Id) && x.Status == "Queued")
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.Status, "Canceled")
                        .SetProperty(x => x.Message, "Quy trình dừng trước khi gửi")
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                ct
            );
    }

    public static void MapProduction(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/workflows",
            async (Guid id, StudioDb db, IConfiguration c, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                var rows = await db
                    .ProductionRuns.AsNoTracking()
                    .Where(x => x.ProjectId == id)
                    .OrderByDescending(x => x.CreatedAt)
                    .Take(20)
                    .ToListAsync(ct);
                return Results.Ok(new { items = rows.Select(View), speechLimit = SpeechLimit(c) });
            }
        );
        foreach (var action in new[] { "plan", "start" })
        {
            var reserve = action == "start";
            app.MapPost(
                "/api/projects/{id:guid}/workflows/" + action,
                async (
                    Guid id,
                    ProductionRequest req,
                    StudioDb db,
                    Storage store,
                    IConfiguration config,
                    ProductionGate gate,
                    ImageIntake imageGate,
                    VideoIntake videoGate,
                    CancellationToken ct
                ) =>
                {
                    await gate.Gate.WaitAsync(ct);
                    try
                    {
                        await imageGate.Gate.WaitAsync(ct);
                        try
                        {
                            await videoGate.Gate.WaitAsync(ct);
                            try
                            {
                                var old = await db
                                    .ProductionRuns.AsNoTracking()
                                    .FirstOrDefaultAsync(x => x.Id == req.RequestId, ct);
                                if (old is not null)
                                    return
                                        old.ProjectId == id
                                        && old.RequestJson == JsonSerializer.Serialize(req)
                                        ? Results.Ok(new { run = View(old), reused = true })
                                        : Results.Conflict(
                                            new { error = "Mã yêu cầu đã dùng cho cấu hình khác." }
                                        );
                                var plan = await Plan(id, req, db, store, config, ct);
                                if (!reserve)
                                    return Results.Ok(
                                        new
                                        {
                                            scenes = plan.Steps.Length,
                                            newImages = plan.Images.Length,
                                            newVideos = plan.Videos.Length,
                                            newSpeech = plan.SpeechCalls,
                                            reusedMedia = plan.Steps.Count(x =>
                                                x.GenerationId.HasValue && !x.Owned
                                            ),
                                            existingMedia = plan.Steps.Count(x =>
                                                x.Provider == "existing"
                                            ),
                                            imageRemaining = ImageFeatures.DailyLimit(config)
                                                - plan.ImageUsed
                                                - plan.Images.Length,
                                            videoRemaining = VideoFeatures.DailyLimit(config)
                                                - plan.VideoUsed
                                                - plan.Videos.Length,
                                            speechRemaining = SpeechLimit(config)
                                                - plan.SpeechUsed
                                                - plan.SpeechCalls,
                                            limitsAreCallsNotMoney = true,
                                        }
                                    );
                                if (
                                    await db.ProductionRuns.CountAsync(
                                        x => x.ProjectId == id && Active.Contains(x.Status),
                                        ct
                                    ) >= 3
                                )
                                    throw new ProductionError(
                                        "Tối đa 3 quy trình đang hoạt động/dự án.",
                                        409
                                    );
                                var row = new ProductionRun
                                {
                                    Id = req.RequestId,
                                    ProjectId = id,
                                    DocumentVersion = req.Version,
                                    RequestJson = JsonSerializer.Serialize(req),
                                    SnapshotJson = JsonSerializer.Serialize(plan.Document),
                                    SpeechReservations = plan.SpeechCalls,
                                    StateJson = JsonSerializer.Serialize(
                                        new ProductionState(plan.Steps).Event(
                                            "Đã chụp storyboard và đặt trước hạn mức"
                                        )
                                    ),
                                };
                                db.ImageGenerations.AddRange(plan.Images);
                                db.VideoGenerations.AddRange(plan.Videos);
                                db.ProductionRuns.Add(row);
                                await db.SaveChangesAsync(ct);
                                return Results.Accepted(
                                    $"/api/projects/{id}/workflows",
                                    new { run = View(row), reused = false }
                                );
                            }
                            finally
                            {
                                videoGate.Gate.Release();
                            }
                        }
                        finally
                        {
                            imageGate.Gate.Release();
                        }
                    }
                    catch (ProductionError e)
                    {
                        return Results.Json(new { error = e.Message }, statusCode: e.Status);
                    }
                    finally
                    {
                        gate.Gate.Release();
                    }
                }
            );
        }
        app.MapGet(
            "/api/projects/{id:guid}/workflows/{runId:guid}/snapshot",
            async (Guid id, Guid runId, StudioDb db, CancellationToken ct) =>
            {
                var row = await db
                    .ProductionRuns.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id && x.Id == runId, ct);
                return row is null
                    ? Results.NotFound()
                    : Results.Ok(
                        new
                        {
                            format = "flowvideo.production",
                            version = 1,
                            run = View(row),
                            snapshot = JsonSerializer.Deserialize<StoryDocument>(row.SnapshotJson),
                            result = JsonSerializer
                                .Deserialize<ProductionState>(row.StateJson)!
                                .Document,
                        }
                    );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/workflows/{runId:guid}/cancel",
            async (
                Guid id,
                Guid runId,
                StudioDb db,
                ProductionGate gate,
                JobControl control,
                CancellationToken ct
            ) =>
            {
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var row = await db.ProductionRuns.FirstOrDefaultAsync(
                        x => x.ProjectId == id && x.Id == runId,
                        ct
                    );
                    if (row is null)
                        return Results.NotFound();
                    if (!Active.Contains(row.Status) && row.Status != "Canceled")
                        return Results.Conflict(new { error = "Quy trình đã kết thúc." });
                    var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
                    await CancelPending(db, state, ct);
                    if (state.RenderId is Guid rid)
                    {
                        await db
                            .Jobs.Where(x =>
                                x.Id == rid && (x.Status == "Queued" || x.Status == "Running")
                            )
                            .ExecuteUpdateAsync(
                                s =>
                                    s.SetProperty(x => x.Status, "Cancelled")
                                        .SetProperty(x => x.Message, "Quy trình đã hủy"),
                                ct
                            );
                        control.Cancel(rid);
                    }
                    row.Status = "Canceled";
                    row.Message =
                        "Đã hủy quy trình. Yêu cầu AI đã gửi có thể vẫn hoàn tất và tính phí.";
                    row.UpdatedAt = DateTime.UtcNow;
                    row.StateJson = JsonSerializer.Serialize(state.Event(row.Message));
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { run = View(row) });
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/workflows/{runId:guid}/retry-render",
            async (Guid id, Guid runId, StudioDb db, ProductionGate gate, CancellationToken ct) =>
            {
                await gate.Gate.WaitAsync(ct);
                try
                {
                    var row = await db.ProductionRuns.FirstOrDefaultAsync(
                        x => x.Id == runId && x.ProjectId == id,
                        ct
                    );
                    if (row is null)
                        return Results.NotFound();
                    var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
                    var old = state.RenderId is Guid rid
                        ? await db
                            .Jobs.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == rid && x.ProjectId == id, ct)
                        : null;
                    if (
                        row.Status is not ("Failed" or "Canceled")
                        || old is null
                        || old.Status is not ("Failed" or "Cancelled")
                    )
                        return Results.Conflict(
                            new { error = "Chỉ dựng lại bước render đã lỗi/hủy; không gửi lại AI." }
                        );
                    var job = new RenderJob { ProjectId = id, RequestJson = old.RequestJson };
                    db.Jobs.Add(job);
                    row.Status = "Rendering";
                    row.Message = "Đang dựng lại; không gọi AI";
                    row.UpdatedAt = DateTime.UtcNow;
                    row.StateJson = JsonSerializer.Serialize(
                        (state with { RenderId = job.Id }).Event(row.Message)
                    );
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted($"/api/jobs/{job.Id}", new { run = View(row) });
                }
                finally
                {
                    gate.Gate.Release();
                }
            }
        );
    }
}
