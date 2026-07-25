namespace SpecRunner.Domain;

/// <summary>A work item's kind — determines the stage sequence it traverses (FR-015).</summary>
public enum Kind
{
    Feature,
    Amendment,
    Chore,
    Spike,
    Audit,
}
