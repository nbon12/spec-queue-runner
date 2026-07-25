using SpecRunner.Ports;

namespace SpecRunner.Adapters.Claude;

/// <summary>
/// Headless Claude Code invocation (contracts/claude-invocation.md): `claude -p &lt;prompt&gt;
/// --permission-mode &lt;configured&gt;`, run with the item's worktree as the working directory
/// (FR-012), through the process boundary. The prompt is a single argument via ArgumentList
/// (§4). The environment is explicit — the runner never inherits a shell alias carrying
/// `--dangerously-skip-permissions` (research R6).
/// </summary>
public sealed class ClaudeInvoker(IProcessRunner processes, string permissionMode)
{
    // T039a proved a stage prompt like "/speckit.plan" dispatches under -p with $ARGUMENTS.
    public async Task<ProcessResult> RunAsync(
        string prompt,
        string worktreePath,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-p", prompt,
            "--permission-mode", permissionMode,
            "--output-format", "json",
        };

        // Inherit the container's already-clean environment (the image guarantees
        // ANTHROPIC_API_KEY / ANTHROPIC_BASE_URL are unset — setting them to empty would make
        // them *present-but-empty*, which outranks and breaks the subscription OAuth path).
        // Process invocation does not inherit shell aliases, so R6's alias concern is moot here;
        // what matters is not injecting a bad env, so we inject none.
        return await processes.RunAsync("claude", args, worktreePath, environment: null, ct)
            .ConfigureAwait(false);
    }
}
