namespace SpecRunner.Domain;

/// <summary>
/// Everything the review stage is <em>told</em> about what it is reviewing (R17). The prompt file
/// supplies the instructions; this supplies their referents — "this pull request", "this item's
/// spec.md", "the diff" — which the reviewer would otherwise have to guess from a worktree.
///
/// Assembled fresh each tick from GitHub, the config, and the item's branch; never persisted
/// (§3, per-iteration statelessness). <see cref="IssueTitle"/> and <see cref="IssueBody"/> are
/// operator-authored content and are rendered as data, after the instructions (§6, FR-034d).
/// </summary>
public sealed record ReviewContext(
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
    int PullRequestNumber,
    string PullRequestUrl,
    string BaseBranch,
    string HeadBranch,
    string? SpecDir,
    string? CoverageManifest)
{
    /// <summary>The "before" side of the diff — the remote-tracking ref, not the local branch,
    /// which the item's worktree does not necessarily have checked out anywhere.</summary>
    public string BaseRef => $"origin/{BaseBranch}";
}
