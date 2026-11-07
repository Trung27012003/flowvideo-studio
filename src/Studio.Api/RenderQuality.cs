using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class RenderQaReport
{
    public Guid JobId { get; set; }
    public Guid ProjectId { get; set; }
    public string ReportJson { get; set; } = "";
    public DateTime CheckedAt { get; set; }
}

public record QaCheck(
    string Code,
    string Severity,
    string Message,
    string? Expected = null,
    string? Actual = null
);

public record QualityReport(
    Guid JobId,
    DateTime CheckedAt,
    string Result,
    string RecipeSha256,
    RenderMediaInfo? Media,
    QaCheck[] Checks,
    bool FullDecode = false
);

public static class RenderQuality
{
    static readonly SemaphoreSlim Gate = new(2, 2);

    public static Task Upgrade(StudioDb db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "RenderQaReports" ("JobId" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"ReportJson" TEXT NOT NULL,"CheckedAt" TEXT NOT NULL,
            FOREIGN KEY("JobId") REFERENCES "Jobs"("Id") ON DELETE CASCADE);
            """
        );

    static double Rate(string? value)
    {
        var p = value?.Split('/');
        return
            p?.Length == 2
            && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && b > 0
            ? a / b
            : 0;
    }

    public static async Task<QualityReport> Inspect(
        RenderJob job,
        StudioDb db,
        Storage store,
        CancellationToken ct
    )
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var token = timeout.Token;
            var req = JsonSerializer.Deserialize<RenderRequest>(job.RequestJson)!;
            var checks = new List<QaCheck>();
            RenderMediaInfo? info = null;
            void Check(
                string code,
                bool ok,
                string message,
                string? expected = null,
                string? actual = null
            ) => checks.Add(new(code, ok ? "pass" : "error", message, expected, actual));
            var file = store.Output(job.Id);
            Check(
                "output-file",
                File.Exists(file) && new FileInfo(file).Length > 0,
                "File bản xuất tồn tại và không rỗng"
            );
            if (checks.All(c => c.Severity != "error"))
            {
                try
                {
                    info = await JobDeliveries.Probe(file, store, token);
                }
                catch (IOException)
                {
                    checks.Add(new("probe", "error", "ffprobe không đọc được bản xuất"));
                }
                if (info is not null)
                {
                    Check(
                        "video-codec",
                        info.VideoCodec == "h264",
                        "Luồng video H.264",
                        "h264",
                        info.VideoCodec
                    );
                    var longEdge = req.Resolution == 1080 ? 1920 : 1280;
                    var w = req.Ratio == "16:9" ? longEdge : req.Resolution;
                    var h = req.Ratio == "16:9" ? req.Resolution : longEdge;
                    Check(
                        "dimensions",
                        info.Width == w && info.Height == h,
                        "Kích thước khớp recipe",
                        $"{w}x{h}",
                        $"{info.Width}x{info.Height}"
                    );
                    var fps = req.Effects?.Fps ?? 30;
                    Check(
                        "frame-rate",
                        Math.Abs(Rate(info.FrameRate) - fps) < .01,
                        "Frame rate khớp recipe",
                        fps.ToString(),
                        info.FrameRate
                    );
                    var tolerance = .12 + req.AssetIds.Length / (double)fps;
                    double? expected =
                        req.Mode == "loop" ? req.Duration
                        : req.Scenes is not null
                            ? Math.Min(req.Duration, req.Scenes.Sum(s => s.Duration))
                        : null;
                    Check(
                        "duration",
                        info.Duration > 0
                            && (
                                expected is double exact
                                    ? Math.Abs(info.Duration - exact) <= tolerance
                                    : info.Duration <= req.Duration + tolerance
                            ),
                        "Thời lượng trong ngân sách frame của recipe",
                        expected?.ToString("0.###", CultureInfo.InvariantCulture)
                            ?? "0 < duration <= " + req.Duration,
                        info.Duration.ToString("0.###", CultureInfo.InvariantCulture)
                    );
                    var audio = req.VoiceId.HasValue || req.MusicId.HasValue;
                    Check(
                        "audio-stream",
                        (info.AudioCodec is not null) == audio,
                        "Luồng audio khớp lựa chọn giọng/nhạc",
                        audio ? "Có audio" : "Không audio",
                        info.AudioCodec ?? "Không audio"
                    );
                    if (audio && info.AudioCodec is not null)
                    {
                        Check(
                            "audio-metadata",
                            info.SampleRate > 0 && info.Channels > 0,
                            "Audio có sample rate và số kênh hợp lệ",
                            null,
                            $"{info.SampleRate} Hz / {info.Channels} kênh"
                        );
                        if (req.Effects?.Mono == true)
                            Check(
                                "mono",
                                info.Channels == 1,
                                "Đầu ra mono theo recipe",
                                "1",
                                info.Channels.ToString()
                            );
                    }
                    var needsSrt =
                        req.SubtitleId.HasValue
                        || req.AutoCaptions
                            && req.Scenes?.Any(s =>
                                s.Options?.Caption != false && !string.IsNullOrWhiteSpace(s.Caption)
                            ) == true;
                    if (needsSrt)
                    {
                        var srt = store.Subtitles(job.Id);
                        Check("subtitle-file", File.Exists(srt), "Có sidecar phụ đề đã dùng");
                        if (File.Exists(srt) && new FileInfo(srt).Length > 1048576)
                            checks.Add(
                                new(
                                    "subtitle-size",
                                    "warning",
                                    "SRT vượt 1 MiB; chưa phân tích nội dung phụ đề."
                                )
                            );
                        else if (File.Exists(srt))
                            try
                            {
                                var cues = Subtitles.Parse(await File.ReadAllTextAsync(srt, token));
                                Check(
                                    "subtitle-format",
                                    cues.Count > 0,
                                    "Phụ đề parse được và có cue"
                                );
                                if (cues.Any(c => c.End > info.Duration + .15))
                                    checks.Add(
                                        new(
                                            "subtitle-tail",
                                            "warning",
                                            "Có cue vượt thời lượng video; xem lại SRT trước khi bàn giao."
                                        )
                                    );
                            }
                            catch (ArgumentException)
                            {
                                checks.Add(
                                    new("subtitle-format", "error", "Sidecar phụ đề không hợp lệ")
                                );
                            }
                    }
                }
            }
            var report = new QualityReport(
                job.Id,
                DateTime.UtcNow,
                checks.Any(c => c.Severity == "error") ? "Failed"
                    : checks.Any(c => c.Severity == "warning") ? "Warnings"
                    : "Passed",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.RequestJson))),
                info,
                checks.ToArray()
            );
            var json = JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO RenderQaReports (JobId,ProjectId,ReportJson,CheckedAt) VALUES ({job.Id},{job.ProjectId},{json},{report.CheckedAt}) ON CONFLICT(JobId) DO UPDATE SET ReportJson={json},CheckedAt={report.CheckedAt}",
                ct
            );
            return report;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void MapRenderQuality(this WebApplication app)
    {
        app.MapGet(
            "/api/jobs/{id:guid}/qa",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                var row = await db
                    .RenderQaReports.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.JobId == id, ct);
                return row is null
                    ? Results.NotFound()
                    : Results.Ok(
                        JsonSerializer.Deserialize<QualityReport>(
                            row.ReportJson,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        )
                    );
            }
        );
        app.MapPost(
            "/api/jobs/{id:guid}/qa",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (job is null)
                    return Results.NotFound();
                if (job.Status != "Succeeded")
                    return Results.Conflict(
                        new { error = "Chỉ kiểm tra lại bản xuất đã hoàn tất." }
                    );
                try
                {
                    return Results.Ok(await Inspect(job, db, store, ct));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Json(
                        new { error = "Kiểm tra quá 20 giây; chưa có báo cáo mới." },
                        statusCode: 504
                    );
                }
                catch (IOException)
                {
                    return Results.Json(
                        new { error = "Không đọc được file bản xuất." },
                        statusCode: 422
                    );
                }
            }
        );
    }
}
