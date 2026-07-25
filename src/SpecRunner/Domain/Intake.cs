namespace SpecRunner.Domain;

/// <summary>The result of intake classification (FR-016): the inferred kind plus the reasoning
/// that becomes the decision comment.</summary>
public sealed record Classification(Kind Kind, string Reasoning);

/// <summary>
/// Intake infers kind from the issue rather than asking (FR-016). The production intake uses
/// Claude for this; this deterministic heuristic is the demo's stand-in and is pure/testable.
/// Kind classification is trivially reversible (relabel), so intake decides and reports.
/// </summary>
public static class Intake
{
    public static Classification Classify(WorkItem item)
    {
        var text = $"{item.Title}\n{item.Body}".ToLowerInvariant();

        // An explicit spec target that already exists reads as an amendment; a new target reads
        // as a feature. The demo heuristic keys off the words a phone-filed issue tends to use.
        if (item.Targets.Count > 0)
        {
            return text.Contains("amend") || text.Contains("change the spec")
                ? new Classification(Kind.Amendment, "Targets a spec and speaks of amending it.")
                : new Classification(Kind.Feature, "Names target specs; reads as new behaviour.");
        }

        if (text.Contains("investigate") || text.Contains("spike") || text.Contains("explore"))
        {
            return new Classification(Kind.Spike, "Asks to investigate/explore, not to build.");
        }

        if (text.Contains("audit") || text.Contains("compare the spec"))
        {
            return new Classification(Kind.Audit, "Asks to audit spec against code.");
        }

        // No target spec, imperative code change → chore (intake → plan → implement → review).
        return new Classification(Kind.Chore, "No target spec; a self-contained code change.");
    }

    /// <summary>The <c>kind/*</c> label for a classification.</summary>
    public static string LabelFor(Kind kind) => "kind/" + kind.ToString().ToLowerInvariant();
}
