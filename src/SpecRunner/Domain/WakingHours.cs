namespace SpecRunner.Domain;

/// <summary>
/// Live sessions spawn only inside configured waking hours (FR-021/026): a block at 3am gets its
/// session at 8am. Pure window arithmetic, so the boundary cases are unit-testable. Supports
/// windows that wrap past midnight (e.g. 22:00-06:00).
/// </summary>
public readonly record struct WakingHours(TimeOnly Start, TimeOnly End)
{
    public static WakingHours Parse(string value)
    {
        var halves = value.Split('-');
        if (halves.Length != 2 ||
            !TimeOnly.TryParseExact(halves[0], "HH:mm", out var start) ||
            !TimeOnly.TryParseExact(halves[1], "HH:mm", out var end))
        {
            throw new FormatException($"waking_hours must be HH:MM-HH:MM, got '{value}'");
        }

        return new WakingHours(start, end);
    }

    public bool Contains(TimeOnly now)
    {
        // Non-wrapping window (start <= end): inside if start <= now < end.
        // Wrapping window (start > end, e.g. 22:00-06:00): inside if now >= start OR now < end.
        return Start <= End
            ? now >= Start && now < End
            : now >= Start || now < End;
    }
}
