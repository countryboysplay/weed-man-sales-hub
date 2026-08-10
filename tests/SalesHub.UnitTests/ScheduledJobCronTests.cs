using SalesHub.Domain.Entities;
using SalesHub.Workers;
using Xunit;

namespace SalesHub.UnitTests;

/// <summary>
/// The 12:30 AM America/Chicago backup schedule (CLAUDE.md §20) must stay
/// 12:30 AM local across DST — meaning its UTC instant moves.
/// </summary>
public class ScheduledJobCronTests
{
    private static ScheduledJob BackupJob() => new()
    {
        CronExpression = "30 0 * * *",
        TimeZoneId = "America/Chicago",
    };

    [Fact]
    public void Backup_fires_at_0630_utc_in_winter()
    {
        var next = ScheduledJobRunner.ComputeNextRun(
            BackupJob(), new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 6, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Backup_fires_at_0530_utc_in_summer()
    {
        var next = ScheduledJobRunner.ComputeNextRun(
            BackupJob(), new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 5, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Fall_back_night_runs_the_backup_exactly_once()
    {
        // Nov 1 2026: 00:30 CDT (05:30 UTC) occurs before the repeat of the
        // 1 AM hour; the next run after it must be Nov 2, not a second Nov 1.
        var first = ScheduledJobRunner.ComputeNextRun(
            BackupJob(), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), first);

        var second = ScheduledJobRunner.ComputeNextRun(BackupJob(), first)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), second);
    }

    [Fact]
    public void Result_is_always_utc()
    {
        var next = ScheduledJobRunner.ComputeNextRun(
            BackupJob(), DateTimeOffset.UtcNow)!.Value;
        Assert.Equal(TimeSpan.Zero, next.Offset);
    }
}
