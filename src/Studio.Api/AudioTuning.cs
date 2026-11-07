using System.Globalization;

namespace Studio;

public record AudioTuning(
    double VoiceHighpass = 0,
    double VoiceLowpass = 0,
    bool DenoiseVoice = false,
    bool CompressVoice = false,
    double VoiceBass = 0,
    double VoiceTreble = 0,
    double VoicePan = 0,
    double VoiceStart = 0,
    double VoiceFadeIn = 0,
    double VoiceFadeOut = 0,
    double MusicSpeed = 1,
    double MusicHighpass = 0,
    double MusicLowpass = 0,
    double MusicPan = 0,
    bool MusicLoop = true,
    double MusicFadeIn = 0,
    double MusicFadeOut = 0
)
{
    public bool Valid() =>
        Cutoff(VoiceHighpass, 20, 500)
        && Cutoff(VoiceLowpass, 1000, 18000)
        && Range(VoiceBass, -12, 12)
        && Range(VoiceTreble, -12, 12)
        && Range(VoicePan, -1, 1)
        && Range(VoiceStart, 0, 600)
        && Range(VoiceFadeIn, 0, 5)
        && Range(VoiceFadeOut, 0, 5)
        && Range(MusicSpeed, .5, 2)
        && Cutoff(MusicHighpass, 20, 500)
        && Cutoff(MusicLowpass, 1000, 18000)
        && Range(MusicPan, -1, 1)
        && Range(MusicFadeIn, 0, 5)
        && Range(MusicFadeOut, 0, 5);

    static bool Range(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;

    static bool Cutoff(double value, double min, double max) =>
        value == 0 || Range(value, min, max);

    static string N(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    public bool VoiceChanged() =>
        VoiceHighpass != 0
        || VoiceLowpass != 0
        || DenoiseVoice
        || CompressVoice
        || VoiceBass != 0
        || VoiceTreble != 0
        || VoicePan != 0
        || VoiceFadeIn != 0
        || VoiceFadeOut != 0;

    public bool MusicChanged() =>
        MusicHighpass != 0
        || MusicLowpass != 0
        || MusicSpeed != 1
        || MusicPan != 0
        || MusicFadeIn != 0
        || MusicFadeOut != 0;

    static void Balance(List<string> filters, double value)
    {
        if (value != 0)
            filters.Add(
                $"aformat=channel_layouts=stereo,pan=stereo|c0={N(Math.Min(1, 1 - value))}*c0|c1={N(Math.Min(1, 1 + value))}*c1"
            );
    }

    public List<string> VoiceFilters()
    {
        var filters = new List<string>();
        if (VoiceChanged())
            filters.Add("aresample=48000");
        if (VoiceHighpass > 0)
            filters.Add($"highpass=f={N(VoiceHighpass)}");
        if (VoiceLowpass > 0)
            filters.Add($"lowpass=f={N(VoiceLowpass)}");
        if (DenoiseVoice)
            filters.Add("afftdn=nr=12:nf=-35");
        if (CompressVoice)
            filters.Add("acompressor=threshold=0.125:ratio=4:attack=10:release=150:makeup=1");
        if (VoiceBass != 0)
            filters.Add($"bass=g={N(VoiceBass)}:f=110");
        if (VoiceTreble != 0)
            filters.Add($"treble=g={N(VoiceTreble)}:f=3500");
        Balance(filters, VoicePan);
        return filters;
    }

    public static void Fades(List<string> filters, double fadeIn, double fadeOut, double duration)
    {
        var first = Math.Min(fadeIn, duration / 2);
        var last = Math.Min(fadeOut, duration / 2);
        if (first > 0)
            filters.Add($"afade=t=in:st=0:d={N(first)}");
        if (last > 0)
            filters.Add($"afade=t=out:st={N(duration - last)}:d={N(last)}");
    }

    public string MusicFilter(double gain, double duration)
    {
        var filters = new List<string>();
        if (MusicChanged())
            filters.Add("aresample=48000");
        if (MusicHighpass > 0)
            filters.Add($"highpass=f={N(MusicHighpass)}");
        if (MusicLowpass > 0)
            filters.Add($"lowpass=f={N(MusicLowpass)}");
        if (MusicSpeed != 1)
            filters.Add($"atempo={N(MusicSpeed)}");
        filters.Add($"volume={N(gain)}");
        Balance(filters, MusicPan);
        Fades(filters, MusicFadeIn, MusicFadeOut, duration);
        if (!MusicLoop)
            filters.Add("apad");
        return string.Join(',', filters);
    }
}
