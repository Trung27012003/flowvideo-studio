using System.Globalization;
using System.Text.RegularExpressions;

namespace Studio;

public record TextOverlay(
    string Text,
    double Start = 0,
    double End = 3,
    string Position = "bottom-center",
    int FontSize = 48,
    string Color = "#ffffff",
    string BoxColor = "#000000",
    double BoxOpacity = .6
);

public record LogoOverlay(
    Guid AssetId,
    double Start = 0,
    double End = 600,
    string Position = "top-right",
    double Width = .15,
    double Opacity = .9
);

public record BrandingOptions(TextOverlay[]? Texts = null, LogoOverlay? Logo = null)
{
    public static readonly string[] Positions =
    [
        "top-left",
        "top-center",
        "top-right",
        "middle-left",
        "center",
        "middle-right",
        "bottom-left",
        "bottom-center",
        "bottom-right",
    ];

    static bool Time(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) && a >= 0 && b > a && b <= 600;

    static bool ColorValid(string? s) => s is not null && Regex.IsMatch(s, @"\A#[0-9a-fA-F]{6}\z");

    public bool Valid() =>
        (Texts ?? []).Length <= 5
        && (Texts ?? []).All(t =>
            t is not null
            && !string.IsNullOrWhiteSpace(t.Text)
            && t.Text.Length <= 300
            && !t.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            && Time(t.Start, t.End)
            && Positions.Contains(t.Position)
            && t.FontSize is >= 12 and <= 96
            && ColorValid(t.Color)
            && ColorValid(t.BoxColor)
            && double.IsFinite(t.BoxOpacity)
            && t.BoxOpacity is >= 0 and <= 1
        )
        && (
            Logo is null
            || Logo.AssetId != Guid.Empty
                && Time(Logo.Start, Logo.End)
                && Positions.Contains(Logo.Position)
                && double.IsFinite(Logo.Width)
                && Logo.Width is >= .05 and <= .5
                && double.IsFinite(Logo.Opacity)
                && Logo.Opacity is >= 0 and <= 1
        );

    static string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);

    public static (string X, string Y) Coordinates(string position, bool logo = false)
    {
        var w = logo ? "main_w" : "w";
        var h = logo ? "main_h" : "h";
        var tw = logo ? "overlay_w" : "text_w";
        var th = logo ? "overlay_h" : "text_h";
        return (
            position.EndsWith("left") ? "24"
            : position.EndsWith("right") ? $"{w}-{tw}-24"
            : $"({w}-{tw})/2",
            position.StartsWith("top") ? "24"
            : position.StartsWith("bottom") ? $"{h}-{th}-24"
            : $"({h}-{th})/2"
        );
    }

    public async Task<string[]> TextFilters(string temp, CancellationToken ct)
    {
        var filters = new List<string>();
        var i = 0;
        foreach (var text in Texts ?? [])
        {
            var name = $"brand-{i++}.txt";
            await File.WriteAllTextAsync(
                Path.Combine(temp, name),
                text.Text.Replace('\t', ' '),
                ct
            );
            var (x, y) = Coordinates(text.Position);
            filters.Add(
                $"drawtext=font='DejaVu Sans':textfile={name}:expansion=none:fontsize={text.FontSize}:fontcolor=0x{text.Color[1..]}:box=1:boxcolor=0x{text.BoxColor[1..]}@{N(text.BoxOpacity)}:boxborderw=8:x={x}:y={y}:enable='between(t,{N(text.Start)},{N(text.End)})'"
            );
        }
        return filters.ToArray();
    }
}
