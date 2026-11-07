using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class RenderWorker(
    IServiceScopeFactory scopes,
    Storage store,
    JobControl control,
    ILogger<RenderWorker> log
) : BackgroundService
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
                var cancellation = control.Register(job.Id, stoppingToken);
                try
                {
                    // Register before claiming so cancellation cannot miss a newly started job.
                    var claimed = await db
                        .Jobs.Where(x => x.Id == job.Id && x.Status == "Queued")
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.Status, "Running")
                                    .SetProperty(x => x.Message, "Đang chuẩn hóa media"),
                            stoppingToken
                        );
                    if (claimed == 0)
                        continue;
                    await db.Entry(job).ReloadAsync(stoppingToken);
                    try
                    {
                        await Render(job, db, cancellation.Token);
                        var completed = await db
                            .Jobs.Where(x => x.Id == job.Id && x.Status == "Running")
                            .ExecuteUpdateAsync(
                                s =>
                                    s.SetProperty(x => x.Status, "Succeeded")
                                        .SetProperty(x => x.Message, "Hoàn tất"),
                                stoppingToken
                            );
                        if (completed == 0)
                            DeleteOutput(job.Id);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        DeleteOutput(job.Id);
                        throw;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        DeleteOutput(job.Id);
                    }
                    catch (Exception ex)
                    {
                        DeleteOutput(job.Id);
                        log.LogError(ex, "Render failed: {JobId}", job.Id);
                        await db
                            .Jobs.Where(x => x.Id == job.Id && x.Status == "Running")
                            .ExecuteUpdateAsync(
                                s =>
                                    s.SetProperty(x => x.Status, "Failed")
                                        .SetProperty(
                                            x => x.Message,
                                            "Render lỗi. Kiểm tra file nguồn, điểm cắt, SRT và FFmpeg; xem log backend để biết chi tiết."
                                        ),
                                stoppingToken
                            );
                    }
                }
                finally
                {
                    control.Remove(job.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Worker iteration failed");
                try
                {
                    await Task.Delay(2000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void DeleteOutput(Guid id)
    {
        try
        {
            File.Delete(store.Output(id));
            File.Delete(store.Subtitles(id));
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Cannot delete output for {JobId}", id);
        }
    }

    private async Task Render(RenderJob job, StudioDb db, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RenderRequest>(job.RequestJson)!;
        var needed = req
            .AssetIds.Concat(new[] { req.VoiceId, req.MusicId, req.SubtitleId }.OfType<Guid>())
            .Distinct()
            .ToArray();
        var assets = await db
            .Assets.AsNoTracking()
            .Where(x => x.ProjectId == job.ProjectId && needed.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var cuts = (req.Cuts ?? []).ToDictionary(x => x.AssetId);
        var temp = Path.Combine(store.Root, "temp", job.Id.ToString("N"));
        Directory.CreateDirectory(temp);
        var shortEdge = req.Resolution;
        var longEdge = req.Resolution == 1080 ? 1920 : 1280;
        var (w, h) = req.Ratio == "16:9" ? (longEdge, shortEdge) : (shortEdge, longEdge);
        try
        {
            var parts = new List<string>();
            var cache = new Dictionary<(Guid, double, double), (string File, double Duration)>();
            var durations = new Dictionary<Guid, double>();
            double remaining = req.Duration;
            var captions = new List<SubtitleCue>();
            double elapsed = 0;
            for (var index = 0; index < req.AssetIds.Length; index++)
            {
                var id = req.AssetIds[index];
                var scene = req.Scenes?[index];
                ct.ThrowIfCancellationRequested();
                if (remaining < 1.0 / 60)
                    break;
                var a = assets[id];
                var cut = cuts.GetValueOrDefault(id);
                var start = scene?.Start ?? cut?.Start ?? 0;
                double sourceDuration;
                if (a.Kind == "image")
                    sourceDuration = scene?.Duration ?? (req.Mode == "loop" ? 1 : 5);
                else if (!durations.TryGetValue(id, out sourceDuration))
                {
                    sourceDuration = await MediaProcess.Duration(store.AssetPath(a), ct);
                    durations[id] = sourceDuration;
                }
                if (
                    start >= sourceDuration
                    || (cut?.End is double end && end > sourceDuration + 0.05)
                )
                    throw new IOException("Trim is outside source duration.");
                var available = Math.Min(cut?.End ?? sourceDuration, sourceDuration) - start;
                var take = Math.Min(remaining, scene?.Duration ?? available);
                var key = (id, start, take);
                if (!cache.TryGetValue(key, out var normalized))
                {
                    var part = $"part{cache.Count}.mp4";
                    var args = new List<string>
                    {
                        "-y",
                        "-nostdin",
                        "-hide_banner",
                        "-loglevel",
                        "error",
                    };
                    if (a.Kind == "image")
                        args.AddRange(["-loop", "1"]);
                    if (start > 0)
                        args.AddRange(["-ss", Number(start)]);
                    var filter =
                        $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30";
                    if (scene is not null && a.Kind == "video")
                        filter += $",tpad=stop_mode=clone:stop_duration={Number(take)}";
                    args.AddRange([
                        "-i",
                        store.AssetPath(a),
                        "-t",
                        Number(take),
                        "-map",
                        "0:v:0",
                        "-vf",
                        filter,
                        "-an",
                        "-c:v",
                        "libx264",
                        "-threads",
                        "2",
                        "-preset",
                        "veryfast",
                        "-crf",
                        "23",
                        "-pix_fmt",
                        "yuv420p",
                        part,
                    ]);
                    await MediaProcess.Run("ffmpeg", args, temp, ct);
                    normalized = (part, await MediaProcess.Duration(Path.Combine(temp, part), ct));
                    cache[key] = normalized;
                }
                parts.Add(normalized.File);
                var actual = Math.Min(remaining, normalized.Duration);
                if (req.AutoCaptions && !string.IsNullOrWhiteSpace(scene?.Caption))
                    captions.Add(
                        new(
                            elapsed,
                            elapsed + actual,
                            scene.Caption.Replace('\r', ' ').Replace('\n', ' ')
                        )
                    );
                elapsed += actual;
                remaining -= normalized.Duration;
                job.Message = $"Đã xử lý {parts.Count}/{req.AssetIds.Length} cảnh";
                await db.SaveChangesAsync(ct);
            }
            var final = new List<string> { "-y", "-nostdin", "-hide_banner", "-loglevel", "error" };
            if (req.Mode == "loop")
                final.AddRange(["-stream_loop", "-1", "-i", parts[0]]);
            else
            {
                await File.WriteAllLinesAsync(
                    Path.Combine(temp, "list.txt"),
                    parts.Select(x => $"file '{x}'"),
                    ct
                );
                final.AddRange(["-f", "concat", "-safe", "0", "-i", "list.txt"]);
            }
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
            var hasCaptions = req.SubtitleId is not null || captions.Count > 0;
            if (captions.Count > 0)
                await File.WriteAllTextAsync(
                    Path.Combine(temp, "captions.srt"),
                    Subtitles.Write(captions),
                    ct
                );
            if (req.SubtitleId is Guid sub)
            {
                File.Copy(store.AssetPath(assets[sub]), Path.Combine(temp, "captions.srt"), true);
            }
            if (hasCaptions)
                final.AddRange([
                    "-vf",
                    "subtitles=captions.srt:force_style='FontName=DejaVu Sans,FontSize=18,MarginV=36'",
                ]);
            final.AddRange(["-map", "0:v:0"]);
            if (voiceIndex is int vi && musicIndex is int mi)
                final.AddRange([
                    "-filter_complex",
                    $"[{vi}:a]volume={Number(req.VoiceGain)},apad[v];[{mi}:a]volume={Number(req.MusicGain)}[m];[v][m]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:level=false:latency=true[a]",
                    "-map",
                    "[a]",
                ]);
            else if (voiceIndex is int v)
                final.AddRange([
                    "-af",
                    $"volume={Number(req.VoiceGain)},apad,alimiter=limit=0.95:level=false:latency=true",
                    "-map",
                    $"{v}:a",
                ]);
            else if (musicIndex is int m)
                final.AddRange([
                    "-af",
                    $"volume={Number(req.MusicGain)},alimiter=limit=0.95:level=false:latency=true",
                    "-map",
                    $"{m}:a",
                ]);
            final.AddRange([
                "-t",
                req.Duration.ToString(CultureInfo.InvariantCulture),
                "-shortest",
                "-c:v",
                hasCaptions ? "libx264" : "copy",
            ]);
            if (hasCaptions)
                final.AddRange([
                    "-threads",
                    "2",
                    "-preset",
                    "veryfast",
                    "-crf",
                    "23",
                    "-pix_fmt",
                    "yuv420p",
                ]);
            final.AddRange(["-c:a", "aac", "-movflags", "+faststart", "export.mp4"]);
            job.Message = "Đang ghép và xuất MP4";
            await db.SaveChangesAsync(ct);
            await MediaProcess.Run("ffmpeg", final, temp, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(Path.Combine(temp, "export.mp4"), store.Output(job.Id), true);
            if (hasCaptions)
                File.Copy(Path.Combine(temp, "captions.srt"), store.Subtitles(job.Id), true);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Cannot clean temporary files for {JobId}", job.Id);
            }
        }
    }

    private static string Number(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);
}
