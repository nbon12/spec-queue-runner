using SpecRunner.Ports;

namespace SpecRunner.Domain;

/// <summary>How a Claude-invoking stage actually ended.</summary>
public enum StageResult
{
    /// <summary>The stage ran to completion. Only this permits advancing the item.</summary>
    Succeeded,

    /// <summary>A usage/rate limit — routine, not an error (FR-043). Revert to ready and retry later.</summary>
    RateLimited,

    /// <summary>The stage failed. The item MUST NOT advance, merge, or close on this.</summary>
    Failed,
}

/// <summary>
/// Classifies a stage's process result, and formats what actually happened for the issue.
///
/// This exists because the runner previously captured Claude's <see cref="ProcessResult"/>, logged
/// only its exit code, and then posted a hardcoded "completed; no blocking finding recorded" —
/// regardless of whether the run had succeeded, crashed, or hit a usage limit. A failed review
/// therefore merged the pull request while asserting it had passed. Nothing may claim an outcome
/// that was not determined (constitution §9).
///
/// Pure, so every branch is testable without a process.
/// </summary>
public static class StageOutcome
{
    /// <summary>Rate limiting is checked before failure: it is a retry, not a fault (FR-043).</summary>
    public static StageResult From(ProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return StageResult.Succeeded;
        }

        return RateLimitDetector.IsRateLimited(result.Combined)
            ? StageResult.RateLimited
            : StageResult.Failed;
    }

    /// <summary>
    /// Claude's own output, folded into a collapsed block so a long transcript does not bury the
    /// issue. Truncated with the elision stated — a silently cut report reads as a complete one.
    /// </summary>
    public static string Excerpt(string? output, string summary, int maxChars = 4000)
    {
        var text = (output ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "_(the run produced no output)_";
        }

        var truncated = text.Length > maxChars;
        if (truncated)
        {
            text = text[..maxChars];
        }

        // Fence at a length no inner fence can match, so output containing ``` cannot break out.
        const string fence = "````";
        var note = truncated
            ? $"\n\n_(truncated — showing the first {maxChars:N0} of {(output ?? string.Empty).Trim().Length:N0} characters)_"
            : string.Empty;

        return $"<details><summary>{summary}</summary>\n\n{fence}\n{text}\n{fence}\n{note}\n</details>";
    }
}
