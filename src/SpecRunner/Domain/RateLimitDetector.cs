using System.Text.RegularExpressions;

namespace SpecRunner.Domain;

/// <summary>
/// Usage-limit failures are routine, not errors (FR-043): on a match the item reverts to ready
/// and the tick exits 0 for a later retry. Detection is a case-insensitive match on
/// <c>rate limit</c> / <c>usage limit</c> against the full output; the corpus grows from real
/// captured failures. Pure and unit-testable.
/// </summary>
public static partial class RateLimitDetector
{
    [GeneratedRegex(@"rate\s*limit|usage\s*limit", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool IsRateLimited(string? output) =>
        !string.IsNullOrEmpty(output) && Pattern().IsMatch(output);
}
