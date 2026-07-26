namespace SpecRunner.Domain;

/// <summary>
/// Pipeline position, derived from the item's <c>stage/*</c> labels (constitution §3, v6.0.0).
///
/// This replaced filesystem-predicate derivation, which could not work: knowing whether a stage
/// was done required knowing WHICH spec directory belonged to the item, and nothing recorded
/// that. The old lookup guessed (highest-sorted directory under <c>specs/</c>), so a second spec
/// directory silently captured every feature/amendment item — they read another spec's artifacts
/// as their own.
///
/// Labels are append-only and one per completed stage, so the record is also the audit trail:
/// the issue shows what has been done, in order, without reading a worktree.
///
/// Pure — the whole rule is one list comparison, and every kind's sequence is testable without
/// touching disk, git, or GitHub.
/// </summary>
public static class StageProgress
{
    public static string LabelFor(Stage stage) => "stage/" + Name(stage);

    /// <summary>The stage a label names, or null if it is not a stage label.</summary>
    public static Stage? FromLabel(string label)
    {
        if (!label.StartsWith("stage/", StringComparison.Ordinal))
        {
            return null;
        }

        var name = label["stage/".Length..];
        foreach (var stage in Enum.GetValues<Stage>())
        {
            if (string.Equals(Name(stage), name, StringComparison.OrdinalIgnoreCase))
            {
                return stage;
            }
        }

        return null;
    }

    /// <summary>
    /// The first stage in this kind's sequence with no completion label — or null when every
    /// stage is recorded, meaning the item is done for its kind.
    /// </summary>
    public static Stage? Derive(Kind kind, IReadOnlyCollection<string> labels)
    {
        foreach (var stage in StageSequence.For(kind))
        {
            if (!labels.Contains(LabelFor(stage)))
            {
                return stage;
            }
        }

        return null;
    }

    /// <summary>Stages already recorded complete, in sequence order — the visible trail.</summary>
    public static IReadOnlyList<Stage> Completed(Kind kind, IReadOnlyCollection<string> labels) =>
        StageSequence.For(kind).Where(s => labels.Contains(LabelFor(s))).ToList();

    /// <summary>The stage's canonical lowercase name — the stem of its label and of any other
    /// per-stage identifier, so the two can never drift apart.</summary>
    public static string Name(Stage stage) => stage.ToString().ToLowerInvariant();
}
