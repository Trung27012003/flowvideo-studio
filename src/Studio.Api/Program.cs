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
builder.Services.AddHostedService<RenderWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
    await db.Database.EnsureCreatedAsync();
    await db
        .Jobs.Where(x => x.Status == "Running")
        .ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.Status, "Failed")
                .SetProperty(x => x.Message, "Ứng dụng đã dừng giữa chừng. Hãy thử lại.")
        );
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.1.0" }));
app.MapGet(
    "/api/capabilities",
    (IConfiguration c) =>
        new
        {
            scriptAi = !string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]),
            aiVideo = false,
            tts = false,
        }
);
app.MapGet(
    "/api/projects",
    async (StudioDb db) => await db.Projects.OrderByDescending(x => x.CreatedAt).ToListAsync()
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
    async (Guid id, StudioDb db) => await db.Assets.Where(x => x.ProjectId == id).ToListAsync()
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
        await using (var stream = File.Create(store.AssetPath(asset)))
            await file.CopyToAsync(stream, ct);
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);
        return Results.Ok(asset);
    }
);
app.MapGet(
    "/api/assets/{id:guid}/file",
    async (Guid id, StudioDb db, Storage s) =>
    {
        var a = await db.Assets.FindAsync(id);
        if (a is null)
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
    async (Guid id, StudioDb db) =>
        await db
            .Jobs.Where(x => x.ProjectId == id)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync()
);
app.MapPost(
    "/api/projects/{id:guid}/renders",
    async (Guid id, RenderRequest req, StudioDb db) =>
    {
        if (!await db.Projects.AnyAsync(x => x.Id == id))
            return Results.NotFound();
        if (
            req.AssetIds is null
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
        var assets = await db.Assets.Where(x => x.ProjectId == id).ToListAsync();
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
        var j = await db.Jobs.FindAsync(id);
        if (j is null)
            return Results.NotFound();
        if (j.Status != "Failed")
            return Results.Conflict(new { error = "Chỉ thử lại job lỗi." });
        j.Status = "Queued";
        j.Message = "Đang chờ thử lại";
        await db.SaveChangesAsync();
        return Results.Ok(j);
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
    async (ScriptRequest req, IConfiguration c, IHttpClientFactory factory, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(req.Idea) || req.Idea.Length > 4000)
            return Results.BadRequest(new { error = "Ý tưởng cần từ 1–4000 ký tự." });
        var key = c["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            return Results.Problem("Chưa cấu hình OPENAI_API_KEY ở backend.", statusCode: 503);
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
        catch (HttpRequestException)
        {
            return Results.Problem("Không kết nối được dịch vụ AI.", statusCode: 502);
        }
    }
);
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
