using SpecRunner.Configuration;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.PropertyTests;

/// <summary>
/// Tier 3 — a tick is stateless reconciliation, so re-running it must converge, not accumulate
/// (constitution §3, FR-046).
///
/// Under v6.0.0 the convergence argument changed shape. Completion is recorded in a label AFTER
/// the work is done, so the dangerous window is a crash between the two: the stage re-runs, and
/// convergence now depends on that re-run being harmless rather than on the filesystem being
/// self-describing. These pin both halves — that repeated ticks do not accumulate side effects,
/// and that the one stage whose re-run WOULD duplicate its artifact is guarded.
/// </summary>
public class CrashConvergenceTests
{
    private static InstanceConfig Config(string dir) => new()
    {
        Slug = "op/repo",
        Path = "/clone",
        WorktreesRoot = dir,
        OperatorLogin = "operator",
        GitHubPatFile = "/run/secrets/pat",
        ClaudeConfigPath = Path.Combine(dir, ".claude.json"),
        Lock = Path.Combine(dir, ".lock"),
    };

    [Fact]
    public async Task Intake_is_idempotent_across_reruns()
    {
        var tmp = Directory.CreateTempSubdirectory("converge");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            // A fresh, unclassified operator item — the tick's first action is intake.
            github.AddIssue(7, "Add a file", "Targets: none", "operator", 100, "status/ready");

            var config = Config(tmp.FullName);
            var processes = new RecordingProcessRunner();

            // Run to quiescence: three ticks. Intake happens once; further ticks must be no-ops
            // on the comment/label state (they'd advance stage in a full build, but must never
            // re-post the intake decision).
            for (var i = 0; i < 3; i++)
            {
                await new Tick(config, github, processes, TextWriter.Null).RunAsync();
            }

            var issue = github.Issue(7);
            var decisionComments = issue.Comments.Count(c => c.Contains("kind=decision", StringComparison.Ordinal));

            Assert.Equal(1, decisionComments);                       // exactly one decision, not three
            Assert.Single(issue.Labels.Where(l => l == "kind/chore")); // no duplicate kind label
            Assert.Contains("stage/intake", issue.Labels);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_crash_between_doing_specify_and_recording_it_does_not_allocate_a_second_spec()
    {
        // The window v6.0.0 opens: specify ran and its directory exists on the branch, but the
        // tick died before writing stage/specify. The next tick must recognise the existing work
        // and record it — not run /speckit.specify again and allocate specs/00N+1 alongside it.
        var tmp = Directory.CreateTempSubdirectory("converge-specify");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(7, "Build a widget", "", "operator", 100,
                "status/ready", "kind/feature", "stage/intake");   // specify NOT recorded

            var config = Config(tmp.FullName);

            // git reports a spec directory already added by this branch — i.e. specify's work
            // survived the crash, as committing before labelling guarantees.
            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                    inv.FileName == "git" && (inv.Arguments.Contains("diff") || inv.Arguments.Contains("ls-files"))
                        ? new SpecRunner.Ports.ProcessResult(0, "specs/002-widget/spec.md\n", "")
                        : new SpecRunner.Ports.ProcessResult(0, "", ""),
            };

            await new Tick(config, github, processes, TextWriter.Null).RunAsync();

            // Recorded complete without re-invoking the allocating command.
            Assert.Contains("stage/specify", github.Issue(7).Labels);
            Assert.DoesNotContain(processes.Invocations,
                i => i.FileName == "claude" && i.Arguments.Any(a => a.Contains("/speckit.specify", StringComparison.Ordinal)));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Repeated_ticks_advance_one_stage_at_a_time_and_never_skip_one()
    {
        // Label-derived position must be monotonic: each tick completes exactly one stage of the
        // kind's sequence, in order, with no gaps — the executable form of "first stage with no
        // completion label".
        var tmp = Directory.CreateTempSubdirectory("converge-order");
        try
        {
            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(7, "Add a file", "", "operator", 100, "status/ready");

            var config = Config(tmp.FullName);
            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                {
                    var isStatus = inv.FileName == "git" && inv.Arguments.Contains("--porcelain");
                    return new SpecRunner.Ports.ProcessResult(
                        0, isStatus ? "?? f.txt" : (inv.Arguments.Contains("HEAD") ? "abc1234" : ""), "");
                },
            };

            // intake -> plan -> implement -> review, one per tick.
            string[] expected = ["stage/intake", "stage/plan", "stage/implement", "stage/review"];
            for (var i = 0; i < expected.Length; i++)
            {
                await new Tick(config, github, processes, TextWriter.Null).RunAsync();
                Assert.Contains(expected[i], github.Issue(7).Labels);
                foreach (var later in expected.Skip(i + 1))
                {
                    Assert.DoesNotContain(later, github.Issue(7).Labels);   // never runs ahead
                }
            }
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
