using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class SavedWorkspaceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContentJson { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record CaptureItem(string Kind, string Name, int Version);

public record RenderPresetPackage(
    string Format,
    int SchemaVersion,
    string Name,
    StoryDocument Settings
);

public record PreflightIssue(string Severity, string Message, Guid? SceneId = null);

public static class WorkspaceWorkflow
{
    private static readonly SemaphoreSlim Intake = new(1, 1);
    private static readonly SemaphoreSlim ProbeGate = new(2, 2);

    public static Task Upgrade(StudioDb db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SavedWorkspaceItems" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_SavedWorkspaceItems" PRIMARY KEY,
            "ProjectId" TEXT NOT NULL, "Kind" TEXT NOT NULL, "Name" TEXT NOT NULL,
            "ContentJson" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_SavedWorkspaceItems_Projects_ProjectId" FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_SavedWorkspaceItems_ProjectId_Kind" ON "SavedWorkspaceItems"("ProjectId","Kind");
            """
        );

    private static bool NameValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120 && !name.Any(char.IsControl);

    private static object Info(SavedWorkspaceItem item) =>
        new
        {
            item.Id,
            item.ProjectId,
            item.Kind,
            item.Name,
            item.CreatedAt,
        };

    private static StoryDocument Preset(StoryDocument doc) =>
        doc with
        {
            Branding = doc.Branding is null ? null : doc.Branding with { Logo = null },
            Idea = "",
            Script = "",
            Scenes = [],
            VoiceId = null,
            MusicId = null,
        };

    private static IResult Conflict() =>
        Results.Conflict(
            new { error = "Bản đã lưu đã thay đổi. Tải lại dự án trước khi tiếp tục." }
        );

    public static void MapWorkspaceWorkflow(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/saved-items",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var items = await db
                    .SavedWorkspaceItems.AsNoTracking()
                    .Where(x => x.ProjectId == id)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(ct);
                return Results.Ok(items.Select(Info));
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/saved-items",
            async (Guid id, CaptureItem request, StudioDb db, CancellationToken ct) =>
            {
                if (request.Kind is not ("checkpoint" or "preset") || !NameValid(request.Name))
                    return Results.BadRequest(
                        new { error = "Chọn loại mốc/preset và tên 1–120 ký tự." }
                    );
                await Intake.WaitAsync(ct);
                try
                {
                    var saved = await db
                        .ProjectDocuments.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                    if (saved is null || saved.Version != request.Version)
                        return Conflict();
                    if (
                        await db.SavedWorkspaceItems.CountAsync(
                            x => x.ProjectId == id && x.Kind == request.Kind,
                            ct
                        ) >= 20
                    )
                        return Results.BadRequest(
                            new { error = "Tối đa 20 mốc lưu và 20 preset/dự án." }
                        );
                    var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var item = new SavedWorkspaceItem
                    {
                        ProjectId = id,
                        Kind = request.Kind,
                        Name = request.Name.Trim(),
                        ContentJson =
                            request.Kind == "checkpoint"
                                ? saved.ContentJson
                                : JsonSerializer.Serialize(Preset(doc)),
                    };
                    db.SavedWorkspaceItems.Add(item);
                    await db.SaveChangesAsync(ct);
                    return Results.Created($"/api/projects/{id}/saved-items/{item.Id}", Info(item));
                }
                finally
                {
                    Intake.Release();
                }
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/saved-items/{itemId:guid}",
            async (Guid id, Guid itemId, StudioDb db, CancellationToken ct) =>
            {
                var item = await db
                    .SavedWorkspaceItems.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id && x.Id == itemId, ct);
                return item is null
                    ? Results.NotFound()
                    : Results.Ok(
                        new
                        {
                            item = Info(item),
                            document = JsonSerializer.Deserialize<StoryDocument>(item.ContentJson),
                        }
                    );
            }
        );
        app.MapPatch(
            "/api/projects/{id:guid}/saved-items/{itemId:guid}",
            async (Guid id, Guid itemId, NameRequest req, StudioDb db, CancellationToken ct) =>
            {
                if (!NameValid(req.Name))
                    return Results.BadRequest(
                        new { error = "Tên cần 1–120 ký tự, không dùng ký tự điều khiển." }
                    );
                var count = await db
                    .SavedWorkspaceItems.Where(x => x.ProjectId == id && x.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, req.Name.Trim()), ct);
                return count == 0
                    ? Results.NotFound()
                    : Results.Ok(new { id = itemId, name = req.Name.Trim() });
            }
        );
        app.MapDelete(
            "/api/projects/{id:guid}/saved-items/{itemId:guid}",
            async (Guid id, Guid itemId, StudioDb db, CancellationToken ct) =>
            {
                var count = await db
                    .SavedWorkspaceItems.Where(x => x.ProjectId == id && x.Id == itemId)
                    .ExecuteDeleteAsync(ct);
                return count == 0 ? Results.NotFound() : Results.NoContent();
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/saved-items/{itemId:guid}/export",
            async (Guid id, Guid itemId, StudioDb db, CancellationToken ct) =>
            {
                var item = await db
                    .SavedWorkspaceItems.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id && x.Id == itemId, ct);
                if (item is null)
                    return Results.NotFound();
                var doc = JsonSerializer.Deserialize<StoryDocument>(item.ContentJson)!;
                object package =
                    item.Kind == "checkpoint"
                        ? new StoryboardPackage("flowvideo.storyboard", 1, item.Name, doc)
                        : new RenderPresetPackage(
                            "flowvideo.render-preset",
                            1,
                            item.Name,
                            Preset(doc)
                        );
                return Results.File(
                    JsonSerializer.SerializeToUtf8Bytes(
                        package,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        {
                            WriteIndented = true,
                        }
                    ),
                    "application/json",
                    $"flowvideo-{item.Kind}-{item.Id}.json"
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/saved-items/import-preset",
            async (Guid id, HttpRequest http, StudioDb db, CancellationToken ct) =>
            {
                if (http.ContentLength > 65536)
                    return Results.Problem("Preset tối đa 64 KiB.", statusCode: 413);
                if (!http.HasJsonContentType())
                    return Results.BadRequest(new { error = "Cần gửi JSON preset." });
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await http.Body.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > 65536)
                        return Results.Problem("Preset tối đa 64 KiB.", statusCode: 413);
                    await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                }
                RenderPresetPackage? request;
                try
                {
                    request = JsonSerializer.Deserialize<RenderPresetPackage>(
                        buffer.ToArray(),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    );
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "JSON preset không hợp lệ." });
                }
                if (
                    request is null
                    || request.Format != "flowvideo.render-preset"
                    || request.SchemaVersion != 1
                    || request.Settings is null
                    || !NameValid(request.Name)
                )
                    return Results.BadRequest(
                        new { error = "Preset sai định dạng hoặc phiên bản." }
                    );
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var settings = Preset(request.Settings);
                var error = await Storyboard.Validate(id, settings, db, false, ct);
                if (error is not null)
                    return Results.BadRequest(new { error });
                await Intake.WaitAsync(ct);
                try
                {
                    if (
                        await db.SavedWorkspaceItems.CountAsync(
                            x => x.ProjectId == id && x.Kind == "preset",
                            ct
                        ) >= 20
                    )
                        return Results.BadRequest(new { error = "Tối đa 20 preset/dự án." });
                    var item = new SavedWorkspaceItem
                    {
                        ProjectId = id,
                        Kind = "preset",
                        Name = request.Name.Trim(),
                        ContentJson = JsonSerializer.Serialize(settings),
                    };
                    db.SavedWorkspaceItems.Add(item);
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(Info(item));
                }
                finally
                {
                    Intake.Release();
                }
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/summary",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id)
                    .ToListAsync(ct);
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                var doc = saved is null
                    ? new StoryDocument(Scenes: [])
                    : JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                var jobs = await db
                    .Jobs.AsNoTracking()
                    .Where(j => j.ProjectId == id)
                    .GroupBy(j => j.Status)
                    .Select(g => new { status = g.Key, count = g.Count() })
                    .ToListAsync(ct);
                return Results.Ok(
                    new
                    {
                        assetCount = assets.Count,
                        bytes = assets.Sum(a =>
                            File.Exists(store.AssetPath(a))
                                ? new FileInfo(store.AssetPath(a)).Length
                                : 0
                        ),
                        missingFiles = assets.Count(a => !File.Exists(store.AssetPath(a))),
                        scenes = doc.Scenes!.Length,
                        duration = doc
                            .Scenes.Where(s => s.Options?.Enabled != false)
                            .Sum(s => s.Duration),
                        version = saved?.Version ?? 0,
                        jobs,
                        checkpoints = await db.SavedWorkspaceItems.CountAsync(
                            x => x.ProjectId == id && x.Kind == "checkpoint",
                            ct
                        ),
                        presets = await db.SavedWorkspaceItems.CountAsync(
                            x => x.ProjectId == id && x.Kind == "preset",
                            ct
                        ),
                    }
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/storyboard/preflight",
            async (Guid id, RenderDocument req, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                if (saved is null || saved.Version != req.Version)
                    return Conflict();
                if (!await ProbeGate.WaitAsync(0, ct))
                    return Results.Problem(
                        "Đang kiểm tra hai dự án. Thử lại sau.",
                        statusCode: 429
                    );
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var token = timeout.Token;
                    var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var assets = await db
                        .Assets.AsNoTracking()
                        .Where(a => a.ProjectId == id)
                        .ToDictionaryAsync(a => a.Id, token);
                    var issues = new List<PreflightIssue>();
                    var durations = new Dictionary<Guid, double>();
                    var validImages = new HashSet<Guid>();
                    if (!doc.Scenes!.Any(s => s.Options?.Enabled != false))
                        issues.Add(new("error", "Chưa có cảnh."));
                    foreach (var scene in doc.Scenes!.Where(s => s.Options?.Enabled != false))
                    {
                        if (
                            scene.AssetId is not Guid aid
                            || !assets.TryGetValue(aid, out var asset)
                            || !File.Exists(store.AssetPath(asset))
                        )
                        {
                            issues.Add(new("error", "Cảnh thiếu tư liệu/file nguồn.", scene.Id));
                            continue;
                        }
                        try
                        {
                            if (asset.Kind == "video")
                            {
                                if (!durations.TryGetValue(aid, out var duration))
                                    durations[aid] = duration = await MediaProcess.Duration(
                                        store.AssetPath(asset),
                                        token
                                    );
                                if (scene.Start >= duration)
                                    issues.Add(new("error", "Điểm vào nằm ngoài video.", scene.Id));
                                else if (
                                    scene.Start + scene.Duration * (scene.Options?.Speed ?? 1)
                                    > duration + .05
                                )
                                    issues.Add(
                                        new(
                                            "warning",
                                            "Nguồn ngắn hơn cảnh; phần cuối sẽ giữ khung hình.",
                                            scene.Id
                                        )
                                    );
                            }
                            else if (validImages.Add(aid))
                            {
                                var raw = await MediaProcess.Run(
                                    "ffprobe",
                                    [
                                        "-v",
                                        "error",
                                        "-select_streams",
                                        "v:0",
                                        "-show_entries",
                                        "stream=width,height",
                                        "-of",
                                        "json",
                                        store.AssetPath(asset),
                                    ],
                                    store.Root,
                                    token
                                );
                                using var parsed = JsonDocument.Parse(raw);
                                if (parsed.RootElement.GetProperty("streams").GetArrayLength() == 0)
                                    throw new IOException("No image stream.");
                            }
                        }
                        catch (Exception e) when (e is IOException or JsonException)
                        {
                            issues.Add(new("error", "Không đọc được metadata tư liệu.", scene.Id));
                        }
                        if (doc.AutoCaptions && string.IsNullOrWhiteSpace(scene.Narration))
                            issues.Add(new("warning", "Cảnh chưa có lời để tạo phụ đề.", scene.Id));
                    }
                    foreach (
                        var (aid, kind) in new[] { (doc.VoiceId, "voice"), (doc.MusicId, "music") }
                    )
                    {
                        if (aid is not Guid audioId)
                            continue;
                        if (
                            !assets.TryGetValue(audioId, out var a)
                            || !File.Exists(store.AssetPath(a))
                        )
                        {
                            issues.Add(new("error", "Thiếu file " + kind + "."));
                            continue;
                        }
                        try
                        {
                            var duration = await MediaProcess.AudioDuration(
                                store.AssetPath(a),
                                token
                            );
                            if (kind == "voice" && (doc.Audio?.VoiceStart ?? 0) >= duration)
                                issues.Add(new("error", "Điểm bắt đầu giọng nằm ngoài nguồn."));
                            if (
                                kind == "music"
                                && doc.Audio?.MusicLoop == false
                                && (duration - (doc.Effects?.MusicStart ?? 0))
                                    / (doc.Audio?.MusicSpeed ?? 1)
                                    < doc.Scenes!.Where(s => s.Options?.Enabled != false)
                                        .Sum(s => s.Duration)
                            )
                                issues.Add(
                                    new(
                                        "warning",
                                        "Nhạc không lặp kết thúc trước hình; phần còn lại im lặng."
                                    )
                                );
                            if (kind == "music" && (doc.Effects?.MusicStart ?? 0) >= duration)
                                issues.Add(new("error", "Điểm bắt đầu nhạc nằm ngoài nguồn."));
                            if (
                                kind == "voice"
                                && (duration - (doc.Audio?.VoiceStart ?? 0))
                                    / (doc.Effects?.VoiceSpeed ?? 1)
                                    + (doc.Effects?.VoiceDelay ?? 0)
                                    > doc.Scenes!.Where(s => s.Options?.Enabled != false)
                                        .Sum(s => s.Duration) + .1
                            )
                                issues.Add(
                                    new("warning", "Giọng dài hơn storyboard; phần dư sẽ bị cắt.")
                                );
                        }
                        catch (IOException)
                        {
                            issues.Add(new("error", "Không đọc được track " + kind + "."));
                        }
                    }
                    if (
                        doc.VoiceId is null
                        && doc.Scenes!.Any(s => !string.IsNullOrWhiteSpace(s.Narration))
                    )
                        issues.Add(
                            new(
                                "warning",
                                "Có lời cảnh nhưng chưa chọn giọng. Dựng không tự gọi TTS."
                            )
                        );
                    return Results.Ok(
                        new
                        {
                            version = saved.Version,
                            ready = !issues.Any(i => i.Severity == "error"),
                            issues,
                        }
                    );
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem("Kiểm tra quá 30 giây.", statusCode: 504);
                }
                finally
                {
                    ProbeGate.Release();
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/storyboard/render-variants",
            async (Guid id, RenderDocument req, StudioDb db, CancellationToken ct) =>
            {
                await Intake.WaitAsync(ct);
                try
                {
                    var saved = await db
                        .ProjectDocuments.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                    if (saved is null || saved.Version != req.Version)
                        return Conflict();
                    var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                    var error = await Storyboard.Validate(id, doc, db, true, ct);
                    if (error is not null)
                        return Results.BadRequest(new { error });
                    if (
                        await db.Jobs.CountAsync(
                            j =>
                                j.ProjectId == id
                                && (j.Status == "Queued" || j.Status == "Running"),
                            ct
                        ) > 98
                    )
                        return Results.BadRequest(
                            new
                            {
                                error = "Hàng đợi quá nhiều tác vụ; đợi bớt trước khi tạo biến thể.",
                            }
                        );
                    var jobs = new[] { "9:16", "16:9" }
                        .Select(r => new RenderJob
                        {
                            ProjectId = id,
                            RequestJson = JsonSerializer.Serialize(
                                Storyboard.CreateRequest(doc with { Ratio = r })
                            ),
                        })
                        .ToArray();
                    db.Jobs.AddRange(jobs);
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted(
                        value: new { jobs = jobs.Select(j => new { j.Id, j.Status }) }
                    );
                }
                finally
                {
                    Intake.Release();
                }
            }
        );
        app.MapPost(
            "/api/jobs/{id:guid}/duplicate",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                await Intake.WaitAsync(ct);
                try
                {
                    var source = await db
                        .Jobs.AsNoTracking()
                        .FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (source is null)
                        return Results.NotFound();
                    if (source.Status != "Succeeded")
                        return Results.Conflict(
                            new { error = "Chỉ dựng lại theo snapshot của job đã hoàn tất." }
                        );
                    if (
                        await db.Jobs.CountAsync(
                            j =>
                                j.ProjectId == source.ProjectId
                                && (j.Status == "Queued" || j.Status == "Running"),
                            ct
                        ) >= 100
                    )
                        return Results.BadRequest(new { error = "Hàng đợi quá nhiều tác vụ." });
                    var copy = new RenderJob
                    {
                        ProjectId = source.ProjectId,
                        RequestJson = source.RequestJson,
                    };
                    db.Jobs.Add(copy);
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted(value: new { copy.Id, copy.Status });
                }
                finally
                {
                    Intake.Release();
                }
            }
        );
    }
}
