using System.Net;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record IncidentEvidence(string Source, string Id, DateTimeOffset ObservedAt, string Context);
public sealed record SourceUpdateSnapshot(DateTimeOffset At, string Body, IncidentState? ExplicitState);
public sealed record GitHubDelivery(string State, string? Error = null);

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
    public IReadOnlyList<IncidentImpactObservation> ImpactHistory { get; init; } = [];
    public IReadOnlyList<IncidentUpdate> SourceStateUpdates { get; init; } = [];
    public IReadOnlyList<SourceUpdateSnapshot> SourceUpdates { get; init; } = [];
    public IReadOnlyList<SourceUpdateSnapshot> PublicSourceUpdates { get; init; } = [];
    public IReadOnlyList<IncidentUpdate> PublicSourceStateUpdates { get; init; } = [];
    public Severity? PublicSourceSeverity { get; init; }
    public IReadOnlyList<int> PublicAliases { get; init; } = [];
    public string? SourceUrl { get; init; }
    public IncidentEvidence? Evidence { get; init; }
    public GitHubDelivery? GitHub { get; init; }
    public string? GitHubVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitHubBaseVersion { get; init; }

    public Incident ToPublicIncident() => new()
    {
        Id = Id,
        Title = Title,
        Components = Components,
        Severity = Severity,
        State = State,
        Url = $"/incident/{Id}",
        SourceUrl = SourceUrl,
        Aliases = PublicAliases,
        CreatedAt = StartedAt,
        StartedAt = StartedAt,
        ResolvedAt = ResolvedAt,
        ImpactHistory = ImpactHistory,
        Updates = PublicUpdates.Select(update => update with { Author = null }).ToArray()
    };

    public IncidentRecord ObserveImpact(IncidentRecord? previous, DateTimeOffset now)
    {
        var history = previous?.ImpactHistory ?? ImpactHistory;
        if (!Published || history.LastOrDefault() is { } last
            && last.State == State && last.Severity == Severity
            && last.Components.ToHashSet().SetEquals(Components)
            && last.ScheduledStart == ScheduledStart && last.ScheduledEnd == ScheduledEnd)
            return this with { ImpactHistory = history };
        return this with
        {
            ImpactHistory = [.. history, new(now, State, Severity, Components, ScheduledStart, ScheduledEnd)]
        };
    }

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
    DateTimeOffset? ScheduledStart = null, DateTimeOffset? ScheduledEnd = null, IncidentEvidence? Evidence = null,
    string Delivery = "local", Guid? OperationId = null)
{
    public string? Validate(SiteDefinition site)
    {
        var known = StatusCatalog.Flatten(site.Components.Components).Select(c => c.Id).ToHashSet();
        if (Delivery is not ("local" or "github") || Delivery == "github" && (!OperationId.HasValue || OperationId == Guid.Empty))
            return "Choose GitHub or local delivery; GitHub changes require a stable operation ID.";
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || string.IsNullOrWhiteSpace(Message) || Message.Length > 20000)
            return "A title (up to 200 characters) and update (up to 20000 characters) are required.";
        if (Components is null || Components.Length == 0 || Components.Any(id => !known.Contains(id))
            || Components.Distinct().Count() != Components.Length)
            return "Select one or more distinct components from this site.";
        if (!Enum.IsDefined(Severity) || !Enum.IsDefined(State))
            return "Invalid incident severity or state.";
        if (HasInvalidEvidence())
            return "Invalid source evidence reference.";
        if (ScheduledStart.HasValue != ScheduledEnd.HasValue || ScheduledEnd <= ScheduledStart
            || (Severity == Severity.Maintenance) != ScheduledStart.HasValue)
            return "Maintenance requires a valid start/end window; incidents must not specify one.";
        var publicComponents = StatusCatalog.PublicComponents(site.Components.Components).Select(c => c.Id).ToHashSet();
        if (Published && Components.Any(id => !publicComponents.Contains(id)))
            return "Restricted components cannot be included in public incidents.";
        return null;
    }

    private bool HasInvalidEvidence() => Evidence is not null
        && (string.IsNullOrWhiteSpace(Evidence.Source) || Evidence.Source.Length > 100
            || string.IsNullOrWhiteSpace(Evidence.Id) || Evidence.Id.Length > 2000
            || string.IsNullOrWhiteSpace(Evidence.Context) || Evidence.Context.Length > 20000
            || Evidence.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5));

    public IncidentRecord Apply(int id, string site, IncidentRecord? previous, DateTimeOffset now)
    {
        GitHubDelivery? delivery = null;
        if (Delivery == "github")
            delivery = new(Published || previous?.SourceUrl is not null ? "pending" : "draft");
        var update = new IncidentUpdate
        {
            At = now,
            OperationId = OperationId,
            State = State,
            Severity = Severity,
            Body = WebUtility.HtmlEncode(Message.Trim().Replace("\r\n", "\n", StringComparison.Ordinal)).Replace("\n", "<br>", StringComparison.Ordinal)
        };
        return ((previous ?? new IncidentRecord()) with
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
            PublicUpdates = Published ? [.. previous?.PublicUpdates ?? [], update] : previous?.PublicUpdates ?? [],
            Evidence = Evidence ?? previous?.Evidence,
            GitHub = delivery
        }).ObserveImpact(previous, now);
    }
}
