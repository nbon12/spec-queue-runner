namespace SpecRunner.Domain;

/// <summary>
/// Work selection: the lowest-numbered open ready item authored by the operator, at most one per
/// tick (FR-009). Pure over a candidate list so the ordering and the author gate are testable
/// without GitHub.
/// </summary>
public static class WorkSelection
{
    public static WorkItem? Select(IEnumerable<WorkItem> readyItems, long operatorId)
    {
        WorkItem? best = null;
        foreach (var item in readyItems)
        {
            if (item.AuthorId != operatorId)
            {
                continue; // not operator-authored — ignored entirely (FR-005)
            }

            if (best is null || item.Number < best.Number)
            {
                best = item;
            }
        }

        return best;
    }
}
