namespace SpecRunner.Domain;

/// <summary>
/// The always-block list (constitution §3, FR-031/033d): actions that MUST block for a human
/// regardless of the run's decide-and-report posture, because a wrong choice can't be undone by
/// a later item. A named, auditable list is preferred over in-the-moment judgement — and with
/// auto-merge enabled it is the only human checkpoint. Pure and testable.
/// </summary>
public static class Reversibility
{
    private static readonly string[] AlwaysBlockSignals =
    [
        "drop table", "drop column", "truncate", "delete from", // destructive data migrations
        "force-push", "push --force", "push -f",                 // history rewrite
        "rm -rf",                                                // destructive fs
    ];

    /// <summary>
    /// True if the described action must block: it matches the always-block list, touches a
    /// configured protected path, or its estimated spend exceeds the cap (FR-033d).
    /// </summary>
    public static bool MustBlock(
        string description,
        IReadOnlyList<string> protectedPaths,
        int estimatedSpend,
        int spendCap)
    {
        if (estimatedSpend > spendCap)
        {
            return true;
        }

        var lower = description.ToLowerInvariant();
        if (AlwaysBlockSignals.Any(s => lower.Contains(s, StringComparison.Ordinal)))
        {
            return true;
        }

        return protectedPaths.Any(p => description.Contains(p, StringComparison.Ordinal));
    }
}
