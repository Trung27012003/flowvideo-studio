using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public class AssetPreference
{
    public Guid AssetId { get; set; }
    public bool Favorite { get; set; }
    public string TagsJson { get; set; } = "[]";
    public string Note { get; set; } = "";
}

public record PreferenceRequest(bool Favorite, string[] Tags, string Note);

public record AssetSelection(Guid[] AssetIds);

public record CopyAssets(Guid[] AssetIds, Guid TargetProjectId);

public record BulkAssetName(
    Guid[] AssetIds,
    string Prefix = "Clip-",
    string Suffix = "",
    int StartNumber = 1
);

public static class LibraryTools
{
    public static Task Upgrade(StudioDb db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AssetPreferences" (
            "AssetId" TEXT NOT NULL CONSTRAINT "PK_AssetPreferences" PRIMARY KEY,
            "Favorite" INTEGER NOT NULL, "TagsJson" TEXT NOT NULL, "Note" TEXT NOT NULL,
            CONSTRAINT "FK_AssetPreferences_Assets_AssetId" FOREIGN KEY("AssetId") REFERENCES "Assets"("Id") ON DELETE CASCADE);
            """
        );

    static bool SelectionValid(Guid[]? ids) =>
        ids is { Length: >= 1 and <= 20 } && ids.Distinct().Count() == ids.Length;

    static object Dto(AssetPreference p) =>
        new
        {
            p.AssetId,
            p.Favorite,
            tags = JsonSerializer.Deserialize<string[]>(p.TagsJson),
            p.Note,
        };

    static async Task<List<Asset>> Selected(
        Guid project,
        Guid[] ids,
        StudioDb db,
        CancellationToken ct
    ) =>
        await db
            .Assets.AsNoTracking()
            .Where(a => a.ProjectId == project && ids.Contains(a.Id))
            .ToListAsync(ct);

    static bool Fragment(string? s) =>
        s is not null
        && s.Length <= 60
        && !s.Any(char.IsControl)
        && !s.Contains('/')
        && !s.Contains('\\');

    public static void MapLibraryTools(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/asset-preferences",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                var ids = db.Assets.Where(a => a.ProjectId == id).Select(a => a.Id);
                var rows = await db
                    .AssetPreferences.AsNoTracking()
                    .Where(p => ids.Contains(p.AssetId))
                    .ToListAsync(ct);
                return Results.Ok(rows.Select(Dto));
            }
        );
        app.MapPut(
            "/api/projects/{id:guid}/asset-preferences/{assetId:guid}",
            async (
                Guid id,
                Guid assetId,
                PreferenceRequest req,
                StudioDb db,
                CancellationToken ct
            ) =>
            {
                if (
                    req.Tags is null
                    || req.Tags.Length > 10
                    || req.Tags.Any(t =>
                        string.IsNullOrWhiteSpace(t)
                        || t.Trim().Length > 30
                        || t.Any(char.IsControl)
                    )
                    || req.Note is null
                    || req.Note.Length > 1000
                    || req.Note.Contains('\0')
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Tối đa 10 nhãn, mỗi nhãn 1–30 ký tự; ghi chú tối đa 1.000 ký tự.",
                        }
                    );
                if (!await db.Assets.AnyAsync(a => a.ProjectId == id && a.Id == assetId, ct))
                    return Results.NotFound();
                var tags = JsonSerializer.Serialize(
                    req.Tags.Select(t => t.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                );
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO AssetPreferences (AssetId, Favorite, TagsJson, Note) VALUES ({assetId},{req.Favorite},{tags},{req.Note}) ON CONFLICT(AssetId) DO UPDATE SET Favorite=excluded.Favorite, TagsJson=excluded.TagsJson, Note=excluded.Note",
                    ct
                );
                return Results.Ok(
                    new
                    {
                        assetId,
                        req.Favorite,
                        tags = JsonSerializer.Deserialize<string[]>(tags),
                        req.Note,
                    }
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/assets/copy",
            async (Guid id, CopyAssets req, StudioDb db, Storage store, CancellationToken ct) =>
            {
                if (!SelectionValid(req.AssetIds) || req.TargetProjectId == id)
                    return Results.BadRequest(
                        new { error = "Chọn 1–20 file và một dự án đích khác." }
                    );
                if (!await db.Projects.AnyAsync(p => p.Id == req.TargetProjectId, ct))
                    return Results.NotFound();
                var assets = await Selected(id, req.AssetIds, db, ct);
                if (assets.Count != req.AssetIds.Length)
                    return Results.BadRequest(
                        new { error = "Tài nguyên không thuộc dự án nguồn." }
                    );
                if (assets.Any(a => !File.Exists(store.AssetPath(a))))
                    return Results.Problem("Thiếu file nguồn.", statusCode: 422);
                if (assets.Sum(a => new FileInfo(store.AssetPath(a)).Length) > 104857600)
                    return Results.Problem("Sao chép tối đa 100 MiB.", statusCode: 413);
                var prefs = await db
                    .AssetPreferences.AsNoTracking()
                    .Where(p => req.AssetIds.Contains(p.AssetId))
                    .ToDictionaryAsync(p => p.AssetId, ct);
                var copies = new List<Asset>();
                var annotations = new List<AssetPreference>();
                bool committed = false;
                try
                {
                    long total = 0;
                    foreach (var a in req.AssetIds.Select(aid => assets.Single(a => a.Id == aid)))
                    {
                        var copy = new Asset
                        {
                            ProjectId = req.TargetProjectId,
                            Name = a.Name,
                            Kind = a.Kind,
                            StoredName =
                                Guid.NewGuid().ToString("N") + Path.GetExtension(a.StoredName),
                        };
                        copies.Add(copy);
                        await using var input = File.OpenRead(store.AssetPath(a));
                        await using var output = File.Create(store.AssetPath(copy));
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            total += read;
                            if (total > 104857600)
                                throw new IOException("Copy size limit.");
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                        if (prefs.TryGetValue(a.Id, out var p))
                            annotations.Add(
                                new()
                                {
                                    AssetId = copy.Id,
                                    Favorite = p.Favorite,
                                    TagsJson = p.TagsJson,
                                    Note = p.Note,
                                }
                            );
                    }
                    db.Assets.AddRange(copies);
                    db.AssetPreferences.AddRange(annotations);
                    await db.SaveChangesAsync(ct);
                    committed = true;
                    return Results.Ok(
                        new { assets = copies, targetProjectId = req.TargetProjectId }
                    );
                }
                finally
                {
                    if (!committed)
                        foreach (var a in copies)
                            File.Delete(store.AssetPath(a));
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/assets/duplicates",
            async (Guid id, AssetSelection req, StudioDb db, Storage store, CancellationToken ct) =>
            {
                if (!SelectionValid(req.AssetIds))
                    return Results.BadRequest(new { error = "Chọn 1–20 file khác nhau." });
                var assets = await Selected(id, req.AssetIds, db, ct);
                if (assets.Count != req.AssetIds.Length)
                    return Results.BadRequest(new { error = "File không thuộc dự án." });
                if (assets.Any(a => !File.Exists(store.AssetPath(a))))
                    return Results.Problem("Thiếu file nguồn.", statusCode: 422);
                if (assets.Sum(a => new FileInfo(store.AssetPath(a)).Length) > 104857600)
                    return Results.Problem("Kiểm tra tối đa 100 MiB.", statusCode: 413);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var hashes = new List<(Asset Asset, string Hash)>();
                    foreach (var a in assets)
                    {
                        await using var input = File.OpenRead(store.AssetPath(a));
                        var hash = Convert.ToHexString(
                            await SHA256.HashDataAsync(input, timeout.Token)
                        );
                        hashes.Add((a, hash));
                    }
                    return Results.Ok(
                        new
                        {
                            checkedCount = assets.Count,
                            groups = hashes
                                .GroupBy(x => x.Hash)
                                .Where(g => g.Count() > 1)
                                .Select(g => new
                                {
                                    hash = g.Key,
                                    assets = g.Select(x => new { x.Asset.Id, x.Asset.Name }),
                                }),
                        }
                    );
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem("Kiểm tra hash quá 30 giây.", statusCode: 504);
                }
            }
        );
        app.MapPatch(
            "/api/projects/{id:guid}/assets/bulk-name",
            async (Guid id, BulkAssetName req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    !SelectionValid(req.AssetIds)
                    || !Fragment(req.Prefix)
                    || !Fragment(req.Suffix)
                    || req.StartNumber < 1
                    || req.StartNumber + req.AssetIds.Length - 1 > 9999
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Chọn 1–20 file, tên không dùng đường dẫn, số thứ tự 1–9999.",
                        }
                    );
                var assets = await db
                    .Assets.Where(a => a.ProjectId == id && req.AssetIds.Contains(a.Id))
                    .ToListAsync(ct);
                if (assets.Count != req.AssetIds.Length)
                    return Results.BadRequest(new { error = "File không thuộc dự án." });
                foreach (var (aid, index) in req.AssetIds.Select((aid, index) => (aid, index)))
                {
                    var a = assets.Single(a => a.Id == aid);
                    a.Name =
                        req.Prefix
                        + (req.StartNumber + index).ToString("D2")
                        + req.Suffix
                        + Path.GetExtension(a.StoredName);
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(assets);
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/assets/{assetId:guid}/usage",
            async (Guid id, Guid assetId, StudioDb db, CancellationToken ct) =>
            {
                if (!await db.Assets.AnyAsync(a => a.ProjectId == id && a.Id == assetId, ct))
                    return Results.NotFound();
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                var doc = saved is null
                    ? new StoryDocument(Scenes: [])
                    : JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                bool AudioUse(StoryDocument d) => d.VoiceId == assetId || d.MusicId == assetId;
                var snapshots = await db
                    .SavedWorkspaceItems.AsNoTracking()
                    .Where(x => x.ProjectId == id && x.Kind == "checkpoint")
                    .ToListAsync(ct);
                var checkpoints = snapshots
                    .Where(x =>
                    {
                        var d = JsonSerializer.Deserialize<StoryDocument>(x.ContentJson)!;
                        return AudioUse(d) || d.Scenes!.Any(s => s.AssetId == assetId);
                    })
                    .Select(x => new { x.Id, x.Name })
                    .ToArray();
                var jobs = await db
                    .Jobs.AsNoTracking()
                    .Where(j => j.ProjectId == id)
                    .Select(j => j.RequestJson)
                    .ToListAsync(ct);
                var jobCount = jobs.Count(json =>
                {
                    var r = JsonSerializer.Deserialize<RenderRequest>(json)!;
                    return r.AssetIds.Contains(assetId)
                        || r.VoiceId == assetId
                        || r.MusicId == assetId
                        || r.SubtitleId == assetId;
                });
                return Results.Ok(
                    new
                    {
                        scenes = doc.Scenes!.Where(s => s.AssetId == assetId)
                            .Select(s => new
                            {
                                s.Id,
                                s.Title,
                                enabled = s.Options?.Enabled != false,
                            }),
                        audio = AudioUse(doc),
                        checkpoints,
                        jobCount,
                    }
                );
            }
        );
    }
}
