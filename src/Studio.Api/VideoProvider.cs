using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public sealed class VideoFailure(string message, bool unknown = false, string? providerId = null)
    : Exception(message)
{
    public bool Unknown { get; } = unknown;
    public string? ProviderId { get; } = providerId;
}

public record VideoResult(byte[] Mp4, string? ProviderId);

public sealed class VideoProvider(IHttpClientFactory clients, IConfiguration config)
{
    public async Task<VideoResult> Generate(
        VideoGeneration job,
        StudioDb db,
        Storage store,
        CancellationToken ct
    )
    {
        var key = config["GEMINI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            throw new VideoFailure("Chưa có GEMINI_API_KEY; không gửi yêu cầu.");
        object input = job.Prompt;
        if (job.ReferenceId is Guid referenceId)
        {
            var asset = await db
                .Assets.AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.Id == referenceId && a.ProjectId == job.ProjectId && a.Kind == "image",
                    ct
                );
            if (asset is null || !File.Exists(store.AssetPath(asset)))
                throw new VideoFailure("Ảnh tham chiếu không còn; chưa gửi yêu cầu.");
            var path = store.AssetPath(asset);
            if (new FileInfo(path).Length > 10 * 1024 * 1024)
                throw new VideoFailure("Ảnh tham chiếu vượt 10 MiB; chưa gửi yêu cầu.");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var png =
                bytes.Length > 8
                && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var jpeg = bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            if (!png && !jpeg)
                throw new VideoFailure("Ảnh tham chiếu phải là PNG/JPEG hợp lệ; chưa gửi yêu cầu.");
            input = new object[]
            {
                new
                {
                    type = "image",
                    mime_type = png ? "image/png" : "image/jpeg",
                    data = Convert.ToBase64String(bytes),
                },
                new { type = "text", text = job.Prompt },
            };
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/interactions"
        );
        request.Headers.Add("x-goog-api-key", key);
        request.Content = JsonContent.Create(
            new
            {
                model = job.Model,
                input,
                response_format = new
                {
                    type = "video",
                    duration = job.Seconds + "s",
                    aspect_ratio = job.Size,
                    resolution = job.Quality,
                },
            }
        );
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        string? providerId = null;
        try
        {
            using var response = await clients
                .CreateClient("GeminiVideo")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new VideoFailure(
                    $"Dịch vụ từ chối tạo video (HTTP {(int)response.StatusCode}). Kiểm tra key, quyền model và hạn mức; không tự gửi lại.",
                    (int)response.StatusCode >= 500
                );
            const int limit = 140 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit)
                throw new VideoFailure("Phản hồi vượt 140 MiB. Dịch vụ có thể đã tính phí.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new VideoFailure("Phản hồi vượt 140 MiB. Dịch vụ có thể đã tính phí.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), deadline.Token);
            }
            buffer.Position = 0;
            using var json = await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: deadline.Token
            );
            var root = json.RootElement;
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                providerId = id.GetString();
            if (
                !root.TryGetProperty("status", out var status)
                || status.GetString() != "completed"
                || !root.TryGetProperty("steps", out var steps)
                || steps.ValueKind != JsonValueKind.Array
            )
                throw new VideoFailure(
                    "Dịch vụ chưa trả kết quả hoàn tất; không tự gửi lại để tránh tính phí hai lần.",
                    true,
                    providerId
                );
            byte[]? mp4 = null;
            foreach (var step in steps.EnumerateArray())
            {
                if (
                    !step.TryGetProperty("type", out var type)
                    || type.GetString() != "model_output"
                    || !step.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array
                )
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var kind) || kind.GetString() != "video")
                        continue;
                    if (
                        mp4 is not null
                        || !part.TryGetProperty("mime_type", out var mime)
                        || mime.GetString() != "video/mp4"
                        || !part.TryGetProperty("data", out var data)
                        || data.ValueKind != JsonValueKind.String
                    )
                        throw new VideoFailure(
                            "Phản hồi video không hợp lệ. Dịch vụ có thể đã tính phí.",
                            false,
                            providerId
                        );
                    mp4 = Convert.FromBase64String(data.GetString()!);
                }
            }
            if (
                mp4 is null
                || mp4.Length is < 12 or > 104857600
                || !mp4.AsSpan(4, 4).SequenceEqual("ftyp"u8)
            )
                throw new VideoFailure(
                    "Không nhận được MP4 hợp lệ (tối đa 100 MiB). Dịch vụ có thể đã tính phí.",
                    false,
                    providerId
                );
            return new(mp4, providerId);
        }
        catch (VideoFailure)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VideoFailure(
                "Hết thời gian chờ. Dịch vụ có thể đã tính phí; không tự gửi lại.",
                true,
                providerId
            );
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new VideoFailure(
                "Mất kết nối với dịch vụ. Kết quả chưa rõ; không tự gửi lại.",
                true,
                providerId
            );
        }
        catch (Exception e)
            when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new VideoFailure(
                "Dịch vụ trả dữ liệu không hợp lệ. Không tự gửi lại.",
                false,
                providerId
            );
        }
    }
}
