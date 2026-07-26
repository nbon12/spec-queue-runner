namespace SpecRunner.Domain;

/// <summary>
/// A projection of one GitHub issue — the queue unit. Built fresh each tick, never cached
/// across ticks (the tick is stateless, §3). Author identity is the numeric id, not the login
/// (research R5): logins can be renamed and re-registered, so string comparison is an
/// impersonation path.
/// </summary>
public sealed record WorkItem(
    int Number,
    string Title,
    string Body,
    string AuthorLogin,
    long AuthorId,
    IReadOnlyList<string> Labels)
{
    public bool HasLabel(string label) => Labels.Contains(label);
}
