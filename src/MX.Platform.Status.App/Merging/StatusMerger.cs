using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.App.Merging;

public sealed class StatusMerger
{
    public StatusApiResponse Merge(
        SiteConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, ComponentLiveTelemetry> liveData,
        HistoryDocument? history,
        IEnumerable<Incident> incidents,
        IEnumerable<MaintenanceWindow> maintenance,
        bool stale = false)
    {
        var publicIds = PublicIds(snapshot.Components.Components).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incidentList = incidents.Where(item => item.Components.All(publicIds.Contains))
            .Select(item => item with { Components = ExpandAffected(snapshot.Components.Components, item.Components) })
            .OrderByDescending(item => item.CreatedAt).ToArray();
        var maintenanceList = maintenance.Where(item => item.Components.All(publicIds.Contains))
            .Select(item => item with { Components = ExpandAffected(snapshot.Components.Components, item.Components) })
            .OrderBy(item => item.ScheduledStart).ToArray();
        var nowDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var componentDtos = snapshot.Components.Components.Where(component => !component.Hidden).Select(component => BuildComponent(component, history, liveData, incidentList, maintenanceList, nowDate)).ToArray();
        var summaryStatus = Summarize(componentDtos.Select(component => ParseStatus(component.Status)));

        return IncidentImpact.Apply(new StatusApiResponse
        {
            Site = new SiteInfo
            {
                Id = snapshot.Site.Id,
                DisplayName = snapshot.Site.DisplayName,
                Tagline = snapshot.Site.Tagline,
                Links = snapshot.Site.Links.Select(link => new LinkDto { Label = link.Label, Href = link.Href }).ToArray()
            },
            GeneratedAt = DateTimeOffset.UtcNow,
            DataFreshness = new DataFreshness
            {
                AppInsightsAt = liveData.Values.Select(value => value.LastSeen).Where(value => value.HasValue).Max(),
                HistoryAt = history?.GeneratedAt,
                Stale = stale
            },
            Components = componentDtos,
            Incidents = incidentList.Select(MapIncident).ToArray(),
            Maintenance = maintenanceList.Select(MapMaintenance).ToArray(),
            Summary = new Summary
            {
                Status = summaryStatus.ToApiString(),
                Message = BuildSummaryMessage(summaryStatus, componentDtos)
            }
        }, incidentList);
    }

    private ComponentDto BuildComponent(
        Component component,
        HistoryDocument? history,
        IReadOnlyDictionary<string, ComponentLiveTelemetry> liveData,
        IReadOnlyList<Incident> incidents,
        IReadOnlyList<MaintenanceWindow> maintenance,
        DateOnly today)
    {
        var children = component.Children.Where(child => !child.Hidden).Select(child => BuildComponent(child, history, liveData, incidents, maintenance, today)).ToArray();
        var windowDays = Math.Max(1, component.Sla.WindowDays);
        var historicalDays = BuildHistoricalWindow(component, history, today, windowDays);
        var todayHistory = historicalDays[^1];
        var (liveStatus, lastSampleAt) = ResolveLiveStatus(component, children, liveData, historicalDays, todayHistory);

        var incidentStatuses = incidents
            .Where(incident => incident.ResolvedAt is null
                && (incident.State != IncidentState.Monitoring || incident.Severity == Severity.Maintenance)
                && incident.Components.Contains(component.Id, StringComparer.OrdinalIgnoreCase))
            .Select(incident => incident.Severity.ToComponentStatus())
            .ToArray();

        var mergedStatus = ApplyMergedStatus(component.Id, liveStatus, incidentStatuses, maintenance);

        var uptimeValues = historicalDays.Where(day => day.Uptime.HasValue && day.Status != ComponentStatus.Maintenance).Select(day => day.Uptime!.Value).ToArray();
        var uptimeRatio = uptimeValues.Length == 0 ? (double?)null : uptimeValues.Average();
        return new ComponentDto
        {
            Id = component.Id,
            Name = component.Name,
            Description = component.Description,
            Link = component.Link,
            Kind = component.Kind,
            Status = mergedStatus.ToApiString(),
            LastSampleAt = lastSampleAt,
            UptimeWindowDays = windowDays,
            UptimeRatio = uptimeRatio,
            History = historicalDays.Select(day => new HistoryDayDto
            {
                Date = day.Date,
                Status = day.Status.ToApiString(),
                Uptime = day.Uptime,
                Total = day.Total,
                Failed = day.Failed,
                IncidentIds = incidents.Where(incident => incident.Components.Contains(component.Id, StringComparer.OrdinalIgnoreCase) && AffectsDate(incident, day.Date)).Select(incident => incident.Id).Distinct().OrderBy(id => id).ToArray()
            }).ToArray(),
            OpenIncidentIds = incidents.Where(incident => incident.ResolvedAt is null && incident.Components.Contains(component.Id, StringComparer.OrdinalIgnoreCase)).Select(incident => incident.Id).Distinct().OrderBy(id => id).ToArray(),
            Children = children.Length == 0 ? null : children,
            Checks = liveData.TryGetValue(component.Id, out var observed) && observed.Checks.Count > 0
                ? observed.Checks.Select(check => new CheckDto(check.Id, check.Name, check.Kind, check.Status.ToApiString(), check.ObservedAt)).ToArray()
                : null
        };
    }

