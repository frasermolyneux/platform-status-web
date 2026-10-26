using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Merging;

public static class IncidentImpact
{
    public static StatusApiResponse Apply(StatusApiResponse publication, IEnumerable<Incident>? incidents = null,
        ImpactPropagation? propagation = null)
    {
        var records = incidents?.ToDictionary(incident => incident.Id) ?? [];
        return publication with
        {
            Components = publication.Components.Select(component => Apply(component, publication, records, [], propagation)).ToArray()
        };
    }

    private static ComponentDto Apply(ComponentDto component, StatusApiResponse publication,
        IReadOnlyDictionary<int, Incident> records, IReadOnlyList<string> ancestors, ImpactPropagation? propagation)
    {
        var children = component.Children?.Select(child => Apply(child, publication, records, [.. ancestors, component.Id], propagation)).ToArray();
        var ids = Descendants(component).Concat(ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incidents = publication.Incidents.Where(incident => incident.Components.Any(ids.Contains)).ToArray();
        return component with
        {
            Children = children,
            OpenIncidentIds = incidents.Where(incident => incident.ResolvedAt is null)
                .Select(incident => incident.Id).Distinct().Order().ToArray(),
            History = component.History.Select(day =>
            {
                var impacts = publication.Incidents.Select(incident =>
                    (incident.Id, States: propagation is null
                        ? ImpactsForDay(incident, records, day.Date, ids, publication.Maintenance).ToArray()
                        : ids.SelectMany(id => ImpactsForDay(incident, records, day.Date,
                            new HashSet<string>([id], StringComparer.OrdinalIgnoreCase), publication.Maintenance)
                            .Select(status => propagation.Contribute(component.Id, id, status))).ToArray()))
                    .Where(impact => impact.States.Length > 0).ToArray();
                var states = impacts.SelectMany(impact => impact.States)
                    .Concat(children?.SelectMany(child => child.History.Where(sample => sample.Date == day.Date)
                        .Select(sample => propagation?.Contribute(component.Id, child.Id, sample.Status) ?? sample.Status)) ?? []);
                return day with
                {
                    Status = Worst(states.Append(day.Status).Where(status => status != "unknown")),
                    IncidentIds = impacts.Select(impact => impact.Id)
                        .Concat(children?.SelectMany(child => child.History.Where(sample => sample.Date == day.Date)
                            .SelectMany(sample => sample.IncidentIds ?? [])) ?? []).Distinct().Order().ToArray()
                };
            }).ToArray()
        };
    }

    private static IEnumerable<string> ImpactsForDay(IncidentDto incident, IReadOnlyDictionary<int, Incident> records,
        DateOnly date, ISet<string> components, IReadOnlyList<MaintenanceDto> maintenance)
    {
        if (records.GetValueOrDefault(incident.Id)?.ImpactHistory is { Count: > 0 } observations)
            return ObservedImpacts(observations, incident.ResolvedAt, date, components);
        return incident.Components.Any(components.Contains) ? Impacts(incident, date, maintenance) : [];
    }

    private static IEnumerable<string> ObservedImpacts(IReadOnlyList<IncidentImpactObservation> history,
        DateTimeOffset? resolvedAt, DateOnly date, ISet<string> components)
    {
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        foreach (var source in history.GroupBy(observation => observation.SourceSite))
        {
            var observations = source.OrderBy(observation => observation.At).ToArray();
            var resolution = SourceResolution(observations, resolvedAt);
            for (var index = 0; index < observations.Length; index++)
            {
                var end = index + 1 < observations.Length ? observations[index + 1].At : resolution ?? DateTimeOffset.UtcNow;
                end = Earlier(end, resolution ?? end);
                if (ObservationImpact(observations[index], end, components, midnight, midnight.AddDays(1)) is { } impact)
                    yield return impact;
            }
        }
    }

    private static string? ObservationImpact(IncidentImpactObservation observation, DateTimeOffset end,
        ISet<string> components, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        if (observation.State == IncidentState.Resolved
            || observation.State == IncidentState.Monitoring && observation.Severity != Severity.Maintenance
            || !observation.Components.Any(components.Contains)) return null;
        var start = observation.At;
        if (observation.Severity == Severity.Maintenance)
        {
            if (observation.ScheduledStart is not { } windowStart || observation.ScheduledEnd is not { } windowEnd) return null;
            start = start > windowStart ? start : windowStart;
            end = Earlier(end, windowEnd);
        }
        return start < end && start < rangeEnd && end > rangeStart ? observation.Severity.ToApiString() : null;
    }

    private static IEnumerable<string> Descendants(ComponentDto component) =>
        new[] { component.Id }.Concat((component.Children ?? []).SelectMany(Descendants));

    internal static bool AffectsDate(DateTimeOffset start, DateTimeOffset? end, DateOnly date)
    {
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var until = end ?? DateTimeOffset.UtcNow;
        return start < until && start < midnight.AddDays(1) && until > midnight;
    }

    private static IEnumerable<string> Impacts(IncidentDto incident, DateOnly date, IReadOnlyList<MaintenanceDto> maintenance)
    {
        if (incident.Severity == "maintenance")
        {
            var window = maintenance.FirstOrDefault(window => window.Id == incident.Id);
            if (window is not null && AffectsDate(window.ScheduledStart,
                Earlier(window.ScheduledEnd, incident.ResolvedAt ?? DateTimeOffset.UtcNow), date))
                yield return "maintenance";
            yield break;
        }
        var updates = incident.Updates.OrderBy(update => update.At).ToArray();
        string? severity = null;
        for (var index = 0; index < updates.Length; index++)
        {
            var update = updates[index];
            severity = update.Severity ?? severity;
            var end = index + 1 < updates.Length ? updates[index + 1].At : incident.ResolvedAt ?? DateTimeOffset.UtcNow;
            end = Earlier(end, incident.ResolvedAt ?? end);
            if (severity is not null && update.State is not ("resolved" or "monitoring") && AffectsDate(update.At, end, date))
                yield return severity;
        }
        // Old incidents may predate impact recording; only their current impact is known.
        if (incident.ResolvedAt is null && incident.State != "monitoring" && date == DateOnly.FromDateTime(DateTime.UtcNow))
            yield return incident.Severity;
    }

    public static IEnumerable<string> ForRange(Incident incident, ISet<string> components,
        DateTimeOffset start, DateTimeOffset end, MaintenanceDto? maintenance = null)
    {
        var observations = incident.ImpactHistory.Count > 0 ? incident.ImpactHistory : LegacyObservations(incident, maintenance);
        foreach (var source in observations.GroupBy(observation => observation.SourceSite))
        {
            var ordered = source.OrderBy(observation => observation.At).ToArray();
            var resolution = SourceResolution(ordered, incident.ResolvedAt);
            for (var index = 0; index < ordered.Length; index++)
            {
                var until = index + 1 < ordered.Length ? ordered[index + 1].At : resolution ?? end;
                until = Earlier(until, resolution ?? until);
                if (ObservationImpact(ordered[index], until, components, start, end) is { } impact)
                    yield return impact;
            }
        }
    }

    private static IReadOnlyList<IncidentImpactObservation> LegacyObservations(Incident incident, MaintenanceDto? maintenance)
    {
        if (incident.Severity == Severity.Maintenance && maintenance is not null)
            return [new(maintenance.ScheduledStart, IncidentState.Investigating, Severity.Maintenance,
                incident.Components, maintenance.ScheduledStart, maintenance.ScheduledEnd)];
        Severity? severity = null;
        var observations = new List<IncidentImpactObservation>();
        foreach (var update in incident.Updates.OrderBy(update => update.At))
        {
            severity = update.Severity ?? severity;
            if (severity.HasValue)
                observations.Add(new(update.At, update.State, severity.Value, incident.Components));
        }
        return observations;
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;

    private static DateTimeOffset? SourceResolution(IncidentImpactObservation[] observations, DateTimeOffset? resolvedAt)
    {
        // Shared projections retain independently resolved brand histories.
        if (observations[0].SourceSite is null) return resolvedAt;
        return observations[^1].State == IncidentState.Resolved ? observations[^1].At : null;
    }

    public static string Worst(IEnumerable<string> statuses)
    {
        var values = statuses.ToHashSet();
        return new[] { "outage", "degraded", "maintenance", "unknown", "operational" }
            .FirstOrDefault(values.Contains) ?? "unknown";
    }
}
