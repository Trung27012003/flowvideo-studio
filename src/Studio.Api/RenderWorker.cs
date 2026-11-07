using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class RenderWorker(IServiceScopeFactory scopes, Storage store, ILogger<RenderWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
                var job = await db
                    .Jobs.Where(x => x.Status == "Queued")
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                job.Status = "Running";
                job.Message = "Đang chuẩn hóa media";
                await db.SaveChangesAsync(stoppingToken);
                try
                {
                    await Render(job, db, stoppingToken);
                    job.Status = "Succeeded";
                    job.Message = "Hoàn tất";
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Render failed: {JobId}", job.Id);
                    job.Status = "Failed";
                    job.Message =
                        "Render lỗi. Kiểm tra file nguồn, SRT và FFmpeg; xem log backend để biết chi tiết.";
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Worker iteration failed");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task Render(RenderJob job, StudioDb db, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RenderRequest>(job.RequestJson)!;
        var assets = await db
            .Assets.Where(x => x.ProjectId == job.ProjectId)
            .ToDictionaryAsync(x => x.Id, ct);
        var temp = Path.Combine(store.Root, "temp", job.Id.ToString("N"));
        Directory.CreateDirectory(temp);
        var (w, h) = req.Ratio == "16:9" ? (1280, 720) : (720, 1280);
        try
        {
            var parts = new List<string>();
            for (var i = 0; i < req.AssetIds.Length; i++)
            {
                var a = assets[req.AssetIds[i]];
                var part = $"part{i}.mp4";
                parts.Add(part);
                var args = new List<string>
                {
                    "-y",
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-threads",
                    "2",
                };
                if (a.Kind == "image")
                    args.AddRange(["-loop", "1"]);
                if (req.Mode == "loop" && a.Kind == "video")
                    args.AddRange(["-stream_loop", "-1"]);
                args.AddRange([
                    "-i",
                    store.AssetPath(a),
                    "-t",
                    (
                        req.Mode == "loop" ? req.Duration
                        : a.Kind == "image" ? 5
                        : Math.Min(req.Duration, 600)
                    ).ToString(),
                    "-vf",
                    $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30",
                    "-an",
                    "-c:v",
                    "libx264",
                    "-preset",
                    "veryfast",
                    "-crf",
                    "23",
                    "-pix_fmt",
                    "yuv420p",
                    part,
                ]);
                await Run(args, temp, ct);
                job.Message = $"Đã xử lý {i + 1}/{req.AssetIds.Length} cảnh";
                await db.SaveChangesAsync(ct);
            }
            await File.WriteAllLinesAsync(
                Path.Combine(temp, "list.txt"),
                parts.Select(x => $"file '{x}'"),
                ct
            );
            var final = new List<string>
            {
                "-y",
                "-nostdin",
                "-hide_banner",
                "-loglevel",
                "error",
                "-threads",
                "2",
                "-f",
                "concat",
                "-safe",
                "0",
                "-i",
                "list.txt",
            };
            int audioIndex = 1;
            int? voiceIndex = null,
                musicIndex = null;
            if (req.VoiceId is Guid voice)
            {
                voiceIndex = audioIndex++;
                final.AddRange(["-i", store.AssetPath(assets[voice])]);
            }
            if (req.MusicId is Guid music)
            {
                musicIndex = audioIndex++;
                final.AddRange(["-stream_loop", "-1", "-i", store.AssetPath(assets[music])]);
            }
            if (req.SubtitleId is Guid sub)
            {
                File.Copy(store.AssetPath(assets[sub]), Path.Combine(temp, "captions.srt"), true);
                final.AddRange([
                    "-vf",
                    "subtitles=captions.srt:force_style='FontName=DejaVu Sans,FontSize=18,MarginV=36'",
                ]);
            }
            final.AddRange(["-map", "0:v:0"]);
            if (voiceIndex is int vi && musicIndex is int mi)
            {
                final.AddRange([
                    "-filter_complex",
                    $"[{vi}:a]apad[v];[{mi}:a]volume=0.18[m];[v][m]amix=inputs=2:duration=longest:normalize=0[a]",
                    "-map",
                    "[a]",
                ]);
            }
            else if (voiceIndex is int onlyVoice)
            {
                final.AddRange(["-af", "apad", "-map", $"{onlyVoice}:a"]);
            }
            else if (musicIndex is int onlyMusic)
            {
                final.AddRange(["-af", "volume=0.18", "-map", $"{onlyMusic}:a"]);
            }
            final.AddRange([
                "-t",
                req.Duration.ToString(),
                "-shortest",
                "-c:v",
                "libx264",
                "-preset",
                "veryfast",
                "-crf",
                "23",
                "-pix_fmt",
                "yuv420p",
                "-c:a",
                "aac",
                "-movflags",
                "+faststart",
                store.Output(job.Id),
            ]);
            job.Message = "Đang ghép và xuất MP4";
            await db.SaveChangesAsync(ct);
            await Run(final, temp, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException) { }
        }
    }

    private static async Task Run(IEnumerable<string> args, string directory, CancellationToken ct)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            WorkingDirectory = directory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start ffmpeg");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        await output;
        var errors = await error;
        if (process.ExitCode != 0)
            throw new IOException(errors.Length > 4000 ? errors[^4000..] : errors);
    }
}
