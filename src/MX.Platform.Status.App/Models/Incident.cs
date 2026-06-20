namespace MX.Platform.Status.App.Models;

public sealed record Incident
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<string> Components { get; init; } = [];
    public Severity Severity { get; init; }
    public IncidentState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public IReadOnlyList<IncidentUpdate> Updates { get; init; } = [];
}

public sealed record IncidentUpdate
{
    public DateTimeOffset At { get; init; }
    public IncidentState State { get; init; }
    public string Body { get; init; } = string.Empty;
    public string? Author { get; init; }
}

public sealed record MaintenanceWindow
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public IReadOnlyList<string> Components { get; init; } = [];
    public DateTimeOffset ScheduledStart { get; init; }
    public DateTimeOffset ScheduledEnd { get; init; }
    public string State { get; init; } = "scheduled";
    public string? Body { get; init; }
}
