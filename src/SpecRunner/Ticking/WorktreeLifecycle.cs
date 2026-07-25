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
    string claudeConfigPath)
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
            // Branch off main if new; -B is safe if the branch already exists from a prior life.
            var result = await git.RunAsync(
                "git",
                ["worktree", "add", "-B", branch, path, "main"],
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
