namespace SpecRunner.Domain;

/// <summary>
/// One issue that another is blocked by — GitHub's native dependency relationship, projected to
/// what held-gating needs. Only blockers that are still open are ever represented here.
/// </summary>
public sealed record BlockingIssue(int Number, string Title);

/// <summary>
/// Held-gating (FR-010): an item is not schedulable while any issue it is blocked by is still
/// open. The dependency is a structured GitHub relationship, not prose — it is expressed in the
/// issue sidebar and cannot be triggered by pasted text. Pure over already-fetched blockers; the
/// GraphQL query that fetches them belongs to the adapter.
/// </summary>
public static class Readiness
{
    /// <summary>No open blockers ⇒ schedulable. An item with no dependencies is unblocked.</summary>
    public static bool IsSchedulable(IReadOnlyList<BlockingIssue> openBlockers) => openBlockers.Count == 0;

    /// <summary>The blocking issues by number, for the held log line: <c>#12, #13</c>.</summary>
    public static string Describe(IReadOnlyList<BlockingIssue> openBlockers) =>
        string.Join(", ", openBlockers.Select(b => $"#{b.Number}"));
}
