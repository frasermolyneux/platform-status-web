using System.Net;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record IncidentEvidence(string Source, string Id, DateTimeOffset ObservedAt, string Context);
public sealed record SourceUpdateSnapshot(DateTimeOffset At, string Body, IncidentState? ExplicitState);
public sealed record GitHubDelivery(string State, string? Error = null, string? SiteId = null, int? IncidentId = null);

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
    public DateTimeOffset? PublicSourceObservedAt { get; init; }
    public IReadOnlyList<int> PublicAliases { get; init; } = [];
    public string? SourceUrl { get; init; }
    public IncidentEvidence? Evidence { get; init; }
    public GitHubDelivery? GitHub { get; init; }
    public string? GitHubVersion { get; init; }
    public bool GitHubSiteWithdrawn { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitHubBaseVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitHubBaseSiteId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? GitHubSourceOperationId { get; init; }

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
        if (history.Count == 0 && previous is { Published: true, State: IncidentState.Resolved })
            history = previous.RecordedLegacyImpact();
        history = ObserveGitHubTransitions(previous, history);
        if (!Published || State == IncidentState.Resolved && history.Count == 0
            || history.LastOrDefault() is { } last
            && last.State == State && last.Severity == Severity
            && last.Components.ToHashSet().SetEquals(Components)
            && last.ScheduledStart == ScheduledStart && last.ScheduledEnd == ScheduledEnd)
            return this with { ImpactHistory = history };
        var at = State == IncidentState.Resolved ? ResolvedAt ?? now : now;
        if (SourceUrl is not null && State == IncidentState.Monitoring && previous?.State != State
            && PublicUpdates.Reverse().TakeWhile(update => update.State == State).LastOrDefault() is { } transition)
            at = transition.At;
        if (history.LastOrDefault() is { } latest && at < latest.At) at = latest.At;
        return this with
        {
            ImpactHistory = [.. history, new(at, State, Severity, Components, ScheduledStart, ScheduledEnd)]
        };
    }

    private IReadOnlyList<IncidentImpactObservation> ObserveGitHubTransitions(IncidentRecord? previous,
        IReadOnlyList<IncidentImpactObservation> history)
    {
        if (!Published || SourceUrl is null) return history;
        var observations = history.ToList();
        var pending = history.Count == 0 ? PublicUpdates : PublicUpdates.Where(update => update.At >= history[^1].At
            && previous?.PublicUpdates.Contains(update) != true).ToArray();
        foreach (var update in pending.Where(update => ResolvedAt is not { } resolved || update.At <= resolved)
            .OrderBy(update => update.At))
        {
            var latest = observations.LastOrDefault();
            var severity = update.Severity ?? latest?.Severity;
            if (!severity.HasValue) continue;
            latest ??= new(update.At, update.State, severity.Value, Components, ScheduledStart, ScheduledEnd);
            if (observations.Count == 0 || latest.State != update.State || latest.Severity != severity)
                observations.Add(latest with { At = update.At, State = update.State, Severity = severity.Value });
        }
        return observations;
    }

    private IReadOnlyList<IncidentImpactObservation> RecordedLegacyImpact()
    {
        if (ResolvedAt is not { } resolved) return [];
        if (Severity == Severity.Maintenance && ScheduledStart is { } start && ScheduledEnd is { } end
            && start < end && start < resolved)
            return
            [
                new(start, IncidentState.Investigating, Severity.Maintenance, Components, ScheduledStart, ScheduledEnd),
                new(resolved, IncidentState.Resolved, Severity.Maintenance, Components, ScheduledStart, ScheduledEnd)
            ];
        var observations = new List<IncidentImpactObservation>();
        Severity? severity = null;
        foreach (var update in PublicUpdates.Where(update => update.At < resolved).OrderBy(update => update.At))
        {
            severity = update.Severity ?? severity;
            if (severity.HasValue)
                observations.Add(new(update.At, update.State, severity.Value, Components, ScheduledStart, ScheduledEnd));
        }
        if (observations.Count > 0)
            observations.Add(new(resolved, IncidentState.Resolved, observations[^1].Severity, Components, ScheduledStart, ScheduledEnd));
        return observations;
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
        if (HasInvalidSchedule())
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

    private bool HasInvalidSchedule() => ScheduledStart.HasValue != ScheduledEnd.HasValue || ScheduledEnd <= ScheduledStart
        || (Severity == Severity.Maintenance) != ScheduledStart.HasValue;

    public IncidentRecord Apply(int id, string site, IncidentRecord? previous, DateTimeOffset now)
    {
        GitHubDelivery? delivery = null;
        if (Delivery == "github")
            delivery = new(Published || previous?.SourceUrl is not null ? "pending" : "draft", SiteId: site, IncidentId: id);
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
            GitHubSiteWithdrawn = !Published && previous?.GitHubSiteWithdrawn == true,
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
