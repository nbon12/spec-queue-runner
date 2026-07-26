using SpecRunner.Adapters.Git;
using SpecRunner.Configuration;
using SpecRunner.Domain;
using SpecRunner.Ports;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.IntegrationTests;

/// <summary>
/// Tier 2 — the paths that turn one stage-level failure into a queue-wide outage.
///
/// These were written against a real incident. A push was rejected because the remote branch had
/// moved; the stage had already committed, so the worktree was clean; every later tick read that
/// clean worktree as "the run produced nothing" and returned without recording the stage. The item
/// re-ran the same stage every five minutes for ninety minutes, and because selection takes the
/// lowest-numbered ready item, one item per tick, everything behind it never ran at all.
///
/// Three things had to be true for that to happen, so there are three fixes and three groups of
/// tests: the push must survive a moved remote; "clean worktree" must not be read as "nothing was
/// done"; and no item may hold the queue indefinitely regardless of why it is stuck.
/// </summary>
public class StallRecoveryTests
{
    private static InstanceConfig ConfigFor(DirectoryInfo tmp) => new()
    {
        Slug = "op/repo",
        Path = "/clone",
        WorktreesRoot = Path.Combine(tmp.FullName, "work"),
        OperatorLogin = "operator",
        BaseBranch = "master",
        AutoMerge = false,
        SpendCap = 100,
        GitHubPatFile = "/run/secrets/pat",
        ClaudeConfigPath = Path.Combine(tmp.FullName, ".claude.json"),
        Lock = Path.Combine(tmp.FullName, ".lock"),
    };

    private static bool Is(RecordingProcessRunner.Invocation inv, params string[] args) =>
        inv.FileName == "git" && args.All(a => inv.Arguments.Contains(a));

    // ---- A moved remote must not strand the branch -------------------------------------------

    [Fact]
    public async Task Push_rejected_by_a_moved_remote_rebases_and_retries()
    {
        var pushes = 0;
        var processes = new RecordingProcessRunner
        {
            // The first push is rejected exactly as git rejects a non-fast-forward; the second,
            // after the rebase, succeeds.
            Respond = inv =>
            {
                if (!Is(inv, "push"))
                {
                    return new ProcessResult(0, "", "");
                }

                return ++pushes == 1
                    ? new ProcessResult(1, "", "! [rejected] work/9 -> work/9 (fetch first)")
                    : new ProcessResult(0, "", "");
            },
        };

        await new Git(processes).PushAsync("/work/9", "work/9");

        Assert.Equal(2, pushes);
        Assert.Contains(processes.Invocations, i => Is(i, "fetch", "origin", "work/9"));
        Assert.Contains(processes.Invocations, i => Is(i, "rebase", "origin/work/9"));
        Assert.DoesNotContain(processes.Invocations, i => Is(i, "rebase", "--abort"));
    }

    [Fact]
    public async Task Push_whose_rebase_conflicts_aborts_and_raises_rather_than_guessing()
    {
        var processes = new RecordingProcessRunner
        {
            Respond = inv => Is(inv, "push") || (Is(inv, "rebase") && !inv.Arguments.Contains("--abort"))
                ? new ProcessResult(1, "", "CONFLICT (content): specs/plan.md")
                : new ProcessResult(0, "", ""),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Git(processes).PushAsync("/work/9", "work/9"));

        // The worktree is left usable — a half-applied rebase would break every later tick.
        Assert.Contains(processes.Invocations, i => Is(i, "rebase", "--abort"));
    }

    // ---- A clean worktree is not proof that nothing was done -----------------------------------

