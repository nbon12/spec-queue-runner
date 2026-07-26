using SpecRunner.Domain;
using SpecRunner.Ports;

namespace SpecRunner.Ticking;

/// <summary>
/// Creates and owns an item's worktree (FR-012/014). The first invocation an item needs triggers
/// <c>git worktree add</c>; the worktree then persists for the item's whole life and is used by
/// both the headless runs and the live session. On creation, workspace trust is pre-seeded
/// (FR-012a) so the live session never stalls on a trust dialog. Nothing ever runs in the clone.
/// </summary>
public sealed class WorktreeLifecycle(
    IProcessRunner git,
    string clonePath,
    string worktreesRoot,
    string claudeConfigPath,
    string baseBranch = "main")
{
    public string PathFor(int number) => Path.Combine(worktreesRoot, number.ToString());

    public static string BranchFor(int number) => $"work/{number}";

    /// <summary>
    /// Ensures the item's worktree exists (branching off main if new) and is trust-seeded.
    /// Idempotent: an existing worktree is left in place. Returns the worktree path.
    /// </summary>
    public async Task<string> EnsureAsync(int number, CancellationToken ct = default)
    {
        var path = PathFor(number);
        var branch = BranchFor(number);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(worktreesRoot);

            // Refresh the clone before branching. Nothing else fetches, so without this every new
            // item branches from whatever the clone held when it was first created — a base that
            // grows staler with every merge, including the runner's own. A fetch failure is not
            // fatal (offline, transient): branching from a stale base still works, so log via the
            // exit code and carry on rather than wedging the queue.
            var fetch = await git.RunAsync(
                "git", ["fetch", "origin", baseBranch], workingDirectory: clonePath, ct: ct)
                .ConfigureAwait(false);

            // Branch from the REMOTE ref when the fetch succeeded, so a stale local branch cannot
            // silently determine the base; fall back to the local ref when offline.
            var startPoint = fetch.ExitCode == 0 ? $"origin/{baseBranch}" : baseBranch;

            var result = await git.RunAsync(
                "git",
                ["worktree", "add", "-B", branch, path, startPoint],
                workingDirectory: clonePath,
                ct: ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"git worktree add failed: {result.Combined}");
            }
        }

        await SeedTrustAsync(path, ct).ConfigureAwait(false);
        return path;
    }

    private async Task SeedTrustAsync(string worktreePath, CancellationToken ct)
    {
        var existing = File.Exists(claudeConfigPath)
            ? await File.ReadAllTextAsync(claudeConfigPath, ct).ConfigureAwait(false)
            : null;

        if (WorkspaceTrust.IsTrusted(existing, worktreePath))
        {
            return;
        }

        var updated = WorkspaceTrust.Seed(existing, worktreePath);
        await File.WriteAllTextAsync(claudeConfigPath, updated, ct).ConfigureAwait(false);
    }
}
