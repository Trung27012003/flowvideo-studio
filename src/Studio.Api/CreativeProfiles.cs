using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public record ProfileContent(
    string Name,
    string Description,
    string Appearance = "",
    string Outfit = "",
    string Voice = "",
    string Weather = "",
    string TimeOfDay = "",
    string Lighting = "",
    string Props = "",
    string Notes = "",
    Guid? ReferenceId = null
)
{
    public bool Valid() =>
        !string.IsNullOrWhiteSpace(Name)
        && Name.Length <= 120
        && Description is not null
        && Description.Length <= 1000
        && new[]
        {
            Name,
            Description,
            Appearance,
            Outfit,
            Voice,
            Weather,
            TimeOfDay,
            Lighting,
            Props,
            Notes,
        }.All(s => s is not null && !s.Contains('\0'))
        && new[] { Appearance, Outfit, Voice, Weather, TimeOfDay, Lighting, Props, Notes }.All(s =>
            s.Length <= 300
        );

    public string Prompt(string kind) =>
        kind == "character"
            ? $"Character {Name}: {Description}; appearance: {Appearance}; outfit: {Outfit}; voice: {Voice}."
            : $"Location {Name}: {Description}; weather: {Weather}; time: {TimeOfDay}; lighting: {Lighting}; props: {Props}.";
}

