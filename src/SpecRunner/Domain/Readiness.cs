namespace SpecRunner.Domain;

/// <summary>
/// Held-gating (FR-010): an item is not schedulable while any spec it targets is absent from
/// main. Dependency ordering falls out of integration, not a scheduler — an amendment to a spec
/// still on an unmerged PR simply holds. Pure over the item's targets and the set of specs known
/// to be on main.
/// </summary>
public static class Readiness
{
    /// <summary>
    /// The targets that are NOT yet on main, given the paths present on main. Empty ⇒ schedulable.
    /// </summary>
    public static IReadOnlyList<string> MissingTargets(WorkItem item, ISet<string> targetsOnMain)
    {
        return item.Targets.Where(t => !targetsOnMain.Contains(t)).ToList();
    }

    public static bool IsSchedulable(WorkItem item, ISet<string> targetsOnMain) =>
        MissingTargets(item, targetsOnMain).Count == 0;
}
