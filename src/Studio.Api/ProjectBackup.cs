using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public record BackupAsset(
    Guid Id,
    string Name,
    string Kind,
    string Entry,
    long Bytes,
    string Sha256,
    AssetPreference? Preference
);

public record BackupProfile(Guid Id, int Version, string Kind, ProfileContent Content);

public record BackupItem(string Kind, string Name, StoryDocument Document);

public record ProjectArchive(
    string Format,
    int SchemaVersion,
    string Name,
    StoryDocument? Document,
    BackupAsset[] Assets,
    BackupProfile[] Profiles,
    BackupItem[] SavedItems
);

public static class ProjectBackup
{
    const long Limit = 240 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, string> Extensions = new()
    {
        [".png"] = "image",
        [".jpg"] = "image",
        [".jpeg"] = "image",
        [".webp"] = "image",
        [".mp4"] = "video",
        [".mov"] = "video",
        [".mkv"] = "video",
        [".webm"] = "video",
        [".mp3"] = "audio",
        [".wav"] = "audio",
        [".m4a"] = "audio",
        [".aac"] = "audio",
        [".ogg"] = "audio",
        [".flac"] = "audio",
        [".srt"] = "subtitle",
    };

    sealed class Cleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException) { }
        }
    }

    static string Temp(Storage s)
    {
        var p = Path.Combine(s.Root, "temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    public static StoryDocument Remap(
        StoryDocument doc,
        Dictionary<Guid, Guid> assets,
        Dictionary<Guid, Guid> profiles
    )
    {
        if (
            doc.Scenes?.Any(s => s is null || s.Continuity is not null && !s.Continuity.Valid())
                == true
            || doc.Branding is not null && !doc.Branding.Valid()
        )
            throw new InvalidDataException("Cảnh hoặc lớp chữ/logo không hợp lệ.");
        Guid? Asset(Guid id) =>
            assets.TryGetValue(id, out var mapped)
                ? mapped
                : throw new InvalidDataException("Thiếu media được tham chiếu.");
        return doc with
        {
            Scenes = doc
                .Scenes?.Select(s =>
                    s with
                    {
                        Id = Guid.NewGuid(),
                        AssetId = s.AssetId is Guid a ? Asset(a) : null,
                        Continuity = SceneContinuity.Map(
                            s.Continuity,
                            Asset,
                            p => profiles.GetValueOrDefault(p, p)
                        ),
                    }
                )
                .ToArray(),
            VoiceId = doc.VoiceId is Guid v ? Asset(v) : null,
            MusicId = doc.MusicId is Guid m ? Asset(m) : null,
            Branding = doc.Branding?.Logo is LogoOverlay logo
                ? doc.Branding with
                {
                    Logo = logo with { AssetId = Asset(logo.AssetId)!.Value },
                }
                : doc.Branding,
        };
    }

    public static void MapProjectBackup(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/backup",
            async (
                Guid id,
                StudioDb db,
                Storage store,
                HttpContext context,
                CancellationToken ct
            ) =>
            {
                var project = await db
                    .Projects.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == id, ct);
                if (project is null)
                    return Results.NotFound();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id)
                    .ToListAsync(ct);
                if (assets.Count > 100 || assets.Any(a => !File.Exists(store.AssetPath(a))))
                    return Results.UnprocessableEntity(
                        new { error = "Tối đa 100 media; tất cả file nguồn phải còn." }
                    );
                if (assets.Sum(a => new FileInfo(store.AssetPath(a)).Length) > Limit)
                    return Results.Json(
                        new { error = "Backup tối đa 240 MiB media." },
                        statusCode: 413
                    );
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                var profiles = (
                    await db
                        .CreativeProfiles.AsNoTracking()
                        .Where(p => p.ProjectId == id)
                        .ToListAsync(ct)
                )
                    .Select(p => new BackupProfile(
                        p.Id,
                        p.Version,
                        p.Kind,
                        CreativeProfiles.Snapshot(p).Content
                    ))
                    .ToArray();
                var items = (
                    await db
                        .SavedWorkspaceItems.AsNoTracking()
                        .Where(p => p.ProjectId == id)
                        .ToListAsync(ct)
                )
                    .Select(p => new BackupItem(
                        p.Kind,
                        p.Name,
                        JsonSerializer.Deserialize<StoryDocument>(p.ContentJson)!
                    ))
                    .ToArray();
                var prefs = await db
                    .AssetPreferences.AsNoTracking()
                    .Where(p => assets.Select(a => a.Id).Contains(p.AssetId))
                    .ToDictionaryAsync(p => p.AssetId, ct);
                await transaction.CommitAsync(ct);
                var temp = Temp(store);
                var success = false;
                try
                {
                    var path = Path.Combine(temp, "backup.zip");
                    await using (var stream = File.Create(path))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    {
                        var list = new List<BackupAsset>();
                        foreach (var a in assets)
                        {
                            var entry =
                                $"media/{a.Id:N}{Path.GetExtension(a.StoredName).ToLowerInvariant()}";
                            await using var source = File.OpenRead(store.AssetPath(a));
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
                            source.Position = 0;
                            await using (
                                var target = zip.CreateEntry(entry, CompressionLevel.NoCompression)
                                    .Open()
                            )
                                await source.CopyToAsync(target, ct);
                            list.Add(
                                new(
                                    a.Id,
                                    a.Name,
                                    a.Kind,
                                    entry,
                                    source.Length,
                                    hash,
                                    prefs.GetValueOrDefault(a.Id)
                                )
                            );
                        }
                        var manifest = new ProjectArchive(
                            "flowvideo.project",
                            1,
                            project.Name,
                            saved is null
                                ? null
                                : JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson),
                            list.ToArray(),
                            profiles,
                            items
                        );
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                        if (bytes.Length > 2 * 1024 * 1024)
                            throw new InvalidDataException("Manifest quá 2 MiB.");
                        await using var output = zip.CreateEntry("manifest.json").Open();
                        await output.WriteAsync(bytes, ct);
                    }
                    context.Response.RegisterForDispose(new Cleanup(temp));
                    success = true;
                    return Results.File(path, "application/zip", $"flowvideo-{id}.zip");
                }
                finally
                {
                    if (!success)
                        Directory.Delete(temp, true);
                }
            }
        );
        app.MapPost(
                "/api/backups/restore",
                async (HttpRequest request, StudioDb db, Storage store, CancellationToken ct) =>
                {
                    if (!request.HasFormContentType)
                        return Results.BadRequest(new { error = "Chọn file ZIP backup." });
                    var form = await request.ReadFormAsync(ct);
                    if (form.Files.Count != 1 || form.Files[0].Length > Limit + 3 * 1024 * 1024)
                        return Results.BadRequest(
                            new { error = "Cần đúng 1 file ZIP, tối đa 243 MiB." }
                        );
                    var copies = new List<Asset>();
                    var committed = false;
                    var temp = Temp(store);
                    try
                    {
                        var path = Path.Combine(temp, "input.zip");
                        await using (var file = File.Create(path))
                            await form.Files[0].CopyToAsync(file, ct);
                        using var zip = ZipFile.OpenRead(path);
                        if (
                            zip.Entries.Count > 101
                            || zip.Entries.Select(e => e.FullName)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count() != zip.Entries.Count
                            || zip.Entries.Any(e => e.Length < 0 || e.Length > Limit)
                            || zip.Entries.Sum(e => e.Length) > Limit + 2 * 1024 * 1024
                        )
                            throw new InvalidDataException(
                                "ZIP trùng đường dẫn hoặc vượt giới hạn giải nén."
                            );
                        var me = zip.GetEntry("manifest.json");
                        if (me is null || me.Length > 2 * 1024 * 1024)
                            throw new InvalidDataException("Thiếu manifest hoặc quá 2 MiB.");
                        ProjectArchive? m;
                        await using (var input = me.Open())
                        {
                            using var buffer = new MemoryStream();
                            var chunk = new byte[65536];
                            int count;
                            while ((count = await input.ReadAsync(chunk, ct)) > 0)
                            {
                                if (buffer.Length + count > 2 * 1024 * 1024)
                                    throw new InvalidDataException("Manifest giải nén vượt 2 MiB.");
                                buffer.Write(chunk, 0, count);
                            }
                            if (buffer.Length != me.Length)
                                throw new InvalidDataException("Kích thước manifest không khớp.");
                            m = JsonSerializer.Deserialize<ProjectArchive>(buffer.ToArray(), Json);
                        }
                        if (
                            m is null
                            || m.Format != "flowvideo.project"
                            || m.SchemaVersion != 1
                            || string.IsNullOrWhiteSpace(m.Name)
                            || m.Name.Length > 120
                            || m.Assets is null
                            || m.Assets.Length > 100
                            || m.Profiles is null
                            || m.Profiles.Length > 50
                            || m.SavedItems is null
                            || m.SavedItems.Length > 40
                            || m.Assets.Any(a => a is null)
                            || m.Profiles.Any(p => p is null)
                            || m.SavedItems.Any(p => p is null)
                            || m.SavedItems.GroupBy(p => p.Kind).Any(g => g.Count() > 20)
                            || m.Assets.Select(a => a.Id).Distinct().Count() != m.Assets.Length
                            || m.Profiles.Select(p => p.Id).Distinct().Count() != m.Profiles.Length
                        )
                            throw new InvalidDataException(
                                "Manifest sai định dạng/phiên bản/giới hạn."
                            );
                        if (zip.Entries.Count != m.Assets.Length + 1)
                            throw new InvalidDataException("ZIP có file thừa hoặc thiếu.");
                        var project = new Project
                        {
                            Name = m.Name[..Math.Min(m.Name.Length, 100)] + " · khôi phục",
                        };
                        var map = m.Assets.ToDictionary(a => a.Id, _ => Guid.NewGuid());
                        var profileMap = m.Profiles.ToDictionary(p => p.Id, _ => Guid.NewGuid());
                        foreach (var a in m.Assets)
                        {
                            var ext = Path.GetExtension(a.Entry ?? "").ToLowerInvariant();
                            if (
                                a.Id == Guid.Empty
                                || !Extensions.TryGetValue(ext, out var kind)
                                || kind != a.Kind
                                || a.Entry != $"media/{a.Id:N}{ext}"
                                || a.Bytes < 0
                                || a.Bytes > Limit
                                || string.IsNullOrWhiteSpace(a.Name)
                                || a.Name.Length > 4096
                                || a.Sha256 is null
                                || a.Sha256.Length != 64
                            )
                                throw new InvalidDataException(
                                    "Thông tin media/đường dẫn không hợp lệ."
                                );
                            var entry = zip.GetEntry(a.Entry);
                            if (entry is null || entry.Length != a.Bytes)
                                throw new InvalidDataException("Thiếu media hoặc kích thước sai.");
                            var copy = new Asset
                            {
                                Id = map[a.Id],
                                ProjectId = project.Id,
                                Name = a.Name,
                                Kind = a.Kind,
                                StoredName = Guid.NewGuid().ToString("N") + ext,
                            };
                            copies.Add(copy);
                            await using (var input = entry.Open())
                            await using (var output = File.Create(store.AssetPath(copy)))
                            {
                                var buffer = new byte[65536];
                                long total = 0;
                                int n;
                                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                                {
                                    total += n;
                                    if (total > a.Bytes)
                                        throw new InvalidDataException(
                                            "Media giải nén vượt kích thước."
                                        );
                                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                                }
                                if (total != a.Bytes)
                                    throw new InvalidDataException("Media bị cắt ngắn.");
                            }
                            await using (var file = File.OpenRead(store.AssetPath(copy)))
                                if (
                                    !Convert
                                        .ToHexString(await SHA256.HashDataAsync(file, ct))
                                        .Equals(a.Sha256, StringComparison.OrdinalIgnoreCase)
                                )
                                    throw new InvalidDataException(
                                        "SHA-256 không khớp; backup đã bị thay đổi."
                                    );
                            if (a.Preference is AssetPreference pref)
                            {
                                var tags = JsonSerializer.Deserialize<string[]>(pref.TagsJson);
                                if (
                                    tags is null
                                    || tags.Length > 10
                                    || tags.Any(t =>
                                        string.IsNullOrWhiteSpace(t)
                                        || t.Length > 30
                                        || t.Any(char.IsControl)
                                    )
                                    || pref.Note is null
                                    || pref.Note.Length > 1000
                                    || pref.Note.Contains('\0')
                                )
                                    throw new InvalidDataException("Metadata không hợp lệ.");
                                db.AssetPreferences.Add(
                                    new()
                                    {
                                        AssetId = copy.Id,
                                        Favorite = pref.Favorite,
                                        TagsJson = pref.TagsJson,
                                        Note = pref.Note,
                                    }
                                );
                            }
                        }
                        await using var tx = await db.Database.BeginTransactionAsync(ct);
                        db.Projects.Add(project);
                        db.Assets.AddRange(copies);
                        await db.SaveChangesAsync(ct);
                        foreach (var p in m.Profiles)
                        {
                            if (
                                p.Id == Guid.Empty
                                || p.Version < 1
                                || p.Kind is not ("character" or "location")
                                || p.Content is null
                                || !p.Content.Valid()
                            )
                                throw new InvalidDataException("Hồ sơ không hợp lệ.");
                            var content = p.Content with
                            {
                                ReferenceId = p.Content.ReferenceId is Guid aid
                                    ? map.TryGetValue(aid, out var mapped)
                                        ? mapped
                                        : throw new InvalidDataException("Thiếu ảnh hồ sơ.")
                                    : null,
                            };
                            if (!await CreativeProfiles.ReferenceValid(project.Id, content, db, ct))
                                throw new InvalidDataException("Ảnh hồ sơ sai loại.");
                            db.CreativeProfiles.Add(
                                new()
                                {
                                    Id = profileMap[p.Id],
                                    ProjectId = project.Id,
                                    Kind = p.Kind,
                                    Version = p.Version,
                                    ContentJson = JsonSerializer.Serialize(content, Json),
                                }
                            );
                        }
                        async Task<StoryDocument> Document(StoryDocument original)
                        {
                            var doc = Remap(original, map, profileMap);
                            var error = await Storyboard.Validate(project.Id, doc, db, false, ct);
                            if (error is not null)
                                throw new InvalidDataException(error);
                            return doc;
                        }
                        if (m.Document is not null)
                            db.ProjectDocuments.Add(
                                new()
                                {
                                    ProjectId = project.Id,
                                    Version = 1,
                                    ContentJson = JsonSerializer.Serialize(
                                        await Document(m.Document)
                                    ),
                                    UpdatedAt = DateTime.UtcNow,
                                }
                            );
                        foreach (var item in m.SavedItems)
                        {
                            if (
                                item.Kind is not ("checkpoint" or "preset")
                                || string.IsNullOrWhiteSpace(item.Name)
                                || item.Name.Length > 120
                                || item.Document is null
                            )
                                throw new InvalidDataException("Mốc lưu không hợp lệ.");
                            db.SavedWorkspaceItems.Add(
                                new()
                                {
                                    ProjectId = project.Id,
                                    Kind = item.Kind,
                                    Name = item.Name,
                                    ContentJson = JsonSerializer.Serialize(
                                        await Document(item.Document)
                                    ),
                                }
                            );
                        }
                        await db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        committed = true;
                        return Results.Created(
                            $"/api/projects/{project.Id}",
                            new { project, restoredAssets = copies.Count }
                        );
                    }
                    catch (Exception ex)
                        when (ex
                                is InvalidDataException
                                    or JsonException
                                    or ArgumentException
                                    or OverflowException
                        )
                    {
                        return Results.BadRequest(new { error = "Không khôi phục: " + ex.Message });
                    }
                    finally
                    {
                        Directory.Delete(temp, true);
                        if (!committed)
                            foreach (var a in copies)
                                File.Delete(store.AssetPath(a));
                    }
                }
            )
            .DisableAntiforgery();
    }
}
