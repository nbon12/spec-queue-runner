using SpecRunner.Configuration;
using SpecRunner.Domain;
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

            // Tick 2: plan. A chore's sequence is intake → plan → implement → review, and every
            // stage in it now actually runs: under the old predicate model `planExists` was
            // vacuously true for non-spec kinds, so plan was silently skipped for every chore.
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Contains("stage/plan", github.Issue(3).Labels);
            Assert.Empty(github.OpenedPrs);

            // Tick 3: implement → PR opened, issue STAYS OPEN (review pending, FR-033a).
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Single(github.OpenedPrs);
            Assert.True(github.Issue(3).Open);
            Assert.Contains("stage/implement", github.Issue(3).Labels);

            // Tick 4: review → merge + digest + close.
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
                "status/ready", "kind/chore", "stage/intake", "stage/plan", "stage/implement");
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
    public async Task Item_blocked_by_an_open_issue_is_held_not_scheduled()
    {
        var tmp = Directory.CreateTempSubdirectory("held");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Amend the widget spec", "", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");
            // GitHub's native relationship: #9 is blocked by #20, which is still open.
            github.AddIssue(20, "Land the widget spec", "", "operator", 100);
            github.AddBlockedBy(9, 20);

            var log = new StringWriter();
            await new Tick(HeldConfig(tmp), github, new RecordingProcessRunner(), log).RunAsync();

            // Held: still ready, still open, untouched — and the blocker is named in the log.
            Assert.True(github.Issue(9).Open);
            Assert.Contains("status/ready", github.Issue(9).Labels);
            Assert.Empty(github.Issue(9).Comments);
            Assert.Empty(github.OpenedPrs);
            Assert.DoesNotContain("stage/implement", github.Issue(9).Labels);
            Assert.Contains("#20", log.ToString(), System.StringComparison.Ordinal);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Iceboxed_item_is_skipped_even_when_labelled_ready()
    {
        var tmp = Directory.CreateTempSubdirectory("icebox");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            // The contradictory case: parked by the operator, yet still carrying status/ready.
            // `icebox` must win — otherwise it is a safeguard a stray label can override.
            github.AddIssue(20, "Parked idea", "", "operator", 100,
                "status/ready", "icebox", "kind/chore", "stage/intake", "stage/plan");

            var processes = new RecordingProcessRunner();
            await new Tick(HeldConfig(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.OpenedPrs);
            Assert.DoesNotContain("stage/implement", github.Issue(20).Labels);
            Assert.Empty(processes.Invocations);   // never reached the process boundary at all
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Every_open_blocker_is_named_when_an_item_is_held()
    {
        var tmp = Directory.CreateTempSubdirectory("held2");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Amend the widget spec", "", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");
            github.AddIssue(20, "Land the spec", "", "operator", 100);
            github.AddIssue(21, "Land the schema", "", "operator", 100);
            github.AddIssue(22, "Already done", "", "operator", 100);
            github.Issue(22).Open = false;              // a closed blocker holds nothing
            github.AddBlockedBy(9, 20, 21, 22);

            var log = new StringWriter();
            await new Tick(HeldConfig(tmp), github, new RecordingProcessRunner(), log).RunAsync();

            Assert.Contains("status/ready", github.Issue(9).Labels);
            Assert.Empty(github.OpenedPrs);
            Assert.Contains("#20, #21", log.ToString(), System.StringComparison.Ordinal);
            Assert.DoesNotContain("#22", log.ToString(), System.StringComparison.Ordinal);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]

    public async Task Item_is_released_once_the_last_blocker_closes()
    {
        var tmp = Directory.CreateTempSubdirectory("held3");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Add a file", "", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");
            github.AddIssue(20, "First blocker", "", "operator", 100);
            github.AddIssue(21, "Second blocker", "", "operator", 100);
            github.AddBlockedBy(9, 20, 21);

            // The fake claude "makes a change", so a released item reaches PR.
            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                {
                    var isStatus = inv.FileName == "git" && inv.Arguments.Contains("--porcelain");
                    var stdout = isStatus ? "?? newfile.txt" : (inv.Arguments.Contains("HEAD") ? "abc1234" : "");
                    return new SpecRunner.Ports.ProcessResult(0, stdout, "");
                },
            };
            var config = HeldConfig(tmp);

            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Empty(github.OpenedPrs);             // held by two

            github.Issue(20).Open = false;
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Empty(github.OpenedPrs);             // still held by one

            // Closing the last blocker releases it — no relabelling, no operator action.
            github.Issue(21).Open = false;
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Single(github.OpenedPrs);
            Assert.Contains("stage/implement", github.Issue(9).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task An_item_with_no_dependencies_is_schedulable_whatever_its_body_says()
    {
        var tmp = Directory.CreateTempSubdirectory("held4");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            // Prose that used to wedge the item forever is now inert: no relationship, no hold.
            github.AddIssue(9, "Add a file", "Targets: specs/010-widget/spec.md\nBlocked by: #999",
                "operator", 100, "status/ready", "kind/chore", "stage/intake", "stage/plan");

            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                {
                    var isStatus = inv.FileName == "git" && inv.Arguments.Contains("--porcelain");
                    var stdout = isStatus ? "?? newfile.txt" : (inv.Arguments.Contains("HEAD") ? "abc1234" : "");
                    return new SpecRunner.Ports.ProcessResult(0, stdout, "");
                },
            };

            await new Tick(HeldConfig(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Single(github.OpenedPrs);
            Assert.Contains("stage/implement", github.Issue(9).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    private static InstanceConfig HeldConfig(DirectoryInfo tmp) => new()
    {
        Slug = "op/repo", Path = "/clone",
        WorktreesRoot = Path.Combine(tmp.FullName, "work"),
        OperatorLogin = "operator", BaseBranch = "master",
        GitHubPatFile = "/run/secrets/pat",
        ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
        Lock = Path.Combine(tmp.FullName, ".lock"),
    };
    [Fact]
    public async Task Stale_in_progress_item_is_reclaimed_to_ready()
    {
        var tmp = Directory.CreateTempSubdirectory("stale1");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(12, "Wedged item", "Targets: none", "operator", 100,
                "status/in-progress", "kind/chore", "stage/intake", "stage/plan");
            // Applied 5h ago; threshold is 2h ⇒ stale.
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            github.SetLabeledAt(12, "status/in-progress", now.AddHours(-5));

            var config = new InstanceConfig
            {
                Slug = "op/repo", Path = "/clone",
                WorktreesRoot = Path.Combine(tmp.FullName, "work"),
                OperatorLogin = "operator", BaseBranch = "master", StaleHours = 2,
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };
            var processes = new RecordingProcessRunner();
            await new Tick(config, github, processes, TextWriter.Null,
                null, null, () => now).RunAsync();

            Assert.DoesNotContain("status/in-progress", github.Issue(12).Labels);
            Assert.Contains("status/ready", github.Issue(12).Labels);
            Assert.Contains(github.Issue(12).Comments,
                c => c.Contains("kind=reclaim", System.StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Fresh_in_progress_item_is_not_reclaimed()
    {
        var tmp = Directory.CreateTempSubdirectory("stale2");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(13, "Working item", "Targets: none", "operator", 100,
                "status/in-progress", "kind/chore", "stage/intake", "stage/plan");
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            github.SetLabeledAt(13, "status/in-progress", now.AddMinutes(-10)); // 10 min < 2h

            var config = new InstanceConfig
            {
                Slug = "op/repo", Path = "/clone",
                WorktreesRoot = Path.Combine(tmp.FullName, "work"),
                OperatorLogin = "operator", BaseBranch = "master", StaleHours = 2,
                GitHubPatFile = "/run/secrets/pat",
                ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
                Lock = Path.Combine(tmp.FullName, ".lock"),
            };
            await new Tick(config, github, new RecordingProcessRunner(), TextWriter.Null,
                null, null, () => now).RunAsync();

            Assert.Contains("status/in-progress", github.Issue(13).Labels); // left alone
            Assert.DoesNotContain(github.Issue(13).Comments,
                c => c.Contains("kind=reclaim", System.StringComparison.Ordinal));
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
                "status/ready", "kind/chore", "stage/intake", "stage/plan", "stage/implement");
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

    /// <summary>
    /// The review stage is told what it is reviewing (R17, FR-034h). Asserted at the process
    /// boundary, because what reaches <c>claude</c>'s argv is the only thing the reviewer actually
    /// sees. This is the feature half of quickstart scenario 5c; the chore half — which has no spec
    /// directory at all — is the test below.
    /// </summary>
    [Fact]
    public async Task Review_invocation_carries_the_pull_request_the_branches_and_the_issue()
    {
        var tmp = Directory.CreateTempSubdirectory("review-context");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(5, "Add a widget", "Targets: none", "operator", 100,
                "status/ready", "kind/feature", "stage/intake", "stage/specify", "stage/clarify",
                "stage/plan", "stage/tasks", "stage/analyze", "stage/implement");
            github.Issue(5).Comments.Add(
                "<!-- spec-runner:v1 kind=pr id=pr-5 number=42 url=https://example/pr/42 -->");

            // Two spec directories sit in the worktree; only `002-widget` is on this item's branch.
            // The decoy sorts higher, so the pre-v6.0.0 "highest-sorted directory under specs/"
            // guess would name it — which is the failure this context is supposed to make
            // impossible. The reviewer must be given the one the branch diff names.
            var specs = Path.Combine(tmp.FullName, "work", "5", "specs");
            Directory.CreateDirectory(Path.Combine(specs, "002-widget"));
            Directory.CreateDirectory(Path.Combine(specs, "900-decoy"));

            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                    inv.FileName == "git" && inv.Arguments.Contains("--name-only")
                        ? new SpecRunner.Ports.ProcessResult(0, "specs/002-widget/spec.md\n", "")
                        : new SpecRunner.Ports.ProcessResult(0, "", ""),
            };

            await new Tick(HeldConfig(tmp), github, processes, TextWriter.Null).RunAsync();

            var prompt = ReviewPromptSentTo(processes);
            Assert.Contains("#42", prompt, System.StringComparison.Ordinal);                 // the PR
            Assert.Contains("https://example/pr/42", prompt, System.StringComparison.Ordinal);
            Assert.Contains("origin/master...work/5", prompt, System.StringComparison.Ordinal);
            Assert.Contains("`master`", prompt, System.StringComparison.Ordinal);             // base
            Assert.Contains("`work/5`", prompt, System.StringComparison.Ordinal);             // head
            Assert.Contains("#5", prompt, System.StringComparison.Ordinal);                   // issue
            Assert.Contains("Add a widget", prompt, System.StringComparison.Ordinal);
            Assert.Contains("specs/002-widget", prompt, System.StringComparison.Ordinal);
            Assert.DoesNotContain("900-decoy", prompt, System.StringComparison.Ordinal);

            // Issue-supplied text only ever appears inside the delimited data region (§6).
            Assert.True(
                prompt.IndexOf("Targets: none", System.StringComparison.Ordinal) >
                prompt.IndexOf(ReviewPrompt.ContextHeading, System.StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task An_item_whose_branch_adds_no_spec_directory_is_told_so_rather_than_left_blank()
    {
        var tmp = Directory.CreateTempSubdirectory("review-context2");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(6, "Bump a dependency", "", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan", "stage/implement");
            github.Issue(6).Comments.Add("<!-- spec-runner:v1 kind=pr id=pr-6 number=43 -->");

            var processes = new RecordingProcessRunner();   // no spec paths on the branch
            await new Tick(HeldConfig(tmp), github, processes, TextWriter.Null).RunAsync();

            var prompt = ReviewPromptSentTo(processes);
            Assert.Contains(ReviewPrompt.NoSpecDir, prompt, System.StringComparison.Ordinal);
            // A legacy marker with no `url=` still reviews — the URL falls back to the slug.
            Assert.Contains("https://github.com/op/repo/pull/43", prompt, System.StringComparison.Ordinal);
            Assert.Contains("stage/review", github.Issue(6).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    private static string ReviewPromptSentTo(RecordingProcessRunner processes) =>
        Assert.Single(processes.Invocations
            .Where(i => i.FileName == "claude")
            .SelectMany(i => i.Arguments)
            .Where(a => a.Contains(ReviewPrompt.ContextHeading, System.StringComparison.Ordinal)));
}
