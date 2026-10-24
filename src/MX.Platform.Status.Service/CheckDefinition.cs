using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

public sealed record CheckDefinition
{
    public string Id { get; init; } = "";
    public string? SharedId { get; init; }
    public string Component { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "http";
    public string? Target { get; init; }
    public int ExpectedIntervalSeconds { get; init; } = 60;

    public string PublicId(SiteDefinition site) => SharedId
        ?? (site.SharedComponents.ContainsKey(Component) ? $"{site.Site.Id}/{Id}" : Id);
}

public sealed record CheckResult(string Site, string CheckId, ComponentStatus Status, DateTimeOffset ObservedAt, string? Detail = null)
{
    public string? Validate() => !Enum.IsDefined(Status) || Status == ComponentStatus.Maintenance
        || ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5) || ObservedAt == default || Detail?.Length > 2000
        ? "A check requires a valid observation time and an operational, degraded, outage or unknown status." : null;
}

public static class ComponentChecks
{
    public static Dictionary<string, ComponentLiveTelemetry> Build(
        SiteDefinition site, IReadOnlyList<ProbeObservation> probes, IReadOnlyList<CheckResult> signals)
    {
        var now = DateTimeOffset.UtcNow;
        return site.EffectiveChecks.GroupBy(check => check.Component).ToDictionary(group => group.Key, group =>
            FromChecks(group.Select(check => Observe(site, check, probes, signals, now)).ToArray()));
    }

    public static ComponentLiveTelemetry FromChecks(IReadOnlyList<CheckTelemetry> checks) =>
        new(checks.All(check => check.Status != ComponentStatus.Unknown)
                || checks.Any(check => check.Status is ComponentStatus.Degraded or ComponentStatus.Outage) ? 1 : 0,
            checks.Any(check => check.Status is ComponentStatus.Degraded or ComponentStatus.Outage) ? 1 : 0,
            checks.Where(check => check.ObservedAt.HasValue).Select(check => check.ObservedAt).Min(), null)
        { Checks = checks };

    private static CheckTelemetry Observe(SiteDefinition site, CheckDefinition check, IReadOnlyList<ProbeObservation> probes,
        IReadOnlyList<CheckResult> signals, DateTimeOffset now)
    {
        var signal = signals.Where(signal => signal.Site == site.Site.Id && signal.CheckId == check.Id).MaxBy(signal => signal.ObservedAt);
        var probe = probes.Where(probe => probe.Site == site.Site.Id && probe.Component == check.Component
            && (probe.CheckId == check.Id || probe.CheckId is null && probe.Target == check.Target)).MaxBy(probe => probe.ObservedAt);
        var at = check.Kind == "http" ? probe?.ObservedAt : signal?.ObservedAt;
        var status = signal?.Status ?? ComponentStatus.Unknown;
        if (check.Kind == "http")
        {
            status = ComponentStatus.Unknown;
            if (probe is not null) status = probe.Success ? ComponentStatus.Operational : ComponentStatus.Outage;
        }
        if (at is null || now - at > TimeSpan.FromSeconds(check.ExpectedIntervalSeconds * 3) || at > now.AddMinutes(5))
            status = ComponentStatus.Unknown;
        return new(check.PublicId(site), check.Name, check.Kind, status, at);
    }
}
