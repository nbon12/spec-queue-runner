namespace SpecRunner.Domain;

/// <summary>
/// The backstop against a queue-wide stall.
///
/// Work selection takes the lowest-numbered ready item and runs one item per tick, so an item that
/// can never finish its stage is not merely stuck itself — it holds every higher-numbered item
/// behind it, silently and for as long as the runner runs. That is the failure mode that turns one
/// stage-level defect into an outage.
///
/// So a stage that reports no progress is counted. Attempts are recorded as comment markers on the
/// item, which keeps the runner stateless across ticks and makes the stall visible where an
/// operator is already looking. After <see cref="MaxAttempts"/> the item is parked — moved out of
/// the ready set — and the queue moves on. Parking is deliberately not a close: the work is not
/// abandoned, it is handed back.
/// </summary>
public static class StallGuard
{
    /// <summary>
    /// Attempts at one stage before the item is parked. Three is enough to ride out a transient
    /// failure (a rate limit, a flaky network) at a five-minute tick, and short enough that a
    /// genuinely stuck item costs a quarter hour rather than a night.
    /// </summary>
    public const int MaxAttempts = 3;

    private const string Prefix = "kind=no-progress id=no-progress-";

    public static string Marker(int issueNumber, Stage stage, int attempt) =>
        $"<!-- spec-runner:v1 {Prefix}{issueNumber}-{StageProgress.Name(stage)} attempt={attempt} -->";

    /// <summary>
    /// How many no-progress attempts this item has already recorded at this stage. Counting per
    /// stage rather than per item means an item that stalls at review does not arrive there with a
    /// budget already spent at implement.
    /// </summary>
    public static int AttemptsAt(IEnumerable<string> commentBodies, int issueNumber, Stage stage)
    {
        var token = $"{Prefix}{issueNumber}-{StageProgress.Name(stage)} ";
        return commentBodies.Count(b => b.Contains(token, StringComparison.Ordinal));
    }
}
