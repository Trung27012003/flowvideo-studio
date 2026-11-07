using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class ImageWorker(
    IServiceScopeFactory scopes,
    Storage store,
    ImageIntake intake,
    ILogger<ImageWorker> log
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
                    .ImageGenerations.Where(x => x.Status == "Queued")
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);
                if (row is null)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                var claimed = await db
                    .ImageGenerations.Where(x => x.Id == row.Id && x.Status == "Queued")
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(x => x.Status, "Running")
                                .SetProperty(x => x.Message, "Đang tạo ảnh AI")
                                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                        stoppingToken
                    );
                if (claimed == 0)
                    continue;
                await db.Entry(row).ReloadAsync(stoppingToken);
                var temp = Path.Combine(store.Root, "temp", "image-" + row.Id.ToString("N"));
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
                        throw new ImageFailure(
                            "Không chuẩn bị được thư mục ảnh. Chưa gửi yêu cầu đến dịch vụ."
                        );
                    }
                    var image = await scope
                        .ServiceProvider.GetRequiredService<ImageProvider>()
                        .Generate(row, stoppingToken);
                    row.ProviderRequestId = image.ProviderId;
                    var source = Path.Combine(temp, "source.png");
                    await File.WriteAllBytesAsync(source, image.Png, stoppingToken);
                    var metadata = await MediaProcess.Run(
                        "ffprobe",
                        [
                            "-v",
                            "error",
                            "-select_streams",
                            "v:0",
                            "-show_entries",
                            "stream=codec_name,width,height",
                            "-of",
                            "json",
                            source,
                        ],
                        temp,
                        stoppingToken
                    );
                    using (var json = JsonDocument.Parse(metadata))
                    {
                        var stream = json.RootElement.GetProperty("streams")[0];
                        var expected = row.Size.Split('x');
                        if (
                            stream.GetProperty("codec_name").GetString() != "png"
                            || stream.GetProperty("width").GetInt32() != int.Parse(expected[0])
                            || stream.GetProperty("height").GetInt32() != int.Parse(expected[1])
                        )
                            throw new ImageFailure(
                                "Ảnh trả về sai định dạng/kích thước. Yêu cầu có thể đã tính phí."
                            );
                    }
                    var asset = new Asset
                    {
                        ProjectId = row.ProjectId,
                        Name = "Ảnh AI · " + row.Id.ToString("N")[..8] + ".png",
                        Kind = "image",
                        StoredName = Guid.NewGuid().ToString("N") + ".png",
                    };
                    destination = store.AssetPath(asset);
                    await MediaProcess.Run(
                        "ffmpeg",
                        [
                            "-y",
                            "-nostdin",
                            "-hide_banner",
                            "-loglevel",
                            "error",
                            "-i",
                            source,
                            "-frames:v",
                            "1",
                            "-threads",
                            "1",
                            destination,
                        ],
                        temp,
                        stoppingToken
                    );
                    db.Assets.Add(asset);
                    row.AssetId = asset.Id;
                    row.Status = "Succeeded";
                    row.Message =
                        "Ảnh đã lưu vào thư viện. Chọn Dùng ảnh cho cảnh để gắn vào storyboard.";
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
                    log.LogError(e, "Image generation failed: {GenerationId}", row.Id);
                    // A failed SaveChanges may leave the pending asset tracked; never persist it as part of error recovery.
                    foreach (var entry in db.ChangeTracker.Entries<Asset>().ToList())
                        entry.State = EntityState.Detached;
                    row.AssetId = null;
                    row.Status = e is ImageFailure { Unknown: true } ? "Unknown" : "Failed";
                    row.ProviderRequestId =
                        (e as ImageFailure)?.ProviderId ?? row.ProviderRequestId;
                    row.Message = e is ImageFailure failure
                        ? failure.Message
                        : "Không lưu được ảnh. Dịch vụ có thể đã tính phí; không tự gửi lại.";
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
                            log.LogWarning(e, "Cannot clean image output");
                        }
                    try
                    {
                        if (Directory.Exists(temp))
                            Directory.Delete(temp, true);
                    }
                    catch (IOException e)
                    {
                        log.LogWarning(e, "Cannot clean image temp files");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogError(e, "Image worker iteration failed");
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
