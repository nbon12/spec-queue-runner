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

    /// <summary>The <c>Targets:</c> line, if present (issue-conventions.md). Empty when none.</summary>
    public IReadOnlyList<string> Targets
    {
        get
        {
            foreach (var line in Body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Targets:", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = trimmed["Targets:".Length..].Trim();
                    if (rest.Length == 0 || rest.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        return [];
                    }

                    return rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
            }

            return [];
        }
    }
}
