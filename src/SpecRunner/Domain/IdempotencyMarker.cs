using System.Security.Cryptography;
using System.Text;

namespace SpecRunner.Domain;

/// <summary>
/// Every runner-authored comment carries a stable identity marker (research R10). Before posting,
/// the runner scans existing comments for the id and skips if present, so a tick killed and
/// re-run never duplicates a comment — the mechanism behind the crash-convergence invariant (§3).
/// The marker also lets the runner recognise its own output vs. operator input (FR-005).
/// </summary>
public static class IdempotencyMarker
{
    /// <summary>The HTML marker line for a comment of a given kind and stable id.</summary>
    public static string For(string kind, string id) =>
        $"<!-- spec-runner:v1 kind={kind} id={id} -->";

    /// <summary>A stable id from the parts that identify a decision (sha256 prefix, hex).</summary>
    public static string StableId(params string[] parts)
    {
        var joined = string.Concat(parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>Whether any of <paramref name="commentBodies"/> already carries this marker.</summary>
    public static bool AlreadyPresent(IEnumerable<string> commentBodies, string kind, string id)
    {
        var marker = For(kind, id);
        return commentBodies.Any(b => b.Contains(marker, StringComparison.Ordinal));
    }
}
