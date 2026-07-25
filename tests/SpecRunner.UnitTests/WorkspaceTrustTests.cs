using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>
/// Tier 1: the pure workspace-trust JSON transform (FR-012a). This is the code path the probe
/// proved makes a fresh worktree launch without a trust dialog (§3).
/// </summary>
public class WorkspaceTrustTests
{
    private const string Path = "/home/runner/work/12";

    [Fact]
    public void Seeds_trust_into_an_empty_config()
    {
        var result = WorkspaceTrust.Seed(null, Path);
        Assert.True(WorkspaceTrust.IsTrusted(result, Path));
    }

    [Fact]
    public void Untrusted_directory_reads_as_false()
    {
        Assert.False(WorkspaceTrust.IsTrusted(null, Path));
        Assert.False(WorkspaceTrust.IsTrusted("""{"projects":{}}""", Path));
    }

    [Fact]
    public void Preserves_existing_projects_and_other_keys()
    {
        var existing = """
            {
              "someTopLevelKey": "keep me",
              "projects": {
                "/home/runner/work/5": { "hasTrustDialogAccepted": true, "extra": 1 }
              }
            }
            """;

        var result = WorkspaceTrust.Seed(existing, Path);

        Assert.True(WorkspaceTrust.IsTrusted(result, Path));            // new one seeded
        Assert.True(WorkspaceTrust.IsTrusted(result, "/home/runner/work/5")); // old one kept
        Assert.Contains("keep me", result, System.StringComparison.Ordinal);  // unrelated key kept
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = WorkspaceTrust.Seed(null, Path);
        var twice = WorkspaceTrust.Seed(once, Path);
        Assert.Equal(once, twice);
    }
}
