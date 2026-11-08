using System.Globalization;

namespace Studio;

public record ClipTransitions(string Style = "cut", double Duration = .3)
{
    public static readonly string[] Styles =
    [
        "cut",
        "fade",
        "fadeblack",
        "fadewhite",
        "wipeleft",
        "slideright",
        "smoothleft",
        "circleopen",
    ];

    public bool Valid(string mode = "concat") =>
        Styles.Contains(Style)
        && double.IsFinite(Duration)
        && Duration is >= .05 and <= 1.5
        && (mode == "concat" || Style == "cut");

    public async Task<string> Join(
        string[] files,
        double[] durations,
        string temp,
        RenderEffects effects,
        CancellationToken ct
    )
    {
        if (files.Length is < 2 or > 10 || files.Length != durations.Length)
            throw new IOException("Chuyển cảnh hỗ trợ 2–10 clip.");
        var d = Math.Max(1, Math.Round(Duration * effects.Fps)) / effects.Fps;
        if (durations.Any(t => t <= d + 1.0 / effects.Fps))
            throw new IOException("Mỗi cảnh phải dài hơn chuyển cảnh và một frame.");
        string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "error",
            "-filter_complex_threads",
            "1",
        };
        foreach (var f in files)
            args.AddRange(["-i", f]);
        var filters = new List<string>();
        for (var i = 0; i < files.Length; i++)
            filters.Add(
                $"[{i}:v]setpts=PTS-STARTPTS,settb=AVTB,fps={effects.Fps}"
                    + (i < files.Length - 1 ? $",tpad=stop_mode=clone:stop_duration={N(d)}" : "")
                    + $"[v{i}]"
            );
        var label = "v0";
        double offset = 0;
        for (var i = 1; i < files.Length; i++)
        {
            offset += durations[i - 1];
            var next = "mix" + i;
            filters.Add(
                $"[{label}][v{i}]xfade=transition={Style}:duration={N(d)}:offset={N(offset)}[{next}]"
            );
            label = next;
        }
        const string output = "joined-transitions.mp4";
        args.AddRange([
            "-filter_complex",
            string.Join(';', filters),
            "-map",
            $"[{label}]",
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
            "-r",
            effects.Fps.ToString(),
            output,
        ]);
        await MediaProcess.Run("ffmpeg", args, temp, ct);
        return output;
    }
}
