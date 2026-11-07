using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Studio;

public static class MediaProcess
{
    public static async Task<double> Duration(string path, CancellationToken ct)
    {
        var json = await Run(
            "ffprobe",
            [
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=duration:format=duration",
                "-of",
                "json",
                path,
            ],
            Path.GetDirectoryName(path)!,
            ct
        );
        using var doc = JsonDocument.Parse(json);
        if (
            doc.RootElement.TryGetProperty("streams", out var streams)
            && streams.GetArrayLength() > 0
            && streams[0].TryGetProperty("duration", out var streamDuration)
            && Parse(streamDuration, out var duration)
        )
            return duration;
        if (
            doc.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var formatDuration)
            && Parse(formatDuration, out duration)
        )
            return duration;
        throw new IOException("Cannot determine video duration.");
    }

    private static bool Parse(JsonElement value, out double duration) =>
        double.TryParse(
            value.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out duration
        )
        && double.IsFinite(duration)
        && duration > 0;

    public static async Task<string> Run(
        string executable,
        IEnumerable<string> args,
        string directory,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process =
            Process.Start(info) ?? throw new IOException("Cannot start media process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var error = ReadTail(process.StandardError, 4096);
        var output = ReadTail(process.StandardOutput, 65536);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(error, output);
            throw;
        }
        var errors = await error;
        var result = await output;
        if (process.ExitCode != 0)
            throw new IOException(errors);
        return result;
    }

    private static async Task<string> ReadTail(StreamReader reader, int limit)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > limit)
                tail.Remove(0, tail.Length - limit);
        }
        return tail.ToString();
    }
}
