using SpecRunner.Domain;

namespace SpecRunner.Ports;

/// <summary>
/// First-party port over GitHub (constitution §5). Production implements it with Octokit;
/// Tiers 2–3 substitute an in-memory fake. The tick reads and writes the book of work only
/// through this boundary (FR-004).
/// </summary>
public interface IGitHubClient
{
    /// <summary>Resolve a login to its immutable numeric user id (research R5); null if unresolvable.</summary>
    Task<long?> ResolveUserIdAsync(string login, CancellationToken ct = default);

    /// <summary>Open issues carrying a label, lowest number first (FR-009 ordering).</summary>
    Task<IReadOnlyList<WorkItem>> ListOpenIssuesWithLabelAsync(string label, CancellationToken ct = default);

    Task<WorkItem> GetIssueAsync(int number, CancellationToken ct = default);

    Task AddCommentAsync(int number, string body, CancellationToken ct = default);

    Task AddLabelsAsync(int number, IReadOnlyList<string> labels, CancellationToken ct = default);

    Task RemoveLabelAsync(int number, string label, CancellationToken ct = default);

    /// <summary>All comment bodies on an issue — used to scan for the runner's own markers (R10).</summary>
    Task<IReadOnlyList<string>> GetCommentBodiesAsync(int number, CancellationToken ct = default);

    /// <summary>
    /// When a label was most recently applied to an issue, or null if it isn't/never was. Drives
    /// stale-reclaim (FR-044): the age of the <c>status/in-progress</c> label is how long a run has
    /// been stuck. Read from the issue's own event timeline — no runner-side state.
    /// </summary>
    Task<DateTimeOffset?> GetLabelAppliedAtAsync(int number, string label, CancellationToken ct = default);

    /// <summary>Open a PR from <paramref name="head"/> into <paramref name="baseBranch"/>.</summary>
    Task<PullRequestRef> CreatePullRequestAsync(
        string title, string body, string head, string baseBranch, CancellationToken ct = default);

    /// <summary>Merge a PR (the operator's gate is delegated to the runner when auto-merge is on, FR-033b).</summary>
    Task MergePullRequestAsync(int number, CancellationToken ct = default);

    Task CloseIssueAsync(int number, CancellationToken ct = default);

    /// <summary>File a new issue (recurrence successor, FR-042); returns its number.</summary>
    Task<int> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default);
}

/// <summary>An opened pull request: its number (for merge) and URL (for links).</summary>
public sealed record PullRequestRef(int Number, string Url);
