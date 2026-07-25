using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>Tier 1: kind -> stage-sequence dispatch (FR-015), all five kinds.</summary>
public class StageSequenceTests
{
    [Theory]
    [InlineData(Kind.Feature)]
    [InlineData(Kind.Amendment)]
    public void Feature_and_amendment_traverse_all_eight_stages(Kind kind)
    {
        Assert.Equal(
            [Stage.Intake, Stage.Specify, Stage.Clarify, Stage.Plan,
             Stage.Tasks, Stage.Analyze, Stage.Implement, Stage.Review],
            StageSequence.For(kind));
    }

    [Fact]
    public void Chore_skips_shaping_stages()
    {
        Assert.Equal(
            [Stage.Intake, Stage.Plan, Stage.Implement, Stage.Review],
            StageSequence.For(Kind.Chore));
    }

    [Theory]
    [InlineData(Kind.Spike)]
    [InlineData(Kind.Audit)]
    public void Investigate_only_kinds_are_intake_then_implement(Kind kind)
    {
        Assert.Equal([Stage.Intake, Stage.Implement], StageSequence.For(kind));
    }

    [Theory]
    [InlineData(Kind.Feature)]
    [InlineData(Kind.Amendment)]
    [InlineData(Kind.Chore)]
    public void Code_writing_kinds_always_end_in_review(Kind kind)
    {
        Assert.Equal(Stage.Review, StageSequence.For(kind)[^1]);
    }

    [Theory]
    [InlineData(Kind.Spike)]
    [InlineData(Kind.Audit)]
    public void Non_diff_kinds_never_include_review(Kind kind)
    {
        Assert.DoesNotContain(Stage.Review, StageSequence.For(kind));
    }
}
