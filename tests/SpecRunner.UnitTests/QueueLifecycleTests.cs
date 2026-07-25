using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

public class StaleReclaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void In_progress_past_threshold_reclaims()
    {
        Assert.True(StaleReclaim.ShouldReclaim(QueueStatus.InProgress, Now.AddHours(-3), Now, staleHours: 2));
    }

    [Fact]
    public void In_progress_within_threshold_does_not_reclaim()
    {
        Assert.False(StaleReclaim.ShouldReclaim(QueueStatus.InProgress, Now.AddHours(-1), Now, staleHours: 2));
    }

    [Theory]
    [InlineData(QueueStatus.Live)]
    [InlineData(QueueStatus.Held)]
    [InlineData(QueueStatus.Waiting)]
    [InlineData(QueueStatus.Ready)]
    public void Non_in_progress_never_reclaims_even_when_old(QueueStatus status)
    {
        Assert.False(StaleReclaim.ShouldReclaim(status, Now.AddHours(-100), Now, staleHours: 2));
    }
}

public class ReadinessTests
{
    private static WorkItem WithTargets(string body) => new(1, "t", body, "u", 1, []);

    [Fact]
    public void Item_with_all_targets_on_main_is_schedulable()
    {
        var item = WithTargets("Targets: specs/a, specs/b");
        Assert.True(Readiness.IsSchedulable(item, new HashSet<string> { "specs/a", "specs/b" }));
    }

    [Fact]
    public void Item_with_a_missing_target_is_held()
    {
        var item = WithTargets("Targets: specs/a, specs/b");
        var onMain = new HashSet<string> { "specs/a" };
        Assert.False(Readiness.IsSchedulable(item, onMain));
        Assert.Equal(["specs/b"], Readiness.MissingTargets(item, onMain));
    }

    [Fact]
    public void No_targets_is_always_schedulable()
    {
        Assert.True(Readiness.IsSchedulable(WithTargets("Targets: none"), new HashSet<string>()));
    }
}

public class RecurrenceTests
{
    private static WorkItem WithBody(string body) => new(1, "t", body, "u", 1, []);

    [Fact]
    public void Recurring_line_is_detected()
    {
        Assert.True(Recurrence.IsRecurring(WithBody("do the thing\nRecurring: monthly")));
        Assert.Equal("monthly", Recurrence.Cadence(WithBody("Recurring: monthly")));
    }

    [Fact]
    public void No_recurring_line_is_not_recurring()
    {
        Assert.False(Recurrence.IsRecurring(WithBody("just a normal issue")));
    }
}

public class ClarifyMarkersTests
{
    [Fact]
    public void Extracts_needs_clarification_markers()
    {
        var spec = "The system MUST authenticate via [NEEDS CLARIFICATION: which method?] and retain data for [NEEDS CLARIFICATION: how long?].";
        var qs = ClarifyMarkers.Extract(spec);
        Assert.Equal(2, qs.Count);
        Assert.Equal("which method?", qs[0]);
        Assert.Equal("how long?", qs[1]);
    }

    [Fact]
    public void No_markers_yields_empty()
    {
        Assert.Empty(ClarifyMarkers.Extract("a clean spec with no markers"));
    }

    [Fact]
    public void Questions_comment_is_numbered_with_marker_and_reason()
    {
        var comment = ClarifyMarkers.QuestionsComment(5, ["which method?"], "Remote Control outage");
        Assert.Contains("kind=questions id=clarify-5", comment, System.StringComparison.Ordinal);
        Assert.Contains("Remote Control outage", comment, System.StringComparison.Ordinal);
        Assert.Contains("1. which method?", comment, System.StringComparison.Ordinal);
    }
}
