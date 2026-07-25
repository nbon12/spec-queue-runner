using SpecRunner.Adapters;
using SpecRunner.Adapters.Claude;
using SpecRunner.Ticking;

namespace SpecRunner.Cli;

/// <summary>
/// Live vertical slice of an execution stage: ensure the item's worktree exists (creating it
/// and pre-seeding trust), then run one headless Claude invocation inside it. Proves the
/// execution-stage spine — worktree lifecycle + trust pre-seed + ClaudeInvoker through the real
/// process boundary. The prompt is passed in for the demo; a real tick derives it from the stage.
///
/// Usage: spec-runner run-stage &lt;clone&gt; &lt;worktrees-root&gt; &lt;claude-config&gt; &lt;item#&gt; &lt;prompt&gt;
/// </summary>
public static class RunStageCommand
{
    public static async Task<int> RunAsync(
        string clonePath, string worktreesRoot, string claudeConfigPath, int number, string prompt)
    {
        var processes = new ProcessRunner();
        var worktrees = new WorktreeLifecycle(processes, clonePath, worktreesRoot, claudeConfigPath);

        Console.WriteLine($"ensuring worktree for item #{number} …");
        var path = await worktrees.EnsureAsync(number).ConfigureAwait(false);
        Console.WriteLine($"worktree: {path} (branch {WorktreeLifecycle.BranchFor(number)}), trust pre-seeded");

        var claude = new ClaudeInvoker(processes, permissionMode: "acceptEdits");
        Console.WriteLine($"invoking claude headlessly in the worktree …");
        var result = await claude.RunAsync(prompt, path).ConfigureAwait(false);

        Console.WriteLine($"claude exit={result.ExitCode}");
        Console.WriteLine("--- claude output (truncated) ---");
        Console.WriteLine(result.Combined.Length > 1200 ? result.Combined[..1200] + " …" : result.Combined);

        return result.ExitCode == 0 ? (int)ExitCode.Ok : (int)ExitCode.InternalError;
    }
}
