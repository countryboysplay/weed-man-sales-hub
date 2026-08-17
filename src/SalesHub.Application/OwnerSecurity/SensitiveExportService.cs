using System.Text;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.OwnerSecurity;

/// <summary>
/// Sensitive employee-history exports (CLAUDE.md §13, docs/04): Manager/Owner
/// with reason + fresh auth (enforced at the endpoint), EXP record, PDF
/// watermarked server-side with the requester and time, 7-year audit, and a
/// child audit row for every re-download.
/// </summary>
public sealed class SensitiveExportService(
    IAppDb db,
    IIdentityService identity,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    IFileBlobStore blobs,
    IPdfComposer pdfComposer,
    IPdfWatermarker watermarker,
    BusinessTime businessTime)
{
    public sealed record ExportResult(SensitiveExport? Export, string? Error)
    {
        public static ExportResult Fail(string error) => new(null, error);
    }

    public async Task<ExportResult> ExportEmployeeHistoryAsync(
        Guid actorUserId, string actorDisplayName, Guid targetUserId,
        string format, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ExportResult.Fail("A sensitive export requires a reason.");
        }

        var normalizedFormat = format?.Trim().ToLowerInvariant() switch
        {
            "pdf" => "Pdf",
            "csv" => "Csv",
            _ => null,
        };
        if (normalizedFormat is null)
        {
            return ExportResult.Fail("Format must be Pdf or Csv.");
        }

        var target = await identity.GetUserDetailsAsync(targetUserId, ct);
        if (target is null)
        {
            return ExportResult.Fail("Employee not found.");
        }

        var sections = await BuildHistoryAsync(target, ct);

        SensitiveExport export = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            var now = businessTime.UtcNow;
            Stream artifact;
            string contentType;
            string extension;
            if (normalizedFormat == "Pdf")
            {
                var composed = await pdfComposer.ComposeAsync(
                    $"Employee history — {target.DisplayName}", sections, token);
                // Confidential watermark with manager identity + time (§13).
                artifact = await watermarker.WatermarkAsync(composed,
                    $"CONFIDENTIAL — {actorDisplayName} — {businessTime.ToLocal(now):yyyy-MM-dd h:mm tt}",
                    token);
                contentType = "application/pdf";
                extension = "pdf";
            }
            else
            {
                var csv = new StringBuilder("Section,Reference,Detail\r\n");
                foreach (var (heading, lines) in sections)
                {
                    foreach (var line in lines)
                    {
                        csv.Append(Csv(heading)).Append(',');
                        var split = line.Split(" — ", 2);
                        csv.Append(Csv(split[0])).Append(',')
                            .Append(Csv(split.Length > 1 ? split[1] : ""))
                            .Append("\r\n");
                    }
                }

                artifact = new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString()));
                contentType = "text/csv";
                extension = "csv";
            }

            var blob = await blobs.SaveAsync(artifact,
                $"employee-history-{target.Id:N}.{extension}", contentType, actorUserId, token);

            export = new SensitiveExport
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("EXP", token),
                RequestedByUserId = actorUserId,
                Kind = "EmployeeHistory",
                TargetUserId = targetUserId,
                Format = normalizedFormat,
                Reason = reason.Trim(),
                BlobId = blob.Id,
                CreatedAtUtc = now,
            };
            db.SensitiveExports.Add(export);

            // 7-year export audit (CLAUDE.md §13).
            await audit.WriteAsync(new AuditEntry(
                "exports", "exports.employeeHistory", AuditRetentionClass.SevenYears)
            {
                ActorUserId = actorUserId,
                TargetType = "User",
                TargetId = targetUserId.ToString(),
                PublicRecordId = export.PublicId,
                Reason = export.Reason,
                After = new { format = normalizedFormat },
            }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new ExportResult(export, null);
    }

    /// <summary>Every download after creation writes a child access audit.</summary>
    public async Task<(Stream? Content, SensitiveExport? Export)> DownloadAsync(
        Guid exportId, Guid actorUserId, CancellationToken ct = default)
    {
        var export = await db.SensitiveExports.FirstOrDefaultAsync(e => e.Id == exportId, ct);
        if (export is null)
        {
            return (null, null);
        }

        db.SensitiveExportAccesses.Add(new SensitiveExportAccess
        {
            Id = Guid.CreateVersion7(),
            ExportId = export.Id,
            AccessedByUserId = actorUserId,
            AccessedAtUtc = businessTime.UtcNow,
        });
        await audit.WriteAsync(new AuditEntry(
            "exports", "exports.accessed", AuditRetentionClass.SevenYears)
        {
            ActorUserId = actorUserId,
            TargetType = "SensitiveExport",
            TargetId = export.Id.ToString(),
            PublicRecordId = export.PublicId,
        }, ct);
        await db.SaveChangesAsync(ct);

        return (await blobs.OpenReadAsync(export.BlobId, ct), export);
    }

    private async Task<List<(string Heading, IReadOnlyList<string> Lines)>> BuildHistoryAsync(
        UserDetails target, CancellationToken ct)
    {
        var sections = new List<(string, IReadOnlyList<string>)>
        {
            ("Profile", new List<string>
            {
                $"{target.DisplayName} — role {target.Role}, "
                    + $"{(target.IsActive ? "active" : "inactive")}, hired {target.HireDate?.ToString("O") ?? "n/a"}",
            }),
        };

        sections.Add(("Management notes", await db.ManagementNotes
            .Where(n => n.EmployeeUserId == target.Id)
            .OrderByDescending(n => n.CreatedAtUtc).Take(200)
            .Select(n => n.PublicId + " — " + n.Category + " " + n.Priority + " "
                + n.Status + " " + n.CreatedAtUtc.ToString("yyyy-MM-dd"))
            .ToListAsync(ct)));
        sections.Add(("Presence flags", await db.PresenceFlags
            .Where(f => f.UserId == target.Id)
            .OrderByDescending(f => f.StartAtUtc).Take(200)
            .Select(f => f.PublicId + " — " + f.Category + " " + f.Severity + " "
                + f.Status + " " + f.BusinessDate.ToString())
            .ToListAsync(ct)));
        sections.Add(("Time off", await db.TimeOffRequests
            .Where(t => t.UserId == target.Id)
            .OrderByDescending(t => t.CreatedAtUtc).Take(200)
            .Select(t => t.PublicId + " — " + t.Status + " " + t.StartDate.ToString()
                + " to " + t.EndDate.ToString())
            .ToListAsync(ct)));
        sections.Add(("Break corrections", await db.BreakCorrectionRequests
            .Where(b => b.RequestedByUserId == target.Id)
            .OrderByDescending(b => b.CreatedAtUtc).Take(200)
            .Select(b => b.PublicId + " — " + b.Status + " "
                + b.CreatedAtUtc.ToString("yyyy-MM-dd"))
            .ToListAsync(ct)));
        sections.Add(("Technical reports", await db.TechnicalReports
            .Where(t => t.ReporterUserId == target.Id)
            .OrderByDescending(t => t.CreatedAtUtc).Take(200)
            .Select(t => t.PublicId + " — " + t.IssueType + " "
                + t.CreatedAtUtc.ToString("yyyy-MM-dd"))
            .ToListAsync(ct)));
        sections.Add(("Support tickets", await db.SupportTickets
            .Where(s => s.ReporterUserId == target.Id)
            .OrderByDescending(s => s.CreatedAtUtc).Take(200)
            .Select(s => s.PublicId + " — " + s.IssueType + " " + s.Status + " "
                + s.CreatedAtUtc.ToString("yyyy-MM-dd"))
            .ToListAsync(ct)));

        var sales = await db.Sales
            .Where(s => s.SellerUserId == target.Id && s.State == SaleState.Active)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(s => s.Amount) })
            .FirstOrDefaultAsync(ct);
        sections.Add(("Sales summary", new List<string>
        {
            $"Totals — {sales?.Count ?? 0} active sales, {sales?.Total ?? 0m:F2} total",
        }));

        return sections;
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
