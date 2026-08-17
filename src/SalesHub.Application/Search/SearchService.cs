using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Search;

/// <summary>
/// Global search (docs/02): every group is filtered by the caller's
/// permissions BEFORE matching — an agent can never see another agent's
/// sales, tickets, or any management record through search. Results carry
/// whyMatched. Searches that touch management records are audited
/// server-side; the client cannot opt out.
/// </summary>
public sealed class SearchService(
    IAppDb db,
    IIdentityService identity,
    IAuditWriter audit)
{
    private const int GroupLimit = 5;

    public sealed record SearchHit(
        string Kind,
        string Reference,     // public id or guid, whatever the client navigates with
        string Title,
        string Snippet,
        string WhyMatched);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid userId, string role, string query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length < 2)
        {
            return [];
        }

        var management = Roles.Management.Contains(role, StringComparer.Ordinal);
        var lowered = q.ToLowerInvariant();
        var hits = new List<SearchHit>();

        // Exact public-record-id jump (SUP-2026-00001 etc.).
        if (PublicRecordId.IsWellFormed(q.ToUpperInvariant()))
        {
            hits.AddRange(await PublicIdLookupAsync(q.ToUpperInvariant(), userId, management, ct));
        }

        // People — the directory is visible to everyone.
        var people = await identity.ListUsersAsync(new UserQuery(), ct);
        hits.AddRange(people
            .Where(u => u.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(GroupLimit)
            .Select(u => new SearchHit(
                "Person", u.Id.ToString(), u.DisplayName, u.Role,
                $"name contains '{q}'")));

        // Announcements (employees see published ones; targeting is enforced
        // at the detail endpoint — search returns title matches only).
        hits.AddRange(await db.Announcements
            .Where(a => a.PublishedAtUtc != null && a.Title.ToLower().Contains(lowered))
            .OrderByDescending(a => a.PublishedAtUtc)
            .Take(GroupLimit)
            .Select(a => new SearchHit(
                "Announcement", a.Id.ToString(), a.Title, "",
                $"title contains '{q}'"))
            .ToListAsync(ct));

        // Resources.
        hits.AddRange(await db.Resources
            .Where(r => r.Title.ToLower().Contains(lowered)
                || r.Description.ToLower().Contains(lowered))
            .OrderBy(r => r.Title)
            .Take(GroupLimit)
            .Select(r => new SearchHit(
                "Resource", r.Id.ToString(), r.Title, r.Description,
                $"title or description contains '{q}'"))
            .ToListAsync(ct));

        // Sales by CID: own for agents, everyone's for management.
        if (q.All(char.IsDigit))
        {
            var sales = db.Sales.Where(s => s.State == SaleState.Active && s.Cid.StartsWith(q));
            if (!management)
            {
                sales = sales.Where(s => s.SellerUserId == userId);
            }

            hits.AddRange(await sales
                .OrderByDescending(s => s.CreatedAtUtc)
                .Take(GroupLimit)
                .Select(s => new SearchHit(
                    "Sale", s.Id.ToString(), $"CID {s.Cid}",
                    $"{s.Campaign} {s.Amount:F2} on {s.BusinessDate}",
                    $"CID starts with '{q}'"))
                .ToListAsync(ct));
        }

        // Support: own tickets, or the whole queue for management.
        var tickets = db.SupportTickets.Where(t =>
            t.Description.ToLower().Contains(lowered) || t.PublicId.ToLower().Contains(lowered));
        if (!management)
        {
            tickets = tickets.Where(t => t.ReporterUserId == userId);
        }

        hits.AddRange(await tickets
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(GroupLimit)
            .Select(t => new SearchHit(
                "Support", t.PublicId, $"{t.PublicId} ({t.IssueType})", t.Status.ToString(),
                $"description contains '{q}'"))
            .ToListAsync(ct));

        // Management records: management only, and the search itself is audited.
        if (management)
        {
            var notes = await db.ManagementNotes
                .Where(n => n.Body.ToLower().Contains(lowered)
                    || n.Category.ToLower().Contains(lowered))
                .OrderByDescending(n => n.CreatedAtUtc)
                .Take(GroupLimit)
                .Select(n => new SearchHit(
                    "ManagementNote", n.PublicId, $"{n.PublicId} ({n.Category})",
                    n.Status.ToString(), $"note contains '{q}'"))
                .ToListAsync(ct);
            if (notes.Count > 0)
            {
                await audit.WriteAsync(new AuditEntry(
                    "search", "search.managementRecordsSearched",
                    AuditRetentionClass.Operational365Days)
                {
                    ActorUserId = userId,
                    After = new { queryLength = q.Length, results = notes.Count },
                }, ct);
                await db.SaveChangesAsync(ct);
            }

            hits.AddRange(notes);
        }

        return hits;
    }

    private async Task<List<SearchHit>> PublicIdLookupAsync(
        string publicId, Guid userId, bool management, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        var prefix = publicId.Split('-')[0];
        switch (prefix)
        {
            case "SUP":
                var ticket = await db.SupportTickets
                    .FirstOrDefaultAsync(t => t.PublicId == publicId
                        && (management || t.ReporterUserId == userId), ct);
                if (ticket is not null)
                {
                    hits.Add(new SearchHit("Support", ticket.PublicId,
                        $"{ticket.PublicId} ({ticket.IssueType})",
                        ticket.Status.ToString(), "exact record ID"));
                }

                break;
            case "TO":
                var timeOff = await db.TimeOffRequests
                    .FirstOrDefaultAsync(t => t.PublicId == publicId
                        && (management || t.UserId == userId), ct);
                if (timeOff is not null)
                {
                    hits.Add(new SearchHit("TimeOff", timeOff.PublicId, timeOff.PublicId,
                        timeOff.Status.ToString(), "exact record ID"));
                }

                break;
            case "NOTE" when management:
                var note = await db.ManagementNotes
                    .FirstOrDefaultAsync(n => n.PublicId == publicId, ct);
                if (note is not null)
                {
                    hits.Add(new SearchHit("ManagementNote", note.PublicId,
                        $"{note.PublicId} ({note.Category})", note.Status.ToString(),
                        "exact record ID"));
                }

                break;
            case "PRS" when management:
                var flag = await db.PresenceFlags
                    .FirstOrDefaultAsync(f => f.PublicId == publicId, ct);
                if (flag is not null)
                {
                    hits.Add(new SearchHit("PresenceFlag", flag.PublicId,
                        $"{flag.PublicId} ({flag.Category})", flag.Status.ToString(),
                        "exact record ID"));
                }

                break;
            case "TECH":
                var tech = await db.TechnicalReports
                    .FirstOrDefaultAsync(t => t.PublicId == publicId
                        && (management || t.ReporterUserId == userId), ct);
                if (tech is not null)
                {
                    hits.Add(new SearchHit("TechnicalReport", tech.PublicId,
                        $"{tech.PublicId} ({tech.IssueType})", "", "exact record ID"));
                }

                break;
            default:
                break;
        }

        return hits;
    }
}
