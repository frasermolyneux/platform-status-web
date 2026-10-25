using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

public sealed record ComponentHealth(
    string Id, string Name, string Status, string Source, string? Target,
    int ExpectedIntervalSeconds, DateTimeOffset? LastSampleAt, ProbeObservation? Probe, ProbeObservation? LastFailure,
    bool MatchesPublication, IReadOnlyList<IncidentDto> Incidents, IReadOnlyList<MaintenanceDto> Maintenance,
    IReadOnlyList<CheckHealth>? Checks = null);

public sealed record CheckHealth(string Id, string Name, string Kind, string Status, DateTimeOffset? ObservedAt,
    string? Target, ProbeObservation? Probe, string? Detail = null);

public static class HealthDiagnostics
{
    public static async Task<IReadOnlyList<ComponentHealth>> ReadAsync(
        StatusCatalog catalog, SiteDefinition site, PublicationStore files, PostgresStore database, CancellationToken ct)
    {
        var publication = await files.ReadAsync(site.Site.Id, ct);
        var probes = await database.ReadProbesAsync(site.Site.Id, ct);
        var signals = await database.ReadChecksAsync(site.Site.Id, ct);
        var telemetry = new Dictionary<string, Dictionary<string, ComponentLiveTelemetry>>
        {
            [site.Site.Id] = ComponentChecks.Build(site, probes, signals)
        };
        foreach (var peer in catalog.Sites.Where(peer => peer.Site.Id != site.Site.Id))
            telemetry[peer.Site.Id] = ComponentChecks.Build(peer,
                await database.ReadProbesAsync(peer.Site.Id, ct), await database.ReadChecksAsync(peer.Site.Id, ct));
        return Build(site, publication, probes, signals, catalog.ProjectTelemetry(site, telemetry));
    }

    internal static IReadOnlyList<ComponentHealth> Build(
        SiteDefinition site, StatusApiResponse publication, IReadOnlyList<ProbeObservation> probes,
        IReadOnlyList<CheckResult>? signals = null,
        IReadOnlyDictionary<string, ComponentLiveTelemetry>? projectedTelemetry = null)
    {
        var siteProbes = probes.Where(probe => probe.Site == site.Site.Id).ToArray();
        var byId = siteProbes.GroupBy(probe => probe.Component)
            .ToDictionary(group => group.Key, group => group.MaxBy(probe => probe.ObservedAt)!);
        var failures = siteProbes.Where(probe => !probe.Success).GroupBy(probe => probe.Component)
            .ToDictionary(group => group.Key, group => group.MaxBy(probe => probe.ObservedAt)!);
        var publicComponents = Flatten(publication.Components).ToDictionary(component => component.Id);
        var observations = projectedTelemetry ?? ComponentChecks.Build(site, siteProbes, signals ?? []);
        return StatusCatalog.PublicComponents(site.Components.Components).Select(component =>
        {
            publicComponents.TryGetValue(component.Id, out var published);
            byId.TryGetValue(component.Id, out var probe);
            failures.TryGetValue(component.Id, out var failure);
            var configured = site.EffectiveChecks.Where(check => check.Component == component.Id).ToArray();
            var checks = published?.Checks?.Select(check =>
            {
                var definition = configured.SingleOrDefault(definition => definition.PublicId(site) == check.Id);
                var result = signals?.Where(signal => signal.Site == site.Site.Id && signal.CheckId == definition?.Id)
                    .MaxBy(signal => signal.ObservedAt);
                return new CheckHealth(check.Id, check.Name, check.Kind, check.Status, check.ObservedAt, null,
                    siteProbes.Where(observation => definition?.Id == (observation.CheckId ?? observation.Component))
                        .MaxBy(observation => observation.ObservedAt), result?.Detail);
            }).ToArray() ?? [];
            var source = configured.Any(check => check.Process is not null)
                ? "Process monitoring via Application Insights"
                : configured.Any(check => check.Monitoring is not null)
                ? "Sitewatch monitoring via Application Insights"
                : "Telemetry checks and declared incidents";
            if (component.Kind == "group") source = "Service aggregate from components";
            return new ComponentHealth(component.Id, component.Name, published?.Status ?? "unknown",
                source,
                null, configured.Select(check => check.Monitoring?.IntervalSeconds ?? check.ExpectedIntervalSeconds)
                    .DefaultIfEmpty(component.Sla.ExpectedIntervalSeconds).Min(),
                published?.LastSampleAt, probe, failure,
                MatchesPublication(published, observations.GetValueOrDefault(component.Id), probe),
                publication.Incidents.Where(incident => incident.Severity != "maintenance"
                    && published?.OpenIncidentIds?.Contains(incident.Id) == true).ToArray(),
                publication.Maintenance.Where(window => window.Components.Contains(component.Id) && window.State == "in-progress").ToArray(), checks);
        }).ToArray();
    }

    private static bool MatchesPublication(ComponentDto? published, ComponentLiveTelemetry? observed, ProbeObservation? probe)
    {
        if (published is null) return false;
        if (observed is null) return true;
        if (published.Checks is { Count: > 0 })
            return observed.Checks.All(expected => published.Checks.Any(check => check.Id == expected.Id
                && check.ObservedAt == expected.ObservedAt && check.Status == expected.Status.ToApiString()));
        return probe is not null && published.LastSampleAt == probe.ObservedAt;
    }

    private static IEnumerable<ComponentDto> Flatten(IEnumerable<ComponentDto> components) =>
        components.SelectMany(component => new[] { component }.Concat(Flatten(component.Children ?? [])));
}
