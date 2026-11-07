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
        var effects = req.Effects ?? new RenderEffects();
        var audio = req.Audio ?? new AudioTuning();
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
            var cache =
                new Dictionary<(Guid, double, double, string), (string File, double Duration)>();
            var durations = new Dictionary<Guid, double>();
            double remaining = req.Duration;
            var captions = new List<SubtitleCue>();
            double elapsed = 0;
            for (var index = 0; index < req.AssetIds.Length; index++)
            {
                var id = req.AssetIds[index];
                var scene = req.Scenes?[index];
                var options = scene?.Options ?? new SceneOptions();
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
                var available =
                    (Math.Min(cut?.End ?? sourceDuration, sourceDuration) - start) / options.Speed;
                var take = Math.Min(remaining, scene?.Duration ?? available);
                var loopStyle = req.Loop?.Style ?? "direct";
                if (
                    req.Mode == "loop"
                    && (
                        (loopStyle is "reverse" or "pingpong") && take > 8.05
                        || loopStyle == "crossfade" && take > 30.05
                    )
                )
                    throw new IOException(
                        "Chu kỳ quá dài cho kiểu loop đã chọn. Reverse/ping-pong tối đa 8 giây; crossfade tối đa 30 giây."
                    );
                var key = (
                    id,
                    start,
                    take,
                    JsonSerializer.Serialize(
                        options with
                        {
                            Enabled = true,
                            Caption = true,
                            Note = "",
                        }
                    )
                );
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
                    var localEffects = effects with
                    {
                        Brightness = options.Brightness ?? effects.Brightness,
                        Rotation = options.Rotation ?? effects.Rotation,
                    };
                    var filter = localEffects.VideoFilter(
                        w,
                        h,
                        options.Fit == "inherit" ? req.Fit : options.Fit,
                        options.FocusX,
                        options.FocusY,
                        options.Zoom
                    );
                    if (options.Speed != 1)
                        filter = $"setpts=(PTS-STARTPTS)/{Number(options.Speed)}," + filter;
                    if (scene is not null && a.Kind == "video")
                        filter += $",tpad=stop_mode=clone:stop_duration={Number(take)}";
                    var localFade = Math.Min(options.Fade, take / 2);
                    if (localFade > 0)
                        filter +=
                            $",fade=t=in:st=0:d={Number(localFade)},fade=t=out:st={Number(take - localFade)}:d={Number(localFade)}";
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
                        effects.Crf,
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
                if (
                    req.AutoCaptions
                    && options.Caption
                    && !string.IsNullOrWhiteSpace(scene?.Caption)
                )
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
            {
                var cycle = await (req.Loop ?? new LoopOptions()).Prepare(
                    parts[0],
                    temp,
                    elapsed,
                    effects,
                    ct
                );
                final.AddRange(["-stream_loop", "-1", "-i", cycle]);
            }
            else
            {
                await File.WriteAllLinesAsync(
                    Path.Combine(temp, "list.txt"),
                    parts.Select(x => $"file '{x}'"),
                    ct
                );
                final.AddRange(["-f", "concat", "-safe", "0", "-i", "list.txt"]);
            }
            var endTime = req.Mode == "loop" ? req.Duration : Math.Min(req.Duration, elapsed);
            double voiceDuration = 0,
                musicDuration = endTime;
            int audioIndex = 1;
            int? voiceIndex = null,
                musicIndex = null;
            if (req.VoiceId is Guid voice)
            {
                voiceIndex = audioIndex++;
                if (audio.VoiceStart > 0 || audio.VoiceFadeIn > 0 || audio.VoiceFadeOut > 0)
                {
                    var duration = await MediaProcess.AudioDuration(
                        store.AssetPath(assets[voice]),
                        ct
                    );
                    if (audio.VoiceStart >= duration)
                        throw new IOException("Voice start exceeds source duration.");
                    voiceDuration = Math.Max(
                        0,
                        Math.Min(
                            (duration - audio.VoiceStart) / effects.VoiceSpeed,
                            endTime - effects.VoiceDelay
                        )
                    );
                }
                if (audio.VoiceStart > 0)
                    final.AddRange(["-ss", Number(audio.VoiceStart)]);
                final.AddRange(["-i", store.AssetPath(assets[voice])]);
            }
            if (req.MusicId is Guid music)
            {
                musicIndex = audioIndex++;
                if (effects.MusicStart > 0 || !audio.MusicLoop || audio.MusicFadeOut > 0)
                {
                    var duration = await MediaProcess.AudioDuration(
                        store.AssetPath(assets[music]),
                        ct
                    );
                    musicDuration = audio.MusicLoop
                        ? endTime
                        : Math.Min(endTime, (duration - effects.MusicStart) / audio.MusicSpeed);
                    if (effects.MusicStart >= duration)
                        throw new IOException("Music start exceeds source duration.");
                }
                if (audio.MusicLoop)
                    final.AddRange(["-stream_loop", "-1"]);
                if (effects.MusicStart > 0)
                    final.AddRange(["-ss", Number(effects.MusicStart)]);
                final.AddRange(["-i", store.AssetPath(assets[music])]);
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

            var fade = Math.Min(req.FadeSeconds, endTime / 2);
            var videoFilters = new List<string>();
            if (hasCaptions)
                videoFilters.Add(
                    "subtitles=captions.srt:force_style='"
                        + (req.Captions ?? new CaptionStyle()).FilterStyle()
                        + "'"
                );
            if (fade > 0)
                videoFilters.Add(
                    $"fade=t=in:st=0:d={Number(fade)},fade=t=out:st={Number(endTime - fade)}:d={Number(fade)}"
                );
            if (videoFilters.Count > 0)
                final.AddRange(["-vf", string.Join(',', videoFilters)]);
            var audioFade =
                fade > 0
                    ? $",afade=t=in:st=0:d={Number(fade)},afade=t=out:st={Number(endTime - fade)}:d={Number(fade)}"
                    : "";
            final.AddRange(["-map", "0:v:0"]);
            var limiter =
                (effects.Mono ? "aformat=channel_layouts=mono," : "")
                + "alimiter=limit=0.95:level=false:latency=true";
            if (voiceIndex is int vi && musicIndex is int mi)
            {
                var mixer = req.DuckMusic
                    ? $"[{vi}:a]{effects.VoiceFilter(req.VoiceGain, audio, voiceDuration)},asplit=2[v][side];[{mi}:a]{audio.MusicFilter(req.MusicGain, musicDuration)}[m];[m][side]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=300[duck];[v][duck]amix=inputs=2:duration=longest:normalize=0,{limiter}{audioFade}[a]"
                    : $"[{vi}:a]{effects.VoiceFilter(req.VoiceGain, audio, voiceDuration)}[v];[{mi}:a]{audio.MusicFilter(req.MusicGain, musicDuration)}[m];[v][m]amix=inputs=2:duration=longest:normalize=0,{limiter}{audioFade}[a]";
                final.AddRange(["-filter_complex", mixer, "-map", "[a]"]);
            }
            else if (voiceIndex is int v)
                final.AddRange([
                    "-af",
                    $"{effects.VoiceFilter(req.VoiceGain, audio, voiceDuration)},{limiter}{audioFade}",
                    "-map",
                    $"{v}:a",
                ]);
            else if (musicIndex is int m)
                final.AddRange([
                    "-af",
                    $"{audio.MusicFilter(req.MusicGain, musicDuration)},{limiter}{audioFade}",
                    "-map",
                    $"{m}:a",
                ]);
            final.AddRange([
                "-t",
                req.Duration.ToString(CultureInfo.InvariantCulture),
                "-shortest",
                "-c:v",
                videoFilters.Count > 0 ? "libx264" : "copy",
            ]);
            if (videoFilters.Count > 0)
                final.AddRange([
                    "-threads",
                    "2",
                    "-preset",
                    "veryfast",
                    "-crf",
                    effects.Crf,
                    "-pix_fmt",
                    "yuv420p",
                ]);
            if (
                (effects.NormalizeVoice || audio.VoiceChanged()) && req.VoiceId is not null
                || audio.MusicChanged() && req.MusicId is not null
            )
                final.AddRange(["-ar", "48000"]);
            if (effects.Mono)
                final.AddRange(["-ac", "1"]);
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
