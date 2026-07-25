using SpecRunner.Configuration;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>Tier 1: fail-fast config validation (constitution §7, contracts/config-schema.md).</summary>
public class ConfigValidationTests
{
    private static InstanceConfig Valid() => new()
    {
        Slug = "nbon12/spec-queue-runner",
        Path = "/code/repo",
        WorktreesRoot = "/code/work",
        OperatorLogin = "nbon12",
        GitHubPatFile = "/run/secrets/github_pat",
    };

    [Fact]
    public void A_well_formed_config_validates()
    {
        Assert.True(ConfigValidation.Validate(Valid()).Ok);
    }

    [Theory]
    [InlineData("noslash")]
    [InlineData("too/many/parts")]
    [InlineData("")]
    public void Bad_slug_fails(string slug)
    {
        Assert.False(ConfigValidation.Validate(Valid() with { Slug = slug }).Ok);
    }

    [Fact]
    public void Tick_interval_below_60_fails()
    {
        Assert.False(ConfigValidation.Validate(Valid() with { TickInterval = 30 }).Ok);
    }

    [Theory]
    [InlineData("08:00-23:00", true)]
    [InlineData("8-23", false)]
    [InlineData("08:00", false)]
    [InlineData("25:00-26:00", false)]
    public void Waking_hours_format_is_enforced(string value, bool ok)
    {
        Assert.Equal(ok, ConfigValidation.Validate(Valid() with { WakingHours = value }).Ok);
    }

    [Fact]
    public void Missing_pat_file_fails()
    {
        Assert.False(ConfigValidation.Validate(Valid() with { GitHubPatFile = "" }).Ok);
    }

    [Fact]
    public void Auto_merge_requires_positive_spend_cap()
    {
        Assert.False(ConfigValidation.Validate(Valid() with { AutoMerge = true, SpendCap = 0 }).Ok);
        Assert.True(ConfigValidation.Validate(Valid() with { AutoMerge = false, SpendCap = 0 }).Ok);
    }

    [Fact]
    public void All_errors_are_collected_not_just_the_first()
    {
        var result = ConfigValidation.Validate(Valid() with { Slug = "bad", TickInterval = 1 });
        Assert.False(result.Ok);
        Assert.True(result.Errors.Count >= 2);
    }
}
