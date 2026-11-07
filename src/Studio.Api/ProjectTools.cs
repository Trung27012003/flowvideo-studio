using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Studio;

public record StoryboardPackage(
    string Format,
    int SchemaVersion,
    string Name,
    StoryDocument Document
);

public static class ProjectTools
{
    public static void MapProjectTools(this WebApplication app)
    {
        app.MapGet(
            "/api/projects/{id:guid}/storyboard/export",
            async (Guid id, StudioDb db, CancellationToken ct) =>
            {
                var project = await db
                    .Projects.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == id, ct);
                if (project is null)
                    return Results.NotFound();
                var doc = await db
                    .ProjectDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == id, ct);
                if (doc is null)
                    return Results.BadRequest(new { error = "Lưu storyboard trước khi xuất." });
                var package = new StoryboardPackage(
                    "flowvideo.storyboard",
                    1,
                    project.Name,
                    JsonSerializer.Deserialize<StoryDocument>(doc.ContentJson)!
                );
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    package,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }
                );
                return Results.File(bytes, "application/json", $"storyboard-{id}.json");
            }
        );
        app.MapPost(
            "/api/storyboards/import",
            async (HttpRequest request, StudioDb db, CancellationToken ct) =>
            {
                if (request.ContentLength > 2097152)
                    return Results.Problem("File JSON tối đa 2 MiB.", statusCode: 413);
                var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is not null && !limit.IsReadOnly)
                    limit.MaxRequestBodySize = 2097152;
                if (!request.HasJsonContentType())
                    return Results.BadRequest(new { error = "Cần file JSON storyboard." });
                StoryboardPackage? package;
                try
                {
                    package = await request.ReadFromJsonAsync<StoryboardPackage>(
                        cancellationToken: ct
                    );
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "File JSON không hợp lệ." });
                }
                if (
                    package is null
                    || package.Format != "flowvideo.storyboard"
                    || package.SchemaVersion != 1
                    || package.Document is null
                    || string.IsNullOrWhiteSpace(package.Name)
                    || package.Name.Length > 120
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Không đúng định dạng storyboard hoặc phiên bản chưa hỗ trợ.",
                        }
                    );
                // A portable document never binds to another project's media by raw IDs.
                var clean = package.Document with
                {
                    Scenes = package
                        .Document.Scenes?.Select(s =>
                            s is null ? null! : s with { AssetId = null, Start = 0 }
                        )
                        .ToArray(),
                    VoiceId = null,
                    MusicId = null,
                };
                var error = await Storyboard.Validate(Guid.Empty, clean, db, false, ct);
                if (error is not null)
                    return Results.BadRequest(new { error });
                var detached =
                    package.Document.Scenes!.Count(s => s.AssetId.HasValue)
                    + (package.Document.VoiceId.HasValue ? 1 : 0)
                    + (package.Document.MusicId.HasValue ? 1 : 0);
                clean = clean with
                {
                    Scenes = clean.Scenes!.Select(s => s with { Id = Guid.NewGuid() }).ToArray(),
                };
                var project = new Project
                {
                    Name =
                        package.Name.Trim()[..Math.Min(package.Name.Trim().Length, 100)]
                        + " · nhập",
                };
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.Projects.Add(project);
                db.ProjectDocuments.Add(
                    new()
                    {
                        ProjectId = project.Id,
                        Version = 1,
                        ContentJson = JsonSerializer.Serialize(clean),
                        UpdatedAt = DateTime.UtcNow,
                    }
                );
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created(
                    $"/api/projects/{project.Id}",
                    new
                    {
                        project,
                        version = 1,
                        detachedMediaCount = detached,
                    }
                );
            }
        );
        app.MapGet(
            "/api/jobs/{id:guid}/subtitles",
            async (Guid id, StudioDb db, Storage store, CancellationToken ct) =>
            {
                var ready = await db.Jobs.AnyAsync(x => x.Id == id && x.Status == "Succeeded", ct);
                if (!ready || !File.Exists(store.Subtitles(id)))
                    return Results.NotFound();
                return Results.File(
                    store.Subtitles(id),
                    "application/x-subrip",
                    $"flowvideo-{id}.srt"
                );
            }
        );
    }
}