    private (ComponentStatus LiveStatus, DateTimeOffset? LastSampleAt) ResolveLiveStatus(
        Component component,
        IReadOnlyList<ComponentDto> children,
        IReadOnlyDictionary<string, ComponentLiveTelemetry> liveData,
        IList<HistoryDay> historicalDays,
        HistoryDay todayHistory)
    {
        if (component.Kind.Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            return (
                Summarize(children.Select(child => ParseStatus(child.Status))),
                children.Select(child => child.LastSampleAt).Where(value => value.HasValue).Max());
        }

        liveData.TryGetValue(component.Id, out var telemetry);
        if (component.Source.Kind.Equals("static", StringComparison.OrdinalIgnoreCase)
            && (telemetry is null || telemetry.Checks.Count == 0))
        {
            return (ParseStaticStatus(component.Source.Status), null);
        }

        if (telemetry is not null)
        {
            var liveStatus = telemetry.Regions.Count > 0
                ? ComponentStatusCalculator.ClassifyLiveStatusRegional(telemetry.Regions, component.Sla)
                : ComponentStatusCalculator.ClassifyLiveStatus(telemetry.Samples, telemetry.Failures, telemetry.LastSeen, component.Sla);
            if (telemetry.Checks.Count > 0)
                liveStatus = Summarize(telemetry.Checks.Select(check => check.Status));
            historicalDays[^1] = todayHistory with
            {
                Status = liveStatus,
                Uptime = telemetry.Samples == 0 ? (double?)null : 1d - (double)telemetry.Failures / telemetry.Samples,
                Total = telemetry.Samples,
                Failed = telemetry.Failures
            };

            return (liveStatus, telemetry.LastSeen);
        }

        return (ComponentStatus.Unknown, null);
    }

    private static ComponentStatus ApplyMergedStatus(
        string componentId,
        ComponentStatus liveStatus,
        IReadOnlyList<ComponentStatus> incidentStatuses,
        IReadOnlyList<MaintenanceWindow> maintenance)
    {
        if (maintenance.Any(item => item.Components.Contains(componentId, StringComparer.OrdinalIgnoreCase) && item.State.Equals("in-progress", StringComparison.OrdinalIgnoreCase)))
        {
            return ComponentStatus.Maintenance;
        }

        return incidentStatuses.Count == 0
            ? liveStatus
            : ComponentStatusCalculator.WorstOf([liveStatus, .. incidentStatuses.Where(status => status != ComponentStatus.Maintenance)]);
    }

    private List<HistoryDay> BuildHistoricalWindow(Component component, HistoryDocument? history, DateOnly today, int windowDays)
    {
        var startDate = today.AddDays(-(windowDays - 1));
        var historicMap = history?.ComponentsById.TryGetValue(component.Id, out var record) == true
            ? record.Days.ToDictionary(day => day.Date, day => day)
            : new Dictionary<DateOnly, HistoryDay>();

        var days = new List<HistoryDay>(windowDays);
        for (var date = startDate; date <= today; date = date.AddDays(1))
        {
            if (historicMap.TryGetValue(date, out var historicDay))
            {
                days.Add(historicDay);
                continue;
            }

            days.Add(new HistoryDay
            {
                Date = date,
                Status = ComponentStatus.Unknown
            });
        }

        return days;
    }

