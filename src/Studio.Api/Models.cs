using Microsoft.EntityFrameworkCore;

namespace Studio;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string StoredName { get; set; } = "";
}

public class RenderJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string RequestJson { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "Đang chờ";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record RenderRequest(
    Guid[] AssetIds,
    string Mode = "concat",
    int Duration = 30,
    Guid? VoiceId = null,
    Guid? MusicId = null,
    Guid? SubtitleId = null,
    string Ratio = "9:16",
    ClipTrim[]? Cuts = null,
    SceneClip[]? Scenes = null,
    bool AutoCaptions = false,
    int Resolution = 720,
    double VoiceGain = 1,
    double MusicGain = .18,
    string Fit = "contain",
    double FadeSeconds = 0,
    bool DuckMusic = false,
    RenderEffects? Effects = null
);

public record SceneClip(Guid AssetId, double Duration, double Start = 0, string Caption = "");

public record ClipTrim(Guid AssetId, double Start = 0, double? End = null);

public record NameRequest(string Name);

public record ScriptRequest(string Idea);

public class StudioDb(DbContextOptions<StudioDb> options) : DbContext(options)
{
    public DbSet<SavedWorkspaceItem> SavedWorkspaceItems => Set<SavedWorkspaceItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<RenderJob> Jobs => Set<RenderJob>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<ImageGeneration> ImageGenerations => Set<ImageGeneration>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SavedWorkspaceItem>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<SavedWorkspaceItem>().HasIndex(x => new { x.ProjectId, x.Kind });
        b.Entity<ImageGeneration>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<ImageGeneration>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<ImageGeneration>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<ProjectDocument>().HasKey(x => x.ProjectId);
        b.Entity<ProjectDocument>()
            .HasOne<Project>()
            .WithOne()
            .HasForeignKey<ProjectDocument>(x => x.ProjectId);
        b.Entity<Asset>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<RenderJob>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
    }
}

public class Storage
{
    public string Root { get; }

    public Storage(IConfiguration c)
    {
        Root = Path.GetFullPath(c["STUDIO_DATA"] ?? "data");
        Directory.CreateDirectory(Path.Combine(Root, "assets"));
        Directory.CreateDirectory(Path.Combine(Root, "exports"));
        Directory.CreateDirectory(Path.Combine(Root, "temp"));
    }

    public string AssetPath(Asset a) => Path.Combine(Root, "assets", a.StoredName);

    public string Subtitles(Guid id) => Path.Combine(Root, "exports", id + ".srt");

    public string Output(Guid id) => Path.Combine(Root, "exports", id + ".mp4");
}
