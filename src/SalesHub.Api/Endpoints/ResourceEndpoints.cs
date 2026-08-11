using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Resources (CLAUDE.md §11): employees read only; agents can never
/// download; PDF viewing streams a copy watermarked with the viewer's
/// identity and date; manager downloads are audited (365 days, Owner-
/// visible) and PDFs carry the manager watermark while office files pass
/// unchanged. No direct file paths ever leave the server.
/// </summary>
public static class ResourceEndpoints
{
    private const long MaxUploadBytes = 100 * 1024 * 1024;
    private static readonly HashSet<string> FileContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "image/webp",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder api)
    {
        var folders = api.MapGroup("/resource-folders").RequireAuthorization(Policies.Employee);
        folders.MapGet("/", ListFoldersAsync);
        folders.MapPost("/", CreateFolderAsync).RequireAuthorization(Policies.Management);

        var resources = api.MapGroup("/resources").RequireAuthorization(Policies.Employee);
        resources.MapGet("/", ListAsync);
        resources.MapGet("/search", SearchAsync);
        resources.MapGet("/{id:guid}/view", ViewAsync);
        resources.MapGet("/{id:guid}/download", DownloadAsync).RequireAuthorization(Policies.Management);
        resources.MapPost("/{id:guid}/favorite", (Guid id, HttpContext http, IAppDb db, BusinessTime bt, CancellationToken ct) =>
            FavoriteAsync(id, http, db, bt, true, ct));
        resources.MapDelete("/{id:guid}/favorite", (Guid id, HttpContext http, IAppDb db, BusinessTime bt, CancellationToken ct) =>
            FavoriteAsync(id, http, db, bt, false, ct));

        resources.MapPost("/upload", UploadAsync)
            .RequireAuthorization(Policies.Management).DisableAntiforgery();
        resources.MapPost("/link", CreateLinkAsync).RequireAuthorization(Policies.Management);
        resources.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(Policies.Management);

        api.MapGet("/resource-download-audit", DownloadAuditAsync)
            .RequireAuthorization(Policies.OwnerOnly);

        return api;
    }

    private static async Task<IResult> ListFoldersAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.ResourceFolders
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new { f.Id, f.ParentId, f.Name, f.SortOrder })
            .ToListAsync(ct));

    private static async Task<IResult> CreateFolderAsync(
        FolderRequest request, HttpContext http, IAppDb db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Problems.Validation(http, "A folder needs a name.");
        }

        var folder = new ResourceFolder
        {
            Id = Guid.CreateVersion7(),
            ParentId = request.ParentId,
            Name = request.Name.Trim(),
            SortOrder = request.SortOrder ?? 0,
        };
        db.ResourceFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/resource-folders/{folder.Id}", new { folder.Id });
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, IAppDb db, Guid? folderId, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var favorites = await db.ResourceFavorites
            .Where(f => f.UserId == userId)
            .Select(f => f.ResourceId)
            .ToListAsync(ct);
        var rows = await db.Resources
            .Where(r => folderId == null || r.FolderId == folderId)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Title)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(r => ToDto(r, favorites.Contains(r.Id))).ToList());
    }

    private static async Task<IResult> SearchAsync(
        HttpContext http, IAppDb db, string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Problems.Validation(http, "A search needs a query.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var favorites = await db.ResourceFavorites
            .Where(f => f.UserId == userId).Select(f => f.ResourceId).ToListAsync(ct);
        var pattern = $"%{q.Trim()}%";
        var rows = await db.Resources
            .Where(r => EF.Functions.ILike(r.Title, pattern)
                || EF.Functions.ILike(r.Description, pattern))
            .OrderBy(r => r.Title)
            .Take(50)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(r => ToDto(r, favorites.Contains(r.Id))).ToList());
    }

    /// <summary>Inline viewer for everyone: PDFs stream as a copy watermarked
    /// with the viewer's name and date; images stream inline; office files
    /// have no employee viewer (and agents may not download them).</summary>
    private static async Task<IResult> ViewAsync(
        Guid id, HttpContext http, IAppDb db, IFileBlobStore blobs,
        IPdfWatermarker watermarker, IIdentityService identity,
        BusinessTime businessTime, CancellationToken ct)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (resource?.BlobId is not { } blobId)
        {
            return Problems.NotFound(http, "Resource not found or not a file.");
        }

        var blob = await db.FileBlobs.FirstAsync(b => b.Id == blobId, ct);
        var (userId, _) = AuthEndpoints.Current(http);

        if (blob.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Stream(await blobs.OpenReadAsync(blobId, ct), blob.ContentType);
        }

        if (!string.Equals(blob.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Problems.Forbidden(http,
                "This file type has no inline viewer. Managers may download it.",
                "noInlineViewer");
        }

        var viewer = await identity.FindByIdAsync(userId, ct);
        var stamp = $"{viewer?.DisplayName} — {businessTime.Now:MMM d, yyyy h:mm tt} Central";
        await using var original = await blobs.OpenReadAsync(blobId, ct);
        var watermarked = await watermarker.WatermarkAsync(original, stamp, ct);
        // Inline only — the viewer route never sets a download disposition.
        return Results.Stream(watermarked, "application/pdf");
    }

    /// <summary>Management-only download, always audited. PDFs carry the
    /// manager+date watermark; office files pass through unchanged.</summary>
    private static async Task<IResult> DownloadAsync(
        Guid id, HttpContext http, IAppDb db, IFileBlobStore blobs,
        IPdfWatermarker watermarker, IIdentityService identity,
        BusinessTime businessTime, CancellationToken ct)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (resource?.BlobId is not { } blobId)
        {
            return Problems.NotFound(http, "Resource not found or not a file.");
        }

        var blob = await db.FileBlobs.FirstAsync(b => b.Id == blobId, ct);
        var (userId, _) = AuthEndpoints.Current(http);
        var isPdf = string.Equals(blob.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

        db.ResourceDownloadAudits.Add(new ResourceDownloadAudit
        {
            Id = Guid.CreateVersion7(),
            ResourceId = resource.Id,
            UserId = userId,
            ResourceTitle = resource.Title,
            Watermarked = isPdf,
            OccurredAtUtc = businessTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        if (isPdf)
        {
            var manager = await identity.FindByIdAsync(userId, ct);
            var stamp = $"{manager?.DisplayName} — {businessTime.Now:MMM d, yyyy} — Confidential";
            await using var original = await blobs.OpenReadAsync(blobId, ct);
            var watermarked = await watermarker.WatermarkAsync(original, stamp, ct);
            return Results.Stream(watermarked, "application/pdf", blob.OriginalName);
        }

        return Results.Stream(await blobs.OpenReadAsync(blobId, ct),
            blob.ContentType, blob.OriginalName);
    }

    private static async Task<IResult> FavoriteAsync(
        Guid id, HttpContext http, IAppDb db, BusinessTime businessTime,
        bool favorite, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var exists = await db.Resources.AnyAsync(r => r.Id == id, ct);
        if (!exists)
        {
            return Problems.NotFound(http, "Resource not found.");
        }

        var row = await db.ResourceFavorites.FirstOrDefaultAsync(
            f => f.UserId == userId && f.ResourceId == id, ct);
        if (favorite && row is null)
        {
            db.ResourceFavorites.Add(new ResourceFavorite
            {
                UserId = userId,
                ResourceId = id,
                CreatedAtUtc = businessTime.UtcNow,
            });
        }
        else if (!favorite && row is not null)
        {
            db.ResourceFavorites.Remove(row);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http, IAppDb db, IFileBlobStore blobs,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
        {
            return Problems.Validation(http, "Send the resource as multipart form data.");
        }

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Problems.Validation(http, "A 'file' upload is required.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return Problems.Validation(http, "Resources are limited to 100 MB.");
        }

        if (!FileContentTypes.Contains(file.ContentType))
        {
            return Problems.Validation(http,
                "Resources accept PDF, XLSX, DOCX, PPTX and images (CLAUDE.md §11).");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        Guid? folderId = Guid.TryParse(form["folderId"], out var parsed) ? parsed : null;
        var title = form["title"].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            title = Path.GetFileNameWithoutExtension(file.FileName);
        }

        // "Replace" repoints an existing resource at a NEW blob (docs/07);
        // the original blob stays immutable.
        Guid? replaceId = Guid.TryParse(form["replaceResourceId"], out var replaceParsed)
            ? replaceParsed : null;

        await using var stream = file.OpenReadStream();
        var blob = await blobs.SaveAsync(stream, file.FileName, file.ContentType, userId, ct);

        if (replaceId is { } existingId)
        {
            var existing = await db.Resources.FirstOrDefaultAsync(r => r.Id == existingId, ct);
            if (existing is null)
            {
                return Problems.NotFound(http, "Resource to replace was not found.");
            }

            existing.BlobId = blob.Id;
            existing.UpdatedAtUtc = businessTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { existing.Id });
        }

        var resource = new Resource
        {
            Id = Guid.CreateVersion7(),
            FolderId = folderId,
            Type = ResourceType.File,
            Title = title!,
            Description = form["description"].FirstOrDefault()?.Trim() ?? "",
            BlobId = blob.Id,
            SensitiveStagingPlaceholder = form["sensitive"] == "true",
            CreatedByUserId = userId,
            CreatedAtUtc = businessTime.UtcNow,
        };
        db.Resources.Add(resource);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/resources/{resource.Id}", new { resource.Id });
    }

    private static async Task<IResult> CreateLinkAsync(
        LinkRequest request, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title)
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Problems.Validation(http, "A link resource needs a title and an https URL.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var resource = new Resource
        {
            Id = Guid.CreateVersion7(),
            FolderId = request.FolderId,
            Type = request.Video ? ResourceType.Video : ResourceType.Link,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? "",
            ExternalUrl = request.Url,
            CreatedByUserId = userId,
            CreatedAtUtc = businessTime.UtcNow,
        };
        db.Resources.Add(resource);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/resources/{resource.Id}", new { resource.Id });
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, HttpContext http, IAppDb db, CancellationToken ct)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (resource is null)
        {
            return Problems.NotFound(http, "Resource not found.");
        }

        db.Resources.Remove(resource);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadAuditAsync(
        IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var rows = await db.ResourceDownloadAudits
            .OrderByDescending(a => a.OccurredAtUtc)
            .Take(500)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(a => new
        {
            a.Id,
            a.ResourceId,
            a.ResourceTitle,
            a.UserId,
            UserDisplayName = users.GetValueOrDefault(a.UserId, "Unknown"),
            a.Watermarked,
            a.OccurredAtUtc,
        }).ToList());
    }

    private static object ToDto(Resource resource, bool favorite) => new
    {
        resource.Id,
        resource.FolderId,
        Type = resource.Type.ToString(),
        resource.Title,
        resource.Description,
        HasFile = resource.BlobId is not null,
        resource.ExternalUrl,
        resource.SortOrder,
        resource.SensitiveStagingPlaceholder,
        Favorite = favorite,
    };

    private sealed record FolderRequest(string Name, Guid? ParentId, int? SortOrder);

    private sealed record LinkRequest(
        string Title, string Url, Guid? FolderId,
        string? Description = null, bool Video = false);
}
