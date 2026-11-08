using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Studio;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Storage>();
builder.Services.AddDbContext<StudioDb>(
    (sp, o) =>
        o.UseSqlite(
            $"Data Source={Path.Combine(sp.GetRequiredService<Storage>().Root, "studio.db")}"
        )
);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 262144000);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 262144000);
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("OpenAiAudio", client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddScoped<AudioProvider>();
builder.Services.AddSingleton<JobControl>();
builder.Services.AddHostedService<RenderWorker>();
builder.Services.AddSingleton<ImageIntake>();
builder.Services.AddHttpClient("OpenAiImage", client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddScoped<ImageProvider>();
builder.Services.AddHostedService<ImageWorker>();
builder.Services.AddSingleton<VideoIntake>();
builder
    .Services.AddHttpClient("GeminiVideo", client => client.Timeout = TimeSpan.FromMinutes(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<VideoProvider>();
builder.Services.AddHostedService<VideoWorker>();
builder.Services.AddSingleton<ProductionGate>();
builder.Services.AddHostedService<ProductionWorker>();
builder.Services.AddSingleton<StoryPlanGate>();
builder
    .Services.AddHttpClient("OpenAiStory", client => client.Timeout = TimeSpan.FromSeconds(90))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<StoryPlanProvider>();
builder.Services.AddHostedService<StoryPlanWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
    await db.Database.EnsureCreatedAsync();
    await Storyboard.Upgrade(db);
    await CreativeProfiles.Upgrade(db);
    await AiBudget.Upgrade(db);
    await StoryPlans.Upgrade(db);
    await RenderQuality.Upgrade(db);
    await ImageFeatures.Upgrade(db);
    await VideoFeatures.Upgrade(db);
    await ProductionWorkflow.Upgrade(db);
    await WorkspaceWorkflow.Upgrade(db);
    await LibraryTools.Upgrade(db);
    await db
        .Jobs.Where(x => x.Status == "Running")
        .ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.Status, "Failed")
                .SetProperty(x => x.Message, "Ứng dụng đã dừng giữa chừng. Hãy thử lại.")
        );
}
app.MapAiBudget();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.15.0" }));
app.MapGet(
    "/api/capabilities",
    (IConfiguration c) =>
        new
        {
            scriptAi = !string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]),
            aiVideo = !string.IsNullOrWhiteSpace(c["GEMINI_API_KEY"]),
            imageAi = !string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]),
            tts = !string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]),
            transcription = !string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]),
            voices = AudioProvider.Voices,
        }
);
app.MapGet(
    "/api/projects",
    async (StudioDb db) =>
        await db.Projects.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync()
);
app.MapPost(
    "/api/projects",
    async (NameRequest req, StudioDb db) =>
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 120)
            return Results.BadRequest(new { error = "Tên dự án cần từ 1–120 ký tự." });
        var p = new Project { Name = req.Name.Trim() };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        return Results.Created($"/api/projects/{p.Id}", p);
    }
);
app.MapGet(
    "/api/projects/{id:guid}/assets",
    async (Guid id, StudioDb db) =>
        await db.Assets.AsNoTracking().Where(x => x.ProjectId == id).ToListAsync()
);
app.MapPost(
    "/api/projects/{id:guid}/assets",
    async (Guid id, HttpRequest request, StudioDb db, Storage store, CancellationToken ct) =>
    {
        if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
            return Results.NotFound();
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Cần gửi multipart/form-data." });
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > 262144000)
            return Results.BadRequest(new { error = "File phải lớn hơn 0 và tối đa 250 MB." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var kind = ext switch
        {
            ".mp4" or ".mov" or ".webm" or ".mkv" => "video",
            ".jpg" or ".jpeg" or ".png" => "image",
            ".mp3" or ".wav" or ".m4a" or ".ogg" => "audio",
            ".srt" => "subtitle",
            _ => "",
        };
        if (kind == "")
            return Results.BadRequest(new { error = "Định dạng chưa hỗ trợ." });
        var asset = new Asset
        {
            ProjectId = id,
            Name = Path.GetFileName(file.FileName),
            Kind = kind,
            StoredName = Guid.NewGuid().ToString("N") + ext,
        };
        var committed = false;
        try
        {
            await using (var stream = File.Create(store.AssetPath(asset)))
                await file.CopyToAsync(stream, ct);
            db.Assets.Add(asset);
            await db.SaveChangesAsync(ct);
            committed = true;
        }
        finally
        {
            if (!committed)
                File.Delete(store.AssetPath(asset));
        }
        return Results.Ok(asset);
    }
);
app.MapGet(
    "/api/assets/{id:guid}/file",
    async (Guid id, StudioDb db, Storage s) =>
    {
        var a = await db.Assets.FindAsync(id);
        if (a is null || !File.Exists(s.AssetPath(a)))
            return Results.NotFound();
        var mime = Path.GetExtension(a.StoredName).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "text/plain",
        };
        return Results.File(s.AssetPath(a), mime, enableRangeProcessing: true);
    }
);
app.MapGet(
    "/api/projects/{id:guid}/jobs",
    async (Guid id, StudioDb db, Storage store) =>
    {
        var rows = await db
            .Jobs.AsNoTracking()
            .Where(x => x.ProjectId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                x.Status,
                x.Message,
                x.CreatedAt,
                x.RequestJson,
            })
            .ToListAsync();
        return Results.Ok(
            rows.Select(x => new
            {
                x.Id,
                x.ProjectId,
                x.Status,
                x.Message,
                x.CreatedAt,
                hasAudio = HasAudio(x.RequestJson),
                hasSubtitles = x.Status == "Succeeded" && File.Exists(store.Subtitles(x.Id)),
            })
        );
    }
);
app.MapPost(
    "/api/projects/{id:guid}/renders",
    async (Guid id, RenderRequest req, StudioDb db) =>
    {
        if (!await db.Projects.AnyAsync(x => x.Id == id))
            return Results.NotFound();
        if (
            (req.Branding is not null && !req.Branding.Valid())
            || (req.Transitions is not null && !req.Transitions.Valid(req.Mode))
            || (req.Transitions is { Style: not "cut" } && req.AssetIds?.Length > 10)
            || (req.Loop is not null && !req.Loop.Valid(req.Mode, req.Resolution))
            || (req.Audio is not null && !req.Audio.Valid())
            || (req.Captions is not null && !req.Captions.Valid())
            || (req.Effects is not null && !req.Effects.Valid())
            || req.Fit is not ("contain" or "cover")
            || !double.IsFinite(req.FadeSeconds)
            || req.FadeSeconds is < 0 or > 2
            || req.Resolution is not (720 or 1080)
            || !double.IsFinite(req.VoiceGain)
            || req.VoiceGain is < 0 or > 2
            || !double.IsFinite(req.MusicGain)
            || req.MusicGain is < 0 or > 2
            || req.AssetIds is null
            || req.AssetIds.Length is < 1 or > 30
            || req.Duration is < 1 or > 600
            || (req.Mode != "concat" && req.Mode != "loop")
            || (req.Ratio != "9:16" && req.Ratio != "16:9")
        )
            return Results.BadRequest(
                new { error = "Chọn 1–30 ảnh/video, thời lượng 1–600 giây và chế độ/tỉ lệ hợp lệ." }
            );
        if (req.Mode == "loop" && req.AssetIds.Length != 1)
            return Results.BadRequest(new { error = "Chế độ loop cần đúng một ảnh hoặc video." });
        var referenced = req
            .AssetIds.Concat(new[] { req.VoiceId, req.MusicId, req.SubtitleId }.OfType<Guid>())
            .Distinct()
            .ToArray();
        var assets = await db
            .Assets.AsNoTracking()
            .Where(x => x.ProjectId == id && referenced.Contains(x.Id))
            .ToListAsync();
        if (
            req.AssetIds.Any(a =>
                !assets.Any(x => x.Id == a && (x.Kind == "video" || x.Kind == "image"))
            )
            || (req.VoiceId is Guid v && !assets.Any(x => x.Id == v && x.Kind == "audio"))
            || (req.MusicId is Guid m && !assets.Any(x => x.Id == m && x.Kind == "audio"))
            || (req.SubtitleId is Guid s && !assets.Any(x => x.Id == s && x.Kind == "subtitle"))
        )
            return Results.BadRequest(
                new { error = "Tài nguyên không hợp lệ hoặc không thuộc dự án." }
            );
        var cuts = req.Cuts ?? [];
        if (
            cuts.Length > 30
            || cuts.Any(x => x is null)
            || cuts.Select(x => x.AssetId).Distinct().Count() != cuts.Length
            || cuts.Any(x =>
                !req.AssetIds.Contains(x.AssetId)
                || !assets.Any(a => a.Id == x.AssetId && a.Kind == "video")
                || !double.IsFinite(x.Start)
                || x.Start < 0
                || (x.End is double end && (!double.IsFinite(end) || end <= x.Start))
            )
        )
            return Results.BadRequest(
                new
                {
                    error = "Điểm cắt không hợp lệ. Chỉ cắt video; điểm ra phải lớn hơn điểm vào.",
                }
            );
        if (
            req.Scenes is not null
            && (
                req.Mode != "concat"
                || req.Cuts is { Length: > 0 }
                || req.Scenes.Length != req.AssetIds.Length
                || req.Scenes.Any(x =>
                    x is null
                    || (x.Options is not null && (!x.Options.Valid() || !x.Options.Enabled))
                    || !double.IsFinite(x.Duration)
                    || x.Duration < .1
                    || x.Duration > 600
                    || !double.IsFinite(x.Start)
                    || x.Start < 0
                    || x.Caption is null
                    || x.Caption.Length > 2000
                    || x.Caption.Contains('\0')
                )
                || !req.Scenes.Select(x => x.AssetId).SequenceEqual(req.AssetIds)
                || req.Scenes.Any(x =>
                    assets.Any(a => a.Id == x.AssetId && a.Kind == "image")
                    && (x.Start != 0 || (x.Options?.Speed ?? 1) != 1)
                )
            )
        )
            return Results.BadRequest(
                new { error = "Danh sách cảnh hoặc thời lượng không hợp lệ." }
            );
        if (req.AutoCaptions && (req.Scenes is null || req.SubtitleId is not null))
            return Results.BadRequest(
                new
                {
                    error = "Phụ đề từ cảnh cần storyboard và không dùng đồng thời SRT bên ngoài.",
                }
            );
        if (
            req.Branding?.Logo is LogoOverlay logo
            && !await db.Assets.AnyAsync(a =>
                a.Id == logo.AssetId && a.ProjectId == id && a.Kind == "image"
            )
        )
            return Results.BadRequest(new { error = "Logo phải là ảnh thuộc dự án." });
        var job = new RenderJob { ProjectId = id, RequestJson = JsonSerializer.Serialize(req) };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    }
);
app.MapPost(
    "/api/jobs/{id:guid}/retry",
    async (Guid id, StudioDb db) =>
    {
        var changed = await db
            .Jobs.Where(x => x.Id == id && x.Status == "Failed")
            .ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.Status, "Queued")
                    .SetProperty(x => x.Message, "Đang chờ thử lại")
            );
        if (changed == 0)
        {
            if (!await db.Jobs.AnyAsync(x => x.Id == id))
                return Results.NotFound();
            return Results.Conflict(new { error = "Chỉ thử lại job lỗi." });
        }
        return Results.Ok(
            new
            {
                id,
                status = "Queued",
                message = "Đang chờ thử lại",
            }
        );
    }
);
app.MapPost(
    "/api/jobs/{id:guid}/cancel",
    async (Guid id, StudioDb db, JobControl control, CancellationToken ct) =>
    {
        var changed = await db
            .Jobs.Where(x => x.Id == id && (x.Status == "Queued" || x.Status == "Running"))
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.Status, "Cancelled")
                        .SetProperty(x => x.Message, "Đã hủy tác vụ"),
                ct
            );
        if (changed == 0)
        {
            var status = await db
                .Jobs.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => x.Status)
                .FirstOrDefaultAsync(ct);
            if (status is null)
                return Results.NotFound();
            if (status != "Cancelled")
                return Results.Conflict(new { error = "Chỉ hủy tác vụ đang chờ hoặc đang chạy." });
        }
        control.Cancel(id);
        return Results.Ok(
            new
            {
                id,
                status = "Cancelled",
                message = "Đã hủy tác vụ",
            }
        );
    }
);
app.MapGet(
    "/api/jobs/{id:guid}/download",
    async (Guid id, StudioDb db, Storage s) =>
    {
        var j = await db.Jobs.FindAsync(id);
        return j?.Status == "Succeeded" && File.Exists(s.Output(id))
            ? Results.File(
                s.Output(id),
                "video/mp4",
                $"flowvideo-{id}.mp4",
                enableRangeProcessing: true
            )
            : Results.NotFound();
    }
);
app.MapPost(
    "/api/ai/script",
    async (
        ScriptRequest req,
        StudioDb db,
        IConfiguration c,
        IHttpClientFactory factory,
        CancellationToken ct
    ) =>
    {
        if (string.IsNullOrWhiteSpace(req.Idea) || req.Idea.Length > 4000)
            return Results.BadRequest(new { error = "Ý tưởng cần từ 1–4000 ký tự." });
        var key = c["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            return Results.Problem("Chưa cấu hình OPENAI_API_KEY ở backend.", statusCode: 503);
        if (
            req.ProjectId is not Guid project
            || !await db.Projects.AnyAsync(p => p.Id == project, ct)
        )
            return Results.BadRequest(new { error = "Chọn dự án trước khi gọi AI." });
        await AiBudget.Reserve(db, project, Guid.NewGuid(), [new("story", 1)], ct);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions"
        );
        message.Headers.Authorization = new("Bearer", key);
        message.Content = JsonContent.Create(
            new
            {
                model = c["OPENAI_MODEL"] ?? "gpt-4o-mini",
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "Bạn là biên kịch video ngắn. Viết tiếng Việt, 5 cảnh cho video 30–60 giây. Mỗi cảnh gồm thời lượng ước lượng, hình ảnh, lời đọc, prompt tạo video. Chỉ trả văn bản kịch bản. Không khẳng định đã tạo media.",
                    },
                    new { role = "user", content = req.Idea },
                },
                max_tokens = 1800,
            }
        );
        try
        {
            using var response = await factory.CreateClient().SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(
                    "Nhà cung cấp AI từ chối yêu cầu. Kiểm tra model, key và hạn mức.",
                    statusCode: 502
                );
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Results.Ok(
                new
                {
                    script = json
                        .RootElement.GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString(),
                }
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem(
                "Dịch vụ AI phản hồi quá chậm. Chưa tự động gửi lại yêu cầu.",
                statusCode: 504
            );
        }
        catch (JsonException)
        {
            return Results.Problem("Dịch vụ AI trả dữ liệu không hợp lệ.", statusCode: 502);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Không kết nối được dịch vụ AI.", statusCode: 502);
        }
    }
);
app.MapAudio();
app.MapStoryboard();
app.MapProfiles();
app.MapProjectBackup();
app.MapStoryPlans();
app.MapRenderQuality();
app.MapImages();
app.MapVideos();
app.MapProduction();
app.MapProjectTools();
app.MapWorkspaceTools();
app.MapMediaTools();
app.MapWorkspaceWorkflow();
app.MapLibraryTools();
app.MapJobDeliveries();
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
static bool HasAudio(string json)
{
    var request = JsonSerializer.Deserialize<RenderRequest>(json);
    return request?.VoiceId is not null || request?.MusicId is not null;
}
app.Run();

public partial class Program { }
