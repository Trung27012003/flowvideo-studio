using System.Globalization;

namespace Studio;

public record LoopOptions(string Style = "direct", double Crossfade = .3)
{
    public bool Valid(string mode, int resolution) =>
        Style is "direct" or "reverse" or "pingpong" or "crossfade"
        && double.IsFinite(Crossfade)
        && Crossfade is >= .05 and <= 2
        && (mode == "loop" || Style == "direct")
        && (!(Style is "reverse" or "pingpong") || resolution == 720);

    public async Task<string> Prepare(
        string source,
        string temp,
        double duration,
        RenderEffects effects,
        CancellationToken ct
    )
    {
        if (Style == "direct")
            return source;
        if ((Style is "reverse" or "pingpong") && duration > 8.05)
            throw new IOException(
                "Reverse/ping-pong hỗ trợ một chu kỳ tối đa 8 giây ở 720p. Cắt ngắn nguồn trước."
            );
        if (Style == "crossfade" && duration > 30.05)
            throw new IOException(
                "Crossfade hỗ trợ một chu kỳ tối đa 30 giây. Cắt ngắn nguồn trước."
            );
        string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);
        string filter;
        if (Style == "reverse")
            filter = "[0:v]reverse,setpts=PTS-STARTPTS[out]";
        else if (Style == "pingpong")
        {
            var frames = (int)Math.Round(duration * effects.Fps);
            if (frames < 3)
                throw new IOException("Ping-pong cần ít nhất 3 frame nguồn.");
            filter =
                $"[0:v]split=2[f][r];[f]setpts=PTS-STARTPTS[f0];[r]reverse,trim=start_frame=1:end_frame={frames - 1},setpts=PTS-STARTPTS[r0];[f0][r0]concat=n=2:v=1:a=0,format=yuv420p[out]";
        }
        else
        {
            var fade = Math.Max(1, Math.Round(Crossfade * effects.Fps)) / effects.Fps;
            if (fade * 2 >= duration - 1.0 / effects.Fps)
                throw new IOException(
                    "Chu kỳ phải dài hơn hai lần thời gian crossfade và một frame."
                );
            filter =
                $"[0:v]split=2[body][head];[body]trim=start={N(fade)},setpts=PTS-STARTPTS,settb=AVTB,fps={effects.Fps}[b];[head]trim=end={N(fade)},setpts=PTS-STARTPTS,settb=AVTB,fps={effects.Fps}[h];[b][h]xfade=transition=fade:duration={N(fade)}:offset={N(duration - 2 * fade)},format=yuv420p[out]";
        }
        const string output = "loop-cycle.mp4";
        await MediaProcess.Run(
            "ffmpeg",
            [
                "-y",
                "-nostdin",
                "-hide_banner",
                "-loglevel",
                "error",
                "-filter_complex_threads",
                "1",
                "-i",
                source,
                "-filter_complex",
                filter,
                "-map",
                "[out]",
                "-an",
                "-c:v",
                "libx264",
                "-preset",
                "veryfast",
                "-crf",
                effects.Crf,
                "-r",
                effects.Fps.ToString(),
                "-pix_fmt",
                "yuv420p",
                "-threads",
                "2",
                output,
            ],
            temp,
            ct
        );
        return output;
    }
}
