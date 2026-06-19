using Azure.Storage.Blobs;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Rollup;

public sealed class YearlyAggregator
{
    private readonly BlobContainerClient _containerClient;

    public YearlyAggregator(BlobServiceClient blobServiceClient)
    {
        var containerName = Environment.GetEnvironmentVariable("HISTORY_BLOB_CONTAINER") ?? "history";
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public YearlyHistoryDocument Build(HistoryDocument history)
    {
        var yearly = new YearlyHistoryDocument
        {
            Site = history.Site,
            GeneratedAt = DateTimeOffset.UtcNow,
            ComponentsById = history.ComponentsById.ToDictionary(
                pair => pair.Key,
                pair => new YearlyComponentHistory
                {
                    DisplayName = pair.Value.DisplayName,
                    Years = pair.Value.Days.GroupBy(day => day.Date.Year)
                        .OrderBy(group => group.Key)
                        .Select(group => BuildYearSummary(group.Key, group.ToArray()))
                        .ToList()
                },
                StringComparer.OrdinalIgnoreCase)
        };

        return yearly;
    }

    public async Task WriteAsync(HistoryDocument history, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var yearly = Build(history);
        var blobClient = _containerClient.GetBlobClient($"history/{history.Site}-yearly.json");
        await blobClient.UploadAsync(BinaryData.FromObjectAsJson(yearly, StatusJson.Options), overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    private static YearSummary BuildYearSummary(int year, IReadOnlyList<HistoryDay> days)
    {
        var quarters = Enumerable.Range(1, 4)
            .Select(quarter => new QuarterSummary
            {
                Q = quarter,
                Uptime = AverageUptime(days.Where(day => ((day.Date.Month - 1) / 3) + 1 == quarter).ToArray())
            })
            .ToList();

        return new YearSummary
        {
            Year = year,
            Uptime = AverageUptime(days),
            TotalDays = days.Count,
            OperationalDays = days.Count(day => day.Status == ComponentStatus.Operational),
            DegradedDays = days.Count(day => day.Status == ComponentStatus.Degraded),
            OutageDays = days.Count(day => day.Status == ComponentStatus.Outage),
            UnknownDays = days.Count(day => day.Status == ComponentStatus.Unknown),
            MaintenanceDays = days.Count(day => day.Status == ComponentStatus.Maintenance),
            // TODO: Phase 2 — derive incident count from incident data once available in rollup context
            IncidentCount = 0,
            Quarters = quarters
        };
    }

    private static double? AverageUptime(IEnumerable<HistoryDay> days)
    {
        var eligible = days.Where(day => day.Status != ComponentStatus.Maintenance && day.Uptime.HasValue).Select(day => day.Uptime!.Value).ToArray();
        return eligible.Length == 0 ? null : eligible.Average();
    }
}
