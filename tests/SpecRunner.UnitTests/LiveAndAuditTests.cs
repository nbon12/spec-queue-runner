using SpecRunner.Domain;
using SpecRunner.Doctor;
using Xunit;

namespace SpecRunner.UnitTests;

public class LiveSessionTests
{
    [Fact]
    public void Tmux_name_is_deterministic_per_item()
    {
        Assert.Equal("spec-runner-12", LiveSession.TmuxName(12));
    }

    [Fact]
    public void Execution_block_kickoff_bars_implementation()
    {
        var k = LiveSession.Kickoff(3, isExecutionBlock: true);
        Assert.Contains("Do NOT continue implementation", k, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Shaping_kickoff_does_not_bar_implementation()
    {
        Assert.DoesNotContain("Do NOT continue", LiveSession.Kickoff(3, isExecutionBlock: false),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void Records_and_recovers_the_session_id()
    {
        var comment = LiveSession.SessionComment("sess_abc123");
        Assert.Equal("sess_abc123", LiveSession.RecordedId([comment]));
    }

    [Fact]
    public void No_recorded_id_returns_null()
    {
        Assert.Null(LiveSession.RecordedId(["just a human comment", "another"]));
    }
}

public class AuditSelectionTests
{
    [Fact]
    public void Never_audited_spec_is_picked_first()
    {
        var specs = new[] { "specs/a", "specs/b", "specs/c" };
        var last = new Dictionary<string, System.DateTimeOffset>
        {
            ["specs/a"] = System.DateTimeOffset.UtcNow,
        };
        // b and c never audited; alphabetically b wins.
        Assert.Equal("specs/b", AuditSelection.LeastRecentlyAudited(specs, last));
    }

    [Fact]
    public void Otherwise_the_oldest_audit_wins()
    {
        var specs = new[] { "specs/a", "specs/b" };
        var now = System.DateTimeOffset.UtcNow;
        var last = new Dictionary<string, System.DateTimeOffset>
        {
            ["specs/a"] = now,
            ["specs/b"] = now.AddDays(-10),
        };
        Assert.Equal("specs/b", AuditSelection.LeastRecentlyAudited(specs, last));
    }

    [Fact]
    public void No_specs_returns_null()
    {
        Assert.Null(AuditSelection.LeastRecentlyAudited([], new Dictionary<string, System.DateTimeOffset>()));
    }
}

public class LaunchdInstallerTests
{
    [Fact]
    public void Plist_invokes_docker_with_the_config_and_mounts()
    {
        var plist = LaunchdInstaller.Plist(
            slug: "nbon12/homelab", image: "spec-runner:latest",
            configPathInContainer: "/etc/spec-runner/homelab.toml",
            hostConfigPath: "/Users/x/.config/spec-runner/homelab.toml",
            patSecretHostPath: "/Users/x/.config/spec-runner/homelab.pat",
            claudeVolume: "sr-homelab-claude", worktreesVolume: "sr-homelab-work",
            intervalSeconds: 300);

        Assert.Contains("com.spec-runner.nbon12.homelab", plist, System.StringComparison.Ordinal);
        Assert.Contains("<integer>300</integer>", plist, System.StringComparison.Ordinal);
        Assert.Contains("docker", plist, System.StringComparison.Ordinal);
        Assert.Contains("--init", plist, System.StringComparison.Ordinal); // reaping
        Assert.Contains("/run/secrets/github_pat:ro", plist, System.StringComparison.Ordinal);
        Assert.Contains("<string>tick</string>", plist, System.StringComparison.Ordinal);
    }
}
