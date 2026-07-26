using SpecRunner.Configuration;
using SpecRunner.Ports;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.IntegrationTests;

/// <summary>
/// Tier 2 — what the runner does when a Claude-invoking stage does NOT succeed.
///
/// These exist because the runner previously logged the exit code and then proceeded regardless:
/// a failed review posted "no blocking finding recorded" and merged the pull request, asserting a
/// verification that never ran. With auto-merge on, that could merge unreviewed code.
/// </summary>
public class StageFailureTests
{
    private static InstanceConfig Config(string tmp, bool autoMerge = true) => new()
    {
        Slug = "op/repo", Path = "/clone",
        WorktreesRoot = Path.Combine(tmp, "work"),
        OperatorLogin = "operator", BaseBranch = "master",
        AutoMerge = autoMerge, SpendCap = 100,
        GitHubPatFile = "/run/secrets/pat",
        ClaudeConfigPath = Path.Combine(tmp, ".claude.json"),
        Lock = Path.Combine(tmp, ".lock"),
    };

    /// <summary>A runner where `claude` fails with the given output and everything else succeeds.</summary>
    private static RecordingProcessRunner ClaudeFails(string stderr, int exitCode = 1) => new()
    {
        Respond = inv => inv.FileName == "claude"
            ? new ProcessResult(exitCode, "", stderr)
            : new ProcessResult(0, "", ""),
    };

    private static InMemoryGitHubClient ItemAwaitingReview(int number)
    {
        var github = new InMemoryGitHubClient();
        github.AddUser("operator", 100);
        github.AddIssue(number, "Add a thing", "Targets: none", "operator", 100,
            "status/ready", "kind/chore", "stage/intake", "stage/implement");
        github.Issue(number).Comments.Add($"<!-- spec-runner:v1 kind=pr id=pr-{number} number=99 -->");
        return github;
    }

    [Fact]
    public async Task A_failed_review_does_not_merge_and_does_not_close()
    {
        var tmp = Directory.CreateTempSubdirectory("revfail");
        try
        {
            var github = ItemAwaitingReview(5);

            await new Tick(Config(tmp.FullName), github, ClaudeFails("boom: crashed"), TextWriter.Null)
                .RunAsync();

            Assert.Empty(github.MergedPrs);                       // the critical assertion
            Assert.True(github.Issue(5).Open);                    // still open for a retry
            Assert.DoesNotContain("stage/review", github.Issue(5).Labels);
            Assert.Contains(github.Issue(5).Comments,
                c => c.Contains("kind=review-failed", StringComparison.Ordinal));
            // It must not claim the change was verified.
            Assert.DoesNotContain(github.Issue(5).Comments,
                c => c.Contains("no blocking finding", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_rate_limited_review_defers_and_returns_the_item_to_ready()
    {
        var tmp = Directory.CreateTempSubdirectory("revlimit");
        try
        {
            var github = ItemAwaitingReview(6);

            await new Tick(Config(tmp.FullName), github,
                ClaudeFails("Claude usage limit reached"), TextWriter.Null).RunAsync();

            Assert.Empty(github.MergedPrs);
            Assert.True(github.Issue(6).Open);
            Assert.Contains("status/ready", github.Issue(6).Labels);   // FR-043: retry later
            Assert.Contains(github.Issue(6).Comments,
                c => c.Contains("usage limit", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_successful_review_records_what_the_review_actually_said()
    {
        var tmp = Directory.CreateTempSubdirectory("revok");
        try
        {
            var github = ItemAwaitingReview(7);
            var processes = new RecordingProcessRunner
            {
                Respond = inv => inv.FileName == "claude"
                    ? new ProcessResult(0, "Checked 3 files; tests cover the stated scenarios.", "")
                    : new ProcessResult(0, "", ""),
            };

            await new Tick(Config(tmp.FullName), github, processes, TextWriter.Null).RunAsync();

            Assert.Single(github.MergedPrs);
            Assert.False(github.Issue(7).Open);
            // The review's own words reach the issue, rather than a hardcoded assertion.
            Assert.Contains(github.Issue(7).Comments,
                c => c.Contains("Checked 3 files", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_audit_does_not_close_the_item()
    {
        var tmp = Directory.CreateTempSubdirectory("auditfail");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(8, "Audit the specs", "Targets: none", "operator", 100,
                "status/ready", "kind/audit", "stage/intake");
            var specDir = Path.Combine(tmp.FullName, "work", "8", "specs", "001-x");
            Directory.CreateDirectory(specDir);
            await File.WriteAllTextAsync(Path.Combine(specDir, "spec.md"), "# x");

            await new Tick(Config(tmp.FullName), github, ClaudeFails("boom"), TextWriter.Null).RunAsync();

            Assert.True(github.Issue(8).Open);   // an audit that never ran has nothing to report
            Assert.Contains(github.Issue(8).Comments,
                c => c.Contains("kind=audit-failed", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_implement_opens_no_pull_request()
    {
        var tmp = Directory.CreateTempSubdirectory("implfail");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Add a thing", "Targets: none", "operator", 100,
                "status/ready", "kind/chore", "stage/intake");

            // claude fails, but `git status --porcelain` would still report a dirty tree —
            // partial work must not be presented as a finished change.
            var processes = new RecordingProcessRunner
            {
                Respond = inv => inv.FileName == "claude"
                    ? new ProcessResult(1, "", "boom")
                    : new ProcessResult(0, inv.Arguments.Contains("--porcelain") ? "?? half.txt" : "", ""),
            };

            await new Tick(Config(tmp.FullName), github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.OpenedPrs);
            Assert.True(github.Issue(9).Open);
            Assert.Contains(github.Issue(9).Comments,
                c => c.Contains("kind=implement-failed", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
