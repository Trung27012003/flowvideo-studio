using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public sealed class ProductionWorker(
    IServiceScopeFactory scopes,
    Storage store,
    ProductionGate gate,
    ILogger<ProductionWorker> log
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
                var ids = await db
                    .ProductionRuns.AsNoTracking()
                    .Where(x => ProductionWorkflow.Active.Contains(x.Status))
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.Id)
                    .Take(20)
                    .ToArrayAsync(stoppingToken);
                foreach (var id in ids)
                {
                    try
                    {
                        await Tick(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        log.LogError(e, "Production step failed: {RunId}", id);
                        await Fail(
                            id,
                            "Failed",
                            e is ProductionError
                                ? e.Message
                                : "Không hoàn tất bước local. Xem log; không tự gửi lại AI.",
                            stoppingToken
                        );
                    }
                }
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogError(e, "Production worker iteration failed");
                try
                {
                    await Task.Delay(1500, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    async Task Tick(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        Guid? speechScene = null;
        bool prepare = false;
        await gate.Gate.WaitAsync(ct);
        try
        {
            var row = await db.ProductionRuns.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || !ProductionWorkflow.Active.Contains(row.Status))
                return;
            var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
            if (row.Status == "Rendering")
            {
                var job = await db
                    .Jobs.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == state.RenderId, ct);
                if (job is null)
                    throw new ProductionError("Không còn tác vụ render.");
                if (job.Status is "Queued" or "Running")
                    return;
                row.Status =
                    job.Status == "Succeeded" ? "Succeeded"
                    : job.Status == "Cancelled" ? "Canceled"
                    : "Failed";
                row.Message =
                    job.Status == "Succeeded"
                        ? "Đã hoàn tất video từ bản chụp quy trình"
                        : "Bước render lỗi/hủy; có thể dựng lại mà không gọi AI.";
                Save(row, state.Event(row.Message));
                await db.SaveChangesAsync(ct);
                return;
            }
            if (row.Status == "Speaking")
                return; // Only the owning call can leave this state; startup marks it Unknown.
            int ready = 0;
            foreach (var step in state.Steps)
            {
                if (step.GenerationId is not Guid gid)
                {
                    ready++;
                    continue;
                }
                string? status;
                if (step.Provider == "images")
                    status = await db
                        .ImageGenerations.Where(x => x.Id == gid && x.ProjectId == row.ProjectId)
                        .Select(x => x.Status)
                        .FirstOrDefaultAsync(ct);
                else
                    status = await db
                        .VideoGenerations.Where(x => x.Id == gid && x.ProjectId == row.ProjectId)
                        .Select(x => x.Status)
                        .FirstOrDefaultAsync(ct);
                if (status == "Unknown")
                {
                    row.Status = "Unknown";
                    row.Message =
                        "Kết quả AI chưa rõ. Không tự gửi lại hoặc dựng bằng tư liệu giả.";
                    await ProductionWorkflow.CancelPending(db, state, ct);
                    Save(row, state.Event(row.Message));
                    await db.SaveChangesAsync(ct);
                    return;
                }
                if (status is null or "Failed" or "Canceled")
                {
                    row.Status = "Failed";
                    row.Message =
                        "Một lượt tạo tư liệu lỗi/hủy. Quy trình dừng; không tự gọi lại AI.";
                    await ProductionWorkflow.CancelPending(db, state, ct);
                    Save(row, state.Event(row.Message));
                    await db.SaveChangesAsync(ct);
                    return;
                }
                if (status == "Succeeded")
                    ready++;
            }
            if (ready < state.Steps.Length)
            {
                var message = $"Tư liệu sẵn sàng {ready}/{state.Steps.Length}";
                if (row.Message != message)
                {
                    row.Message = message;
                    Save(row, state);
                    await db.SaveChangesAsync(ct);
                }
                return;
            }
            var next = state.Steps.FirstOrDefault(s => s.SpeechStatus == "Queued");
            if (next is not null)
            {
                speechScene = next.SceneId;
                state = state with
                {
                    Steps = state
                        .Steps.Select(s =>
                            s.SceneId == next.SceneId ? s with { SpeechStatus = "Running" } : s
                        )
                        .ToArray(),
                };
                row.Status = "Speaking";
                row.Message =
                    "Đang tạo giọng cho cảnh "
                    + (Array.FindIndex(state.Steps, s => s.SceneId == next.SceneId) + 1);
                Save(row, state.Event(row.Message));
                await db.SaveChangesAsync(ct);
            }
            else
            {
                row.Status = "Preparing";
                row.Message = "Đang căn cảnh và ghép giọng";
                Save(row, state);
                await db.SaveChangesAsync(ct);
                prepare = true;
            }
        }
        finally
        {
            gate.Gate.Release();
        }
        if (speechScene is Guid sid)
            await Speak(id, sid, ct);
        else if (prepare)
            await Prepare(id, ct);
    }

    static void Save(ProductionRun row, ProductionState state)
    {
        row.StateJson = JsonSerializer.Serialize(state);
        row.UpdatedAt = DateTime.UtcNow;
    }

    async Task Fail(Guid id, string status, string message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        await gate.Gate.WaitAsync(ct);
        try
        {
            var row = await db.ProductionRuns.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || !ProductionWorkflow.Active.Contains(row.Status))
                return;
            var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
            state = state with
            {
                Steps = state
                    .Steps.Select(s =>
                        s.SpeechStatus == "Running" ? s with { SpeechStatus = status } : s
                    )
                    .ToArray(),
            };
            await ProductionWorkflow.CancelPending(db, state, ct);
            row.Status = status;
            row.Message = message;
            Save(row, state.Event(message));
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Gate.Release();
        }
    }

    async Task Speak(Guid id, Guid sceneId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        var row = await db.ProductionRuns.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var req = JsonSerializer.Deserialize<ProductionRequest>(row.RequestJson)!;
        var doc = JsonSerializer.Deserialize<StoryDocument>(row.SnapshotJson)!;
        var scene = doc.Scenes!.Single(s => s.Id == sceneId);
        var asset = new Asset
        {
            ProjectId = row.ProjectId,
            Kind = "audio",
            Name =
                "Giọng cảnh · "
                + id.ToString("N")[..6]
                + " · "
                + scene.Title[..Math.Min(60, scene.Title.Length)]
                + ".mp3",
            StoredName = Guid.NewGuid().ToString("N") + ".mp3",
        };
        var saved = false;
        try
        {
            await scope
                .ServiceProvider.GetRequiredService<AudioProvider>()
                .Speech(new(scene.Narration, req.Voice, req.Speed), store.AssetPath(asset), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var duration = await MediaProcess.AudioDuration(store.AssetPath(asset), timeout.Token);
            if (duration > 600)
                throw new ProductionError("Giọng trả về quá 600 giây; không dùng file này.");
            await gate.Gate.WaitAsync(ct);
            try
            {
                var current = await db.ProductionRuns.SingleAsync(x => x.Id == id, ct);
                db.Assets.Add(asset);
                if (current.Status is "Speaking" or "Canceled")
                {
                    var state = JsonSerializer.Deserialize<ProductionState>(current.StateJson)!;
                    state = state with
                    {
                        Steps = state
                            .Steps.Select(s =>
                                s.SceneId == sceneId
                                    ? s with
                                    {
                                        SpeechId = asset.Id,
                                        SpeechStatus = "Succeeded",
                                        SpeechDuration = duration,
                                    }
                                    : s
                            )
                            .ToArray(),
                    };
                    if (current.Status == "Speaking")
                    {
                        current.Status = "Preparing";
                        current.Message = "Đã lưu giọng một cảnh";
                    }
                    else
                        current.Message =
                            "Quy trình đã hủy; giọng đã gửi vẫn hoàn tất và được giữ trong thư viện.";
                    Save(current, state.Event(current.Message));
                }
                await db.SaveChangesAsync(ct);
                saved = true;
            }
            finally
            {
                gate.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log.LogError(e, "Production speech failed: {RunId}", id);
            await Fail(
                id,
                e is ProductionError || e is AudioFeatureException { Status: 503 }
                    ? "Failed"
                    : "Unknown",
                e is ProductionError
                    ? e.Message
                    : "Tạo giọng thất bại hoặc chưa rõ kết quả. Dịch vụ có thể đã tính phí; không tự gửi lại.",
                ct
            );
        }
        finally
        {
            if (!saved)
                File.Delete(store.AssetPath(asset));
        }
    }

    async Task Prepare(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
        var row = await db.ProductionRuns.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (row.Status != "Preparing")
            return;
        var req = JsonSerializer.Deserialize<ProductionRequest>(row.RequestJson)!;
        var state = JsonSerializer.Deserialize<ProductionState>(row.StateJson)!;
        var doc = JsonSerializer.Deserialize<StoryDocument>(row.SnapshotJson)!;
        var scenes = new List<StoryScene>();
        foreach (var scene in doc.Scenes!)
        {
            var step = state.Steps.FirstOrDefault(s => s.SceneId == scene.Id);
            if (step is null)
            {
                scenes.Add(scene);
                continue;
            }
            var next = scene;
            if (step.GenerationId is Guid gid)
            {
                var assetId =
                    step.Provider == "images"
                        ? await db
                            .ImageGenerations.Where(x => x.Id == gid && x.Status == "Succeeded")
                            .Select(x => x.AssetId)
                            .SingleAsync(ct)
                        : await db
                            .VideoGenerations.Where(x => x.Id == gid && x.Status == "Succeeded")
                            .Select(x => x.AssetId)
                            .SingleAsync(ct);
                if (assetId is null)
                    throw new ProductionError("Kết quả AI không còn tư liệu.");
                next = next with
                {
                    AssetId = assetId,
                    Start = 0,
                    Options = (scene.Options ?? new SceneOptions()) with { Speed = 1 },
                };
            }
            if (req.AutoTiming && req.Speech && step.SpeechDuration is double length)
                next = next with
                {
                    Duration = Math.Ceiling(Math.Max(.1, length + req.Padding) * 10) / 10,
                };
            scenes.Add(next);
        }
        doc = doc with { Scenes = scenes.ToArray() };
        if (doc.Scenes.Sum(s => s.Duration) > 600)
            throw new ProductionError(
                "Thời lượng sau căn giọng vượt 600 giây. Tư liệu đã tạo vẫn giữ trong thư viện."
            );
        var error = await Storyboard.Validate(row.ProjectId, doc, db, true, ct);
        if (error is not null)
            throw new ProductionError(error);
        Asset? voice = null;
        bool saved = false;
        var temp = Path.Combine(store.Root, "temp", "production-" + id.ToString("N"));
        try
        {
            if (req.Speech)
            {
                voice = new Asset
                {
                    ProjectId = row.ProjectId,
                    Kind = "audio",
                    Name = "Giọng toàn phim · " + id.ToString("N")[..8] + ".mp3",
                    StoredName = Guid.NewGuid().ToString("N") + ".mp3",
                };
                Directory.CreateDirectory(temp);
                var args = new List<string>
                {
                    "-y",
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                };
                var filters = new List<string>();
                int i = 0;
                foreach (var scene in doc.Scenes.Where(s => s.Options?.Enabled != false))
                {
                    var step = state.Steps.Single(s => s.SceneId == scene.Id);
                    if (step.SpeechId is Guid sid)
                    {
                        var speech = await db
                            .Assets.AsNoTracking()
                            .SingleAsync(a => a.Id == sid && a.ProjectId == row.ProjectId, ct);
                        args.AddRange(["-i", store.AssetPath(speech)]);
                    }
                    else
                        args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
                    filters.Add(
                        $"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration={scene.Duration.ToString("0.########", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a{i}]"
                    );
                    i++;
                }
                filters.Add(
                    string.Concat(Enumerable.Range(0, i).Select(n => $"[a{n}]"))
                        + $"concat=n={i}:v=0:a=1[out]"
                );
                args.AddRange([
                    "-filter_complex",
                    string.Join(';', filters),
                    "-map",
                    "[out]",
                    "-ar",
                    "48000",
                    "-ac",
                    "2",
                    "-c:a",
                    "libmp3lame",
                    "-b:a",
                    "192k",
                    "-threads",
                    "2",
                    store.AssetPath(voice),
                ]);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await MediaProcess.Run("ffmpeg", args, temp, timeout.Token);
                doc = doc with
                {
                    VoiceId = voice.Id,
                    Effects = (doc.Effects ?? new RenderEffects()) with
                    {
                        VoiceSpeed = 1,
                        VoiceDelay = 0,
                    },
                    Audio = (doc.Audio ?? new AudioTuning()) with { VoiceStart = 0 },
                };
            }
            await gate.Gate.WaitAsync(ct);
            try
            {
                var current = await db.ProductionRuns.SingleAsync(x => x.Id == id, ct);
                if (current.Status != "Preparing")
                    return;
                if (voice is not null)
                    db.Assets.Add(voice);
                var job = new RenderJob
                {
                    ProjectId = row.ProjectId,
                    RequestJson = JsonSerializer.Serialize(Storyboard.CreateRequest(doc)),
                };
                db.Jobs.Add(job);
                state = state with { VoiceId = doc.VoiceId, RenderId = job.Id, Document = doc };
                current.Status = "Rendering";
                current.Message = "Đã tự gắn tư liệu/giọng và xếp hàng dựng";
                Save(current, state.Event(current.Message));
                await db.SaveChangesAsync(ct);
                saved = true;
            }
            finally
            {
                gate.Gate.Release();
            }
        }
        finally
        {
            if (!saved && voice is not null)
                File.Delete(store.AssetPath(voice));
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }
}
