namespace SpecRunner.Domain;

/// <summary>
/// Audit selection (FR-038/041): pick the spec least recently audited, determined from prior
/// audit history. One spec per run; coverage comes from cadence, not breadth. An audit modifies
/// nothing (FR-039) and files follow-ups unlabelled as to kind (FR-040). Pure selection.
/// </summary>
public static class AuditSelection
{
    /// <summary>
    /// The spec to audit: any never-audited spec first (by name), otherwise the one whose last
    /// audit is oldest. Returns null when there are no specs.
    /// </summary>
    public static string? LeastRecentlyAudited(
        IReadOnlyList<string> specs,
        IReadOnlyDictionary<string, DateTimeOffset> lastAudited)
    {
        if (specs.Count == 0)
        {
            return null;
        }

        var neverAudited = specs.Where(s => !lastAudited.ContainsKey(s)).OrderBy(s => s).ToList();
        if (neverAudited.Count > 0)
        {
            return neverAudited[0];
        }

        return specs.OrderBy(s => lastAudited[s]).ThenBy(s => s).First();
    }
}
