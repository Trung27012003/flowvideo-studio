using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Studio;

public record RenderEffects(
    double Brightness = 0,
    double Contrast = 1,
    double Saturation = 1,
    bool FlipHorizontal = false,
    bool FlipVertical = false,
    int Rotation = 0,
    bool Vignette = false,
    double Sharpen = 0,
    string Background = "#000000",
    int Fps = 30,
    string Quality = "standard",
    bool Mono = false,
    double MusicStart = 0,
    double VoiceDelay = 0,
    double VoiceSpeed = 1,
    bool NormalizeVoice = false
)
{
    public bool Valid() =>
        Range(Brightness, -.5, .5)
        && Range(Contrast, .5, 2)
        && Range(Saturation, 0, 2)
        && Rotation is 0 or 90 or 180 or 270
        && Range(Sharpen, 0, 1.5)
        && Background is not null
        && Regex.IsMatch(Background, @"\A#[0-9a-fA-F]{6}\z")
        && Fps is 24 or 30 or 60
        && Quality is "compact" or "standard" or "high"
        && Range(MusicStart, 0, 600)
        && Range(VoiceDelay, 0, 60)
        && Range(VoiceSpeed, .75, 1.5);

    private static bool Range(double n, double low, double high) =>
        double.IsFinite(n) && n >= low && n <= high;

    [JsonIgnore]
    public string Crf =>
        Quality == "high" ? "18"
        : Quality == "compact" ? "28"
        : "23";

    private static string N(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);

    public string VideoFilter(
        int w,
        int h,
        string fit,
        double focusX = .5,
        double focusY = .5,
        double zoom = 1
    )
    {
        var filters = new List<string>();
        if (Rotation == 90)
            filters.Add("transpose=clock");
        if (Rotation == 180)
        {
            filters.Add("hflip");
            filters.Add("vflip");
        }
        if (Rotation == 270)
            filters.Add("transpose=cclock");
        if (FlipHorizontal)
            filters.Add("hflip");
        if (FlipVertical)
            filters.Add("vflip");
        if (Brightness != 0 || Contrast != 1 || Saturation != 1)
            filters.Add(
                $"eq=brightness={N(Brightness)}:contrast={N(Contrast)}:saturation={N(Saturation)}"
            );
        filters.Add(
            fit == "cover"
                ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-ow)*{N(focusX)}:(ih-oh)*{N(focusY)}"
                : $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=0x{Background[1..]}"
        );
        if (zoom > 1)
        {
            var cw = Math.Max(2, (int)(w / zoom) / 2 * 2);
            var ch = Math.Max(2, (int)(h / zoom) / 2 * 2);
            filters.Add($"crop={cw}:{ch}:(iw-ow)*{N(focusX)}:(ih-oh)*{N(focusY)},scale={w}:{h}");
        }
        filters.Add($"setsar=1,fps={Fps}");
        if (Sharpen > 0)
            filters.Add($"unsharp=5:5:{N(Sharpen)}:5:5:0");
        if (Vignette)
            filters.Add("vignette=PI/5");
        return string.Join(',', filters);
    }

    public string VoiceFilter(double gain, AudioTuning? audio = null, double duration = 0)
    {
        audio ??= new AudioTuning();
        var filters = audio.VoiceFilters();
        if (VoiceSpeed != 1)
            filters.Add($"atempo={N(VoiceSpeed)}");
        if (NormalizeVoice)
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        filters.Add($"volume={N(gain)}");
        AudioTuning.Fades(filters, audio.VoiceFadeIn, audio.VoiceFadeOut, duration);
        if (VoiceDelay > 0)
            filters.Add($"adelay={Math.Round(VoiceDelay * 1000)}:all=1");
        filters.Add("apad");
        return string.Join(',', filters);
    }
}
