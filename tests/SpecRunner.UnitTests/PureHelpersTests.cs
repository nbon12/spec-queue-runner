using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

public class RateLimitDetectorTests
{
    [Theory]
    [InlineData("Claude usage limit reached", true)]
    [InlineData("Error: rate limit exceeded, retry later", true)]
    [InlineData("RATE LIMIT", true)]
    [InlineData("ratelimit hit", true)]
    [InlineData("all good, done", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_rate_and_usage_limits_case_insensitively(string? output, bool expected)
    {
        Assert.Equal(expected, RateLimitDetector.IsRateLimited(output));
    }
}

public class IdempotencyMarkerTests
{
    [Fact]
    public void Marker_has_stable_shape()
    {
        Assert.Equal("<!-- spec-runner:v1 kind=decision id=abc -->",
            IdempotencyMarker.For("decision", "abc"));
    }

    [Fact]
    public void Stable_id_is_deterministic_and_hex()
    {
        var a = IdempotencyMarker.StableId("intake", "12", "chore");
        var b = IdempotencyMarker.StableId("intake", "12", "chore");
        Assert.Equal(a, b);
        Assert.Equal(12, a.Length);
        Assert.Matches("^[0-9a-f]+$", a);
    }

    [Fact]
    public void Detects_its_own_marker_among_comments()
    {
        var bodies = new[] { "some human comment", IdempotencyMarker.For("decision", "x") + "\nbody" };
        Assert.True(IdempotencyMarker.AlreadyPresent(bodies, "decision", "x"));
        Assert.False(IdempotencyMarker.AlreadyPresent(bodies, "decision", "y"));
    }
}

public class WakingHoursTests
{
    [Theory]
    [InlineData("08:00-23:00", "12:00", true)]
    [InlineData("08:00-23:00", "07:59", false)]
    [InlineData("08:00-23:00", "23:00", false)]
    [InlineData("08:00-23:00", "08:00", true)]
    public void Non_wrapping_window(string window, string now, bool inside)
    {
        Assert.Equal(inside, WakingHours.Parse(window).Contains(TimeOnly.Parse(now)));
    }

    [Theory]
    [InlineData("22:00-06:00", "23:30", true)]
    [InlineData("22:00-06:00", "05:00", true)]
    [InlineData("22:00-06:00", "12:00", false)]
    public void Wrapping_window_past_midnight(string window, string now, bool inside)
    {
        Assert.Equal(inside, WakingHours.Parse(window).Contains(TimeOnly.Parse(now)));
    }

    [Fact]
    public void Bad_format_throws()
    {
        Assert.Throws<FormatException>(() => WakingHours.Parse("8 to 11"));
    }
}

public class StatusLabelTests
{
    [Theory]
    [InlineData(QueueStatus.Ready, "status/ready")]
    [InlineData(QueueStatus.InProgress, "status/in-progress")]
    [InlineData(QueueStatus.Held, "status/held")]
    public void Round_trips(QueueStatus status, string label)
    {
        Assert.Equal(label, status.Label());
        Assert.Equal(status, StatusLabels.FromLabel(label));
    }

    [Fact]
    public void Unknown_label_is_null()
    {
        Assert.Null(StatusLabels.FromLabel("kind/chore"));
    }
}

public class WorkSelectionTests
{
    private static WorkItem Item(int n, long author) => new(n, "t", "", "u", author, []);

    [Fact]
    public void Picks_lowest_numbered_operator_authored()
    {
        var items = new[] { Item(5, 1), Item(2, 1), Item(9, 1) };
        Assert.Equal(2, WorkSelection.Select(items, operatorId: 1)!.Number);
    }

    [Fact]
    public void Ignores_non_operator_items_even_if_lower_numbered()
    {
        var items = new[] { Item(1, 999), Item(4, 1) };
        Assert.Equal(4, WorkSelection.Select(items, operatorId: 1)!.Number);
    }

    [Fact]
    public void Returns_null_when_nothing_operator_authored()
    {
        Assert.Null(WorkSelection.Select([Item(1, 999)], operatorId: 1));
    }
}

public class ReversibilityTests
{
    [Theory]
    [InlineData("run DROP TABLE users")]
    [InlineData("git push --force origin main")]
    [InlineData("rm -rf /data")]
    public void Always_block_signals_block(string description)
    {
        Assert.True(Reversibility.MustBlock(description, [], estimatedSpend: 0, spendCap: 100));
    }

    [Fact]
    public void Spend_over_cap_blocks_even_if_reversible()
    {
        Assert.True(Reversibility.MustBlock("bump the instance size", [], estimatedSpend: 150, spendCap: 100));
    }

    [Fact]
    public void Protected_path_blocks()
    {
        Assert.True(Reversibility.MustBlock("edit infra/prod.tf", ["infra/"], 0, 100));
    }

    [Fact]
    public void Ordinary_reversible_change_does_not_block()
    {
        Assert.False(Reversibility.MustBlock("rename a variable", ["infra/"], 5, 100));
    }
}
