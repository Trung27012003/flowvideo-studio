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
    RenderEffects? Effects = null,
    CaptionStyle? Captions = null,
    AudioTuning? Audio = null,
    LoopOptions? Loop = null,
    BrandingOptions? Branding = null,
    ClipTransitions? Transitions = null
);

public record SceneClip(
    Guid AssetId,
    double Duration,
    double Start = 0,
    string Caption = "",
    SceneOptions? Options = null
);

public record ClipTrim(Guid AssetId, double Start = 0, double? End = null);

public record NameRequest(string Name);

public record ScriptRequest(string Idea, Guid? ProjectId = null);

public class StudioDb(DbContextOptions<StudioDb> options) : DbContext(options)
{
    public DbSet<ProjectBudget> ProjectBudgets => Set<ProjectBudget>();
    public DbSet<BudgetEntry> BudgetEntries => Set<BudgetEntry>();
    public DbSet<CreativeProfile> CreativeProfiles => Set<CreativeProfile>();
    public DbSet<AssetPreference> AssetPreferences => Set<AssetPreference>();
    public DbSet<SavedWorkspaceItem> SavedWorkspaceItems => Set<SavedWorkspaceItem>();
    public DbSet<RenderQaReport> RenderQaReports => Set<RenderQaReport>();
    public DbSet<StoryPlan> StoryPlans => Set<StoryPlan>();
    public DbSet<ProductionRun> ProductionRuns => Set<ProductionRun>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<RenderJob> Jobs => Set<RenderJob>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<VideoGeneration> VideoGenerations => Set<VideoGeneration>();

    public DbSet<ImageGeneration> ImageGenerations => Set<ImageGeneration>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<VideoGeneration>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<VideoGeneration>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<VideoGeneration>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<ProductionRun>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<ProductionRun>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<ProductionRun>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<StoryPlan>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<StoryPlan>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<StoryPlan>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<RenderQaReport>().HasKey(x => x.JobId);
        b.Entity<RenderQaReport>()
            .HasOne<RenderJob>()
            .WithOne()
            .HasForeignKey<RenderQaReport>(x => x.JobId);
        b.Entity<ProjectBudget>().HasKey(x => x.ProjectId);
        b.Entity<ProjectBudget>()
            .HasOne<Project>()
            .WithOne()
            .HasForeignKey<ProjectBudget>(x => x.ProjectId);
        b.Entity<BudgetEntry>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<BudgetEntry>().HasIndex(x => new { x.OperationId, x.Kind }).IsUnique();
        b.Entity<BudgetEntry>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<CreativeProfile>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        b.Entity<CreativeProfile>().HasIndex(x => new { x.ProjectId, x.Kind });
        b.Entity<AssetPreference>().HasKey(x => x.AssetId);
        b.Entity<AssetPreference>()
            .HasOne<Asset>()
            .WithOne()
            .HasForeignKey<AssetPreference>(x => x.AssetId);
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
