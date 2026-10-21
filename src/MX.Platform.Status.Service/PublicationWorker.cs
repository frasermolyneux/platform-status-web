using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

public sealed class PublicationWorker(
    StatusCatalog catalog, RuntimeOptions options, PublicationStore files, PostgresStore database,
    IHttpClientFactory clients, ILogger<PublicationWorker> logger) : BackgroundService
{
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
            try { await Task.Delay(TimeSpan.FromSeconds(options.Publisher ? options.RefreshSeconds : 10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!options.Publisher)
        {
            foreach (var site in catalog.Sites)
            {
                var publication = await database.ReadPublicationAsync(site.Site.Id, ct);
                if (publication is not null) await files.WriteAsync(publication, ct);
            }
            return;
        }
        await database.InitializeAsync(ct);
        if (!await database.IsWritableAsync(ct))
            throw new InvalidOperationException("Publisher database is in recovery; refusing publication.");
        await using var publisherLock = await database.TryPublisherLockAsync(ct)
            ?? throw new InvalidOperationException("Another publisher holds the database lock.");
        await PublishAsync(ct);
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        foreach (var site in catalog.Sites)
        {
            var publicIds = StatusCatalog.PublicComponents(site.Components.Components).Select(c => c.Id).ToHashSet();
            var incidents = (await database.ReadIncidentsAsync(site.Site.Id, ct))
                .Where(i => i.Published && i.Components.All(publicIds.Contains)).ToArray();
            var live = new Dictionary<string, ComponentLiveTelemetry>();
            foreach (var (component, target) in site.Probes)
            {
                var success = false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, target);
                    using var response = await clients.CreateClient("probe").SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    success = (int)response.StatusCode is >= 200 and < 400;
                }
                catch (HttpRequestException exception)
                {
                    logger.LogWarning(exception, "Independent probe failed for {Site}/{Component}.", site.Site.Id, component);
                }
                catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Independent probe timed out for {Site}/{Component}.", site.Site.Id, component);
                }
                ct.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                var maintenance = incidents.Any(i => i.Severity == Severity.Maintenance
                    && i.State != IncidentState.Resolved && i.ScheduledStart <= now && i.ScheduledEnd > now
                    && i.Components.Any(id => StatusCatalog.Flatten(site.Components.Components)
                        .Where(c => c.Id == id).SelectMany(c => StatusCatalog.Flatten([c])).Any(c => c.Id == component)));
                if (!maintenance) await database.RecordProbeAsync(site.Site.Id, component, success, ct);
                live[component] = new ComponentLiveTelemetry(1, success ? 0 : 1, now, null);
            }

            var history = await database.ReadHistoryAsync(site.Site.Id, ct);
            var publication = new StatusMerger().Merge(site.Snapshot, live, history,
                incidents.Where(i => i.Severity != Severity.Maintenance).Select(i => i.ToPublicIncident()),
                incidents.Where(i => i.Severity == Severity.Maintenance).Select(i => i.ToMaintenance(DateTimeOffset.UtcNow)));
            publication = CorrectTodayHistory(publication, history);
            await database.SavePublicationAsync(publication, ct);
            await files.WriteAsync(publication, ct);
        }
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
        return children is { Length: > 0 } ? GroupDay(day, children) : day;
    }

    private static HistoryDayDto GroupDay(HistoryDayDto day, IReadOnlyList<ComponentDto> children)
    {
        var samples = children.Select(child => child.History.Single(sample => sample.Date == day.Date)).ToArray();
        var total = samples.Sum(sample => sample.Total ?? 0);
        var failures = samples.Sum(sample => sample.Failed ?? 0);
        var state = new[] { "outage", "degraded", "maintenance", "unknown", "operational" }
            .FirstOrDefault(status => samples.Any(sample => sample.Status == status)) ?? "unknown";
        return day with { Status = state, Total = total, Failed = failures, Uptime = total == 0 ? null : 1d - (double)failures / total };
    }
}
