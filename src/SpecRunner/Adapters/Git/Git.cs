using SpecRunner.Ports;

namespace SpecRunner.Adapters.Git;

/// <summary>Git operations the tick needs beyond worktree management, through the process boundary.</summary>
public sealed class Git(IProcessRunner processes)
{
    public async Task<bool> HasChangesAsync(string worktree, CancellationToken ct = default)
    {
        var r = await processes.RunAsync("git", ["status", "--porcelain"], worktree, ct: ct)
            .ConfigureAwait(false);
        return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.Stdout);
    }

    public async Task CommitAllAsync(string worktree, string message, CancellationToken ct = default)
    {
        await Run(worktree, ct, "add", "-A").ConfigureAwait(false);
        await Run(worktree, ct, "commit", "-m", message).ConfigureAwait(false);
    }

    public async Task PushAsync(string worktree, string branch, CancellationToken ct = default) =>
        await Run(worktree, ct, "push", "-u", "origin", branch).ConfigureAwait(false);

    public async Task<string> HeadShaAsync(string worktree, CancellationToken ct = default)
    {
        var r = await processes.RunAsync("git", ["rev-parse", "HEAD"], worktree, ct: ct)
            .ConfigureAwait(false);
        return r.Stdout.Trim();
    }

    /// <summary>
    /// Paths tracked on <paramref name="branch"/> (recursive). Used to answer "is this spec on
    /// main yet?" for held-gating (FR-010) without checking anything out.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListPathsAsync(
        string repo, string branch, CancellationToken ct = default)
    {
        var r = await processes.RunAsync("git", ["ls-tree", "-r", "--name-only", branch], repo, ct: ct)
            .ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            return [];
        }

        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task Run(string worktree, CancellationToken ct, params string[] args)
    {
        var r = await processes.RunAsync("git", args, worktree, ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.Combined}");
        }
    }
}
