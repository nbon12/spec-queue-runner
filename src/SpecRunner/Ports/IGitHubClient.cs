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
}
