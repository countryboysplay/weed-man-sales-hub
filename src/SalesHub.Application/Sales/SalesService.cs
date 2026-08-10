using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Sales;

/// <summary>
/// The Sales domain (CLAUDE.md §6). Everything here is server-authoritative:
/// timestamps, business dates, duplicate rules, the large-amount second
/// confirmation, same-day ownership, and correction audit. Realtime updates
/// ride the outbox in the same transaction — a saved sale can never miss the
/// dashboard, and the celebration payload deliberately omits the CID.
/// </summary>
public sealed class SalesService(
    IAppDb db,
    IIdentityService identity,
    IResaleConfirmationTokens resaleTokens,
    IAuditWriter audit,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── outcomes ─────────────────────────────────────────────────────────────

    public enum Failure
    {
        None = 0,
        Validation,
        LargeAmountConfirmationRequired,
        DuplicateProgram,
        DuplicateUpsell,
        NotFound,
        NotEditableToday,
        Forbidden,
    }

    public sealed record CreateOutcome(
        Failure Failure,
        string? Error,
        Sale? Sale,
        DuplicateInfo? Duplicate);

    public sealed record DuplicateInfo(
        Guid PriorSaleId,
        DateOnly PriorBusinessDate,
        decimal PriorAmount,
        string? ConfirmationToken);

    public sealed record MutationOutcome(Failure Failure, string? Error, Sale? Sale);

    public sealed record CreateInput(
        Guid SellerUserId,
        string Cid,
        SaleType SaleType,
        string Campaign,
        decimal Amount,
        string? DuplicateResaleConfirmation,
        bool LargeAmountConfirmed,
        Guid? SessionId);

    // ── create ───────────────────────────────────────────────────────────────

    public async Task<CreateOutcome> CreateAsync(CreateInput input, CancellationToken ct = default)
    {
        var (failure, error) = ValidateCore(input.Cid, input.SaleType, input.Campaign, input.Amount);
        if (failure != Failure.None)
        {
            return new CreateOutcome(failure, error, null, null);
        }

        if (SalesRules.NeedsLargeAmountConfirmation(input.Amount) && !input.LargeAmountConfirmed)
        {
            return new CreateOutcome(
                Failure.LargeAmountConfirmationRequired,
                $"Sales over {SalesRules.LargeAmountThreshold:C0} need a second confirmation.",
                null, null);
        }

        var now = businessTime.UtcNow;
        var businessDate = businessTime.BusinessDateOf(now);
        var campaign = SalesRules.NormalizeCampaign(input.Campaign);
        var yearStart = new DateOnly(businessDate.Year, 1, 1);

        // Duplicate rules: current business year, nondeleted rows only.
        Guid? confirmedPriorSaleId = null;
        if (input.SaleType == SaleType.Program)
        {
            var prior = await db.Sales
                .Where(s => s.Cid == input.Cid
                    && s.SaleType == SaleType.Program
                    && s.State == SaleState.Active
                    && s.BusinessDate >= yearStart)
                .OrderByDescending(s => s.BusinessDate)
                .FirstOrDefaultAsync(ct);
            if (prior is not null)
            {
                if (input.DuplicateResaleConfirmation is { } token
                    && resaleTokens.ValidatePriorSaleId(token, input.SellerUserId, input.Cid) == prior.Id)
                {
                    confirmedPriorSaleId = prior.Id;
                }
                else
                {
                    // 409 with the prior sale's date/amount + a fresh token.
                    return new CreateOutcome(Failure.DuplicateProgram,
                        "A program sale already exists for this CID this year.",
                        null,
                        new DuplicateInfo(prior.Id, prior.BusinessDate, prior.Amount,
                            resaleTokens.Issue(input.SellerUserId, input.Cid, prior.Id)));
                }
            }
        }
        else
        {
            var prior = await db.Sales
                .Where(s => s.Cid == input.Cid
                    && s.SaleType == SaleType.Upsell
                    && s.Campaign == campaign
                    && s.State == SaleState.Active
                    && s.BusinessDate >= yearStart)
                .OrderByDescending(s => s.BusinessDate)
                .FirstOrDefaultAsync(ct);
            if (prior is not null)
            {
                // Upsell CID+campaign duplicates have no override path.
                return new CreateOutcome(Failure.DuplicateUpsell,
                    "An upsell with this campaign already exists for this CID this year.",
                    null,
                    new DuplicateInfo(prior.Id, prior.BusinessDate, prior.Amount, null));
            }
        }

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            SellerUserId = input.SellerUserId,
            Cid = input.Cid,
            SaleType = input.SaleType,
            Campaign = campaign,
            Amount = input.Amount,
            BusinessDate = businessDate,
            CreatedAtUtc = now,
        };

        var seller = await identity.FindByIdAsync(input.SellerUserId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Sales.Add(sale);
            if (confirmedPriorSaleId is { } priorId)
            {
                db.SaleDuplicateOverrides.Add(new SaleDuplicateOverride
                {
                    Id = Guid.CreateVersion7(),
                    SaleId = sale.Id,
                    PriorSaleId = priorId,
                    ConfirmedByUserId = input.SellerUserId,
                    ConfirmedAtUtc = now,
                });
            }

            await outbox.EnqueueAsync(EventTypes.SaleCreated, CelebrationPayload(sale, seller), token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new CreateOutcome(Failure.None, null, sale, null);
    }

    // ── same-day seller edit/delete ──────────────────────────────────────────

    public sealed record EditInput(
        SaleType? SaleType,
        string? Campaign,
        decimal? Amount,
        bool LargeAmountConfirmed);

    public async Task<MutationOutcome> EditSameDayAsync(
        Guid saleId, Guid actorUserId, EditInput input, CancellationToken ct = default)
    {
        var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null || sale.State == SaleState.Deleted)
        {
            return new MutationOutcome(Failure.NotFound, "Sale not found.", null);
        }

        if (sale.SellerUserId != actorUserId)
        {
            return new MutationOutcome(Failure.Forbidden, "You can only edit your own sales.", null);
        }

        if (sale.BusinessDate != businessTime.Today)
        {
            return new MutationOutcome(Failure.NotEditableToday,
                "Sales can only be edited on their business day. Ask management for a correction.",
                null);
        }

        var newType = input.SaleType ?? sale.SaleType;
        var newCampaign = SalesRules.NormalizeCampaign(input.Campaign ?? sale.Campaign);
        var newAmount = input.Amount ?? sale.Amount;

        var (failure, error) = ValidateCore(sale.Cid, newType, newCampaign, newAmount);
        if (failure != Failure.None)
        {
            return new MutationOutcome(failure, error, null);
        }

        if (newAmount != sale.Amount
            && SalesRules.NeedsLargeAmountConfirmation(newAmount)
            && !input.LargeAmountConfirmed)
        {
            return new MutationOutcome(Failure.LargeAmountConfirmationRequired,
                $"Sales over {SalesRules.LargeAmountThreshold:C0} need a second confirmation.", null);
        }

        // A type/campaign change re-enters duplicate territory.
        if ((newType, newCampaign) != (sale.SaleType, sale.Campaign))
        {
            var yearStart = new DateOnly(sale.BusinessDate.Year, 1, 1);
            var clash = newType == SaleType.Program
                ? await db.Sales.AnyAsync(s => s.Id != sale.Id
                    && s.Cid == sale.Cid && s.SaleType == SaleType.Program
                    && s.State == SaleState.Active && s.BusinessDate >= yearStart, ct)
                : await db.Sales.AnyAsync(s => s.Id != sale.Id
                    && s.Cid == sale.Cid && s.SaleType == SaleType.Upsell
                    && s.Campaign == newCampaign
                    && s.State == SaleState.Active && s.BusinessDate >= yearStart, ct);
            if (clash)
            {
                return new MutationOutcome(
                    newType == SaleType.Program ? Failure.DuplicateProgram : Failure.DuplicateUpsell,
                    "That change collides with an existing sale for this CID this year.", null);
            }
        }

        var seller = await identity.FindByIdAsync(sale.SellerUserId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            sale.SaleType = newType;
            sale.Campaign = newCampaign;
            sale.Amount = newAmount;
            sale.UpdatedAtUtc = businessTime.UtcNow;
            await outbox.EnqueueAsync(EventTypes.SaleUpdated, CelebrationPayload(sale, seller), token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new MutationOutcome(Failure.None, null, sale);
    }

    /// <summary>
    /// Same-day seller delete: no reason, no undo, immediately out of totals,
    /// tombstone stays visible in the current-day view until business
    /// midnight, does not block a replacement duplicate.
    /// </summary>
    public async Task<MutationOutcome> DeleteSameDayAsync(
        Guid saleId, Guid actorUserId, CancellationToken ct = default)
    {
        var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null || sale.State == SaleState.Deleted)
        {
            return new MutationOutcome(Failure.NotFound, "Sale not found.", null);
        }

        if (sale.SellerUserId != actorUserId)
        {
            return new MutationOutcome(Failure.Forbidden, "You can only delete your own sales.", null);
        }

        if (sale.BusinessDate != businessTime.Today)
        {
            return new MutationOutcome(Failure.NotEditableToday,
                "Sales can only be deleted on their business day. Ask management for a correction.",
                null);
        }

        var seller = await identity.FindByIdAsync(sale.SellerUserId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            sale.State = SaleState.Deleted;
            sale.DeletedAtUtc = businessTime.UtcNow;
            sale.DeletedByUserId = actorUserId;
            await outbox.EnqueueAsync(EventTypes.SaleDeleted, CelebrationPayload(sale, seller), token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new MutationOutcome(Failure.None, null, sale);
    }

    // ── management historical correction / delete ────────────────────────────

    public sealed record CorrectionInput(
        SaleType? SaleType,
        string? Campaign,
        decimal? Amount,
        string Reason,
        Guid ActorUserId,
        Guid? SessionId);

    public async Task<MutationOutcome> CorrectHistoricalAsync(
        Guid saleId, CorrectionInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Reason))
        {
            return new MutationOutcome(Failure.Validation, "A correction needs a reason.", null);
        }

        var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null || sale.State == SaleState.Deleted)
        {
            return new MutationOutcome(Failure.NotFound, "Sale not found.", null);
        }

        var newType = input.SaleType ?? sale.SaleType;
        var newCampaign = SalesRules.NormalizeCampaign(input.Campaign ?? sale.Campaign);
        var newAmount = input.Amount ?? sale.Amount;
        var (failure, error) = ValidateCore(sale.Cid, newType, newCampaign, newAmount);
        if (failure != Failure.None)
        {
            return new MutationOutcome(failure, error, null);
        }

        var before = Snapshot(sale);
        var seller = await identity.FindByIdAsync(sale.SellerUserId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            sale.SaleType = newType;
            sale.Campaign = newCampaign;
            sale.Amount = newAmount;
            sale.UpdatedAtUtc = businessTime.UtcNow;
            var after = Snapshot(sale);

            db.SaleCorrections.Add(new SaleCorrection
            {
                Id = Guid.CreateVersion7(),
                SaleId = sale.Id,
                CorrectionType = SaleCorrectionType.Amend,
                BeforeJson = JsonSerializer.Serialize(before, Json),
                AfterJson = JsonSerializer.Serialize(after, Json),
                Reason = input.Reason.Trim(),
                ActorUserId = input.ActorUserId,
                OccurredAtUtc = businessTime.UtcNow,
            });
            await audit.WriteAsync(new AuditEntry(
                "sales", "sales.historicalCorrection", AuditRetentionClass.SevenYears)
            {
                ActorUserId = input.ActorUserId,
                SessionId = input.SessionId,
                TargetType = "Sale",
                TargetId = sale.Id.ToString(),
                Reason = input.Reason.Trim(),
                Before = before,
                After = after,
            }, token);
            await outbox.EnqueueAsync(EventTypes.SaleCorrected, CelebrationPayload(sale, seller), token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new MutationOutcome(Failure.None, null, sale);
    }

    public async Task<MutationOutcome> DeleteHistoricalAsync(
        Guid saleId, string reason, Guid actorUserId, Guid? sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new MutationOutcome(Failure.Validation, "A historical deletion needs a reason.", null);
        }

        var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null || sale.State == SaleState.Deleted)
        {
            return new MutationOutcome(Failure.NotFound, "Sale not found.", null);
        }

        var before = Snapshot(sale);
        var seller = await identity.FindByIdAsync(sale.SellerUserId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            sale.State = SaleState.Deleted;
            sale.DeletedAtUtc = businessTime.UtcNow;
            sale.DeletedByUserId = actorUserId;

            db.SaleCorrections.Add(new SaleCorrection
            {
                Id = Guid.CreateVersion7(),
                SaleId = sale.Id,
                CorrectionType = SaleCorrectionType.Delete,
                BeforeJson = JsonSerializer.Serialize(before, Json),
                AfterJson = JsonSerializer.Serialize(Snapshot(sale), Json),
                Reason = reason.Trim(),
                ActorUserId = actorUserId,
                OccurredAtUtc = businessTime.UtcNow,
            });
            await audit.WriteAsync(new AuditEntry(
                "sales", "sales.historicalDelete", AuditRetentionClass.SevenYears)
            {
                ActorUserId = actorUserId,
                SessionId = sessionId,
                TargetType = "Sale",
                TargetId = sale.Id.ToString(),
                Reason = reason.Trim(),
                Before = before,
            }, token);
            await outbox.EnqueueAsync(EventTypes.SaleDeleted, CelebrationPayload(sale, seller), token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new MutationOutcome(Failure.None, null, sale);
    }

    // ── queries and aggregates ───────────────────────────────────────────────

    /// <summary>The seller's current-day view: active rows plus today's
    /// tombstones (visible until business midnight); totals exclude deleted.</summary>
    public async Task<(IReadOnlyList<Sale> Sales, int Count, decimal Net)> MyTodayAsync(
        Guid userId, CancellationToken ct = default)
    {
        var today = businessTime.Today;
        var sales = await db.Sales
            .Where(s => s.SellerUserId == userId && s.BusinessDate == today)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);
        var active = sales.Where(s => s.State == SaleState.Active).ToList();
        return (sales, active.Count, active.Sum(s => s.Amount));
    }

    public sealed record TeamRow(Guid UserId, string DisplayName, string Role, int Count, decimal Net);

    /// <summary>
    /// Team Today (CLAUDE.md §6): every active Sales Agent appears even at
    /// zero; management appears only with a sale; net descending, then count.
    /// </summary>
    public async Task<IReadOnlyList<TeamRow>> TeamTodayAsync(CancellationToken ct = default)
    {
        var today = businessTime.Today;
        var totals = await db.Sales
            .Where(s => s.BusinessDate == today && s.State == SaleState.Active)
            .GroupBy(s => s.SellerUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), Net = g.Sum(s => s.Amount) })
            .ToDictionaryAsync(g => g.UserId, ct);

        var everyone = await identity.ListUsersAsync(new UserQuery(), ct);
        var rows = new List<TeamRow>();
        foreach (var user in everyone)
        {
            var has = totals.TryGetValue(user.Id, out var t);
            if (user.Role == Roles.SalesAgent || has)
            {
                rows.Add(new TeamRow(user.Id, user.DisplayName, user.Role,
                    has ? t!.Count : 0, has ? t!.Net : 0m));
            }
        }

        return rows
            .OrderByDescending(r => r.Net)
            .ThenByDescending(r => r.Count)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public sealed record CategoryRow(SaleType SaleType, string Campaign, int Count, decimal Net);

    /// <summary>Drilldown for a Team Today row: category count/net only —
    /// deliberately no CID and no times (CLAUDE.md §6).</summary>
    public async Task<IReadOnlyList<CategoryRow>> TodayBreakdownAsync(
        Guid userId, CancellationToken ct = default)
    {
        var today = businessTime.Today;
        return await CategoriesAsync(
            db.Sales.Where(s => s.SellerUserId == userId
                && s.BusinessDate == today && s.State == SaleState.Active), ct);
    }

    public sealed record Summary(
        int Year,
        IReadOnlyList<(int Month, int Count, decimal Net, IReadOnlyList<CategoryRow> Categories)> Months,
        int YtdCount,
        decimal YtdNet,
        IReadOnlyList<CategoryRow> YtdCategories);

    public async Task<Summary> SummaryAsync(Guid userId, CancellationToken ct = default)
    {
        var today = businessTime.Today;
        var yearStart = new DateOnly(today.Year, 1, 1);
        var rows = await db.Sales
            .Where(s => s.SellerUserId == userId
                && s.State == SaleState.Active
                && s.BusinessDate >= yearStart)
            .Select(s => new { s.BusinessDate, s.SaleType, s.Campaign, s.Amount })
            .ToListAsync(ct);

        var months = rows
            .GroupBy(r => r.BusinessDate.Month)
            .OrderBy(g => g.Key)
            .Select(g => (
                Month: g.Key,
                Count: g.Count(),
                Net: g.Sum(r => r.Amount),
                Categories: (IReadOnlyList<CategoryRow>)g
                    .GroupBy(r => (r.SaleType, r.Campaign))
                    .Select(c => new CategoryRow(c.Key.SaleType, c.Key.Campaign,
                        c.Count(), c.Sum(r => r.Amount)))
                    .OrderBy(c => c.Campaign)
                    .ToList()))
            .ToList();

        var ytdCategories = rows
            .GroupBy(r => (r.SaleType, r.Campaign))
            .Select(c => new CategoryRow(c.Key.SaleType, c.Key.Campaign,
                c.Count(), c.Sum(r => r.Amount)))
            .OrderBy(c => c.Campaign)
            .ToList();

        return new Summary(
            today.Year, months, rows.Count, rows.Sum(r => r.Amount), ytdCategories);
    }

    /// <summary>Own current-year final corrected rows as CSV (CLAUDE.md §6).</summary>
    public async Task<string> ExportCurrentYearCsvAsync(Guid userId, CancellationToken ct = default)
    {
        var yearStart = new DateOnly(businessTime.Today.Year, 1, 1);
        var rows = await db.Sales
            .Where(s => s.SellerUserId == userId
                && s.State == SaleState.Active
                && s.BusinessDate >= yearStart)
            .OrderBy(s => s.BusinessDate).ThenBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);

        var lines = new List<string>(rows.Count + 1)
        {
            "BusinessDate,CID,SaleType,Campaign,Amount",
        };
        lines.AddRange(rows.Select(s =>
            $"{s.BusinessDate:yyyy-MM-dd},{s.Cid},{s.SaleType},{s.Campaign},{s.Amount:0.00}"));
        return string.Join("\n", lines) + "\n";
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (Failure, string?) ValidateCore(
        string cid, SaleType saleType, string campaign, decimal amount)
    {
        if (!SalesRules.IsValidCid(cid))
        {
            return (Failure.Validation, "CID must contain numbers only.");
        }

        if (!SalesRules.IsValidCampaign(saleType, campaign))
        {
            return (Failure.Validation,
                saleType == SaleType.Program
                    ? "Program sales use campaign AS01."
                    : "Upsell sales use campaign GC01, AE01 or OS01.");
        }

        if (!SalesRules.IsValidAmount(amount))
        {
            return (Failure.Validation,
                "The amount must be a positive dollar value with at most two decimals.");
        }

        return (Failure.None, null);
    }

    private static async Task<IReadOnlyList<CategoryRow>> CategoriesAsync(
        IQueryable<Sale> sales, CancellationToken ct)
    {
        var rows = await sales
            .GroupBy(s => new { s.SaleType, s.Campaign })
            .Select(g => new CategoryRow(
                g.Key.SaleType, g.Key.Campaign, g.Count(), g.Sum(s => s.Amount)))
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Campaign).ToList();
    }

    /// <summary>Realtime payload. Never includes the CID — the celebration
    /// broadcast reaches every user (sales-celebrations mockup: CID not shown).</summary>
    private static object CelebrationPayload(Sale sale, AppUserInfo? seller) => new
    {
        saleId = sale.Id,
        sellerUserId = sale.SellerUserId,
        sellerDisplayName = seller?.DisplayName ?? "",
        saleType = sale.SaleType.ToString(),
        campaign = sale.Campaign,
        amount = sale.Amount,
        businessDate = sale.BusinessDate.ToString("yyyy-MM-dd"),
        state = sale.State.ToString(),
    };

    private static object Snapshot(Sale sale) => new
    {
        saleType = sale.SaleType.ToString(),
        campaign = sale.Campaign,
        amount = sale.Amount,
        state = sale.State.ToString(),
        cid = sale.Cid,
        businessDate = sale.BusinessDate.ToString("yyyy-MM-dd"),
    };
}
