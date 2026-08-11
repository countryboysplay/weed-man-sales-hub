using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Recognitions;

/// <summary>
/// Recognitions (CLAUDE.md §13): management issues them, everyone reacts and
/// comments, the feed keeps them active for 30 days and then archives.
/// The recipient and the team are notified (DND suppression joins in Wave 4).
/// </summary>
public sealed class RecognitionService(
    IAppDb db,
    IIdentityService identity,
    NotificationService notifications,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    public static readonly TimeSpan ActiveLifetime = TimeSpan.FromDays(30);

    public static readonly (string Name, string Emoji)[] BuiltInBadges =
    [
        ("Top Performer", "🏆"),
        ("Customer Hero", "⭐"),
        ("Momentum", "🚀"),
        ("Great Idea", "💡"),
        ("On Fire", "🔥"),
    ];

    public async Task<(Recognition? Value, string? Error)> PublishAsync(
        Guid authorUserId, Guid recipientUserId, Guid badgeId, string category,
        string message, CancellationToken ct = default)
    {
        var recipient = await identity.FindByIdAsync(recipientUserId, ct);
        if (recipient is null || !recipient.IsActive)
        {
            return (null, "That employee is not available.");
        }

        var badge = await db.RecognitionBadges
            .FirstOrDefaultAsync(b => b.Id == badgeId && b.Active, ct);
        if (badge is null)
        {
            return (null, "Unknown badge.");
        }

        var now = businessTime.UtcNow;
        var recognition = new Recognition
        {
            Id = Guid.CreateVersion7(),
            RecipientUserId = recipientUserId,
            AuthorUserId = authorUserId,
            BadgeId = badgeId,
            Category = category?.Trim() ?? "",
            Message = message?.Trim() ?? "",
            CreatedAtUtc = now,
            ActiveUntilUtc = now + ActiveLifetime,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Recognitions.Add(recognition);
            _ = await notifications.CreateAsync(recipientUserId,
                new NotificationService.NewNotification(
                    "recognitions",
                    $"{badge.Emoji} You were recognized: {badge.Name}",
                    recognition.Message.Length is > 0 and <= 80
                        ? recognition.Message
                        : "Open the recognition feed to see it.",
                    ReferenceType: "Recognition",
                    ReferenceId: recognition.Id.ToString()), token);
            await outbox.EnqueueAsync(EventTypes.RecognitionPublished, new
            {
                recognitionId = recognition.Id,
                recipientUserId,
                recipientDisplayName = recipient.DisplayName,
                badgeName = badge.Name,
                badgeEmoji = badge.Emoji,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return (recognition, null);
    }

    public async Task<int> ArchiveExpiredAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        return await db.Recognitions
            .Where(r => r.ArchivedAtUtc == null && r.ActiveUntilUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ArchivedAtUtc, now), ct);
    }

    public async Task EnsureBuiltInBadgesAsync(CancellationToken ct = default)
    {
        var existing = await db.RecognitionBadges.Select(b => b.Name).ToListAsync(ct);
        foreach (var (name, emoji) in BuiltInBadges.Where(b => !existing.Contains(b.Name)))
        {
            db.RecognitionBadges.Add(new RecognitionBadge
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                Emoji = emoji,
                BuiltIn = true,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
