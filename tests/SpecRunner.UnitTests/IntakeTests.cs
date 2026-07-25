using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>Tier 1: intake kind inference and Targets parsing (FR-016, issue-conventions.md).</summary>
public class IntakeTests
{
    private static WorkItem Item(string title, string body) =>
        new(1, title, body, "nbon12", 42, []);

    [Fact]
    public void No_target_spec_is_a_chore()
    {
        var c = Intake.Classify(Item("Add a banner line", "Make version print a tagline.\nTargets: none"));
        Assert.Equal(Kind.Chore, c.Kind);
    }

    [Fact]
    public void Named_target_reads_as_feature()
    {
        var c = Intake.Classify(Item("New widget", "Build it.\nTargets: specs/003-widget"));
        Assert.Equal(Kind.Feature, c.Kind);
    }

    [Fact]
    public void Amend_language_with_target_reads_as_amendment()
    {
        var c = Intake.Classify(Item("Tweak widget", "Amend the behaviour.\nTargets: specs/003-widget"));
        Assert.Equal(Kind.Amendment, c.Kind);
    }

    [Theory]
    [InlineData("Investigate the flaky test", Kind.Spike)]
    [InlineData("Audit the auth spec against code", Kind.Audit)]
    public void Investigate_and_audit_language_classify_accordingly(string title, Kind expected)
    {
        Assert.Equal(expected, Intake.Classify(Item(title, "Targets: none")).Kind);
    }

    [Fact]
    public void Label_for_kind_is_lowercased()
    {
        Assert.Equal("kind/chore", Intake.LabelFor(Kind.Chore));
    }

    [Theory]
    [InlineData("Targets: none", 0)]
    [InlineData("Targets: specs/a", 1)]
    [InlineData("Targets: specs/a, specs/b", 2)]
    [InlineData("no targets line at all", 0)]
    public void Targets_line_parses_to_the_right_count(string body, int expected)
    {
        Assert.Equal(expected, Item("t", body).Targets.Count);
    }
}
