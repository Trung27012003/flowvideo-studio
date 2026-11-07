using System.Globalization;
using System.Text.RegularExpressions;

namespace Studio;

public record SubtitleCue(double Start, double End, string Text);

public record SubtitleRequest(string Name, string Content);

public static class Subtitles
{
    private static readonly Regex Timing = new(
        @"^(\d{2}):([0-5]\d):([0-5]\d),(\d{3}) --> (\d{2}):([0-5]\d):([0-5]\d),(\d{3})$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100)
    );

    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 100000)
            throw new ArgumentException("Phụ đề cần từ 1–100.000 ký tự.");
        content = content
            .Trim()
            .TrimStart('\uFEFF')
            .Trim()
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        var blocks = Regex.Split(content, @"\n[ \t]*\n");
        var cues = new List<SubtitleCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            if (lines.Length < 3 || !int.TryParse(lines[0].Trim(), out var index) || index < 1)
                throw new ArgumentException(
                    "Mỗi mục SRT cần số thứ tự, dòng thời gian và nội dung."
                );
            var match = Timing.Match(lines[1].Trim());
            if (!match.Success)
                throw new ArgumentException("Thời gian cần dạng 00:00:01,000 --> 00:00:03,000.");
            double At(int offset) =>
                int.Parse(match.Groups[offset].Value) * 3600
                + int.Parse(match.Groups[offset + 1].Value) * 60
                + int.Parse(match.Groups[offset + 2].Value)
                + int.Parse(match.Groups[offset + 3].Value) / 1000.0;
            cues.Add(new(At(1), At(5), string.Join("\n", lines.Skip(2)).Trim()));
        }
        Validate(cues);
        return cues;
    }

    public static void Validate(IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count is < 1 or > 5000)
            throw new ArgumentException("Cần từ 1–5.000 mục phụ đề.");
        double previous = -1;
        foreach (var cue in cues)
        {
            if (
                !double.IsFinite(cue.Start)
                || !double.IsFinite(cue.End)
                || cue.Start < 0
                || cue.End <= cue.Start
                || Math.Round(cue.End * 1000, MidpointRounding.AwayFromZero)
                    <= Math.Round(cue.Start * 1000, MidpointRounding.AwayFromZero)
                || cue.End > 359999.999
                || cue.Start < previous
                || string.IsNullOrWhiteSpace(cue.Text)
                || cue.Text.Length > 2000
                || cue.Text.Contains('\0')
            )
                throw new ArgumentException(
                    "Phụ đề có nội dung hoặc mốc thời gian không hợp lệ; các mục phải theo thứ tự thời gian."
                );
            previous = cue.Start;
        }
    }

    public static string Write(IReadOnlyList<SubtitleCue> cues)
    {
        Validate(cues);
        string Stamp(double seconds)
        {
            var ms = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
            return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";
        }
        return string.Join(
                "\n\n",
                cues.Select(
                    (c, i) => $"{i + 1}\n{Stamp(c.Start)} --> {Stamp(c.End)}\n{c.Text.Trim()}"
                )
            ) + "\n";
    }
}
