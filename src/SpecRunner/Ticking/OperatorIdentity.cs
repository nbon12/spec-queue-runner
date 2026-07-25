using SpecRunner.Domain;
using SpecRunner.Ports;

namespace SpecRunner.Ticking;

/// <summary>
/// The single-operator allowlist (constitution §6, FR-005). The configured login is resolved
/// to its immutable numeric id once, and every issue/comment author is checked against that
/// number — never the login string, which can be renamed and re-registered (research R5).
/// Fails closed: if the login can't be resolved, no content is allowlisted.
/// </summary>
public sealed class OperatorIdentity
{
    private readonly long? _operatorId;

    private OperatorIdentity(long? operatorId) => _operatorId = operatorId;

    public bool Resolved => _operatorId is not null;

    public static async Task<OperatorIdentity> ResolveAsync(
        IGitHubClient github, string operatorLogin, CancellationToken ct = default)
    {
        var id = await github.ResolveUserIdAsync(operatorLogin, ct).ConfigureAwait(false);
        return new OperatorIdentity(id);
    }

    /// <summary>True only when the item's author is the resolved operator (numeric-id match).</summary>
    public bool IsOperator(WorkItem item) => _operatorId is { } id && item.AuthorId == id;

    /// <summary>True only when a numeric author id is the resolved operator (e.g. a comment author).</summary>
    public bool IsOperatorId(long authorId) => _operatorId is { } id && authorId == id;
}
