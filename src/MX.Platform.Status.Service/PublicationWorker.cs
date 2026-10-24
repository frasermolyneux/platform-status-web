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
        await RecordHistoryAsync(publisherLock, telemetry, signals, ct);
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

    private async Task RecordHistoryAsync(PublisherLease lease, Dictionary<string, Dictionary<string, ComponentLiveTelemetry>> telemetry,
        IReadOnlyDictionary<string, IReadOnlyList<CheckResult>> signals, CancellationToken ct)
    {
        await using var transaction = await lease.Connection.BeginTransactionAsync(ct);
        foreach (var site in catalog.Sites)
        {
            var incidents = await database.ReadIncidentsAsync(catalog, site, true, ct);
            var live = catalog.ProjectTelemetry(site, telemetry);
            foreach (var (component, sample) in live.Where(pair => pair.Value.Samples > 0))
            {
                var shared = site.SharedComponents.GetValueOrDefault(component);
                var identities = catalog.Sites.SelectMany(source => source.EffectiveChecks
                    .Where(check => shared is null
                        ? source.Site.Id == site.Site.Id && check.Component == component
                        : source.SharedComponents.GetValueOrDefault(check.Component) == shared)
                    .Select(check => InputIdentity(source, check))).ToHashSet();
                var inputs = catalog.Sites.SelectMany(source => source.EffectiveChecks
                    .Where(check => identities.Contains(InputIdentity(source, check)))
                    .SelectMany(check => signals[source.Site.Id].Where(signal => signal.CheckId == check.Id)
                        .Select(signal => (Identity: InputIdentity(source, check), Signal: signal))))
                    .GroupBy(input => input.Identity)
                    .Select(group => group.MaxBy(input => input.Signal.ObservedAt))
                    .OrderBy(input => input.Identity, StringComparer.Ordinal).ToArray();
                if (inputs.Length == 0) continue;
                var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs.Select(input =>
                    new { input.Identity, Key = input.Signal.ObservationKey
                        ?? $"{input.Signal.ObservedAt:O}/{input.Signal.Status}" }), StatusJson.Options)));
                var observedAt = inputs.Max(input => input.Signal.ObservedAt);
                await lease.RecordMonitoringSampleAsync(site.Site.Id, component, fingerprint, observedAt,
                    IsMaintained(site, component, observedAt, incidents) ? null : sample.Failures == 0, ct);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private static string InputIdentity(SiteDefinition site, CheckDefinition check) =>
        check.Monitoring?.Identity ?? check.SharedId ?? $"{site.Site.Id}/{check.Id}";

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
