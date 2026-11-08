using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public record ArchiveRequest(Guid[] AssetIds);

public static class WorkspaceTools
{
    public static string Csv(string value)
    {
        var text = value.TrimStart();
        if (text.Length > 0 && "=+-@".Contains(text[0]))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static byte[] Utf8(string text) =>
        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    private static bool ValidName(string? name, int max) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= max
        && !name.Any(char.IsControl)
        && !name.Contains('/')
        && !name.Contains('\\');

    public static void MapWorkspaceTools(this WebApplication app)
    {
        app.MapPatch(
            "/api/projects/{id:guid}",
            async (Guid id, NameRequest req, StudioDb db, CancellationToken ct) =>
            {
                if (!ValidName(req.Name, 120))
                    return Results.BadRequest(
                        new
                        {
                            error = "Tên cần 1–120 ký tự, không dùng ký tự điều khiển hoặc dấu đường dẫn.",
                        }
                    );
                var name = req.Name.Trim();
                var count = await db
                    .Projects.Where(x => x.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name), ct);
                return count == 0 ? Results.NotFound() : Results.Ok(new { id, name });
            }
        );
        app.MapPatch(
            "/api/assets/{id:guid}",
            async (Guid id, NameRequest req, StudioDb db, CancellationToken ct) =>
            {
                var asset = await db.Assets.FindAsync([id], ct);
                if (asset is null)
                    return Results.NotFound();
                if (!ValidName(req.Name, 180))
                    return Results.BadRequest(
                        new
                        {
                            error = "Tên cần 1–180 ký tự, không dùng dấu đường dẫn/ký tự điều khiển.",
                        }
                    );
                var name = req.Name.Trim();
                var ext = Path.GetExtension(asset.StoredName);
                if (Path.GetExtension(name) == "")
                    name += ext;
                if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(
                        new { error = "Giữ nguyên phần mở rộng của tài nguyên." }
                    );
                asset.Name = name;
                await db.SaveChangesAsync(ct);
                return Results.Ok(asset);
            }
        );
        app.MapGet(
            "/api/assets/{id:guid}/metadata",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
                if (asset is null || !File.Exists(store.AssetPath(asset)))
                    return Results.NotFound();
                var bytes = new FileInfo(store.AssetPath(asset)).Length;
                if (asset.Kind == "subtitle")
                    return Results.Ok(
                        new
                        {
                            bytes,
                            duration = (double?)null,
                            width = (int?)null,
                            height = (int?)null,
                            codec = "SRT",
                            audioCodec = (string?)null,
                            frameRate = (double?)null,
                        }
                    );
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var raw = await MediaProcess.Run(
                        "ffprobe",
                        [
                            "-v",
                            "error",
                            "-show_entries",
                            "format=duration:stream=codec_type,codec_name,width,height,r_frame_rate,duration",
                            "-of",
                            "json",
                            store.AssetPath(asset),
                        ],
                        store.Root,
                        timeout.Token
                    );
                    using var json = JsonDocument.Parse(raw);
                    var streams = json
                        .RootElement.GetProperty("streams")
                        .EnumerateArray()
                        .ToArray();
                    var video = streams.FirstOrDefault(s =>
                        s.GetProperty("codec_type").GetString() == "video"
                    );
                    var audio = streams.FirstOrDefault(s =>
                        s.GetProperty("codec_type").GetString() == "audio"
                    );
                    string? Text(JsonElement element, string key) =>
                        element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty(key, out var v)
                        && v.ValueKind == JsonValueKind.String
                            ? v.GetString()
                            : null;
                    double? Number(string? value) =>
                        double.TryParse(
                            value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var d
                        )
                        && double.IsFinite(d)
                        && d >= 0
                            ? d
                            : null;
                    int? Int(JsonElement element, string key) =>
                        element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty(key, out var v)
                        && v.TryGetInt32(out var n)
                            ? n
                            : null;
                    var duration =
                        asset.Kind == "image"
                            ? null
                            : Number(Text(json.RootElement.GetProperty("format"), "duration"))
                                ?? Number(Text(video, "duration"))
                                ?? Number(Text(audio, "duration"));
                    var rate = Text(video, "r_frame_rate")?.Split('/');
                    double? fps =
                        rate?.Length == 2
                        && Number(rate[0]) is double numerator
                        && Number(rate[1]) is double denominator
                        && denominator > 0
                            ? numerator / denominator
                            : null;
                    return Results.Ok(
                        new
                        {
                            bytes,
                            duration,
                            width = Int(video, "width"),
                            height = Int(video, "height"),
                            codec = Text(video, "codec_name") ?? Text(audio, "codec_name"),
                            audioCodec = Text(audio, "codec_name"),
                            frameRate = fps,
                        }
                    );
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem("Đọc metadata quá thời gian chờ.", statusCode: 504);
                }
                catch (Exception e)
                    when (e
                            is IOException
                                or JsonException
                                or InvalidOperationException
                                or KeyNotFoundException
                    )
                {
                    return Results.Problem("Không đọc được metadata của file.", statusCode: 422);
                }
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/assets/manifest",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id)
                    .OrderBy(a => a.Name)
                    .ToListAsync(ct);
                var csv =
                    "id,name,type,bytes\r\n"
                    + string.Join(
                        "\r\n",
                        assets.Select(a =>
                            string.Join(
                                ',',
                                new[]
                                {
                                    a.Id.ToString(),
                                    a.Name,
                                    a.Kind,
                                    File.Exists(store.AssetPath(a))
                                        ? new FileInfo(store.AssetPath(a)).Length.ToString(
                                            CultureInfo.InvariantCulture
                                        )
                                        : "missing",
                                }.Select(Csv)
                            )
                        )
                    );
                return Results.File(Utf8(csv), "text/csv; charset=utf-8", $"assets-{id}.csv");
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/assets/archive",
            async (
                Guid id,
                ArchiveRequest req,
                StudioDb db,
                Storage store,
                HttpContext context,
                CancellationToken ct
            ) =>
            {
                if (
                    req.AssetIds is null
                    || req.AssetIds.Length is < 1 or > 20
                    || req.AssetIds.Distinct().Count() != req.AssetIds.Length
                )
                    return Results.BadRequest(new { error = "Chọn 1–20 tài nguyên khác nhau." });
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id && req.AssetIds.Contains(a.Id))
                    .ToListAsync(ct);
                if (assets.Count != req.AssetIds.Length)
                    return Results.BadRequest(new { error = "Tài nguyên không thuộc dự án." });
                if (assets.Any(a => !File.Exists(store.AssetPath(a))))
                    return Results.Problem("Thiếu file nguồn.", statusCode: 422);
                if (assets.Sum(a => new FileInfo(store.AssetPath(a)).Length) > 104857600)
                    return Results.Problem("ZIP tối đa 100 MiB dữ liệu nguồn.", statusCode: 413);
                var path = Path.Combine(
                    store.Root,
                    "temp",
                    "archive-" + Guid.NewGuid().ToString("N") + ".zip"
                );
                var ready = false;
                try
                {
                    using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
                    {
                        long total = 0;
                        int index = 0;
                        foreach (var asset in assets)
                        {
                            var name = Path.GetFileName(asset.Name.Replace('\\', '/'));
                            name = string.Concat(name.EnumerateRunes().Take(120)).Replace(':', '_');
                            if (string.IsNullOrWhiteSpace(name))
                                name = asset.Id.ToString();
                            var entry = zip.CreateEntry(
                                $"{++index:00}-{name}",
                                CompressionLevel.NoCompression
                            );
                            await using var output = entry.Open();
                            await using var input = File.OpenRead(store.AssetPath(asset));
                            var buffer = new byte[81920];
                            int read;
                            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                total += read;
                                if (total > 104857600)
                                    throw new IOException("Archive exceeded limit.");
                                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            }
                        }
                    }
                    context.Response.RegisterForDispose(new TempFile(path));
                    ready = true;
                    return Results.File(path, "application/zip", $"assets-{id}.zip");
                }
                finally
                {
                    if (!ready)
                        File.Delete(path);
                }
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/duplicate",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var source = await db
                    .Projects.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == id, ct);
                if (source is null)
                    return Results.NotFound();
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id)
                    .ToListAsync(ct);
                if (assets.Count > 200)
                    return Results.BadRequest(new { error = "Nhân bản tối đa 200 tài nguyên." });
                if (assets.Any(a => !File.Exists(store.AssetPath(a))))
                    return Results.Problem("Thiếu file nguồn; chưa nhân bản.", statusCode: 422);
                if (assets.Sum(a => new FileInfo(store.AssetPath(a)).Length) > 524288000)
                    return Results.Problem("Nhân bản tối đa 500 MiB media.", statusCode: 413);
                var stored = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                var doc = stored is null
                    ? null
                    : JsonSerializer.Deserialize<StoryDocument>(stored.ContentJson);
                var project = new Project
                {
                    Name = source.Name[..Math.Min(source.Name.Length, 100)] + " · bản sao",
                };
                var map = new Dictionary<Guid, Guid>();
                var copies = new List<Asset>();
                bool committed = false;
                try
                {
                    foreach (var a in assets)
                    {
                        ct.ThrowIfCancellationRequested();
                        var copy = new Asset
                        {
                            ProjectId = project.Id,
                            Name = a.Name,
                            Kind = a.Kind,
                            StoredName =
                                Guid.NewGuid().ToString("N") + Path.GetExtension(a.StoredName),
                        };
                        copies.Add(copy);
                        map[a.Id] = copy.Id;
                        await using var input = File.OpenRead(store.AssetPath(a));
                        await using var output = File.Create(store.AssetPath(copy));
                        await input.CopyToAsync(output, ct);
                    }
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    db.Projects.Add(project);
                    db.Assets.AddRange(copies);
                    var profiles = await db
                        .CreativeProfiles.AsNoTracking()
                        .Where(p => p.ProjectId == id)
                        .ToListAsync(ct);
                    var profileMap = profiles.ToDictionary(p => p.Id, _ => Guid.NewGuid());
                    foreach (var profile in profiles)
                        db.CreativeProfiles.Add(
                            new CreativeProfile
                            {
                                Id = profileMap[profile.Id],
                                ProjectId = project.Id,
                                Kind = profile.Kind,
                                Version = profile.Version,
                                ContentJson = JsonSerializer.Serialize(
                                    CreativeProfiles.Snapshot(profile).Content with
                                    {
                                        ReferenceId = CreativeProfiles
                                            .Snapshot(profile)
                                            .Content.ReferenceId
                                            is Guid refId
                                            ? map.GetValueOrDefault(refId)
                                            : null,
                                    },
                                    CreativeProfiles.Web
                                ),
                            }
                        );

                    if (doc is not null)
                    {
                        var cloned = doc with
                        {
                            Scenes = doc.Scenes!.Select(s =>
                                    s with
                                    {
                                        Continuity = SceneContinuity.Map(
                                            s.Continuity,
                                            aid =>
                                                map.TryGetValue(aid, out var mapped)
                                                    ? mapped
                                                    : null,
                                            pid => profileMap.GetValueOrDefault(pid, pid)
                                        ),
                                        Id = Guid.NewGuid(),
                                        AssetId = s.AssetId is Guid aid
                                            ? map.GetValueOrDefault(aid)
                                            : null,
                                    }
                                )
                                .ToArray(),
                            Branding = doc.Branding?.Logo is LogoOverlay logo
                                ? doc.Branding with
                                {
                                    Logo = logo with { AssetId = map[logo.AssetId] },
                                }
                                : doc.Branding,
                            VoiceId = doc.VoiceId is Guid v ? map.GetValueOrDefault(v) : null,
                            MusicId = doc.MusicId is Guid m ? map.GetValueOrDefault(m) : null,
                        };
                        db.ProjectDocuments.Add(
                            new()
                            {
                                ProjectId = project.Id,
                                Version = 1,
                                ContentJson = JsonSerializer.Serialize(cloned),
                                UpdatedAt = DateTime.UtcNow,
                            }
                        );
                    }
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    committed = true;
                    return Results.Created(
                        $"/api/projects/{project.Id}",
                        new { project, copiedAssets = copies.Count }
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
        app.MapGet(
            "/api/projects/{id:guid}/storyboard/narration",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                if (saved is null)
                    return Results.NotFound();
                var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                return Results.File(
                    Encoding.UTF8.GetBytes(
                        string.Join(
                            "\n\n",
                            doc.Scenes!.Where(s => !string.IsNullOrWhiteSpace(s.Narration))
                                .Select(s => s.Narration)
                        )
                    ),
                    "text/plain; charset=utf-8",
                    $"narration-{id}.txt"
                );
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/storyboard/shotlist",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                var saved = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ProjectId == id, ct);
                if (saved is null)
                    return Results.NotFound();
                var doc = JsonSerializer.Deserialize<StoryDocument>(saved.ContentJson)!;
                var assets = await db
                    .Assets.AsNoTracking()
                    .Where(a => a.ProjectId == id)
                    .ToDictionaryAsync(a => a.Id, ct);
                double start = 0;
                var lines = new List<string>
                {
                    "scene,title,start,duration,narration,prompt,asset",
                };
                int n = 0;
                foreach (var s in doc.Scenes!)
                {
                    lines.Add(
                        string.Join(
                            ',',
                            new[]
                            {
                                (++n).ToString(),
                                s.Title,
                                start.ToString("0.###", CultureInfo.InvariantCulture),
                                s.Duration.ToString("0.###", CultureInfo.InvariantCulture),
                                s.Narration,
                                s.Prompt,
                                s.AssetId is Guid aid
                                    ? assets.GetValueOrDefault(aid)?.Name ?? ""
                                    : "",
                            }.Select(Csv)
                        )
                    );
                    start += s.Duration;
                }
                return Results.File(
                    Utf8(string.Join("\r\n", lines)),
                    "text/csv; charset=utf-8",
                    $"shotlist-{id}.csv"
                );
            }
        );
    }

    private sealed class TempFile(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
        }
    }
}
