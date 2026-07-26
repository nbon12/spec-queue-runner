using SpecRunner.Configuration;
using SpecRunner.TestKit;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.IntegrationTests;

/// <summary>
/// Tier 2 — the live channel's decision logic and recorded side effects, driven through the real
/// Tick with an in-memory GitHub, a scripted process runner (tmux/git/claude never really run), a
/// fake conversation-id store, and a fixed clock. The live Remote Control handshake itself is
/// verified by hand (probe §5/§7); these lock down everything around it: waking-hours gating,
/// spawn + kickoff + id recording, reap-on-resolution, and resume-on-death.
/// </summary>
public class LiveChannelTests
{
    private static void SeedSpec(string worktreesRoot, int number, bool withMarker)
    {
        var specDir = Path.Combine(worktreesRoot, number.ToString(), "specs", "001-widget");
        Directory.CreateDirectory(specDir);
        var body = withMarker
            ? "# Widget\n\n- [NEEDS CLARIFICATION: which colour?]\n"
            : "# Widget\n\nAll resolved.\n";
        File.WriteAllText(Path.Combine(specDir, "spec.md"), body);
    }

    private static InstanceConfig ConfigFor(string tmp, string worktreesRoot) => new()
    {
        Slug = "op/repo", Path = "/clone", WorktreesRoot = worktreesRoot,
        OperatorLogin = "operator", BaseBranch = "master",
        WakingHours = "08:00-23:00",
        GitHubPatFile = "/run/secrets/pat",
        ClaudeConfigPath = Path.Combine(tmp, ".claude.json"),
        Lock = Path.Combine(tmp, ".lock"),
    };

    /// <summary>
    /// A process runner that reports the seeded spec directory as this branch's addition, which
    /// is how the runner now locates an item's spec (constitution §3, v6.0.0).
    /// </summary>
    private static RecordingProcessRunner RunnerWithSpecOnBranch() => new()
    {
        Respond = inv =>
            inv.FileName == "git" && (inv.Arguments.Contains("diff") || inv.Arguments.Contains("ls-files"))
                ? new SpecRunner.Ports.ProcessResult(0, "specs/001-widget/spec.md\n", "")
                : new SpecRunner.Ports.ProcessResult(0, "", ""),
    };

