using System.Net.Http.Headers;
using System.Text.Json;

namespace Studio;

public sealed class ImageFailure(string message, bool unknown = false, string? providerId = null)
    : Exception(message)
{
    public bool Unknown { get; } = unknown;
    public string? ProviderId { get; } = providerId;
}

public record ImageResult(byte[] Png, string? ProviderId);

public class ImageProvider(IHttpClientFactory clients, IConfiguration config)
{
    public async Task<ImageResult> Generate(ImageGeneration job, CancellationToken ct)
    {
        var key = config["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            throw new ImageFailure("Chưa có API key; không gửi yêu cầu.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/images/generations"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(
            new
            {
                model = job.Model,
                prompt = job.Prompt,
                n = 1,
                size = job.Size,
                quality = job.Quality,
                output_format = "png",
            }
        );
        string? providerId = null;
        try
        {
            using var response = await clients
                .CreateClient("OpenAiImage")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            providerId = response.Headers.TryGetValues("x-request-id", out var ids)
                ? ids.FirstOrDefault()
                : null;
            if (!response.IsSuccessStatusCode)
                throw new ImageFailure(
                    "Dịch vụ từ chối tạo ảnh. Kiểm tra key, quyền model và hạn mức; không tự gửi lại.",
                    false,
                    providerId
                );
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[81920];
            int read,
                total = 0;
            while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            {
                total += read;
                if (total > 24 * 1024 * 1024)
                    throw new ImageFailure(
                        "Phản hồi ảnh vượt giới hạn. Yêu cầu có thể đã được tính phí.",
                        false,
                        providerId
                    );
                await buffer.WriteAsync(bytes.AsMemory(0, read), deadline.Token);
            }
            buffer.Position = 0;
            using var json = await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: deadline.Token
            );
            if (
                json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() != 1
                || data[0].ValueKind != JsonValueKind.Object
                || !data[0].TryGetProperty("b64_json", out var b64)
                || b64.ValueKind != JsonValueKind.String
            )
                throw new ImageFailure(
                    "Dịch vụ không trả ảnh hợp lệ. Yêu cầu có thể đã được tính phí.",
                    false,
                    providerId
                );
            byte[] png;
            try
            {
                png = Convert.FromBase64String(b64.GetString()!);
            }
            catch (FormatException)
            {
                throw new ImageFailure(
                    "Dữ liệu ảnh không hợp lệ; không tự gửi lại.",
                    false,
                    providerId
                );
            }
            if (
                png.Length is < 24 or > 16777216
                || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            )
                throw new ImageFailure(
                    "Dữ liệu không phải PNG hợp lệ; không tự gửi lại.",
                    false,
                    providerId
                );
            return new(png, providerId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ImageFailure(
                "Tạo ảnh quá thời gian chờ. Chưa rõ kết quả/tính phí; không tự gửi lại.",
                true,
                providerId
            );
        }
        catch (HttpRequestException)
        {
            throw new ImageFailure(
                "Mất kết nối dịch vụ. Chưa rõ kết quả/tính phí; không tự gửi lại.",
                true,
                providerId
            );
        }
        catch (IOException)
        {
            throw new ImageFailure(
                "Mất kết nối khi nhận ảnh. Chưa rõ kết quả/tính phí; không tự gửi lại.",
                true,
                providerId
            );
        }
        catch (JsonException)
        {
            throw new ImageFailure(
                "Phản hồi ảnh không đọc được; yêu cầu có thể đã tính phí.",
                false,
                providerId
            );
        }
    }
}
