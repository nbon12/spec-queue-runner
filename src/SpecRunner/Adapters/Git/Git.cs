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
    /// The spec directory this item's branch adds relative to the base — the item's own spec,
    /// discovered rather than guessed (constitution §3, v6.0.0). Null means the branch has not
    /// created one, which is exactly the "specify has not run yet" case.
    ///
    /// Scanning <c>specs/</c> and taking the highest-sorted entry — the previous approach —
    /// returns some other item's spec the moment more than one exists.
    /// </summary>
    public async Task<string?> SpecDirOnBranchAsync(
        string worktree, string baseBranch, CancellationToken ct = default)
    {
        // Three-dot: what this branch added since it diverged, ignoring base-branch churn.
        var committed = await processes.RunAsync(
            "git", ["diff", "--name-only", $"origin/{baseBranch}...HEAD", "--", "specs/"],
            worktree, ct: ct).ConfigureAwait(false);

        // Untracked too: specify may have written the directory in this very tick, before the
        // commit that follows it.
        var untracked = await processes.RunAsync(
            "git", ["ls-files", "--others", "--exclude-standard", "--", "specs/"],
            worktree, ct: ct).ConfigureAwait(false);

        return FirstSpecDir(committed.Stdout) ?? FirstSpecDir(untracked.Stdout);
    }

    private static string? FirstSpecDir(string? pathList)
    {
        foreach (var line in (pathList ?? string.Empty)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('/');
            if (parts.Length >= 2 && parts[0] == "specs" && parts[1].Length > 0)
            {
                return $"specs/{parts[1]}";
            }
        }

        return null;
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
