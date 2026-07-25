namespace SpecRunner.Domain;

/// <summary>
/// The stage is derived, not stored: it is the first stage in the kind's sequence whose exit
/// predicate is unsatisfied (FR-013). The <c>stage/*</c> label is a cache over this; the
/// worktree snapshot is the authority and the crash checkpoint. Pure — no I/O.
/// </summary>
public static class StageDerivation
{
    /// <summary>
    /// Returns the current stage for an item of <paramref name="kind"/> given
    /// <paramref name="snapshot"/>, or <c>null</c> when every stage's predicate is satisfied
    /// (the item is complete for its kind).
    /// </summary>
    public static Stage? Derive(Kind kind, WorktreeSnapshot snapshot)
    {
        foreach (var stage in StageSequence.For(kind))
        {
            if (!ExitPredicateMet(stage, snapshot))
            {
                return stage;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a stage's exit condition holds. Each mirrors the stage table (spec §Stages):
    /// the observable filesystem/issue fact that lets any tick, with no memory, know the stage
    /// is done.
    /// </summary>
    private static bool ExitPredicateMet(Stage stage, WorktreeSnapshot s) => stage switch
    {
        Stage.Intake => s.KindResolved,
        Stage.Specify => s.SpecExists,
        Stage.Clarify => s.SpecExists && s.UnresolvedMarkerCount == 0,
        Stage.Plan => s.PlanExists,
        Stage.Tasks => s.TasksExists,
        Stage.Analyze => s.AnalysisRecorded,
        Stage.Implement => s.PullRequestOpen,
        Stage.Review => s.ReviewRecorded,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown stage"),
    };
}
