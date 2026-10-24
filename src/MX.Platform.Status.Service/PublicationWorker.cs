using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;
using System.Security.Cryptography;
using System.Text.Json;

namespace MX.Platform.Status.Service;

public sealed class PublicationWorker(
    StatusCatalog catalog, RuntimeOptions options, PublicationStore files, PostgresStore database,
    IHttpClientFactory clients, ILogger<PublicationWorker> logger) : BackgroundService
{
    private DateTimeOffset nextProbeAt;
    private DateTimeOffset nextTelemetryAt;
    private readonly Dictionary<string, (string Fingerprint, DateTimeOffset At)> published = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.DatabaseConnectionFile is null && options.DatabaseHost is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                await RefreshAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Status background refresh failed; retaining locally persisted publications.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!options.Publisher)
        {
            foreach (var siteId in catalog.Sites.Select(definition => definition.Site.Id))
            {
                var publication = await database.ReadPublicationAsync(siteId, ct);
                if (publication is not null && (!published.TryGetValue(siteId, out var previous)
                    || previous.At != publication.GeneratedAt))
                {
                    await files.WriteAsync(publication, ct);
                    RememberPublication(publication);
                }
            }
            return;
        }
        await database.InitializeAsync(ct);
        if (!await database.IsWritableAsync(ct))
            throw new InvalidOperationException("Publisher database is in recovery; refusing publication.");
        await using var publisherLock = await database.TryPublisherLockAsync(ct)
            ?? throw new InvalidOperationException("Another publisher holds the database lock.");
        await PublishAsync(publisherLock, ct);
    }

    internal async Task PublishAsync(PublisherLease publisherLock, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow >= nextProbeAt)
        {
            // Back off failed collections; successful rounds check individual deadlines every worker tick.
            nextProbeAt = DateTimeOffset.UtcNow.AddSeconds(options.RefreshSeconds);
            var sampleTelemetry = DateTimeOffset.UtcNow >= nextTelemetryAt;
            await RefreshProbesAsync(publisherLock, ct, sampleTelemetry);
            if (sampleTelemetry) nextTelemetryAt = DateTimeOffset.UtcNow.AddSeconds(options.RefreshSeconds);
            nextProbeAt = DateTimeOffset.MinValue;
        }
        var telemetry = new Dictionary<string, Dictionary<string, ComponentLiveTelemetry>>();
        foreach (var definition in catalog.Sites)
            telemetry[definition.Site.Id] = ComponentChecks.Build(definition,
                await database.ReadProbesAsync(definition.Site.Id, ct), await database.ReadChecksAsync(definition.Site.Id, ct));
        foreach (var site in catalog.Sites)
        {
            var publicIds = StatusCatalog.PublicComponents(site.Components.Components).Select(c => c.Id).ToHashSet();
            var incidents = (await database.ReadIncidentsAsync(catalog, site, true, ct))
                .Where(i => i.Published && i.Components.All(publicIds.Contains)).ToArray();
            var live = catalog.ProjectTelemetry(site, telemetry);
            var history = await database.ReadHistoryAsync(site, ct);
            var publication = new StatusMerger().Merge(site.Snapshot, live, history,
                incidents.Select(i => i.ToPublicIncident()),
                incidents.Where(i => i.Severity == Severity.Maintenance).Select(i => i.ToMaintenance(DateTimeOffset.UtcNow)));
            publication = IncidentImpact.Apply(CorrectTodayHistory(publication, history), incidents.Select(incident => incident.ToPublicIncident()));
            publication = publication with
            {
                Banners = (await database.ReadBannersAsync(site.Site.Id, ct))
                    .Where(banner => banner.Published && !banner.Archived
                        && (!banner.ExpiresAt.HasValue || banner.ExpiresAt > DateTimeOffset.UtcNow))
                    .Select(banner => banner.ToPublicBanner()).ToArray()
            };
            if (RequiresPublication(publication))
            {
                await publisherLock.SavePublicationAsync(publication, ct);
                await files.WriteAsync(publication, ct);
                RememberPublication(publication);
            }
        }
    }

    private static string Fingerprint(StatusApiResponse publication) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(publication with { GeneratedAt = DateTimeOffset.UnixEpoch, DataFreshness = null },
            StatusJson.Options)));

    private bool RequiresPublication(StatusApiResponse publication) =>
        !published.TryGetValue(publication.Site.Id, out var previous)
        || previous.Fingerprint != Fingerprint(publication)
        || publication.GeneratedAt - previous.At >= TimeSpan.FromSeconds(options.RefreshSeconds);

    private void RememberPublication(StatusApiResponse publication) =>
        published[publication.Site.Id] = (Fingerprint(publication), publication.GeneratedAt);

    internal static Dictionary<string, ComponentLiveTelemetry> LatestTelemetry(
        SiteDefinition site, IReadOnlyList<ProbeObservation> probes) =>
        probes.Where(probe => probe.Site == site.Site.Id && site.EffectiveChecks.Any(check => check.Component == probe.Component))
            .GroupBy(probe => probe.Component).Select(group => group.MaxBy(probe => probe.ObservedAt)!)
            .ToDictionary(probe => probe.Component, probe =>
                new ComponentLiveTelemetry(1, probe.Success ? 0 : 1, probe.ObservedAt, null));

    internal async Task RefreshProbesAsync(PublisherLease lease, CancellationToken ct, bool sampleTelemetry = true)
    {
        var previous = new Dictionary<string, IReadOnlyList<ProbeObservation>>();
        foreach (var siteId in catalog.Sites.Select(site => site.Site.Id))
            previous[siteId] = await database.ReadProbesAsync(siteId, ct);
        var now = DateTimeOffset.UtcNow;
        using var concurrency = new SemaphoreSlim(5);
        var collected = await Task.WhenAll(catalog.Sites.SelectMany(site => site.EffectiveChecks.Where(check => check.Kind == "http")
            .Select(check => (Site: site, Check: check))).GroupBy(entry =>
                (entry.Check.SharedId, Local: entry.Check.SharedId is null ? $"{entry.Site.Site.Id}/{entry.Check.Id}" : null))
            .Where(group => group.Any(entry => entry.Check.IsDue(entry.Site.Site.Id, previous[entry.Site.Site.Id], now)))
            .Select(async group =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                var first = group.First();
                var name = StatusCatalog.Flatten(first.Site.Components.Components).Single(component => component.Id == first.Check.Component).Name;
                var observation = await IndependentProbe.ObserveAsync(clients.CreateClient("probe"),
                    first.Site.Site.Id, first.Check.Component, name, first.Check.Target!, ct, first.Check.Method);
                return group.Select(entry => observation with
                {
                    Site = entry.Site.Site.Id,
                    Component = entry.Check.Component,
                    CheckId = entry.Check.Id,
                    Name = StatusCatalog.Flatten(entry.Site.Components.Components).Single(component => component.Id == entry.Check.Component).Name
                }).ToArray();
            }
            finally { concurrency.Release(); }
        }));
        var observations = collected.SelectMany(group => group).ToArray();
        if (observations.Length == 0 && !sampleTelemetry) return;
        var telemetry = new Dictionary<string, Dictionary<string, ComponentLiveTelemetry>>();
        var observedShared = observations.Select(observation =>
            catalog.Get(observation.Site).SharedComponents.GetValueOrDefault(observation.Component)).OfType<string>().ToHashSet();
        foreach (var site in catalog.Sites)
        {
            foreach (var observation in observations.Where(observation => observation.Site == site.Site.Id))
            {
                if (!observation.Success)
                    logger.LogWarning("Independent probe failed for {Site}/{Component}: {Reason}.",
                        site.Site.Id, observation.Component, observation.Error);
                await lease.SaveProbeEvidenceAsync(observation, ct);
            }
            telemetry[site.Site.Id] = ComponentChecks.Build(site, [.. previous[site.Site.Id], .. observations],
                await database.ReadChecksAsync(site.Site.Id, ct));
        }
        foreach (var site in catalog.Sites)
        {
            var incidents = await database.ReadIncidentsAsync(catalog, site, true, ct);
            var live = catalog.ProjectTelemetry(site, telemetry);
            foreach (var (component, sample) in live.Where(pair => pair.Value.Samples > 0))
                if ((sampleTelemetry && sample.Checks.Any(check => check.Kind == "telemetry")
                    || observations.Any(observation => observation.Site == site.Site.Id && observation.Component == component)
                    || site.SharedComponents.TryGetValue(component, out var shared) && observedShared.Contains(shared))
                    && !IsMaintained(site, component, DateTimeOffset.UtcNow, incidents))
                    await lease.RecordProbeAsync(site.Site.Id, component, sample.Failures == 0, ct);
        }
    }

    private static bool IsMaintained(SiteDefinition site, string componentId, DateTimeOffset at, IReadOnlyList<IncidentRecord> incidents) =>
        incidents.Any(incident => incident.Published && incident.Severity == Severity.Maintenance
            && incident.State != IncidentState.Resolved && incident.ScheduledStart <= at
            && incident.ScheduledEnd > at
            && incident.Components.Any(id => StatusCatalog.PublicComponents(site.Components.Components)
                .Where(component => component.Id == id).SelectMany(component => StatusCatalog.PublicComponents([component]))
                .Any(component => component.Id == componentId)));

    internal static StatusApiResponse CorrectTodayHistory(StatusApiResponse publication, HistoryDocument history) =>
        publication with { Components = publication.Components.Select(c => CorrectComponent(c, history)).ToArray() };

    private static ComponentDto CorrectComponent(ComponentDto component, HistoryDocument history)
    {
        var days = history.ComponentsById.TryGetValue(component.Id, out var record)
            ? record.Days.ToDictionary(day => day.Date) : [];
        var children = component.Children?.Select(child => CorrectComponent(child, history)).ToArray();
        var corrected = component.History.Select(day => CorrectDay(day, days, children)).ToArray();
        var known = corrected.Where(day => day.Total > 0 && day.Failed.HasValue && day.Status != "maintenance").ToArray();
        var total = known.Sum(day => day.Total!.Value);
        return component with
        {
            History = corrected,
            UptimeRatio = total == 0 ? null : 1d - (double)known.Sum(day => day.Failed!.Value) / total,
            Children = children
        };
    }

    private static HistoryDayDto CorrectDay(HistoryDayDto day, IReadOnlyDictionary<DateOnly, HistoryDay> days, ComponentDto[]? children)
    {
        if (days.TryGetValue(day.Date, out var sample))
            return day with { Status = sample.Status.ToApiString(), Uptime = sample.Uptime, Total = sample.Total, Failed = sample.Failed };
        return children is { Length: > 0 } ? GroupDay(day, children)
            : day with { Status = "unknown", Uptime = null, Total = null, Failed = null };
    }

    private static HistoryDayDto GroupDay(HistoryDayDto day, IReadOnlyList<ComponentDto> children)
    {
        var samples = children.Select(child => child.History.FirstOrDefault(sample => sample.Date == day.Date)
            ?? new HistoryDayDto { Date = day.Date }).ToArray();
        var total = samples.Sum(sample => sample.Total ?? 0);
        var failures = samples.Sum(sample => sample.Failed ?? 0);
        var state = new[] { "outage", "degraded", "maintenance", "operational" }
            .FirstOrDefault(status => samples.Any(sample => sample.Total > 0 && sample.Status == status)) ?? "unknown";
        return day with { Status = state, Total = total, Failed = failures, Uptime = total == 0 ? null : 1d - (double)failures / total };
    }
}
