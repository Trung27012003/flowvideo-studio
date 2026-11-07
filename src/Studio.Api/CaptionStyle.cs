using System.Globalization;
using System.Text.RegularExpressions;

namespace Studio;

public record CaptionStyle(
    string Font = "sans",
    int Size = 18,
    string Color = "#ffffff",
    string OutlineColor = "#000000",
    double Outline = 1,
    double Shadow = 0,
    bool Bold = false,
    int Alignment = 2,
    int Margin = 36,
    bool Box = false,
    string BoxColor = "#000000",
    double BoxOpacity = .5
)
{
    public bool Valid() =>
        Font is "sans" or "serif" or "mono"
        && Size is >= 12 and <= 48
        && Hex(Color)
        && Hex(OutlineColor)
        && Range(Outline, 0, 4)
        && Range(Shadow, 0, 4)
        && Alignment is >= 1 and <= 9
        && Margin is >= 0 and <= 200
        && Hex(BoxColor)
        && Range(BoxOpacity, 0, 1);

    static bool Range(double n, double low, double high) =>
        double.IsFinite(n) && n >= low && n <= high;

    static bool Hex(string? s) => s is not null && Regex.IsMatch(s, @"\A#[0-9a-fA-F]{6}\z");

    static string AssColor(string color, double opacity = 1) =>
        $"&H{(int)Math.Round((1 - opacity) * 255):X2}{color[5..7]}{color[3..5]}{color[1..3]}";

    static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public string FilterStyle()
    {
        // force_style parses Alignment using legacy SSA values, while the UI uses ASS numpad positions.
        var alignment =
            Alignment <= 3 ? Alignment
            : Alignment <= 6 ? Alignment + 5
            : Alignment - 2;
        var font =
            Font == "serif" ? "DejaVu Serif"
            : Font == "mono" ? "DejaVu Sans Mono"
            : "DejaVu Sans";
        return $"FontName={font},FontSize={Size},PrimaryColour={AssColor(Color)},OutlineColour={AssColor(Box ? BoxColor : OutlineColor, Box ? BoxOpacity : 1)},BackColour={AssColor(BoxColor, BoxOpacity)},Outline={N(Outline)},Shadow={N(Shadow)},Bold={(Bold ? -1 : 0)},Alignment={alignment},MarginV={Margin},BorderStyle={(Box ? 3 : 1)}";
    }
}
