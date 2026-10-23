using MX.Platform.Status.App.Contracts;

namespace MX.Platform.Status.Service;

public sealed record ComponentHealth(
    string Id, string Name, string Status, string Source, string? Target,
    int ExpectedIntervalSeconds, DateTimeOffset? LastSampleAt, ProbeObservation? Probe, ProbeObservation? LastFailure,
    bool MatchesPublication, IReadOnlyList<IncidentDto> Incidents, IReadOnlyList<MaintenanceDto> Maintenance);

public static class HealthDiagnostics
{
    public static async Task<IReadOnlyList<ComponentHealth>> ReadAsync(
        SiteDefinition site, PublicationStore files, PostgresStore database, CancellationToken ct)
    {
        var publication = await files.ReadAsync(site.Site.Id, ct);
        return Build(site, publication, await database.ReadProbesAsync(site.Site.Id, ct));
    }

    internal static IReadOnlyList<ComponentHealth> Build(
        SiteDefinition site, StatusApiResponse publication, IReadOnlyList<ProbeObservation> probes)
    {
        var siteProbes = probes.Where(probe => probe.Site == site.Site.Id).ToArray();
        var byId = siteProbes.GroupBy(probe => probe.Component)
            .ToDictionary(group => group.Key, group => group.MaxBy(probe => probe.ObservedAt)!);
        var failures = siteProbes.Where(probe => !probe.Success).GroupBy(probe => probe.Component)
            .ToDictionary(group => group.Key, group => group.MaxBy(probe => probe.ObservedAt)!);
        var publicComponents = Flatten(publication.Components).ToDictionary(component => component.Id);
        return StatusCatalog.PublicComponents(site.Components.Components).Select(component =>
        {
            publicComponents.TryGetValue(component.Id, out var published);
            byId.TryGetValue(component.Id, out var probe);
            failures.TryGetValue(component.Id, out var failure);
            site.Probes.TryGetValue(component.Id, out var target);
            return new ComponentHealth(component.Id, component.Name, published?.Status ?? "unknown",
                target is null ? "Incident-based; no public HTTP check" : "Independent HTTP probe (publisher)",
                target, component.Sla.ExpectedIntervalSeconds, published?.LastSampleAt, probe, failure,
                probe is not null && published?.LastSampleAt == probe.ObservedAt,
                publication.Incidents.Where(incident => incident.Severity != "maintenance"
                    && published?.OpenIncidentIds?.Contains(incident.Id) == true).ToArray(),
                publication.Maintenance.Where(window => window.Components.Contains(component.Id) && window.State == "in-progress").ToArray());
        }).ToArray();
    }

    private static IEnumerable<ComponentDto> Flatten(IEnumerable<ComponentDto> components) =>
        components.SelectMany(component => new[] { component }.Concat(Flatten(component.Children ?? [])));
}
