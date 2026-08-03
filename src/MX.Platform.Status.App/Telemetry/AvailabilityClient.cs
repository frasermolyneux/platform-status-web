using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Core;

namespace MX.Platform.Status.App.Telemetry;

public sealed class AvailabilityClient
{
    private readonly LogsQueryClient _logsQueryClient;
    private readonly AvailabilityQueryBuilder _queryBuilder;

    public AvailabilityClient(LogsQueryClient logsQueryClient, AvailabilityQueryBuilder queryBuilder)
    {
        _logsQueryClient = logsQueryClient;
        _queryBuilder = queryBuilder;
    }

    public async Task<IReadOnlyList<RegionAvailabilityTelemetry>> QueryLiveRegionalAsync(string resourceId, IReadOnlyDictionary<string, object?> filters, int lookbackMinutes, CancellationToken cancellationToken = default)
    {
        var query = _queryBuilder.BuildLiveRegionalQuery(filters, lookbackMinutes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await _logsQueryClient.QueryResourceAsync(new ResourceIdentifier(resourceId), query, new QueryTimeRange(TimeSpan.FromMinutes(lookbackMinutes)), cancellationToken: timeout.Token).ConfigureAwait(false);
        var table = response.Value.Table;
        if (table.Rows.Count == 0)
        {
            return [];
        }

        var indices = BuildColumnIndex(table);
        var results = new List<RegionAvailabilityTelemetry>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var region = ReadString(row, indices, "region");
            results.Add(new RegionAvailabilityTelemetry(
                string.IsNullOrWhiteSpace(region) ? "unknown" : region,
                ReadInt(row, indices, "total"),
                ReadInt(row, indices, "failures"),
                ReadDateTimeOffset(row, indices, "lastSeen")));
        }

        return results;
    }

    public async Task<IReadOnlyList<DailyAvailabilityTelemetry>> QueryDailyRollupAsync(string resourceId, IReadOnlyDictionary<string, object?> filters, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var query = _queryBuilder.BuildDailyRollupQuery(filters, startDate, endDate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await _logsQueryClient.QueryResourceAsync(new ResourceIdentifier(resourceId), query, new QueryTimeRange(startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), endDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), cancellationToken: timeout.Token).ConfigureAwait(false);
        var table = response.Value.Table;
        if (table.Rows.Count == 0)
        {
            return [];
        }

        var indices = BuildColumnIndex(table);
        var results = new List<DailyAvailabilityTelemetry>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var day = ReadDateTimeOffset(row, indices, "day") ?? new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            results.Add(new DailyAvailabilityTelemetry(
                DateOnly.FromDateTime(day.UtcDateTime),
                ReadInt(row, indices, "total"),
                ReadInt(row, indices, "failures"),
                ReadDouble(row, indices, "uptime"),
                ReadDateTimeOffset(row, indices, "lastSeen"),
                ReadDouble(row, indices, "p95")));
        }

        return results;
    }

    private static Dictionary<string, int> BuildColumnIndex(LogsTable table) =>
        table.Columns.Select((column, index) => new { column.Name, index })
            .ToDictionary(item => item.Name, item => item.index, StringComparer.OrdinalIgnoreCase);

    private static object? GetValue(LogsTableRow row, IReadOnlyDictionary<string, int> indices, string name) =>
        indices.TryGetValue(name, out var index) ? row[index] : null;

    private static string? ReadString(LogsTableRow row, IReadOnlyDictionary<string, int> indices, string name) =>
        GetValue(row, indices, name) as string;

    private static int ReadInt(LogsTableRow row, IReadOnlyDictionary<string, int> indices, string name)
    {
        var value = GetValue(row, indices, name);
        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => checked((int)doubleValue),
            decimal decimalValue => checked((int)decimalValue),
            _ => 0
        };
    }

    private static double? ReadDouble(LogsTableRow row, IReadOnlyDictionary<string, int> indices, string name)
    {
        var value = GetValue(row, indices, name);
        return value switch
        {
            null => null,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            decimal decimalValue => (double)decimalValue,
            long longValue => longValue,
            int intValue => intValue,
            _ => null
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(LogsTableRow row, IReadOnlyDictionary<string, int> indices, string name)
    {
        var value = GetValue(row, indices, name);
        return value switch
        {
            null => null,
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => null
        };
    }
}

public sealed record ComponentLiveTelemetry(int Samples, int Failures, DateTimeOffset? LastSeen, double? P95)
{
    /// <summary>
    /// Per-region breakdown backing regional status classification (see
    /// <see cref="ComponentStatusCalculator.ClassifyLiveStatusRegional"/>). Empty when the component
    /// has no regional telemetry (e.g. static components) or the underlying query returned no rows.
    /// </summary>
    public IReadOnlyList<RegionAvailabilityTelemetry> Regions { get; init; } = [];
}

public sealed record RegionAvailabilityTelemetry(string Region, int Samples, int Failures, DateTimeOffset? LastSeen);

public sealed record DailyAvailabilityTelemetry(DateOnly Date, int Total, int Failures, double? Uptime, DateTimeOffset? LastSeen, double? P95);
