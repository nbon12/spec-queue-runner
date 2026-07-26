using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>
/// Tier 1 — the stale-branch rule as pure logic (CLAUDE.md). The asymmetry is the whole point:
/// a rebase is a fine way to CONTINUE and a disqualifying way to arrive at a MERGE.
/// </summary>
public class BranchFreshnessTests
{
    [Theory]
    [InlineData(Freshness.UpToDate, true)]
    [InlineData(Freshness.Rebased, true)]
    [InlineData(Freshness.Conflicted, false)]
    [InlineData(Freshness.BaseUnknown, false)]
    public void Only_a_branch_on_the_latest_base_is_judged(Freshness freshness, bool expected) =>
        Assert.Equal(expected, BranchFreshness.MayContinue(freshness));

    [Theory]
    [InlineData(Freshness.UpToDate, true)]
    [InlineData(Freshness.Rebased, false)]
    [InlineData(Freshness.Conflicted, false)]
    [InlineData(Freshness.BaseUnknown, false)]
    public void Only_a_branch_that_needed_no_catching_up_is_merged(Freshness freshness, bool expected) =>
        Assert.Equal(expected, BranchFreshness.MayMerge(freshness));

    [Fact]
    public void A_base_that_could_not_be_read_is_not_treated_as_an_unchanged_base()
    {
        // The failure that this distinction exists to prevent: an unreachable remote reading as
        // "nothing new upstream", and a months-old branch merging on the strength of it.
        Assert.False(BranchFreshness.MayMerge(Freshness.BaseUnknown));
        Assert.False(BranchFreshness.MayContinue(Freshness.BaseUnknown));
    }

    [Theory]
    [InlineData(Freshness.Rebased)]
    [InlineData(Freshness.Conflicted)]
    [InlineData(Freshness.BaseUnknown)]
    public void Every_refusal_names_the_base_branch_it_is_about(Freshness freshness)
    {
        var explanation = BranchFreshness.Explain(freshness, "master");
        Assert.Contains("master", explanation, System.StringComparison.Ordinal);
        Assert.NotEqual("unknown branch state", explanation);
    }
}
