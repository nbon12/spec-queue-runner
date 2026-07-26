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

    /// <summary>
    /// Push the item's branch, recovering from the one rejection that is expected in normal
    /// operation: the remote copy moved while the runner held the worktree (an operator pushed a
    /// fix, a rebase landed). A plain push throws there, and because the stage has already
    /// committed, the item is left committed-but-unpushed — a state no later tick could leave,
    /// since a clean worktree reads as "nothing to do". So rebase onto the remote and retry once.
    ///
    /// Only a fast-forward rejection is recoverable this way. A conflicting rebase is aborted and
    /// raised: the worktree is left exactly as it was, for a human to resolve.
    /// </summary>
    public async Task PushAsync(string worktree, string branch, CancellationToken ct = default)
    {
        var first = await processes.RunAsync("git", ["push", "-u", "origin", branch], worktree, ct: ct)
            .ConfigureAwait(false);
        if (first.ExitCode == 0)
        {
            return;
        }

        await Run(worktree, ct, "fetch", "origin", branch).ConfigureAwait(false);

        var rebase = await processes.RunAsync("git", ["rebase", $"origin/{branch}"], worktree, ct: ct)
            .ConfigureAwait(false);
        if (rebase.ExitCode != 0)
        {
            // Leave no half-applied rebase behind; the next tick must find a usable worktree.
            await processes.RunAsync("git", ["rebase", "--abort"], worktree, ct: ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"git push origin {branch} was rejected and the branch could not be rebased onto " +
                $"origin/{branch}: {rebase.Combined}");
        }

        await Run(worktree, ct, "push", "-u", "origin", branch).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits on HEAD that origin's copy of <paramref name="branch"/> does not have. A positive
    /// count with a clean working tree is the "committed, but the push did not land" state: real
    /// work that exists only in the worktree, which a check for uncommitted changes cannot see.
    /// A branch with no remote counterpart yet counts everything since the base as unpushed.
    /// </summary>
    public async Task<int> UnpushedCommitsAsync(
        string worktree, string branch, string baseBranch, CancellationToken ct = default)
    {
        // Refresh the remote-tracking ref. A branch never pushed simply fails here, which is the
        // signal to compare against the base instead.
        await processes.RunAsync("git", ["fetch", "origin", branch], worktree, ct: ct)
            .ConfigureAwait(false);

        var known = await processes.RunAsync(
            "git", ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"],
            worktree, ct: ct).ConfigureAwait(false);
        var upstream = known.ExitCode == 0 ? $"origin/{branch}" : $"origin/{baseBranch}";

        var count = await processes.RunAsync(
            "git", ["rev-list", "--count", $"{upstream}..HEAD"], worktree, ct: ct).ConfigureAwait(false);
        return count.ExitCode == 0 && int.TryParse(count.Stdout.Trim(), out var n) ? n : 0;
    }

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
