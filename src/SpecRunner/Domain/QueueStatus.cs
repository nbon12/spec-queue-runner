namespace SpecRunner.Domain;

/// <summary>Queue state — who may touch this item right now (issue-conventions.md). Closed = done.</summary>
public enum QueueStatus
{
    Ready,
    InProgress,
    Live,
    Waiting,
    Held,
    Closed,
}

public static class StatusLabels
{
    public static string Label(this QueueStatus status) =>
        "status/" + status switch
        {
            QueueStatus.InProgress => "in-progress",
            _ => status.ToString().ToLowerInvariant(),
        };

    public static QueueStatus? FromLabel(string label)
    {
        if (!label.StartsWith("status/", StringComparison.Ordinal))
        {
            return null;
        }

        return label["status/".Length..] switch
        {
            "ready" => QueueStatus.Ready,
            "in-progress" => QueueStatus.InProgress,
            "live" => QueueStatus.Live,
            "waiting" => QueueStatus.Waiting,
            "held" => QueueStatus.Held,
            _ => null,
        };
    }
}