    /// <summary>
    /// The incident itself: implement had already committed, so `git status --porcelain` is empty,
    /// but the commit never reached origin. The stage must resume at the push rather than conclude
    /// there was nothing to do — otherwise finished work is invisible and the item can never leave
    /// the stage, no matter how many times it is retried.
    /// </summary>
    [Fact]
    public async Task Implement_resumes_from_a_commit_whose_push_never_landed()
    {
        var tmp = Directory.CreateTempSubdirectory("stall-resume");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Add a file", "body", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");

            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                {
                    // Clean worktree: the previous run committed everything it wrote.
                    if (Is(inv, "--porcelain"))
                    {
                        return new ProcessResult(0, "", "");
                    }

                    // …and one of those commits is not on origin's copy of the branch.
                    if (Is(inv, "rev-list", "--count"))
                    {
                        return new ProcessResult(0, "1\n", "");
                    }

                    return new ProcessResult(0, inv.Arguments.Contains("HEAD") ? "abc1234" : "", "");
                },
            };

            await new Tick(ConfigFor(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Contains(processes.Invocations, i => Is(i, "push", "origin", "work/9"));
            Assert.Single(github.OpenedPrs);
            Assert.Contains("stage/implement", github.Issue(9).Labels);

            // Nothing new was written, so nothing new is committed — the resumed stage must not
            // manufacture an empty commit on top of the work it is recovering.
            Assert.DoesNotContain(processes.Invocations, i => Is(i, "commit"));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Implement_that_truly_wrote_nothing_still_reports_no_progress()
    {
        var tmp = Directory.CreateTempSubdirectory("stall-empty");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Add a file", "body", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");

            // Clean worktree AND nothing unpushed: there is genuinely no work.
            var processes = new RecordingProcessRunner
            {
                Respond = inv => Is(inv, "rev-list", "--count")
                    ? new ProcessResult(0, "0\n", "")
                    : new ProcessResult(0, "", ""),
            };

            await new Tick(ConfigFor(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.OpenedPrs);
            Assert.DoesNotContain("stage/implement", github.Issue(9).Labels);
            Assert.DoesNotContain(processes.Invocations, i => Is(i, "push"));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Re-running a recovered implement must not open a second pull request — the PR is the
    /// stage's artifact, and §3 requires a re-runnable stage to guard its artifact.
    /// </summary>
    [Fact]
    public async Task Implement_does_not_open_a_second_pull_request_when_one_is_recorded()
    {
        var tmp = Directory.CreateTempSubdirectory("stall-dup-pr");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Add a file", "body", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");
            github.Issue(9).Comments.Add("<!-- spec-runner:v1 kind=pr id=pr-9 number=42 -->");

            var processes = new RecordingProcessRunner
            {
                Respond = inv => Is(inv, "rev-list", "--count")
                    ? new ProcessResult(0, "1\n", "")
                    : new ProcessResult(0, "", ""),
            };

            await new Tick(ConfigFor(tmp), github, processes, TextWriter.Null).RunAsync();

            Assert.Empty(github.OpenedPrs);                            // #42 stands
            Assert.Contains("stage/implement", github.Issue(9).Labels); // and the stage completes
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    // ---- No item may hold the queue indefinitely -----------------------------------------------

    [Fact]
    public void Attempts_are_counted_per_stage_not_per_item()
    {
        var bodies = new List<string>
        {
            StallGuard.Marker(9, Stage.Implement, 1),
            StallGuard.Marker(9, Stage.Implement, 2),
            StallGuard.Marker(9, Stage.Review, 1),
            "an ordinary comment",
        };

        Assert.Equal(2, StallGuard.AttemptsAt(bodies, 9, Stage.Implement));
        Assert.Equal(1, StallGuard.AttemptsAt(bodies, 9, Stage.Review));
        Assert.Equal(0, StallGuard.AttemptsAt(bodies, 9, Stage.Plan));
        Assert.Equal(0, StallGuard.AttemptsAt(bodies, 10, Stage.Implement)); // another item's markers
    }

    /// <summary>
    /// The queue-level guarantee. An item that can never finish is parked after a bounded number
    /// of tries and leaves the ready set, so the item behind it runs. Without this, one stuck item
    /// starves every higher-numbered item for as long as the runner runs.
    /// </summary>
    [Fact]
    public async Task An_item_that_never_progresses_is_parked_and_stops_blocking_the_queue()
    {
        var tmp = Directory.CreateTempSubdirectory("stall-park");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Stuck", "body", "operator", 100,
                "status/ready", "kind/chore", "stage/intake", "stage/plan");
            github.AddIssue(10, "Behind it", "body", "operator", 100, "status/ready");

            // Implement can never finish: clean worktree, nothing unpushed, forever.
            var processes = new RecordingProcessRunner
            {
                Respond = inv => Is(inv, "rev-list", "--count")
                    ? new ProcessResult(0, "0\n", "")
                    : new ProcessResult(0, "", ""),
            };

            var config = ConfigFor(tmp);
            for (var i = 0; i < StallGuard.MaxAttempts; i++)
            {
                Assert.Contains("status/ready", github.Issue(9).Labels); // still first in line
                await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            }

            // Parked: out of the ready set, still open, nothing thrown away.
            Assert.DoesNotContain("status/ready", github.Issue(9).Labels);
            Assert.Contains("status/waiting", github.Issue(9).Labels);
            Assert.True(github.Issue(9).Open);
            Assert.Contains(github.Issue(9).Comments,
                c => c.Contains("Parked at Implement", StringComparison.Ordinal));

            // And the item behind it — untouched until now — is what the next tick picks up.
            Assert.DoesNotContain("kind/chore", github.Issue(10).Labels);
            await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            Assert.Contains("kind/chore", github.Issue(10).Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A stage that hands the item to the operator on purpose — clarify, which moves it to
    /// waiting or to a live session — also reports "not completed". Counting that as a stall would
    /// spend the item's budget on the runner working exactly as designed.
    /// </summary>
    [Fact]
    public async Task A_stage_that_parks_the_item_deliberately_is_not_counted_as_a_stall()
    {
        var tmp = Directory.CreateTempSubdirectory("stall-clarify");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            var specDir = Path.Combine(worktreesRoot, "9", "specs", "001-widget");
            Directory.CreateDirectory(specDir);
            File.WriteAllText(Path.Combine(specDir, "spec.md"),
                "# Widget\n\n- [NEEDS CLARIFICATION: which colour?]\n");

            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(9, "Amend widget", "body", "operator", 100,
                "status/ready", "kind/amendment", "stage/intake", "stage/specify");

            var processes = new RecordingProcessRunner
            {
                Respond = inv => Is(inv, "diff") || Is(inv, "ls-files")
                    ? new ProcessResult(0, "specs/001-widget/spec.md\n", "")
                    : new ProcessResult(0, "", ""),
            };

            var config = ConfigFor(tmp) with { WakingHours = "08:00-23:00" };
            await new Tick(config, github, processes, TextWriter.Null,
                new InMemoryClaudeSessionStore(), () => new TimeOnly(3, 0)).RunAsync();

            // It went to waiting because clarify sent it there, not because it stalled.
            Assert.Contains("status/waiting", github.Issue(9).Labels);
            Assert.DoesNotContain(github.Issue(9).Comments,
                c => c.Contains("kind=no-progress", StringComparison.Ordinal));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
