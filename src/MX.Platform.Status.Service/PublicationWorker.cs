using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;
using System.Security.Cryptography;
using System.Text.Json;

namespace MX.Platform.Status.Service;

public sealed class PublicationWorker(
    StatusCatalog catalog, RuntimeOptions options, PublicationStore files, PostgresStore database,
    ILogger<PublicationWorker> logger) : BackgroundService
{
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
        var telemetry = new Dictionary<string, Dictionary<string, ComponentLiveTelemetry>>();
        var signals = new Dictionary<string, IReadOnlyList<CheckResult>>();
        foreach (var definition in catalog.Sites)
        {
            signals[definition.Site.Id] = await database.ReadChecksAsync(definition.Site.Id, ct);
            telemetry[definition.Site.Id] = ComponentChecks.Build(definition, [], signals[definition.Site.Id]);
        }
        await RecordHistoryAsync(publisherLock, ct);
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
            var propagation = new ImpactPropagation(site.Components);
            publication = IncidentImpact.Apply(CorrectTodayHistory(publication, history, propagation),
                incidents.Select(incident => incident.ToPublicIncident()), propagation);
            publication = RecentHistory.Apply(publication, await database.ReadRecentBucketsAsync(site.Site.Id, ct),
                incidents.Select(incident => incident.ToPublicIncident()).ToArray(), propagation);
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

    private async Task RecordHistoryAsync(PublisherLease lease, CancellationToken ct)
    {
        await using var transaction = await lease.Connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var pending = await lease.ReadPendingMonitoringAsync(ct);
        foreach (var batch in pending)
        {
            await RecordHistorySnapshotAsync(lease, batch.Signals, ct);
            await lease.AcknowledgeMonitoringAsync(batch.Id, ct);
        }
        // A newer cache vector must not advance cursors past queued outages.
        if (pending.Count < PublisherLease.MonitoringBatchLimit)
            await RecordHistorySnapshotAsync(lease, await lease.ReadCheckSnapshotAsync(ct), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task RecordHistorySnapshotAsync(PublisherLease lease, IReadOnlyList<CheckResult> observations,
        CancellationToken ct)
    {
        var signals = catalog.Sites.ToDictionary(site => site.Site.Id,
            site => observations.Where(signal => signal.Site == site.Site.Id).ToArray());
        var telemetry = catalog.Sites.ToDictionary(site => site.Site.Id,
            site => ComponentChecks.Build(site, [], signals[site.Site.Id]));
        foreach (var site in catalog.Sites)
        {
            var incidents = await database.ReadIncidentsAsync(catalog, site, true, ct);
            var live = catalog.ProjectTelemetry(site, telemetry);
            foreach (var component in live.Keys)
            {
                var identities = HistoryInputIdentities(site, component);
                var inputs = catalog.Sites.SelectMany(source => source.EffectiveChecks
                    .Where(check => identities.Contains(InputIdentity(source, check)))
                    .SelectMany(check => signals[source.Site.Id].Where(signal => signal.CheckId == check.Id
                            && ComponentChecks.MatchesMonitoringSource(check, signal))
                        .Select(signal => (Identity: InputIdentity(source, check), Signal: signal))))
                    .GroupBy(input => input.Identity)
                    .Select(group => group.MaxBy(input => input.Signal.ObservedAt))
                    .OrderBy(input => input.Identity, StringComparer.Ordinal).ToArray();
                if (inputs.Length == 0) continue;
                var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs.Select(input =>
                    new
                    {
                        input.Identity,
                        Key = input.Signal.HistoryKey
                    }), StatusJson.Options)));
                var observedAt = inputs.Max(input => input.Signal.ObservedAt);
                var measured = catalog.Sites.ToDictionary(source => source.Site.Id,
                    source => ComponentChecks.Build(source, [], signals[source.Site.Id], observedAt));
                var sample = catalog.ProjectTelemetry(site, measured).GetValueOrDefault(component);
                if (sample is null || sample.Samples == 0) continue;
                await lease.RecordMonitoringSampleAsync(site.Site.Id, component, fingerprint, observedAt,
                    IsMaintained(site, component, observedAt, incidents) ? null : sample.Failures == 0, ct,
                    ComponentStatusCalculator.WorstOf(sample.Checks.Select(check => check.Status)));
            }
        }
    }

    private static string InputIdentity(SiteDefinition site, CheckDefinition check) =>
        check.Process?.Identity ?? check.Monitoring?.Identity ?? check.SharedId ?? $"{site.Site.Id}/{check.Id}";

    private HashSet<string> HistoryInputIdentities(SiteDefinition site, string component)
    {
        var shared = site.SharedComponents.GetValueOrDefault(component);
        return catalog.Sites.SelectMany(source => source.EffectiveChecks
            .Where(check => shared is null
                ? source.Site.Id == site.Site.Id && check.Component == component
                : source.SharedComponents.GetValueOrDefault(check.Component) == shared)
            .Select(check => InputIdentity(source, check))).ToHashSet();
    }

    internal static bool IsMaintained(SiteDefinition site, string componentId, DateTimeOffset at,
        IReadOnlyList<IncidentRecord> incidents)
    {
        var scopes = StatusCatalog.PublicComponents(site.Components.Components)
            .Where(component => StatusCatalog.PublicComponents([component]).Any(child => child.Id == componentId))
            .Select(component => component.Id).ToHashSet();
        return incidents.Where(incident => incident.Published && !(incident.ResolvedAt <= at)).Any(incident =>
        {
            if (incident.ImpactHistory.Count == 0)
                return incident.Severity == Severity.Maintenance && incident.StartedAt <= at
                    && incident.ScheduledStart <= at && incident.ScheduledEnd > at
                    && incident.Components.Any(scopes.Contains);
            return incident.ImpactHistory.Where(observation => observation.At <= at)
                .GroupBy(observation => observation.SourceSite)
                .Select(source => source.MaxBy(observation => observation.At)!)
                .Any(observation => observation.State != IncidentState.Resolved && observation.Severity == Severity.Maintenance
                    && observation.ScheduledStart <= at && observation.ScheduledEnd > at
                    && observation.Components.Any(scopes.Contains));
        });
    }

    internal static StatusApiResponse CorrectTodayHistory(StatusApiResponse publication, HistoryDocument history,
        ImpactPropagation? propagation = null) =>
        publication with { Components = publication.Components.Select(c => CorrectComponent(c, history, propagation)).ToArray() };

    private static ComponentDto CorrectComponent(ComponentDto component, HistoryDocument history, ImpactPropagation? propagation)
    {
        var days = history.ComponentsById.TryGetValue(component.Id, out var record)
            ? record.Days.ToDictionary(day => day.Date) : [];
        var children = component.Children?.Select(child => CorrectComponent(child, history, propagation)).ToArray();
        var corrected = component.History.Select(day => CorrectDay(day, days, children, component.Id, propagation)).ToArray();
        var known = corrected.Where(day => day.Total > 0 && day.Failed.HasValue && day.Status != "maintenance").ToArray();
        var total = known.Sum(day => day.Total!.Value);
        return component with
        {
            History = corrected,
            UptimeRatio = total == 0 ? null : 1d - (double)known.Sum(day => day.Failed!.Value) / total,
            Children = children
        };
    }

    private static HistoryDayDto CorrectDay(HistoryDayDto day, IReadOnlyDictionary<DateOnly, HistoryDay> days,
        ComponentDto[]? children, string component, ImpactPropagation? propagation)
    {
        if (days.TryGetValue(day.Date, out var sample))
            return day with { Status = sample.Status.ToApiString(), Uptime = sample.Uptime, Total = sample.Total, Failed = sample.Failed };
        return children is { Length: > 0 } ? GroupDay(day, children, component, propagation)
            : day with { Status = "unknown", Uptime = null, Total = null, Failed = null };
    }

    private static HistoryDayDto GroupDay(HistoryDayDto day, IReadOnlyList<ComponentDto> children,
        string component, ImpactPropagation? propagation)
    {
        var samples = children.Select(child =>
        {
            var sample = child.History.FirstOrDefault(sample => sample.Date == day.Date) ?? new HistoryDayDto { Date = day.Date };
            return sample with { Status = propagation?.Contribute(component, child.Id, sample.Status) ?? sample.Status };
        }).ToArray();
        var total = samples.Sum(sample => sample.Total ?? 0);
        var failures = samples.Sum(sample => sample.Failed ?? 0);
        var state = new[] { "outage", "degraded", "maintenance", "operational" }
            .FirstOrDefault(status => samples.Any(sample => sample.Total > 0 && sample.Status == status)) ?? "unknown";
        return day with { Status = state, Total = total, Failed = failures, Uptime = total == 0 ? null : 1d - (double)failures / total };
    }
}
