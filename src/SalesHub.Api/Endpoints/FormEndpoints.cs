using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Forms;
using SalesHub.Contracts.Forms;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class FormEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapFormEndpoints(this IEndpointRouteBuilder api)
    {
        var forms = api.MapGroup("/forms").RequireAuthorization(Policies.Employee);
        forms.MapGet("/", ListAsync);
        forms.MapGet("/{id:guid}", DetailAsync);
        forms.MapPost("/{id:guid}/submissions", SubmitAsync);

        forms.MapPost("/native", CreateNativeAsync).RequireAuthorization(Policies.Management);
        forms.MapPatch("/native/{id:guid}", UpdateNativeAsync).RequireAuthorization(Policies.Management);
        forms.MapPost("/google-link", CreateGoogleLinkAsync).RequireAuthorization(Policies.Management);
        forms.MapGet("/{id:guid}/submissions", SubmissionsAsync).RequireAuthorization(Policies.Management);
        forms.MapPost("/submissions/{id:guid}/complete", CompleteSubmissionAsync)
            .RequireAuthorization(Policies.Management);

        var email = api.MapGroup("/email-requests").RequireAuthorization(Policies.Employee);
        email.MapPost("/", CreateEmailRequestAsync);
        email.MapGet("/", ListEmailRequestsAsync).RequireAuthorization(Policies.Management);
        email.MapPost("/{id:guid}/complete", CompleteEmailRequestAsync)
            .RequireAuthorization(Policies.Management);

        return api;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var isManagement = Domain.Roles.IsManagement(
            http.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "");
        var query = isManagement
            ? db.Forms
            : db.Forms.Where(f => f.Status == FormStatus.Published);
        var forms = await query.OrderBy(f => f.DisplayName).ToListAsync(ct);
        var versionNumbers = await db.FormVersions
            .GroupBy(v => v.FormId)
            .Select(g => new { FormId = g.Key, Max = g.Max(v => v.VersionNumber) })
            .ToDictionaryAsync(g => g.FormId, g => g.Max, ct);

        return Results.Ok(forms.Select(f => new FormListItem(
            f.Id, f.Type.ToString(), f.DisplayName, f.Status.ToString(),
            f.ExternalUrl, f.TracksCompletion,
            versionNumbers.GetValueOrDefault(f.Id, 0))).ToList());
    }

    private static async Task<IResult> DetailAsync(
        Guid id, HttpContext http, IAppDb db, CancellationToken ct)
    {
        var form = await db.Forms.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (form is null || (form.Status == FormStatus.Draft
            && !Domain.Roles.IsManagement(
                http.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "")))
        {
            return Problems.NotFound(http, "Form not found.");
        }

        FormDefinition? definition = null;
        if (form.CurrentVersionId is { } versionId)
        {
            var version = await db.FormVersions.FirstAsync(v => v.Id == versionId, ct);
            definition = JsonSerializer.Deserialize<FormDefinition>(version.DefinitionJson, Json);
        }

        return Results.Ok(new FormDetailResponse(
            form.Id, form.Type.ToString(), form.DisplayName, form.Status.ToString(),
            form.ExternalUrl, form.CurrentVersionId, definition));
    }

    private static async Task<IResult> CreateNativeAsync(
        CreateNativeFormRequest request, HttpContext http,
        FormService formService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (form, error) = await formService.CreateNativeAsync(
            userId, request.DisplayName, request.Definition,
            request.TracksCompletion, request.Publish, ct);
        return form is null
            ? Problems.Validation(http, error!)
            : Results.Created($"/api/v1/forms/{form.Id}", new { form.Id });
    }

    private static async Task<IResult> UpdateNativeAsync(
        Guid id, UpdateNativeFormRequest request, HttpContext http,
        FormService formService, CancellationToken ct)
    {
        var (ok, error) = await formService.UpdateNativeAsync(
            id, request.DisplayName, request.Definition, request.Publish, ct);
        return ok ? Results.NoContent() : Problems.Validation(http, error!);
    }

    private static async Task<IResult> CreateGoogleLinkAsync(
        CreateGoogleLinkRequest request, HttpContext http,
        FormService formService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (form, error) = await formService.CreateGoogleLinkAsync(
            userId, request.DisplayName, request.ExternalUrl, ct);
        return form is null
            ? Problems.Validation(http, error!)
            : Results.Created($"/api/v1/forms/{form.Id}", new { form.Id });
    }

    private static async Task<IResult> SubmitAsync(
        Guid id, SubmitFormRequest request, HttpContext http,
        FormService formService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (submission, error) = await formService.SubmitAsync(id, userId, request.Answers, ct);
        return submission is null
            ? Problems.Validation(http, error!)
            : Results.Created($"/api/v1/forms/submissions/{submission.Id}", new { submission.Id });
    }

    private static async Task<IResult> SubmissionsAsync(
        Guid id, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var form = await db.Forms.FirstOrDefaultAsync(f => f.Id == id, ct);
        var rows = await db.FormSubmissions
            .Where(s => s.FormId == id)
            .OrderByDescending(s => s.SubmittedAtUtc)
            .Take(200)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(s => new SubmissionDto(
            s.Id, s.FormId, form?.DisplayName ?? "", s.UserId,
            users.GetValueOrDefault(s.UserId, "Unknown"),
            s.Status.ToString(), s.SubmittedAtUtc,
            JsonSerializer.Deserialize<Dictionary<string, string>>(s.AnswersJson, Json) ?? [])).ToList());
    }

    private static async Task<IResult> CompleteSubmissionAsync(
        Guid id, HttpContext http, FormService formService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await formService.CompleteSubmissionAsync(id, userId, ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "No open submission with that id.");
    }

    private static async Task<IResult> CreateEmailRequestAsync(
        CreateEmailRequestRequest request, HttpContext http,
        FormService formService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (created, error) = await formService.CreateEmailRequestAsync(
            userId, request.Cid, request.CustomerEmail, request.QuoteType,
            request.LawnArea, request.Coverage, ct);
        return created is null
            ? Problems.Validation(http, error!)
            : Results.Created($"/api/v1/email-requests/{created.Id}", new { created.Id });
    }

    private static async Task<IResult> ListEmailRequestsAsync(
        IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var rows = await db.EmailRequests.OrderBy(e => e.CreatedAtUtc).ToListAsync(ct);
        return Results.Ok(rows.Select(e => new EmailRequestDto(
            e.Id, e.SubmitterUserId,
            users.GetValueOrDefault(e.SubmitterUserId, "Unknown"),
            e.Cid, e.CustomerEmail, e.QuoteType, e.LawnArea, e.Coverage,
            e.CreatedAtUtc)).ToList());
    }

    private static async Task<IResult> CompleteEmailRequestAsync(
        Guid id, HttpContext http, FormService formService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await formService.CompleteEmailRequestAsync(id, userId, ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "No open email request with that id.");
    }
}
