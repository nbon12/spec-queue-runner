using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

public class StageProgressTests
{
    [Fact]
    public void A_fresh_item_is_at_the_first_stage_of_its_kind()
    {
        Assert.Equal(Stage.Intake, StageProgress.Derive(Kind.Feature, []));
        Assert.Equal(Stage.Intake, StageProgress.Derive(Kind.Chore, []));
    }

    [Fact]
    public void Position_is_the_first_stage_with_no_completion_label()
    {
        string[] labels = ["kind/feature", "stage/intake", "stage/specify", "status/ready"];
        Assert.Equal(Stage.Clarify, StageProgress.Derive(Kind.Feature, labels));
    }

    [Fact]
    public void A_chore_skips_the_shaping_stages_entirely()
    {
        // Chore's sequence is intake -> plan -> implement -> review; specify/clarify are not in
        // it at all, so their absence must not hold the item.
        string[] labels = ["stage/intake"];
        Assert.Equal(Stage.Plan, StageProgress.Derive(Kind.Chore, labels));
    }

    [Fact]
    public void Labels_outside_the_kinds_sequence_are_ignored()
    {
        // An item relabelled from feature to chore may carry stage/specify; chore has no such
        // stage, and it must not be mistaken for progress through chore's own sequence.
        string[] labels = ["stage/intake", "stage/specify", "stage/clarify"];
        Assert.Equal(Stage.Plan, StageProgress.Derive(Kind.Chore, labels));
    }

    [Fact]
    public void An_item_with_every_stage_recorded_is_done()
    {
        var labels = StageSequence.For(Kind.Chore).Select(StageProgress.LabelFor).ToList();
        Assert.Null(StageProgress.Derive(Kind.Chore, labels));
    }

    [Fact]
    public void A_gap_in_the_middle_returns_the_gap_not_the_furthest_label()
    {
        // Derivation is "first unsatisfied", not "furthest reached" — a missing earlier stage
        // must be run, otherwise a partially-labelled item would skip work.
        string[] labels = ["stage/intake", "stage/implement", "stage/review"];
        Assert.Equal(Stage.Plan, StageProgress.Derive(Kind.Chore, labels));
    }

    [Theory]
    [InlineData(Stage.Intake, "stage/intake")]
    [InlineData(Stage.Specify, "stage/specify")]
    [InlineData(Stage.Implement, "stage/implement")]
    [InlineData(Stage.Review, "stage/review")]
    public void Labels_round_trip(Stage stage, string label)
    {
        Assert.Equal(label, StageProgress.LabelFor(stage));
        Assert.Equal(stage, StageProgress.FromLabel(label));
    }

    [Fact]
    public void Non_stage_labels_do_not_parse_as_stages()
    {
        Assert.Null(StageProgress.FromLabel("status/ready"));
        Assert.Null(StageProgress.FromLabel("kind/chore"));
        Assert.Null(StageProgress.FromLabel("stage/nonsense"));
    }

    [Fact]
    public void Completed_lists_the_trail_in_sequence_order()
    {
        string[] labels = ["stage/review", "stage/intake"];   // recorded out of order
        Assert.Equal([Stage.Intake, Stage.Review], StageProgress.Completed(Kind.Chore, labels));
    }
}
