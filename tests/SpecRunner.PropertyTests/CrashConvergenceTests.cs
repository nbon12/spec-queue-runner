using SpecRunner.Configuration;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.PropertyTests;

/// <summary>
/// Tier 3 — a tick is stateless reconciliation, so re-running it must converge, not accumulate
/// (constitution §3, FR-046). This exercises the intake path: running the tick twice against the
/// same state must leave exactly one decision comment and one set of labels — the executable form
/// of "a killed-and-re-run tick produces no duplicates."
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
}
