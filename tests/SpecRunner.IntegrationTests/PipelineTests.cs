using SpecRunner.Configuration;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.IntegrationTests;

/// <summary>
/// Tier 2 — a chore flows the full pipeline across ticks: intake → implement (PR opened, issue
/// stays open) → review (merge + digest + close). Uses in-memory GitHub and a scripted process
/// runner (the fake claude "makes a change"), so it runs credit-free and offline.
/// </summary>
public class PipelineTests
{
    [Fact]
    public async Task Chore_flows_intake_to_implement_to_review_merge()
    {
        var tmp = Directory.CreateTempSubdirectory("pipeline");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(3, "Add a file", "Targets: none", "operator", 100, "status/ready");

            var config = new InstanceConfig
            {
                Slug = "op/repo",
                Path = "/clone",
                WorktreesRoot = worktreesRoot,
                OperatorLogin = "operator",
                BaseBranch = "master",
                AutoMerge = true,
                SpendCap = 100,
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };

            // Scripted process runner: git worktree add / status / commit / push succeed, and
            // `git status --porcelain` reports a change (so implement proceeds to PR).
            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                {
                    var isStatus = inv.FileName == "git" && inv.Arguments.Contains("--porcelain");
                    var stdout = isStatus ? "?? newfile.txt" : (inv.Arguments.Contains("HEAD") ? "abc1234" : "");
                    return new SpecRunner.Ports.ProcessResult(0, stdout, "");
                },
            };

            // Tick 1: intake.
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Contains("kind/chore", github.Issue(3).Labels);
            Assert.True(github.Issue(3).Open);
            Assert.Empty(github.OpenedPrs);

            // Tick 2: implement → PR opened, issue STAYS OPEN (review pending, FR-033a).
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Single(github.OpenedPrs);
            Assert.True(github.Issue(3).Open);
            Assert.Contains("stage/implement", github.Issue(3).Labels);

            // Tick 3: review → merge + digest + close.
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Contains("stage/review", github.Issue(3).Labels);
            Assert.Single(github.MergedPrs);
            Assert.False(github.Issue(3).Open); // closed only after review

            // The digest and review records are present (FR-033c/034f).
            Assert.Contains(github.Issue(3).Comments, c => c.Contains("kind=digest", System.StringComparison.Ordinal));
            Assert.Contains(github.Issue(3).Comments, c => c.Contains("kind=review", System.StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Auto_merge_off_leaves_the_pr_for_the_operator()
    {
        var tmp = Directory.CreateTempSubdirectory("pipeline2");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(4, "Add a file", "Targets: none", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/implement");
            github.Issue(4).Comments.Add("<!-- spec-runner:v1 kind=pr id=pr-4 number=42 -->");

            var config = new InstanceConfig
            {
                Slug = "op/repo", Path = "/clone",
                WorktreesRoot = Path.Combine(tmp.FullName, "work"),
                OperatorLogin = "operator", BaseBranch = "master",
                AutoMerge = false, SpendCap = 100,
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };
            var processes = new RecordingProcessRunner();

            await new Tick(config, github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.MergedPrs);                  // NOT merged — operator's gate
            Assert.False(github.Issue(4).Open);              // still closed (runner's work done)
            Assert.Contains("stage/review", github.Issue(4).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Item_targeting_a_spec_not_on_base_is_held_not_scheduled()
    {
        var tmp = Directory.CreateTempSubdirectory("held");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            // Targets a spec that isn't on base (ls-tree returns nothing) → must hold, untouched.
            github.AddIssue(9, "Amend the widget spec", "Targets: specs/010-widget/spec.md",
                "operator", 100, "status/ready", "kind/amendment", "stage/intake");

            var config = new InstanceConfig
            {
                Slug = "op/repo", Path = "/clone",
                WorktreesRoot = Path.Combine(tmp.FullName, "work"),
                OperatorLogin = "operator", BaseBranch = "master",
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };
            // ls-tree returns empty ⇒ no specs on base ⇒ the target is missing.
            var processes = new RecordingProcessRunner();

            await new Tick(config, github, processes, TextWriter.Null).RunAsync();

            // Held: still ready, still open, no worktree/PR work happened.
            Assert.True(github.Issue(9).Open);
            Assert.Contains("status/ready", github.Issue(9).Labels);
            Assert.Empty(github.OpenedPrs);
            Assert.DoesNotContain("stage/implement", github.Issue(9).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Audit_reports_and_closes_without_opening_a_pr()
    {
        var tmp = Directory.CreateTempSubdirectory("audit");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(11, "Audit the specs", "Targets: none", "operator", 100,
                "status/ready", "kind/audit", "stage/intake");

            var config = new InstanceConfig
            {
                Slug = "op/repo", Path = "/clone",
                WorktreesRoot = Path.Combine(tmp.FullName, "work"),
                OperatorLogin = "operator", BaseBranch = "master",
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };
            var processes = new RecordingProcessRunner();

            await new Tick(config, github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.OpenedPrs);                  // audit modifies nothing (FR-039)
            Assert.False(github.Issue(11).Open);             // reported and closed
            Assert.Contains(github.Issue(11).Comments,
                c => c.Contains("kind=audit", System.StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Recurring_item_files_a_successor_when_it_closes()
    {
        var tmp = Directory.CreateTempSubdirectory("pipeline3");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(7, "Nightly audit", "Recurring: nightly\nTargets: none", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/implement");
            github.Issue(7).Comments.Add("<!-- spec-runner:v1 kind=pr id=pr-7 number=71 -->");

            var config = new InstanceConfig
            {
                Slug = "op/repo", Path = "/clone",
                WorktreesRoot = Path.Combine(tmp.FullName, "work"),
                OperatorLogin = "operator", BaseBranch = "master",
                AutoMerge = true, SpendCap = 100,
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };
            var processes = new RecordingProcessRunner();

            await new Tick(config, github, processes, TextWriter.Null).RunAsync();

            Assert.False(github.Issue(7).Open);              // predecessor closed
            var successor = github.Issue(8);                 // successor filed with the next number
            Assert.True(successor.Open);
            Assert.Contains("status/ready", successor.Labels);
            Assert.Contains("Recurring: nightly", successor.Body, System.StringComparison.Ordinal);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
