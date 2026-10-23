using System.Net;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record IncidentRecord
{
    public int Id { get; init; }
    public string SiteId { get; init; } = "";
    public int Revision { get; init; }
    public bool Published { get; init; }
    public string Title { get; init; } = "";
    public string[] Components { get; init; } = [];
    public Severity Severity { get; init; } = Severity.Degraded;
    public IncidentState State { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ScheduledStart { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
    public IReadOnlyList<IncidentUpdate> Updates { get; init; } = [];
    public IReadOnlyList<IncidentUpdate> PublicUpdates { get; init; } = [];
    public string? SourceUrl { get; init; }

    public Incident ToPublicIncident() => new()
    {
        Id = Id,
        Title = Title,
        Components = Components,
        Severity = Severity,
        State = State,
        Url = $"/incident/{Id}",
        CreatedAt = StartedAt,
        StartedAt = StartedAt,
        ResolvedAt = ResolvedAt,
        Updates = PublicUpdates.Select(update => update with { Author = null }).ToArray()
    };

    public MaintenanceWindow ToMaintenance(DateTimeOffset now)
    {
        var maintenanceState = now >= ScheduledStart ? "in-progress" : "scheduled";
        if (State == IncidentState.Resolved || now >= ScheduledEnd) maintenanceState = "completed";
        return new()
        {
            Id = Id,
            Title = Title,
            Components = Components,
            Url = $"/incident/{Id}",
            ScheduledStart = ScheduledStart!.Value,
            ScheduledEnd = ScheduledEnd!.Value,
            State = maintenanceState,
            Body = PublicUpdates.LastOrDefault()?.Body
        };
    }
}

public sealed record IncidentCommand(
    string Title, string[] Components, Severity Severity, IncidentState State,
    string Message, bool Published, int Revision = 0,
    DateTimeOffset? ScheduledStart = null, DateTimeOffset? ScheduledEnd = null)
{
    public string? Validate(SiteDefinition site)
    {
        var known = StatusCatalog.Flatten(site.Components.Components).Select(c => c.Id).ToHashSet();
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || string.IsNullOrWhiteSpace(Message) || Message.Length > 20000)
            return "A title (up to 200 characters) and update (up to 20000 characters) are required.";
        if (Components is null || Components.Length == 0 || Components.Any(id => !known.Contains(id))
            || Components.Distinct().Count() != Components.Length)
            return "Select one or more distinct components from this site.";
        if (!Enum.IsDefined(Severity) || !Enum.IsDefined(State))
            return "Invalid incident severity or state.";
        if (ScheduledStart.HasValue != ScheduledEnd.HasValue || ScheduledEnd <= ScheduledStart
            || (Severity == Severity.Maintenance) != ScheduledStart.HasValue)
            return "Maintenance requires a valid start/end window; incidents must not specify one.";
        var publicComponents = StatusCatalog.PublicComponents(site.Components.Components).Select(c => c.Id).ToHashSet();
        if (Published && Components.Any(id => !publicComponents.Contains(id)))
            return "Restricted components cannot be included in public incidents.";
        return null;
    }

    public IncidentRecord Apply(int id, string site, IncidentRecord? previous, DateTimeOffset now)
    {
        var update = new IncidentUpdate
        {
            At = now,
            State = State,
            Body = WebUtility.HtmlEncode(Message.Trim()).Replace("\n", "<br>", StringComparison.Ordinal)
        };
        return new()
        {
            Id = id,
            SiteId = site,
            Revision = (previous?.Revision ?? 0) + 1,
            Published = Published,
            Title = Title.Trim(),
            Components = Components,
            Severity = Severity,
            State = State,
            StartedAt = previous?.StartedAt ?? now,
            ResolvedAt = State == IncidentState.Resolved ? previous?.ResolvedAt ?? now : null,
            ScheduledStart = ScheduledStart,
            ScheduledEnd = ScheduledEnd,
            Updates = [.. previous?.Updates ?? [], update],
            PublicUpdates = Published ? [.. previous?.PublicUpdates ?? [], update] : previous?.PublicUpdates ?? []
        };
    }
}
