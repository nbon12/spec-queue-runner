namespace SpecRunner.Domain;

/// <summary>
/// Recurrence (FR-042): an item marked recurring by a <c>Recurring:</c> body line, on reaching a
/// terminal state, causes a successor issue to be filed with the body copied forward. Closed
/// issues stay closed; the book is append-only. Pure body parsing.
/// </summary>
public static class Recurrence
{
    /// <summary>The <c>Recurring:</c> cadence from the body, or null if the item is not recurring.</summary>
    public static string? Cadence(WorkItem item)
    {
        foreach (var line in item.Body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Recurring:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmed["Recurring:".Length..].Trim();
                return rest.Length == 0 ? null : rest;
            }
        }

        return null;
    }

    public static bool IsRecurring(WorkItem item) => Cadence(item) is not null;
}
