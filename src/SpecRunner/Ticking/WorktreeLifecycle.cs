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
    /// Ensures the item's worktree exists (branching off the LATEST base if new) and is
    /// trust-seeded. Idempotent: an existing worktree is left in place.
    ///
    /// Returns the worktree path, or null when a new worktree could not be branched off the
    /// latest base because the fetch failed. Null is not an error to report on the issue — it is
    /// "not now": the item is left exactly as it was and a later tick, with the network back,
    /// starts it on a base that is actually current (CLAUDE.md, "branch off the latest base").
    /// </summary>
    public async Task<string?> EnsureAsync(int number, CancellationToken ct = default)
    {
        var path = PathFor(number);
        var branch = BranchFor(number);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(worktreesRoot);

            // Refresh the clone before branching. Nothing else fetches, so without this every new
            // item branches from whatever the clone held when it was first created — a base that
            // grows staler with every merge, including the runner's own.
            var fetch = await git.RunAsync(
                "git", ["fetch", "origin", baseBranch], workingDirectory: clonePath, ct: ct)
                .ConfigureAwait(false);

            // A failed fetch used to fall back to the clone's local ref. That silently produced
            // exactly the thing the rule forbids — a branch cut from a base of unknown age — and
            // the staleness then travelled all the way to the merge. Waiting a tick is cheap;
            // an item built on a base nobody can name is not.
            if (fetch.ExitCode != 0)
            {
                return null;
            }

            // Branch from the REMOTE ref, never the local one: a stale local branch must not be
            // able to determine the base of new work.
            var startPoint = $"origin/{baseBranch}";

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
