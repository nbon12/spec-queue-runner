namespace SpecRunner.Domain;

/// <summary>
/// Stale reclaim (FR-044): an item left in-progress longer than the threshold is reset to ready
/// at the next tick — a run that died without reverting shouldn't wedge the item. Live and held
/// are exempt: both wait indefinitely by design. Pure over timestamps so it is unit-testable.
/// </summary>
public static class StaleReclaim
{
    public static bool ShouldReclaim(
        QueueStatus status, DateTimeOffset inProgressSince, DateTimeOffset now, int staleHours)
    {
        if (status is not QueueStatus.InProgress)
        {
            return false; // only in-progress reclaims; live/held/waiting/ready are left alone
        }

        return now - inProgressSince > TimeSpan.FromHours(staleHours);
    }
}
