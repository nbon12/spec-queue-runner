using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>
/// Tier 1: stage derivation as a pure function — directory-fixture-in, stage-out
/// (testing constitution §3.1). Data-varied cases use a Theory (testing §2.5).
/// </summary>
public class StageDerivationTests
{
    // A snapshot where nothing exists yet but intake is done — the item is a fresh feature
    // that has been classified. Individual tests flip the fields they care about.
    private static WorktreeSnapshot AfterIntake() => new(
        KindResolved: true,
        SpecExists: false,
        UnresolvedMarkerCount: 0,
        PlanExists: false,
        TasksExists: false,
        AnalysisRecorded: false,
        PullRequestOpen: false,
        ReviewRecorded: false);

    [Fact]
    public void Unclassified_item_is_at_intake()
    {
        var snapshot = AfterIntake() with { KindResolved = false };
        Assert.Equal(Stage.Intake, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void No_spec_means_specify()
    {
        Assert.Equal(Stage.Specify, StageDerivation.Derive(Kind.Feature, AfterIntake()));
    }

    [Fact]
    public void Spec_with_unresolved_markers_means_clarify()
    {
        var snapshot = AfterIntake() with { SpecExists = true, UnresolvedMarkerCount = 3 };
        Assert.Equal(Stage.Clarify, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void Spec_with_zero_markers_but_no_plan_means_plan()
    {
        var snapshot = AfterIntake() with { SpecExists = true, UnresolvedMarkerCount = 0 };
        Assert.Equal(Stage.Plan, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void Plan_without_tasks_means_tasks()
    {
        var snapshot = AfterIntake() with
        {
            SpecExists = true,
            PlanExists = true,
        };
        Assert.Equal(Stage.Tasks, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void Tasks_without_analysis_means_analyze()
    {
        var snapshot = AfterIntake() with
        {
            SpecExists = true,
            PlanExists = true,
            TasksExists = true,
        };
        Assert.Equal(Stage.Analyze, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void Analyzed_but_no_pr_means_implement()
    {
        var snapshot = AfterIntake() with
        {
            SpecExists = true,
            PlanExists = true,
            TasksExists = true,
            AnalysisRecorded = true,
        };
        Assert.Equal(Stage.Implement, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void Pr_open_but_not_reviewed_means_review()
    {
        var snapshot = AfterIntake() with
        {
            SpecExists = true,
            PlanExists = true,
            TasksExists = true,
            AnalysisRecorded = true,
            PullRequestOpen = true,
        };
        Assert.Equal(Stage.Review, StageDerivation.Derive(Kind.Feature, snapshot));
    }

    [Fact]
    public void Fully_satisfied_feature_derives_to_null()
    {
        var snapshot = new WorktreeSnapshot(
            KindResolved: true, SpecExists: true, UnresolvedMarkerCount: 0,
            PlanExists: true, TasksExists: true, AnalysisRecorded: true,
            PullRequestOpen: true, ReviewRecorded: true);
        Assert.Null(StageDerivation.Derive(Kind.Feature, snapshot));
    }

    // A spike never touches specify/clarify/plan/tasks/analyze/review — only intake then
    // implement. So an unbuilt spike with a resolved kind sits at implement, and a spec
    // existing (or not) is irrelevant to it.
    [Theory]
    [InlineData(Kind.Spike)]
    [InlineData(Kind.Audit)]
    public void Investigate_only_kinds_go_straight_to_implement(Kind kind)
    {
        var snapshot = AfterIntake() with { SpecExists = false };
        Assert.Equal(Stage.Implement, StageDerivation.Derive(kind, snapshot));
    }

    // A chore skips the shaping stages: intake -> plan -> implement -> review. With intake
    // done and no plan, it's at plan (not specify — a chore never specifies).
    [Fact]
    public void Chore_skips_specify_and_clarify()
    {
        Assert.Equal(Stage.Plan, StageDerivation.Derive(Kind.Chore, AfterIntake()));
    }
}
