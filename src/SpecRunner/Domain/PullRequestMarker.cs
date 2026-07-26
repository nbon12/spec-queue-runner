using SpecRunner.Ports;

namespace SpecRunner.Domain;

/// <summary>
/// The <c>kind=pr</c> comment marker: the only record that ties a stateless later tick back to the
/// pull request implement opened (contracts/issue-conventions.md). It carries the PR's identity as
/// marker fields rather than only in prose, so review can locate the PR and name it to the reviewer.
///
/// A marker written before <c>url=</c> existed still parses — the URL falls back to the canonical
/// GitHub path for the instance's slug, so old items keep reviewing.
/// </summary>
public static class PullRequestMarker
{
    private const string Prefix = "kind=pr id=pr-";

    public static string For(int issueNumber, int prNumber, string url) =>
        $"<!-- spec-runner:v1 {Prefix}{issueNumber} number={prNumber} url={url} -->";

    /// <summary>The pull request recorded on this item, or null if no marker names one.</summary>
    public static PullRequestRef? Find(IEnumerable<string> commentBodies, string slug)
    {
        foreach (var body in commentBodies)
        {
            var start = body.IndexOf(Prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            // Bound the scan to the marker itself: prose after `-->` is a comment body, not fields.
            var close = body.IndexOf("-->", start, StringComparison.Ordinal);
            var marker = close < 0 ? body[start..] : body[start..close];

            // A marker whose number is missing or unparseable is skipped whole rather than
            // half-read — a review pointed at the wrong PR is worse than one that waits a tick.
            if (!int.TryParse(Field(marker, "number="), out var number) || number <= 0)
            {
                continue;
            }

            var url = Field(marker, "url=");
            return new PullRequestRef(
                number,
                string.IsNullOrEmpty(url) ? $"https://github.com/{slug}/pull/{number}" : url);
        }

        return null;
    }

    private static string? Field(string marker, string name)
    {
        var at = marker.IndexOf(name, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + name.Length;
        var end = start;
        while (end < marker.Length && !char.IsWhiteSpace(marker[end]))
        {
            end++;
        }

        return end > start ? marker[start..end] : null;
    }
}