    [Fact]
    public async Task Clarify_block_in_waking_hours_spawns_a_live_session()
    {
        var tmp = Directory.CreateTempSubdirectory("live1");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            SeedSpec(worktreesRoot, 5, withMarker: true);

            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(5, "Amend widget", "Targets: none", "operator", 100,
                "status/ready", "kind/amendment", "stage/intake", "stage/specify");

            var worktreePath = Path.Combine(worktreesRoot, "5");
            var sessions = new InMemoryClaudeSessionStore();
            sessions.Set(worktreePath, "conv-abc");
            var processes = new RecordingProcessRunner();

            var tick = new Tick(ConfigFor(tmp.FullName, worktreesRoot), github, processes,
                TextWriter.Null, sessions, () => new TimeOnly(12, 0));
            await tick.RunAsync();

            var issue = github.Issue(5);
            Assert.Contains("status/live", issue.Labels);
            Assert.DoesNotContain("status/ready", issue.Labels);
            Assert.Contains(issue.Comments, c => c.Contains("Live session: conv-abc", StringComparison.Ordinal));

            // The tmux session was created and the kickoff was actually delivered (not blind).
            Assert.Contains(processes.Invocations, i =>
                i.FileName == "tmux" && i.Arguments.Contains("new-session") && i.Arguments.Contains("spec-runner-5"));
            Assert.Contains(processes.Invocations, i =>
                i.FileName == "tmux" && i.Arguments.Any(a => a.Contains("resolving the open questions", StringComparison.Ordinal)));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Clarify_block_outside_waking_hours_uses_the_comment_fallback()
    {
        var tmp = Directory.CreateTempSubdirectory("live2");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            SeedSpec(worktreesRoot, 5, withMarker: true);

            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(5, "Amend widget", "Targets: none", "operator", 100,
                "status/ready", "kind/amendment", "stage/intake", "stage/specify");

            var processes = RunnerWithSpecOnBranch();
            var tick = new Tick(ConfigFor(tmp.FullName, worktreesRoot), github, processes,
                TextWriter.Null, new InMemoryClaudeSessionStore(), () => new TimeOnly(3, 0)); // 3am
            await tick.RunAsync();

            var issue = github.Issue(5);
            Assert.Contains("status/waiting", issue.Labels);
            Assert.DoesNotContain("status/live", issue.Labels);
            Assert.DoesNotContain(processes.Invocations, i => i.FileName == "tmux" && i.Arguments.Contains("new-session"));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Live_sweep_reaps_and_re_readies_a_resolved_item()
    {
        var tmp = Directory.CreateTempSubdirectory("live3");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            SeedSpec(worktreesRoot, 6, withMarker: false); // markers cleared ⇒ resolved

            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(6, "Amend widget", "Targets: none", "operator", 100,
                "status/live", "kind/amendment", "stage/clarify");
            github.Issue(6).Comments.Add("<!-- spec-runner:v1 kind=session id=live-6 -->\nLive session: conv-xyz");

            var processes = RunnerWithSpecOnBranch();
            var tick = new Tick(ConfigFor(tmp.FullName, worktreesRoot), github, processes,
                TextWriter.Null, new InMemoryClaudeSessionStore(), () => new TimeOnly(12, 0));
            await tick.RunAsync();

            var issue = github.Issue(6);
            Assert.DoesNotContain("status/live", issue.Labels);
            Assert.Contains("status/ready", issue.Labels);
            Assert.Contains(issue.Comments, c => c.Contains("kind=live-resolved", StringComparison.Ordinal));
            Assert.Contains(processes.Invocations, i => i.FileName == "tmux" && i.Arguments.Contains("kill-session"));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Live_sweep_resolves_when_the_operator_replies_by_comment()
    {
        var tmp = Directory.CreateTempSubdirectory("live5");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            SeedSpec(worktreesRoot, 8, withMarker: true); // worktree still shows the marker...

            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(8, "Amend widget", "Targets: none", "operator", 100,
                "status/live", "kind/amendment", "stage/clarify");
            github.Issue(8).Comments.Add("<!-- spec-runner:v1 kind=session id=live-8 -->\nLive session: conv-xyz");
            github.Issue(8).Authored.Add(new SpecRunner.Ports.IssueComment(0,
                "<!-- spec-runner:v1 kind=session id=live-8 -->\nLive session: conv-xyz"));
            // ...but the operator answered the block directly in a comment (FR-024).
            github.AddComment(8, 100, "Use the blue variant, ship it.");

            var processes = RunnerWithSpecOnBranch();
            var tick = new Tick(ConfigFor(tmp.FullName, worktreesRoot), github, processes,
                TextWriter.Null, new InMemoryClaudeSessionStore(), () => new TimeOnly(12, 0));
            await tick.RunAsync();

            var issue = github.Issue(8);
            Assert.DoesNotContain("status/live", issue.Labels);   // redundant session closed
            Assert.Contains("status/ready", issue.Labels);
            Assert.Contains(processes.Invocations, i => i.FileName == "tmux" && i.Arguments.Contains("kill-session"));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Live_sweep_resumes_a_dead_session_by_conversation_id()
    {
        var tmp = Directory.CreateTempSubdirectory("live4");
        try
        {
            var worktreesRoot = Path.Combine(tmp.FullName, "work");
            SeedSpec(worktreesRoot, 7, withMarker: true); // still blocked

            var github = new InMemoryGitHubClient();
            github.AddUser("operator", 100);
            github.AddIssue(7, "Amend widget", "Targets: none", "operator", 100,
                "status/live", "kind/amendment", "stage/clarify");
            github.Issue(7).Comments.Add("<!-- spec-runner:v1 kind=session id=live-7 -->\nLive session: conv-xyz");

            // tmux has-session reports the session is gone (exit 1); everything else succeeds.
            var processes = new RecordingProcessRunner
            {
                Respond = inv =>
                    inv.FileName == "tmux" && inv.Arguments.Contains("has-session")
                        ? new SpecRunner.Ports.ProcessResult(1, "", "")
                        : new SpecRunner.Ports.ProcessResult(0, "", ""),
            };
            var tick = new Tick(ConfigFor(tmp.FullName, worktreesRoot), github, processes,
                TextWriter.Null, new InMemoryClaudeSessionStore(), () => new TimeOnly(12, 0));
            await tick.RunAsync();

            var issue = github.Issue(7);
            Assert.Contains("status/live", issue.Labels); // still blocked, still live
            Assert.Contains(processes.Invocations, i =>
                i.FileName == "tmux" && i.Arguments.Any(a => a.Contains("claude --resume conv-xyz", StringComparison.Ordinal)));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