    private static bool AffectsDate(Incident incident, DateOnly date)
    {
        var started = DateOnly.FromDateTime(incident.StartedAt.UtcDateTime);
        var ended = DateOnly.FromDateTime((incident.ResolvedAt ?? DateTimeOffset.UtcNow).UtcDateTime);
        return date >= started && date <= ended;
    }

    private static IncidentDto MapIncident(Incident incident) => new()
    {
        Id = incident.Id,
        Title = incident.Title,
        Url = incident.Url,
        SourceUrl = incident.SourceUrl,
        Aliases = incident.Aliases.Count > 0 ? incident.Aliases : null,
        Components = incident.Components,
        Severity = incident.Severity.ToApiString(),
        State = incident.State.ToApiString(),
        CreatedAt = incident.CreatedAt,
        StartedAt = incident.StartedAt,
        ResolvedAt = incident.ResolvedAt,
        Updates = incident.Updates.Select(update => new IncidentUpdateDto
        {
            At = update.At,
            State = update.State.ToApiString(),
            Severity = update.Severity?.ToApiString(),
            Body = update.Body,
            Author = update.Author
        }).ToArray()
    };

    private static MaintenanceDto MapMaintenance(MaintenanceWindow maintenance) => new()
    {
        Id = maintenance.Id,
        Title = maintenance.Title,
        Url = maintenance.Url,
        Components = maintenance.Components,
        ScheduledStart = maintenance.ScheduledStart,
        ScheduledEnd = maintenance.ScheduledEnd,
        State = maintenance.State,
        Body = maintenance.Body
    };

    private static ComponentStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "operational" => ComponentStatus.Operational,
        "degraded" => ComponentStatus.Degraded,
        "outage" => ComponentStatus.Outage,
        "maintenance" => ComponentStatus.Maintenance,
        _ => ComponentStatus.Unknown
    };

    private static ComponentStatus ParseStaticStatus(string? status) => ParseStatus(status);

    private static IEnumerable<string> PublicIds(IEnumerable<Component> components) =>
        components.Where(component => !component.Hidden)
            .SelectMany(component => new[] { component.Id }.Concat(PublicIds(component.Children)));

    private static ComponentStatus Summarize(IEnumerable<ComponentStatus> statuses)
    {
        var values = statuses.ToArray();
        if (values.Contains(ComponentStatus.Outage)) return ComponentStatus.Outage;
        if (values.Contains(ComponentStatus.Degraded)) return ComponentStatus.Degraded;
        if (values.Contains(ComponentStatus.Maintenance)) return ComponentStatus.Maintenance;
        if (values.Contains(ComponentStatus.Unknown)) return ComponentStatus.Unknown;
        if (values.Contains(ComponentStatus.Operational)) return ComponentStatus.Operational;
        return ComponentStatus.Unknown;
    }

    private static IReadOnlyList<string> ExpandAffected(IEnumerable<Component> components, IEnumerable<string> affected)
    {
        var ids = affected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return components.Where(component => !component.Hidden).SelectMany(component =>
            ids.Contains(component.Id) ? PublicIds([component]) : ExpandAffected(component.Children, ids))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<ComponentDto> Leaves(IEnumerable<ComponentDto> components) =>
        components.SelectMany(component => component.Children is { Count: > 0 } ? Leaves(component.Children) : [component]);

    private static string BuildSummaryMessage(ComponentStatus status, IEnumerable<ComponentDto> components)
    {
        if (status == ComponentStatus.Operational) return "All systems operational.";
        var affected = Leaves(components).Where(component => component.Status != "operational")
            .GroupBy(component => component.Status).Select(group =>
                $"{SummaryLabel(group.Key)}: {string.Join(", ", group.Select(component => component.Name))}.");
        var message = string.Join(" ", affected);
        return message.Length > 0 ? message : "No public service status is available.";
    }

    private static string SummaryLabel(string status) => status switch
    {
        "outage" => "Outage",
        "degraded" => "Degraded service",
        "maintenance" => "Maintenance",
        _ => "Awaiting status"
    };
}
