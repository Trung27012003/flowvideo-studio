namespace Studio;

public record SceneDirection(
    string Shot = "medium",
    string Camera = "static",
    string Lighting = "",
    string Location = "",
    string Characters = "",
    string Purpose = ""
)
{
    public static readonly string[] Shots =
    [
        "establishing",
        "wide",
        "medium",
        "close",
        "extreme-close",
    ];
    public static readonly string[] Cameras =
    [
        "static",
        "pan",
        "tilt",
        "dolly",
        "zoom",
        "handheld",
    ];

    public bool Valid() =>
        Shots.Contains(Shot)
        && Cameras.Contains(Camera)
        && new[] { Lighting, Location, Characters, Purpose }.All(v =>
            v is not null && v.Length <= 300 && !v.Contains('\0')
        );

    public static string VisualPrompt(StoryScene scene) =>
        scene.Prompt.Trim()
        + (
            scene.Direction is not null
                ? $"\nShot: {scene.Direction.Shot}; camera: {scene.Direction.Camera}; lighting: {scene.Direction.Lighting}; location: {scene.Direction.Location}; characters: {scene.Direction.Characters}."
                : ""
        );
}
