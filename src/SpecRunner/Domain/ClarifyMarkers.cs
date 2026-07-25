using System.Text.RegularExpressions;

namespace SpecRunner.Domain;

/// <summary>
/// Clarify consumes the markers specify materialized (FR-018/019). In the comment fallback, the
/// runner reads the outstanding <c>[NEEDS CLARIFICATION: …]</c> markers from the spec and posts
/// them as one numbered comment with recommended defaults (FR-027). Pure extraction/formatting.
/// </summary>
public static partial class ClarifyMarkers
{
    [GeneratedRegex(@"\[NEEDS CLARIFICATION:\s*(?<q>[^\]]*)\]")]
    private static partial Regex MarkerPattern();

    public static IReadOnlyList<string> Extract(string specText)
    {
        var list = new List<string>();
        foreach (Match m in MarkerPattern().Matches(specText))
        {
            list.Add(m.Groups["q"].Value.Trim());
        }

        return list;
    }

    /// <summary>The single fallback comment: numbered questions, each with a recommended default (FR-027).</summary>
    public static string QuestionsComment(int itemNumber, IReadOnlyList<string> questions, string reason)
    {
        var lines = new List<string>
        {
            IdempotencyMarker.For("questions", $"clarify-{itemNumber}"),
            $"**Live session unavailable** — {reason}",
            "",
        };

        for (var i = 0; i < questions.Count; i++)
        {
            lines.Add($"{i + 1}. {questions[i]} — *Recommended:* proceed with the most conventional choice — it is reversible by a later amendment.");
        }

        return string.Join('\n', lines);
    }
}
