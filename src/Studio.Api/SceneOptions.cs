namespace Studio;

public record SceneOptions(
    bool Enabled = true,
    string Fit = "inherit",
    double? Brightness = null,
    int? Rotation = null,
    double Speed = 1,
    double Zoom = 1,
    double FocusX = .5,
    double FocusY = .5,
    bool Caption = true,
    double Fade = 0,
    string Note = ""
)
{
    public bool Valid() =>
        Fit is "inherit" or "contain" or "cover"
        && (Brightness is null || Range(Brightness.Value, -.5, .5))
        && (Rotation is null or 0 or 90 or 180 or 270)
        && Range(Speed, .5, 2)
        && Range(Zoom, 1, 2)
        && Range(FocusX, 0, 1)
        && Range(FocusY, 0, 1)
        && Range(Fade, 0, 1)
        && Note is not null
        && Note.Length <= 1000
        && !Note.Contains('\0');

    static bool Range(double n, double min, double max) =>
        double.IsFinite(n) && n >= min && n <= max;
}
