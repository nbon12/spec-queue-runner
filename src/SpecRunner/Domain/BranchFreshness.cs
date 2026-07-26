namespace SpecRunner.Domain;

/// <summary>
/// How an item's branch stands against the base branch at the moment it was checked.
/// <see cref="BaseUnknown"/> is deliberately distinct from <see cref="UpToDate"/>: a base we
/// could not read is not a base we agree with.
/// </summary>
public enum Freshness
{
    /// <summary>The branch already contains every commit on the base — nothing to replay.</summary>
    UpToDate,

    /// <summary>The base had moved; the branch was replayed onto it and force-pushed.</summary>
    Rebased,

    /// <summary>The replay conflicted and was aborted — a human has to resolve it.</summary>
    Conflicted,

    /// <summary>The base could not be fetched, so the branch's staleness is unknown.</summary>
    BaseUnknown,
}

/// <summary>
/// The stale-branch rule (CLAUDE.md), as a pure decision so it is testable without git: a branch
/// is only ever merged when it already sits on top of the latest base.
///
/// The two gates differ on purpose. Before the expensive stages, a rebase is a fine outcome —
/// the point of rebasing is to carry on with fresh code. Immediately before the merge, a rebase
/// is a REFUSAL: it means the base moved after the change was reviewed and verified, so what
/// would land is no longer what was checked. That item waits for a tick that reviews and verifies
/// the new base.
/// </summary>
public static class BranchFreshness
{
    /// <summary>May the pipeline proceed to judge this branch (review, verify)?</summary>
    public static bool MayContinue(Freshness freshness) =>
        freshness is Freshness.UpToDate or Freshness.Rebased;

    /// <summary>May this branch be merged right now? Only if it needed no catching up at all.</summary>
    public static bool MayMerge(Freshness freshness) => freshness is Freshness.UpToDate;

    /// <summary>Why the pipeline stopped, in the operator's words, for the issue comment.</summary>
    public static string Explain(Freshness freshness, string baseBranch) => freshness switch
    {
        Freshness.UpToDate => $"the branch is up to date with `{baseBranch}`",
        Freshness.Rebased =>
            $"`{baseBranch}` moved while this change was being reviewed, so the branch was rebased " +
            "onto it. What was reviewed and verified is no longer what would land, so the merge " +
            "waits for the next tick to review and verify the rebased branch",
        Freshness.Conflicted =>
            $"rebasing onto `{baseBranch}` conflicted, so the rebase was aborted and nothing was " +
            "merged. Resolve the conflict on the branch by hand; the runner picks the item up again " +
            "once the branch replays cleanly",
        Freshness.BaseUnknown =>
            $"`origin/{baseBranch}` could not be fetched, so whether this branch is stale is unknown. " +
            "Nothing is merged on an unknown base; the next tick retries",
        _ => "unknown branch state",
    };
}
