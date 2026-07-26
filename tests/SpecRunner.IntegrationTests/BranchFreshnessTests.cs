using SpecRunner.Configuration;
using SpecRunner.Ports;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.IntegrationTests;

/// <summary>
/// Tier 2 — the stale-branch rule (CLAUDE.md): work is cut from the latest base, and a branch
/// that has fallen behind it is rebased before it is judged and never merged as-is.
///
/// The scripted process runner models the one fact that matters — how far behind the base the
/// branch is — and lets the rebase change it, so these tests exercise the real decision sequence
/// (fetch → count → rebase → force-push → re-check) rather than a stubbed verdict.
/// </summary>
public class BranchFreshnessTests
{
    [Fact]
    public async Task Branch_behind_the_base_is_rebased_and_force_pushed_before_merging()
    {
        var tmp = Directory.CreateTempSubdirectory("fresh1");
        try
        {
            var github = ItemAtReview(5, prNumber: 51);
            var git = new FakeGit { Behind = 2 };
            var processes = git.Runner();

            await new Tick(Config(tmp), github, processes, TextWriter.Null).RunAsync();

            // Rebased onto the base and pushed with a lease — never a bare --force.
            Assert.Contains(processes.Invocations, i => i.Arguments.Contains("rebase"));
            Assert.Contains(processes.Invocations, i => i.Arguments.Contains("--force-with-lease"));
            Assert.DoesNotContain(processes.Invocations,
                i => i.Arguments.Contains("--force") && !i.Arguments.Contains("--force-with-lease"));

            // …and only then merged.
            Assert.Single(github.MergedPrs);
            Assert.False(github.Issue(5).Open);
            Assert.Contains("stage/review", github.Issue(5).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_branch_that_conflicts_with_the_base_is_not_reviewed_or_merged()
    {
        var tmp = Directory.CreateTempSubdirectory("fresh2");
        try
        {
            var github = ItemAtReview(6, prNumber: 61);
            var git = new FakeGit { Behind = 3, RebaseConflicts = true };
            var processes = git.Runner();

            await new Tick(Config(tmp), github, processes, TextWriter.Null).RunAsync();

            // The rebase is aborted rather than left half-applied in the worktree.
            Assert.Contains(processes.Invocations, i => i.Arguments.Contains("--abort"));

            // Nothing merged, nothing claimed — and no credits spent reviewing a diff that
            // cannot land in the shape it was reviewed.
            Assert.Empty(github.MergedPrs);
            Assert.DoesNotContain(processes.Invocations, i => i.FileName == "claude");
            Assert.True(github.Issue(6).Open);
            Assert.DoesNotContain("stage/review", github.Issue(6).Labels);

            // The item is left retryable, and the reason is on the issue.
            Assert.Contains("status/ready", github.Issue(6).Labels);
            Assert.Contains(github.Issue(6).Comments,
                c => c.Contains("kind=stale-branch", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_base_that_moves_during_the_review_defers_the_merge()
    {
        var tmp = Directory.CreateTempSubdirectory("fresh3");
        try
        {
            var github = ItemAtReview(7, prNumber: 71);

            // Current when the review starts; three commits land on the base while it runs. What
            // was reviewed is no longer what would merge, so this tick must not merge it.
            var git = new FakeGit { Behind = 0, BehindAfterClaude = 3 };
            var processes = git.Runner();

            await new Tick(Config(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.MergedPrs);
            Assert.True(github.Issue(7).Open);
            Assert.Contains("status/ready", github.Issue(7).Labels);
            Assert.Contains(github.Issue(7).Comments,
                c => c.Contains("kind=stale-branch", StringComparison.Ordinal));

            // The branch is still brought up to date, so the next tick reviews the fresh base
            // rather than rediscovering the same drift.
            Assert.Contains(processes.Invocations, i => i.Arguments.Contains("--force-with-lease"));

            // Review is NOT recorded complete, or derivation would find nothing left to do and
            // the item would sit open forever with its PR unmerged.
            Assert.DoesNotContain("stage/review", github.Issue(7).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_merge_github_refuses_leaves_the_item_open_and_retryable()
    {
        var tmp = Directory.CreateTempSubdirectory("fresh4");
        try
        {
            var github = ItemAtReview(8, prNumber: 81);
            github.MergeRefusal = _ =>
                new InvalidOperationException("Required status check \"verify\" is expected.");
            var processes = new FakeGit { Behind = 0 }.Runner();

            await new Tick(Config(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.MergedPrs);
            Assert.True(github.Issue(8).Open);
            Assert.Contains("status/ready", github.Issue(8).Labels);
            Assert.DoesNotContain("stage/review", github.Issue(8).Labels);
            Assert.Contains(github.Issue(8).Comments,
                c => c.Contains("kind=merge-refused", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task No_branch_is_cut_when_the_base_cannot_be_fetched()
    {
        var tmp = Directory.CreateTempSubdirectory("fresh5");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Add a file", "Targets: none", "operator", 100,
                "status/ready", "kind/chore", "stage/intake");

            var processes = new FakeGit { FetchFails = true }.Runner();
            var log = new StringWriter();

            await new Tick(Config(tmp), github, processes, log).RunAsync();

            // Branching from the clone's last-seen base is exactly what the rule forbids, so the
            // worktree is not created at all and the item is left untouched for the next tick.
            Assert.DoesNotContain(processes.Invocations, i => i.Arguments.Contains("worktree"));
            Assert.Contains("status/ready", github.Issue(9).Labels);
            Assert.Empty(github.Issue(9).Comments);
            Assert.DoesNotContain("stage/plan", github.Issue(9).Labels);
            Assert.Contains("stale base", log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    /// <summary>An operator item sitting at the review stage with its PR marker recorded.</summary>
    private static InMemoryGitHubClient ItemAtReview(int number, int prNumber)
    {
        var github = new InMemoryGitHubClient();
        github.AddUser("operator", 100);
        github.AddIssue(number, "Add a file", "Targets: none", "operator", 100,
            "status/ready", "kind/chore", "stage/intake", "stage/plan", "stage/implement");
        github.Issue(number).Comments.Add(
            $"<!-- spec-runner:v1 kind=pr id=pr-{number} number={prNumber} -->");
        return github;
    }

    private static InstanceConfig Config(DirectoryInfo tmp) => new()
    {
        Slug = "op/repo",
        Path = "/clone",
        WorktreesRoot = Path.Combine(tmp.FullName, "work"),
        OperatorLogin = "operator",
        BaseBranch = "master",
        AutoMerge = true,
        SpendCap = 100,
        GitHubPatFile = "/run/secrets/pat",
        ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
        Lock = Path.Combine(tmp.FullName, ".lock"),
    };

    /// <summary>
    /// A git that actually models staleness: it answers <c>rev-list --count</c> from a counter,
    /// and a successful rebase both zeroes that counter and moves HEAD — which is what lets these
    /// tests distinguish "rebased" from "was already current".
    /// </summary>
    private sealed class FakeGit
    {
        public int Behind { get; set; }

        /// <summary>Commits that land on the base while the review runs.</summary>
        public int? BehindAfterClaude { get; set; }

        public bool RebaseConflicts { get; set; }

        public bool FetchFails { get; set; }

        private int _head;

        public RecordingProcessRunner Runner() => new() { Respond = Handle };

        private ProcessResult Handle(RecordingProcessRunner.Invocation inv)
        {
            if (inv.FileName == "claude")
            {
                if (BehindAfterClaude is { } drift)
                {
                    Behind = drift;
                }

                return new ProcessResult(0, "no blocking findings", string.Empty);
            }

            if (inv.FileName != "git" || inv.Arguments.Count == 0)
            {
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            switch (inv.Arguments[0])
            {
                case "fetch":
                    return new ProcessResult(FetchFails ? 128 : 0, string.Empty,
                        FetchFails ? "could not read from remote repository" : string.Empty);

                case "rev-list":
                    return new ProcessResult(0, Behind.ToString(), string.Empty);

                case "rev-parse":
                    return new ProcessResult(0, $"sha{_head}", string.Empty);

                case "rebase" when !inv.Arguments.Contains("--abort"):
                    if (RebaseConflicts)
                    {
                        return new ProcessResult(1, string.Empty, "CONFLICT (content): merge conflict");
                    }

                    if (Behind != 0)
                    {
                        Behind = 0;
                        _head++;      // the replay rewrote history
                    }

                    return new ProcessResult(0, string.Empty, string.Empty);

                case "status":
                    return new ProcessResult(0, string.Empty, string.Empty);   // nothing uncommitted

                default:
                    return new ProcessResult(0, string.Empty, string.Empty);
            }
        }
    }
}
