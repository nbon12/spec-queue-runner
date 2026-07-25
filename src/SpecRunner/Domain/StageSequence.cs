namespace SpecRunner.Domain;

/// <summary>
/// Each kind traverses a fixed stage sequence (FR-015). Spike and audit skip review
/// because neither produces a diff (a spike investigates; an audit modifies nothing).
/// </summary>
public static class StageSequence
{
    private static readonly Stage[] FeatureLike =
    [
        Stage.Intake, Stage.Specify, Stage.Clarify,
        Stage.Plan, Stage.Tasks, Stage.Analyze, Stage.Implement, Stage.Review,
    ];

    private static readonly Stage[] Chore =
    [
        Stage.Intake, Stage.Plan, Stage.Implement, Stage.Review,
    ];

    private static readonly Stage[] InvestigateOnly =
    [
        Stage.Intake, Stage.Implement,
    ];

    public static IReadOnlyList<Stage> For(Kind kind) => kind switch
    {
        Kind.Feature or Kind.Amendment => FeatureLike,
        Kind.Chore => Chore,
        Kind.Spike or Kind.Audit => InvestigateOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown kind"),
    };
}