public class CreativeProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Kind { get; set; } = "character";
    public string ContentJson { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record ProfileSnapshot(Guid Id, int Version, string Kind, ProfileContent Content);

public record SceneContinuity(
    ProfileSnapshot[]? Characters = null,
    ProfileSnapshot? Location = null,
    string CharacterState = ""
)
{
    public IEnumerable<ProfileSnapshot> Items() =>
        (Characters ?? []).Concat(Location is null ? [] : new[] { Location });

    public bool Valid() =>
        (Characters ?? []).Length <= 2
        && (Characters ?? []).All(p => p is not null && p.Kind == "character")
        && (Location is null || Location.Kind == "location")
        && Items()
            .All(p =>
                p.Id != Guid.Empty && p.Version >= 1 && p.Content is not null && p.Content.Valid()
            )
        && Items().Select(p => p.Id).Distinct().Count() == Items().Count()
        && CharacterState is not null
        && CharacterState.Length <= 300
        && !CharacterState.Contains('\0');

    public string Prompt() =>
        string.Join('\n', Items().Select(p => p.Content.Prompt(p.Kind)))
        + (string.IsNullOrWhiteSpace(CharacterState) ? "" : "\nCharacter state: " + CharacterState);

    public static SceneContinuity? Map(
        SceneContinuity? value,
        Func<Guid, Guid?> assets,
        Func<Guid, Guid>? profiles = null
    )
    {
        if (value is null || !value.Valid())
            return value;
        ProfileSnapshot One(ProfileSnapshot p) =>
            p with
            {
                Id = profiles?.Invoke(p.Id) ?? p.Id,
                Content = p.Content with
                {
                    ReferenceId = p.Content.ReferenceId is Guid id ? assets(id) : null,
                },
            };
        return value with
        {
            Characters = value.Characters?.Select(One).ToArray(),
            Location = value.Location is null ? null : One(value.Location),
        };
    }
}

public record ProfileWrite(string Kind, ProfileContent Content, int Version = 0);

public record ResolveProfiles(
    Guid[] CharacterIds,
    Guid? LocationId = null,
    string CharacterState = ""
);

public record ProfilePackage(string Format, int SchemaVersion, ProfileWrite[] Profiles);

public static class CreativeProfiles
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task Upgrade(StudioDb db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "CreativeProfiles" ("Id" TEXT NOT NULL PRIMARY KEY,"ProjectId" TEXT NOT NULL,"Kind" TEXT NOT NULL,"ContentJson" TEXT NOT NULL,"Version" INTEGER NOT NULL,"UpdatedAt" TEXT NOT NULL,FOREIGN KEY("ProjectId") REFERENCES "Projects"("Id") ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS "IX_CreativeProfiles_ProjectId_Kind" ON "CreativeProfiles"("ProjectId","Kind");
            """
        );

    public static ProfileSnapshot Snapshot(CreativeProfile p) =>
        new(
            p.Id,
            p.Version,
            p.Kind,
            JsonSerializer.Deserialize<ProfileContent>(p.ContentJson, Web)!
        );

    public static object View(CreativeProfile p) =>
        new
        {
            p.Id,
            p.ProjectId,
            p.Kind,
            p.Version,
            p.UpdatedAt,
            content = Snapshot(p).Content,
        };

    public static async Task<bool> ReferenceValid(
        Guid project,
        ProfileContent content,
        StudioDb db,
        CancellationToken ct
    ) =>
        content.ReferenceId is not Guid id
        || await db.Assets.AnyAsync(
            a => a.Id == id && a.ProjectId == project && a.Kind == "image",
            ct
        );

    public static void MapProfiles(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/profiles",
            async (Guid id, StudioDb db, CancellationToken ct) =>
                Results.Ok(
                    (
                        await db
                            .CreativeProfiles.AsNoTracking()
                            .Where(x => x.ProjectId == id)
                            .OrderBy(x => x.Kind)
                            .ThenByDescending(x => x.UpdatedAt)
                            .ToListAsync(ct)
                    ).Select(View)
                )
        );
        app.MapPost(
            "/api/projects/{id:guid}/profiles",
            async (Guid id, ProfileWrite req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    req.Kind is not ("character" or "location")
                    || req.Content is null
                    || !req.Content.Valid()
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Hồ sơ không hợp lệ; tên tối đa 120, mô tả 1.000 và từng thuộc tính 300 ký tự.",
                        }
                    );
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                if (!await ReferenceValid(id, req.Content, db, ct))
                    return Results.BadRequest(new { error = "Ảnh tham chiếu phải thuộc dự án." });
                await Gate.WaitAsync(ct);
                try
                {
                    if (await db.CreativeProfiles.CountAsync(x => x.ProjectId == id, ct) >= 50)
                        return Results.Conflict(new { error = "Tối đa 50 hồ sơ/dự án." });
                    var row = new CreativeProfile
                    {
                        ProjectId = id,
                        Kind = req.Kind,
                        ContentJson = JsonSerializer.Serialize(req.Content, Web),
                    };
                    db.CreativeProfiles.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Created($"/api/projects/{id}/profiles", View(row));
                }
                finally
                {
                    Gate.Release();
                }
            }
        );
        app.MapPut(
            "/api/projects/{id:guid}/profiles/{profileId:guid}",
            async (Guid id, Guid profileId, ProfileWrite req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    req.Content is null
                    || !req.Content.Valid()
                    || req.Version < 1
                    || req.Version == int.MaxValue
                    || !await ReferenceValid(id, req.Content, db, ct)
                )
                    return Results.BadRequest(
                        new { error = "Nội dung, ảnh hoặc phiên bản hồ sơ không hợp lệ." }
                    );
                var changed = await db
                    .CreativeProfiles.Where(p =>
                        p.Id == profileId
                        && p.ProjectId == id
                        && p.Version == req.Version
                        && p.Kind == req.Kind
                    )
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(
                                    p => p.ContentJson,
                                    JsonSerializer.Serialize(req.Content, Web)
                                )
                                .SetProperty(p => p.Version, req.Version + 1)
                                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow),
                        ct
                    );
                return changed == 0
                    ? Results.Conflict(
                        new { error = "Hồ sơ đã đổi/không còn; tải lại trước khi sửa." }
                    )
                    : Results.Ok(new { version = req.Version + 1 });
            }
        );
        app.MapDelete(
            "/api/projects/{id:guid}/profiles/{profileId:guid}",
            async (Guid id, Guid profileId, StudioDb db, CancellationToken ct) =>
                await db
                    .CreativeProfiles.Where(x => x.Id == profileId && x.ProjectId == id)
                    .ExecuteDeleteAsync(ct) > 0
                    ? Results.NoContent()
                    : Results.NotFound()
        );
        app.MapPost(
            "/api/projects/{id:guid}/profiles/resolve",
            async (Guid id, ResolveProfiles req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    req.CharacterIds is null
                    || req.CharacterIds.Length > 2
                    || req.CharacterIds.Distinct().Count() != req.CharacterIds.Length
                    || req.CharacterState is null
                    || req.CharacterState.Length > 300
                    || req.CharacterState.Contains('\0')
                )
                    return Results.BadRequest(
                        new { error = "Chọn tối đa 2 nhân vật và trạng thái tối đa 300 ký tự." }
                    );
                var ids = req
                    .CharacterIds.Concat(req.LocationId is Guid lid ? new[] { lid } : [])
                    .ToArray();
                var rows = await db
                    .CreativeProfiles.AsNoTracking()
                    .Where(p => p.ProjectId == id && ids.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, ct);
                if (
                    req.CharacterIds.Any(cid =>
                        !rows.TryGetValue(cid, out var row) || row.Kind != "character"
                    )
                    || req.LocationId is Guid l
                        && (!rows.TryGetValue(l, out var loc) || loc.Kind != "location")
                )
                    return Results.BadRequest(
                        new { error = "Hồ sơ sai loại hoặc không thuộc dự án." }
                    );
                return Results.Ok(
                    new SceneContinuity(
                        req.CharacterIds.Select(cid => Snapshot(rows[cid])).ToArray(),
                        req.LocationId is Guid location ? Snapshot(rows[location]) : null,
                        req.CharacterState
                    )
                );
            }
        );
        app.MapGet(
            "/api/projects/{id:guid}/profiles/export",
            async (Guid id, StudioDb db, CancellationToken ct) =>
                Results.Ok(
                    new ProfilePackage(
                        "flowvideo.profiles",
                        1,
                        (
                            await db
                                .CreativeProfiles.AsNoTracking()
                                .Where(p => p.ProjectId == id)
                                .ToListAsync(ct)
                        )
                            .Select(p => new ProfileWrite(
                                p.Kind,
                                Snapshot(p).Content with
                                {
                                    ReferenceId = null,
                                }
                            ))
                            .ToArray()
                    )
                )
        );
        app.MapPost(
            "/api/projects/{id:guid}/profiles/import",
            async (Guid id, ProfilePackage req, StudioDb db, CancellationToken ct) =>
            {
                if (
                    req.Format != "flowvideo.profiles"
                    || req.SchemaVersion != 1
                    || req.Profiles is null
                    || req.Profiles.Length is < 1 or > 50
                    || req.Profiles.Any(p =>
                        p is null
                        || p.Kind is not ("character" or "location")
                        || p.Content is null
                        || !p.Content.Valid()
                    )
                )
                    return Results.BadRequest(new { error = "Gói hồ sơ không hợp lệ." });
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                await Gate.WaitAsync(ct);
                try
                {
                    if (
                        await db.CreativeProfiles.CountAsync(p => p.ProjectId == id, ct)
                            + req.Profiles.Length
                        > 50
                    )
                        return Results.Conflict(new { error = "Vượt 50 hồ sơ/dự án." });
                    var rows = req
                        .Profiles.Select(p => new CreativeProfile
                        {
                            ProjectId = id,
                            Kind = p.Kind,
                            ContentJson = JsonSerializer.Serialize(
                                p.Content with
                                {
                                    ReferenceId = null,
                                },
                                Web
                            ),
                        })
                        .ToArray();
                    db.CreativeProfiles.AddRange(rows);
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(rows.Select(View));
                }
                finally
                {
                    Gate.Release();
                }
            }
        );
    }
}
