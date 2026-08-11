using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Forms;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Forms;

/// <summary>
/// Forms (CLAUDE.md §10): management-authored native forms with versioned
/// published snapshots (edits take effect immediately; answers re-map by
/// field id, incompatible ones cleared), Google Form links, and the
/// dedicated Email Request workflow whose completed requests disappear.
/// </summary>
public sealed class FormService(
    IAppDb db,
    NotificationService notifications,
    BusinessTime businessTime)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] FieldTypes = ["SingleLine", "Number", "Dropdown", "YesNo"];

    // ── authoring ────────────────────────────────────────────────────────────

    public async Task<(Form? Form, string? Error)> CreateNativeAsync(
        Guid actorId, string displayName, FormDefinition definition,
        bool tracksCompletion, bool publish, CancellationToken ct = default)
    {
        var definitionError = ValidateDefinition(definition);
        if (definitionError is not null)
        {
            return (null, definitionError);
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, "A form needs a display name.");
        }

        var now = businessTime.UtcNow;
        var form = new Form
        {
            Id = Guid.CreateVersion7(),
            Type = FormType.Native,
            DisplayName = displayName.Trim(),
            CreatedByUserId = actorId,
            CreatedAtUtc = now,
            TracksCompletion = tracksCompletion,
            Status = publish ? FormStatus.Published : FormStatus.Draft,
        };
        var version = new FormVersion
        {
            Id = Guid.CreateVersion7(),
            FormId = form.Id,
            VersionNumber = 1,
            DefinitionJson = JsonSerializer.Serialize(definition, Json),
            CreatedAtUtc = now,
        };
        form.CurrentVersionId = version.Id;

        db.Forms.Add(form);
        db.FormVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return (form, null);
    }

    public async Task<(bool Ok, string? Error)> UpdateNativeAsync(
        Guid formId, string? displayName, FormDefinition? definition, bool? publish,
        CancellationToken ct = default)
    {
        var form = await db.Forms.FirstOrDefaultAsync(
            f => f.Id == formId && f.Type == FormType.Native, ct);
        if (form is null)
        {
            return (false, "Form not found.");
        }

        if (definition is not null)
        {
            var definitionError = ValidateDefinition(definition);
            if (definitionError is not null)
            {
                return (false, definitionError);
            }

            // Published edits take effect immediately as a fresh version;
            // older submissions keep pointing at the snapshot they answered.
            var lastNumber = await db.FormVersions
                .Where(v => v.FormId == formId)
                .MaxAsync(v => v.VersionNumber, ct);
            var version = new FormVersion
            {
                Id = Guid.CreateVersion7(),
                FormId = formId,
                VersionNumber = lastNumber + 1,
                DefinitionJson = JsonSerializer.Serialize(definition, Json),
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.FormVersions.Add(version);
            form.CurrentVersionId = version.Id;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            form.DisplayName = displayName.Trim();
        }

        if (publish is { } p)
        {
            form.Status = p ? FormStatus.Published : FormStatus.Draft;
        }

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(Form? Form, string? Error)> CreateGoogleLinkAsync(
        Guid actorId, string displayName, string externalUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, "A form needs a display name.");
        }

        if (!Uri.TryCreate(externalUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "A Google Form link must be an https URL.");
        }

        var form = new Form
        {
            Id = Guid.CreateVersion7(),
            Type = FormType.GoogleLink,
            DisplayName = displayName.Trim(),
            ExternalUrl = externalUrl,
            CreatedByUserId = actorId,
            CreatedAtUtc = businessTime.UtcNow,
            Status = FormStatus.Published,
        };
        db.Forms.Add(form);
        await db.SaveChangesAsync(ct);
        return (form, null);
    }

    // ── submissions ──────────────────────────────────────────────────────────

    public async Task<(FormSubmission? Submission, string? Error)> SubmitAsync(
        Guid formId, Guid userId, IReadOnlyDictionary<string, string> answers,
        CancellationToken ct = default)
    {
        var form = await db.Forms.FirstOrDefaultAsync(
            f => f.Id == formId && f.Type == FormType.Native
                && f.Status == FormStatus.Published, ct);
        if (form?.CurrentVersionId is not { } versionId)
        {
            return (null, "Form not found or not published.");
        }

        var version = await db.FormVersions.FirstAsync(v => v.Id == versionId, ct);
        var definition = JsonSerializer.Deserialize<FormDefinition>(version.DefinitionJson, Json)!;

        var (kept, error) = ValidateAnswers(definition, answers);
        if (error is not null)
        {
            return (null, error);
        }

        var submission = new FormSubmission
        {
            Id = Guid.CreateVersion7(),
            FormId = formId,
            FormVersionId = versionId,
            UserId = userId,
            AnswersJson = JsonSerializer.Serialize(kept, Json),
            SubmittedAtUtc = businessTime.UtcNow,
            Status = form.TracksCompletion ? FormSubmissionStatus.Open : FormSubmissionStatus.Submitted,
        };
        db.FormSubmissions.Add(submission);
        await db.SaveChangesAsync(ct);
        return (submission, null);
    }

    /// <summary>Tracked workflow forms only: management marks complete, the
    /// submitter is notified, and the entry leaves the open queue.</summary>
    public async Task<bool> CompleteSubmissionAsync(
        Guid submissionId, Guid actorId, CancellationToken ct = default)
    {
        var submission = await db.FormSubmissions.FirstOrDefaultAsync(
            s => s.Id == submissionId && s.Status == FormSubmissionStatus.Open, ct);
        if (submission is null)
        {
            return false;
        }

        var form = await db.Forms.FirstAsync(f => f.Id == submission.FormId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            submission.Status = FormSubmissionStatus.Completed;
            submission.CompletedByUserId = actorId;
            submission.CompletedAtUtc = businessTime.UtcNow;
            _ = await notifications.CreateAsync(submission.UserId,
                new NotificationService.NewNotification(
                    "forms",
                    $"Completed: {form.DisplayName}",
                    "Your request was handled by management.",
                    ReferenceType: "Form",
                    ReferenceId: form.Id.ToString()), token);
            await db.SaveChangesAsync(token);
        }, ct);
        return true;
    }

    // ── email requests (dedicated workflow) ──────────────────────────────────

    public async Task<(EmailRequest? Value, string? Error)> CreateEmailRequestAsync(
        Guid submitterId, string cid, string customerEmail, string quoteType,
        string lawnArea, string coverage, CancellationToken ct = default)
    {
        if (!SalesRules.IsValidCid(cid))
        {
            return (null, "CID must contain numbers only.");
        }

        if (string.IsNullOrWhiteSpace(customerEmail) || !customerEmail.Contains('@'))
        {
            return (null, "A valid customer email is required.");
        }

        var request = new EmailRequest
        {
            Id = Guid.CreateVersion7(),
            SubmitterUserId = submitterId,
            Cid = cid,
            CustomerEmail = customerEmail.Trim(),
            QuoteType = quoteType?.Trim() ?? "",
            LawnArea = lawnArea?.Trim() ?? "",
            Coverage = coverage?.Trim() ?? "",
            CreatedAtUtc = businessTime.UtcNow,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.EmailRequests.Add(request);
            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "forms",
                "New email quote request",
                $"CID {cid} — {request.QuoteType}.",
                ReferenceType: "EmailRequest",
                ReferenceId: request.Id.ToString()), submitterId, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return (request, null);
    }

    /// <summary>Complete = notify submitter, then the request disappears —
    /// deliberately not archived (CLAUDE.md §10).</summary>
    public async Task<bool> CompleteEmailRequestAsync(
        Guid requestId, Guid actorId, CancellationToken ct = default)
    {
        var request = await db.EmailRequests.FirstOrDefaultAsync(e => e.Id == requestId, ct);
        if (request is null)
        {
            return false;
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            _ = await notifications.CreateAsync(request.SubmitterUserId,
                new NotificationService.NewNotification(
                    "forms",
                    "Email request completed",
                    $"The quote email for CID {request.Cid} was sent.",
                    ReferenceType: "EmailRequest",
                    ReferenceId: request.Id.ToString()), token);
            db.EmailRequests.Remove(request);
            await db.SaveChangesAsync(token);
        }, ct);
        _ = actorId;
        return true;
    }

    // ── validation ───────────────────────────────────────────────────────────

    internal static string? ValidateDefinition(FormDefinition definition)
    {
        if (definition.Sections is not { Count: > 0 })
        {
            return "A form needs at least one section.";
        }

        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in definition.Sections)
        {
            foreach (var field in section.Fields ?? [])
            {
                if (string.IsNullOrWhiteSpace(field.Id) || !fieldIds.Add(field.Id))
                {
                    return "Every field needs a unique id.";
                }

                if (!FieldTypes.Contains(field.Type, StringComparer.Ordinal))
                {
                    return $"Unknown field type '{field.Type}'.";
                }

                if (field.Type == "Dropdown" && field.Options is not { Count: > 0 })
                {
                    return $"Dropdown '{field.Label}' needs options.";
                }
            }
        }

        foreach (var condition in definition.Sections
            .SelectMany(s => s.Fields ?? [])
            .Select(f => f.VisibleWhen)
            .Where(c => c is not null))
        {
            if (!fieldIds.Contains(condition!.FieldId))
            {
                return "A branching condition references an unknown field.";
            }
        }

        return null;
    }

    /// <summary>Keeps matching answers, clears hidden/unknown ones, enforces
    /// required + typed values on visible fields (CLAUDE.md §10).</summary>
    internal static (Dictionary<string, string> Kept, string? Error) ValidateAnswers(
        FormDefinition definition, IReadOnlyDictionary<string, string> answers)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in definition.Sections.SelectMany(s => s.Fields ?? []))
        {
            var visible = field.VisibleWhen is null
                || (answers.TryGetValue(field.VisibleWhen.FieldId, out var gate)
                    && string.Equals(gate, field.VisibleWhen.Value, StringComparison.OrdinalIgnoreCase));

            if (!visible)
            {
                continue; // hidden answers are cleared, never stored
            }

            answers.TryGetValue(field.Id, out var value);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                {
                    return ([], $"'{field.Label}' is required.");
                }

                continue;
            }

            switch (field.Type)
            {
                case "Number" when !decimal.TryParse(value, out _):
                    return ([], $"'{field.Label}' must be a number.");
                case "Dropdown" when !(field.Options ?? []).Contains(value, StringComparer.Ordinal):
                    return ([], $"'{field.Label}' must be one of the listed options.");
                case "YesNo" when value.ToLowerInvariant() is not ("yes" or "no" or "true" or "false"):
                    return ([], $"'{field.Label}' must be yes or no.");
            }

            kept[field.Id] = value.Trim();
        }

        return (kept, null);
    }
}
