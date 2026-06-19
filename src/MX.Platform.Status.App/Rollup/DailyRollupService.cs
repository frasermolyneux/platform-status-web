using Azure.Storage.Blobs;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.App.Rollup;

public sealed class DailyRollupService
{
    private readonly BlobContainerClient _containerClient;
    private readonly AvailabilityClient _availabilityClient;
    private readonly ComponentStatusCalculator _statusCalculator;
    private readonly OverrideApplier _overrideApplier;

    public DailyRollupService(BlobServiceClient blobServiceClient, AvailabilityClient availabilityClient, ComponentStatusCalculator statusCalculator, OverrideApplier overrideApplier)
    {
        var containerName = Environment.GetEnvironmentVariable("HISTORY_BLOB_CONTAINER") ?? "history";
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        _availabilityClient = availabilityClient;
        _statusCalculator = statusCalculator;
        _overrideApplier = overrideApplier;
    }

    public async Task<HistoryDocument> RollAsync(SiteConfigurationSnapshot snapshot, int replayDays, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var blobClient = _containerClient.GetBlobClient($"history/{snapshot.Site.Id}.json");
        var existing = await ReadExistingAsync(blobClient, cancellationToken).ConfigureAwait(false)
            ?? new HistoryDocument { Site = snapshot.Site.Id };

        var endDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var startDate = endDate.AddDays(-(Math.Max(1, replayDays) - 1));

        foreach (var component in Flatten(snapshot.Components.Components))
        {
            if (!existing.ComponentsById.TryGetValue(component.Id, out var record))
            {
                record = new ComponentHistoryRecord
                {
                    DisplayName = component.Name,
                    Kind = component.Kind
                };
                existing.ComponentsById[component.Id] = record;
            }

            if (component.Kind.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rollupDays = await BuildDaysAsync(snapshot, component, startDate, endDate, cancellationToken).ConfigureAwait(false);
            MergeDays(record.Days, rollupDays);
        }

        foreach (var component in Flatten(snapshot.Components.Components).Where(component => component.Kind.Equals("group", StringComparison.OrdinalIgnoreCase)))
        {
            existing.ComponentsById[component.Id] = BuildGroupRecord(existing, component);
        }

        await _overrideApplier.ApplyAsync(snapshot.Site.Id, existing, cancellationToken).ConfigureAwait(false);
        var updated = existing with { GeneratedAt = DateTimeOffset.UtcNow, RolledUpThrough = endDate };
        await blobClient.UploadAsync(BinaryData.FromObjectAsJson(updated, StatusJson.Options), overwrite: true, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<List<HistoryDay>> BuildDaysAsync(SiteConfigurationSnapshot snapshot, Component component, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        if (component.Source.Kind.Equals("static", StringComparison.OrdinalIgnoreCase))
        {
            var status = ParseStaticStatus(component.Source.Status);
            var results = new List<HistoryDay>();
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                results.Add(new HistoryDay
                {
                    Date = date,
                    Status = status,
                    Uptime = status == ComponentStatus.Operational ? 1d : status == ComponentStatus.Unknown ? null : 0d,
                    Total = status == ComponentStatus.Unknown ? 0 : 1,
                    Failed = status == ComponentStatus.Operational ? 0 : (status == ComponentStatus.Unknown ? 0 : 1)
                });
            }

            return results;
        }

        if (!component.Source.Kind.Equals("appInsights", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(component.Source.Resource))
        {
            return [];
        }

        if (!snapshot.Site.AppInsights.TryGetValue(component.Source.Resource, out var resource))
        {
            return [];
        }

        var queryResults = await _availabilityClient.QueryDailyRollupAsync(resource.ResourceId, component.Source.Filter, startDate, endDate, cancellationToken).ConfigureAwait(false);
        var byDate = queryResults.ToDictionary(result => result.Date, result => result);
        var days = new List<HistoryDay>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            if (byDate.TryGetValue(date, out var result))
            {
                days.Add(new HistoryDay
                {
                    Date = date,
                    Status = _statusCalculator.ClassifyHistoricStatus(result.Total, result.Uptime, component.Sla),
                    Uptime = result.Uptime,
                    Total = result.Total,
                    Failed = result.Failures,
                    P95 = result.P95
                });
            }
            else
            {
                days.Add(new HistoryDay { Date = date, Status = ComponentStatus.Unknown, Total = 0 });
            }
        }

        return days;
    }

    private static ComponentHistoryRecord BuildGroupRecord(HistoryDocument existing, Component group)
    {
        var children = group.Children.Where(child => existing.ComponentsById.ContainsKey(child.Id)).ToArray();
        var days = children
            .SelectMany(child => existing.ComponentsById[child.Id].Days)
            .GroupBy(day => day.Date)
            .OrderBy(grouping => grouping.Key)
            .Select(grouping => new HistoryDay
            {
                Date = grouping.Key,
                Status = MergeStatuses(grouping.Select(item => item.Status)),
                Uptime = grouping.Any(item => item.Uptime.HasValue)
                    ? grouping.Where(item => item.Uptime.HasValue).Average(item => item.Uptime!.Value)
                    : null,
                Total = grouping.Sum(item => item.Total ?? 0),
                Failed = grouping.Sum(item => item.Failed ?? 0),
                P95 = grouping.Any(item => item.P95.HasValue)
                    ? grouping.Where(item => item.P95.HasValue).Max(item => item.P95!.Value)
                    : null
            }).ToList();

        return new ComponentHistoryRecord
        {
            DisplayName = group.Name,
            Kind = group.Kind,
            Days = days
        };
    }

    private static ComponentStatus ParseStaticStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "operational" => ComponentStatus.Operational,
        "degraded" => ComponentStatus.Degraded,
        "outage" => ComponentStatus.Outage,
        "maintenance" => ComponentStatus.Maintenance,
        _ => ComponentStatus.Unknown
    };

    private static ComponentStatus MergeStatuses(IEnumerable<ComponentStatus> statuses)
    {
        var values = statuses.ToArray();
        if (values.Contains(ComponentStatus.Outage)) return ComponentStatus.Outage;
        if (values.Contains(ComponentStatus.Degraded)) return ComponentStatus.Degraded;
        if (values.Contains(ComponentStatus.Maintenance)) return ComponentStatus.Maintenance;
        if (values.Contains(ComponentStatus.Operational)) return ComponentStatus.Operational;
        return ComponentStatus.Unknown;
    }

    private static void MergeDays(List<HistoryDay> existingDays, IEnumerable<HistoryDay> updatedDays)
    {
        var byDate = existingDays.ToDictionary(day => day.Date, day => day);
        foreach (var day in updatedDays)
        {
            byDate[day.Date] = day;
        }

        existingDays.Clear();
        existingDays.AddRange(byDate.Values.OrderBy(day => day.Date));
    }

    private static IEnumerable<Component> Flatten(IEnumerable<Component> components)
    {
        foreach (var component in components)
        {
            yield return component;
            foreach (var child in Flatten(component.Children))
            {
                yield return child;
            }
        }
    }

    private static async Task<HistoryDocument?> ReadExistingAsync(BlobClient blobClient, CancellationToken cancellationToken)
    {
        if (!await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var download = await blobClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return download.Value.Content.ToObjectFromJson<HistoryDocument>(StatusJson.Options);
    }
}
