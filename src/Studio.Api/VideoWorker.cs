using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class VideoWorker(
    IServiceScopeFactory scopes,
    Storage store,
    VideoIntake intake,
    ILogger<VideoWorker> log
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
                var row = await db
                    .VideoGenerations.Where(x => x.Status == "Queued")
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);
                if (row is null)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                var claimed = await db
                    .VideoGenerations.Where(x => x.Id == row.Id && x.Status == "Queued")
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(x => x.Status, "Running")
                                .SetProperty(x => x.Message, "Đang tạo video AI")
                                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                        stoppingToken
                    );
                if (claimed == 0)
                    continue;
                await db.Entry(row).ReloadAsync(stoppingToken);
                var temp = Path.Combine(store.Root, "temp", "video-" + row.Id.ToString("N"));
                string? destination = null;
                bool saved = false;
                try
                {
                    try
                    {
                        Directory.CreateDirectory(temp);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        throw new VideoFailure(
                            "Không chuẩn bị được thư mục video. Chưa gửi yêu cầu đến dịch vụ."
                        );
                    }
                    var provider = scope.ServiceProvider.GetRequiredService<VideoProvider>();
                    var video = await provider.Generate(row, db, store, stoppingToken);
                    row.ProviderRequestId = video.ProviderId;
                    var source = Path.Combine(temp, "source.mp4");
                    await File.WriteAllBytesAsync(source, video.Mp4, stoppingToken);
                    using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken
                    );
                    probeDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var metadata = await JobDeliveries.Probe(source, store, probeDeadline.Token);
                    if (
                        metadata.Duration is <= 0 or > 16
                        || metadata.Width is < 64 or > 4096
                        || metadata.Height is < 64 or > 4096
                        || metadata.VideoCodec != "h264"
                    )
                        throw new VideoFailure(
                            "Video trả về không đúng định dạng H.264 hoặc vượt giới hạn 16 giây/4096 px. Dịch vụ có thể đã tính phí."
                        );
                    var asset = new Asset
                    {
                        ProjectId = row.ProjectId,
                        Name = "Video AI · " + row.Id.ToString("N")[..8] + ".mp4",
                        Kind = "video",
                        StoredName = Guid.NewGuid().ToString("N") + ".mp4",
                    };
                    destination = store.AssetPath(asset);
                    File.Move(source, destination);
                    db.Assets.Add(asset);
                    row.AssetId = asset.Id;
                    row.Status = "Succeeded";
                    row.Message =
                        "Video đã lưu vào thư viện. Chọn Dùng video cho cảnh để gắn vào storyboard.";
                    row.UpdatedAt = DateTime.UtcNow;
                    // Serialize terminal commits with intake cache/active/quota reads.
                    await intake.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        await db.SaveChangesAsync(stoppingToken);
                        saved = true;
                    }
                    finally
                    {
                        intake.Gate.Release();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Video generation failed: {GenerationId}", row.Id);
                    // A failed SaveChanges may leave the pending asset tracked; never persist it as part of error recovery.
                    foreach (var entry in db.ChangeTracker.Entries<Asset>().ToList())
                        entry.State = EntityState.Detached;
                    row.AssetId = null;
                    row.Status = e is VideoFailure { Unknown: true } ? "Unknown" : "Failed";
                    row.ProviderRequestId =
                        (e as VideoFailure)?.ProviderId ?? row.ProviderRequestId;
                    row.Message = e is VideoFailure failure
                        ? failure.Message
                        : "Không lưu được video. Dịch vụ có thể đã tính phí; không tự gửi lại.";
                    row.UpdatedAt = DateTime.UtcNow;
                    await intake.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    finally
                    {
                        intake.Gate.Release();
                    }
                }
                finally
                {
                    if (!saved && destination is not null)
                        try
                        {
                            File.Delete(destination);
                        }
                        catch (IOException e)
                        {
                            log.LogWarning(e, "Cannot clean video output");
                        }
                    try
                    {
                        if (Directory.Exists(temp))
                            Directory.Delete(temp, true);
                    }
                    catch (IOException e)
                    {
                        log.LogWarning(e, "Cannot clean video temp files");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogError(e, "Video worker iteration failed");
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
}
