using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Merging;

public static class IncidentImpact
{
    public static StatusApiResponse Apply(StatusApiResponse publication, IEnumerable<Incident>? incidents = null)
    {
        var records = incidents?.ToDictionary(incident => incident.Id) ?? [];
        return publication with
        {
            Components = publication.Components.Select(component => Apply(component, publication, records, [])).ToArray()
        };
    }

    private static ComponentDto Apply(ComponentDto component, StatusApiResponse publication,
        IReadOnlyDictionary<int, Incident> records, IReadOnlyList<string> ancestors)
    {
        var children = component.Children?.Select(child => Apply(child, publication, records, [.. ancestors, component.Id])).ToArray();
        var ids = Descendants(component).Concat(ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incidents = publication.Incidents.Where(incident => incident.Components.Any(ids.Contains)).ToArray();
        return component with
        {
            Children = children,
            OpenIncidentIds = incidents.Where(incident => incident.ResolvedAt is null)
                .Select(incident => incident.Id).Distinct().Order().ToArray(),
            History = component.History.Select(day =>
            {
                var impacts = publication.Incidents.Select(incident => (incident.Id, States: records.GetValueOrDefault(incident.Id)?.ImpactHistory is { Count: > 0 } observations
                    ? ObservedImpacts(observations, incident.ResolvedAt, day.Date, ids).ToArray()
                    : incident.Components.Any(ids.Contains) ? Impacts(incident, day.Date, publication.Maintenance).ToArray() : []))
                    .Where(impact => impact.States.Length > 0).ToArray();
                var states = impacts.SelectMany(impact => impact.States)
                    .Concat(children?.SelectMany(child => child.History.Where(sample => sample.Date == day.Date)
                        .Select(sample => sample.Status)) ?? []);
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

    private static IEnumerable<string> ObservedImpacts(IReadOnlyList<IncidentImpactObservation> history,
        DateTimeOffset? resolvedAt, DateOnly date, ISet<string> components)
    {
        var observations = history.OrderBy(observation => observation.At).ToArray();
        for (var index = 0; index < observations.Length; index++)
        {
            var observation = observations[index];
            if (observation.State == IncidentState.Resolved || !observation.Components.Any(components.Contains)) continue;
            var end = index + 1 < observations.Length ? observations[index + 1].At : resolvedAt ?? DateTimeOffset.UtcNow;
            var start = observation.At;
            if (observation.Severity == Severity.Maintenance)
            {
                if (observation.ScheduledStart is not { } windowStart || observation.ScheduledEnd is not { } windowEnd) continue;
                start = start > windowStart ? start : windowStart;
                end = Earlier(end, windowEnd);
            }
            if (AffectsDate(start, end, date)) yield return observation.Severity.ToApiString();
        }
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
            if (severity is not null && update.State != "resolved" && AffectsDate(update.At, end, date))
                yield return severity;
        }
        // Old incidents may predate impact recording; only their current impact is known.
        if (incident.ResolvedAt is null && date == DateOnly.FromDateTime(DateTime.UtcNow))
            yield return incident.Severity;
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;

    internal static string Worst(IEnumerable<string> statuses)
    {
        var values = statuses.ToHashSet();
        return new[] { "outage", "degraded", "maintenance", "unknown", "operational" }
            .FirstOrDefault(values.Contains) ?? "unknown";
    }
}
