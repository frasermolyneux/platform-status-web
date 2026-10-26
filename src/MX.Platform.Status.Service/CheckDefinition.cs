using System.Text.Json.Serialization;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckDefinition
{
    public string Id { get; init; } = "";
    public string? SharedId { get; init; }
    public string Component { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "telemetry";
    public string Method { get; init; } = "GET";
    public string? Target { get; init; }
    public int ExpectedIntervalSeconds { get; init; } = 60;
    public MonitoringReference? Monitoring { get; init; }
    public ProcessReference? Process { get; init; }

    public string PublicId(SiteDefinition site) => SharedId
        ?? (site.SharedComponents.ContainsKey(Component) ? $"{site.Site.Id}/{Id}" : Id);

}

public sealed record CheckResult(string Site, string CheckId, [property: JsonRequired] ComponentStatus Status, DateTimeOffset ObservedAt, string? Detail = null)
{
    public string? Component { get; init; }
    public string? Name { get; init; }
    public MonitoringReference? Monitoring { get; init; }
    public ProcessReference? Process { get; init; }
    public string? ObservationKey { get; init; }
    internal string HistoryKey => ObservationKey ?? $"{ObservedAt:O}/{Status}";
    public IReadOnlyList<RegionAvailabilityTelemetry> Regions { get; init; } = [];
    public IReadOnlyDictionary<string, string> RegionMessages { get; init; } = new Dictionary<string, string>();

    public string? Validate() => !Enum.IsDefined(Status) || Status == ComponentStatus.Maintenance
        || ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5) || ObservedAt == default || Detail?.Length > 2000
        || ObservationKey?.Length > 128
        ? "A check requires a valid observation time and an operational, degraded, outage or unknown status." : null;
}

public static class ComponentChecks
{
    public static Dictionary<string, ComponentLiveTelemetry> Build(
        SiteDefinition site, IReadOnlyList<ProbeObservation> probes, IReadOnlyList<CheckResult> signals,
        DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        return site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key, group =>
            FromChecks(group.Select(check => Observe(site, check, signals, now)).ToArray()));
    }

    public static ComponentLiveTelemetry FromChecks(IReadOnlyList<CheckTelemetry> checks) =>
        new(checks.All(check => check.Status != ComponentStatus.Unknown)
                || checks.Any(check => check.Status is ComponentStatus.Degraded or ComponentStatus.Outage) ? 1 : 0,
            checks.Any(check => check.Status is ComponentStatus.Degraded or ComponentStatus.Outage) ? 1 : 0,
            checks.Where(check => check.ObservedAt.HasValue).Select(check => check.ObservedAt).Min(), null)
        { Checks = checks };

    internal static bool MatchesMonitoringSource(CheckDefinition check, CheckResult signal) =>
        check.Process is { } process ? signal.Process?.Identity == process.Identity :
        check.Monitoring is null
        || (signal.Monitoring?.Identity == check.Monitoring.Identity && signal.Regions.Count > 0);

    internal static CheckTelemetry Observe(SiteDefinition site, CheckDefinition check,
        IReadOnlyList<CheckResult> signals, DateTimeOffset now)
    {
        var signal = signals.Where(signal => signal.Site == site.Site.Id && signal.CheckId == check.Id
            && MatchesMonitoringSource(check, signal)).MaxBy(signal => signal.ObservedAt);
        var at = signal?.ObservedAt;
        var status = check.Kind == "telemetry" ? signal?.Status ?? ComponentStatus.Unknown : ComponentStatus.Unknown;
        if (check.Monitoring is { } source && signal is { Regions.Count: > 0 })
            status = MonitoringAvailability.CurrentStatus(source, signal.Regions, now);
        var freshness = check.Process is not null ? ProcessReference.FreshnessSeconds
            : check.Monitoring?.FreshnessSeconds ?? check.ExpectedIntervalSeconds * 3;
        if (at is null || now - at > TimeSpan.FromSeconds(freshness)
            || at > now.AddMinutes(5))
            status = ComponentStatus.Unknown;
        return new(check.PublicId(site), check.Name, check.Kind, status, at);
    }
}
