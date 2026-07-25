namespace SpecRunner.Domain;

/// <summary>
/// Immutable input to stage derivation (research R9). Reading the worktree filesystem happens
/// in an adapter; the derivation itself is a pure function over this snapshot, so Tier 1 can
/// drive every case from fixtures with no I/O. Predicates are evaluated against the item's OWN
/// worktree, never the shared clone (constitution §3, FR-013) — passing the wrong snapshot is
/// the bug this type exists to make visible.
/// </summary>
/// <param name="KindResolved">Intake done: kind and targets established from the issue (FR-016).</param>
/// <param name="SpecExists">Target spec.md present on the item's branch.</param>
/// <param name="UnresolvedMarkerCount">Outstanding clarification markers in the target spec.</param>
/// <param name="PlanExists">plan.md present.</param>
/// <param name="TasksExists">tasks.md present.</param>
/// <param name="AnalysisRecorded">analyze output recorded.</param>
/// <param name="PullRequestOpen">PR opened for this item — the entry condition for review, not the finish line.</param>
/// <param name="ReviewRecorded">review ran and recorded its outcome (FR-034f) — the exit predicate for review.</param>
public readonly record struct WorktreeSnapshot(
    bool KindResolved,
    bool SpecExists,
    int UnresolvedMarkerCount,
    bool PlanExists,
    bool TasksExists,
    bool AnalysisRecorded,
    bool PullRequestOpen,
    bool ReviewRecorded);
