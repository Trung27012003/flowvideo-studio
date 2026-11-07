using System.Net.Http.Headers;
using System.Text.Json;

namespace Studio;

public record SpeechRequest(
    string Text,
    string Voice = "alloy",
    double Speed = 1,
    string Name = "Giọng đọc AI"
);

public record TranscriptionRequest(Guid AssetId, string Language = "vi");

public sealed class AudioFeatureException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

public class AudioProvider(IHttpClientFactory clients, IConfiguration config)
{
    public static readonly string[] Voices = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"];

    private HttpRequestMessage Request(string route)
    {
        var key = config["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            throw new AudioFeatureException("Chưa cấu hình OPENAI_API_KEY ở backend.", 503);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/" + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    public async Task Speech(SpeechRequest input, string destination, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        ct = deadline.Token;
        using var request = Request("audio/speech");
        request.Content = JsonContent.Create(
            new
            {
                model = config["OPENAI_TTS_MODEL"] ?? "gpt-4o-mini-tts",
                input = input.Text,
                voice = input.Voice,
                speed = input.Speed,
                response_format = "mp3",
            }
        );
        using var response = await clients
            .CreateClient("OpenAiAudio")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Check(response);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);
        await CopyBounded(source, target, 16 * 1024 * 1024, ct);
        if (target.Length == 0)
            throw new AudioFeatureException("Dịch vụ AI trả audio rỗng.", 502);
    }

    public async Task<List<SubtitleCue>> Transcribe(
        string path,
        string language,
        CancellationToken ct
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        ct = deadline.Token;
        using var request = Request("audio/transcriptions");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (language != "auto")
            form.Add(new StringContent(language), "language");
        var file = new StreamContent(File.OpenRead(path));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", "source.mp3");
        request.Content = form;
        using var response = await clients
            .CreateClient("OpenAiAudio")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Check(response);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await CopyBounded(source, buffer, 2 * 1024 * 1024, ct);
        buffer.Position = 0;
        using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
        if (
            json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array
        )
            throw new AudioFeatureException("Dịch vụ AI không trả phụ đề có mốc thời gian.", 502);
        var cues = new List<SubtitleCue>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (
                segment.ValueKind != JsonValueKind.Object
                || !segment.TryGetProperty("start", out var start)
                || start.ValueKind != JsonValueKind.Number
                || !start.TryGetDouble(out var from)
                || !segment.TryGetProperty("end", out var end)
                || end.ValueKind != JsonValueKind.Number
                || !end.TryGetDouble(out var to)
                || !segment.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String
            )
                throw new AudioFeatureException("Dịch vụ AI trả mốc thời gian không hợp lệ.", 502);
            var value = text.GetString()?.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (!string.IsNullOrWhiteSpace(value))
                cues.Add(new(from, to, value));
        }
        try
        {
            Subtitles.Validate(cues);
        }
        catch (ArgumentException)
        {
            throw new AudioFeatureException(
                "Không có lời nói hoặc phụ đề từ AI không hợp lệ.",
                502
            );
        }
        return cues;
    }

    private static void Check(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new AudioFeatureException(
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Dịch vụ AI đang giới hạn yêu cầu. Kiểm tra hạn mức trước khi thử lại."
                    : "Dịch vụ AI từ chối yêu cầu. Kiểm tra key, model và hạn mức.",
                502
            );
    }

    private static async Task CopyBounded(
        Stream source,
        Stream target,
        int limit,
        CancellationToken ct
    )
    {
        var bytes = new byte[81920];
        int read,
            total = 0;
        while ((read = await source.ReadAsync(bytes, ct)) > 0)
        {
            total += read;
            if (total > limit)
                throw new AudioFeatureException(
                    "Phản hồi dịch vụ AI vượt giới hạn dung lượng.",
                    502
                );
            await target.WriteAsync(bytes.AsMemory(0, read), ct);
        }
    }
}
