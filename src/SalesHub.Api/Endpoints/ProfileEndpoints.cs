using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Users;
using SalesHub.Contracts.Users;
using SalesHub.Domain;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Self-service profile (profile mockup: core fields manager-maintained,
/// phone/birthday/photo user-editable) and self password change.
/// </summary>
public static class ProfileEndpoints
{
    private const long MaxPhotoBytes = 5 * 1024 * 1024;
    private static readonly HashSet<string> PhotoContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder api)
    {
        var profile = api.MapGroup("/profile").RequireAuthorization(Policies.Employee);
        profile.MapGet("/", GetAsync);
        profile.MapPatch("/", UpdateAsync);
        profile.MapPost("/change-password", ChangePasswordAsync).RequireRateLimiting("auth");
        profile.MapPost("/photo", UploadPhotoAsync).DisableAntiforgery();
        return api;
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, IIdentityService identity, IAppDb db, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var user = await identity.GetUserDetailsAsync(userId, ct);
        if (user is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        string? branchName = null;
        if (user.BranchId is { } branchId)
        {
            branchName = await db.Branches
                .Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync(ct);
        }

        return Results.Ok(new ProfileResponse(
            user.Id, user.Username, user.DisplayName, user.Role, branchName,
            user.Email, user.Phone, user.HireDate, user.Birthday,
            user.ProfilePhotoBlobId is not null));
    }

    private static async Task<IResult> UpdateAsync(
        UpdateProfileRequest request, HttpContext http,
        IIdentityService identity, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        await identity.UpdateProfileAsync(
            userId, new ProfileUpdate(request.Phone, request.Birthday, null), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, HttpContext http,
        IIdentityService identity, IAppDb db, IAuditWriter audit, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        bool changed;
        try
        {
            changed = await identity.ChangePasswordAsync(
                userId, request.CurrentPassword, request.NewPassword, ct);
        }
        catch (IdentityOperationException ex)
        {
            return Problems.Validation(http, ex.Message);
        }

        if (!changed)
        {
            return Problems.Auth(http, "invalidCredentials", "The current password is incorrect.");
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            await audit.WriteAsync(new AuditEntry(
                "auth", "auth.passwordChanged", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = userId,
                SessionId = session.Id,
                TargetType = "User",
                TargetId = userId.ToString(),
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadPhotoAsync(
        HttpContext http, IIdentityService identity, IFileBlobStore blobs, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        if (!http.Request.HasFormContentType)
        {
            return Problems.Validation(http, "Send the photo as multipart form data.");
        }

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("photo");
        if (file is null || file.Length == 0)
        {
            return Problems.Validation(http, "A 'photo' file is required.");
        }

        if (file.Length > MaxPhotoBytes)
        {
            return Problems.Validation(http, "Photos are limited to 5 MB.");
        }

        // Do not trust the browser MIME wholesale (docs/07) — the allowlist
        // plus server-side storage naming keeps this safe; content sniffing
        // hardens further when the resources module lands.
        if (!PhotoContentTypes.Contains(file.ContentType))
        {
            return Problems.Validation(http, "Photos must be JPEG, PNG or WebP.");
        }

        await using var stream = file.OpenReadStream();
        var blob = await blobs.SaveAsync(stream, file.FileName, file.ContentType, userId, ct);
        await identity.UpdateProfileAsync(userId, new ProfileUpdate(null, null, blob.Id), ct);
        return Results.NoContent();
    }
}
