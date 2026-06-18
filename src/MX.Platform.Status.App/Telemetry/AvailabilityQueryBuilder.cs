using System.Globalization;
using System.Text.Json;

namespace MX.Platform.Status.App.Telemetry;

public sealed class AvailabilityQueryBuilder
{
    private static readonly string[] ForbiddenTokens = [";", "|", "//", "/*", "*/", "\r", "\n"];

    public string BuildLiveTodayQuery(IReadOnlyDictionary<string, object?> filters)
    {
        var where = BuildWhereClauses(filters);
        return $$"""
availabilityResults
| where timestamp >= startofday(now())
{{where}}
| summarize total = sum(itemCount), failures = sumif(itemCount, success == false), lastSeen = max(timestamp), p95 = percentile(duration, 95)
| project total, failures, lastSeen, p95
""";
    }

    public string BuildRecentProbeQuery(IReadOnlyDictionary<string, object?> filters, int lookbackMinutes = 15)
    {
        if (lookbackMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookbackMinutes));
        }

        var where = BuildWhereClauses(filters);
        return $$"""
availabilityResults
| where timestamp >= ago({{lookbackMinutes}}m)
{{where}}
| summarize total = sum(itemCount), failures = sumif(itemCount, success == false), lastSeen = max(timestamp), p95 = percentile(duration, 95)
| project total, failures, lastSeen, p95
""";
    }

    public string BuildDailyRollupQuery(IReadOnlyDictionary<string, object?> filters, DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate)
        {
            throw new ArgumentOutOfRangeException(nameof(endDate));
        }

        var where = BuildWhereClauses(filters);
        var start = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endExclusive = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return $$"""
availabilityResults
| where timestamp >= datetime({{start:yyyy-MM-ddTHH:mm:ssZ}}) and timestamp < datetime({{endExclusive:yyyy-MM-ddTHH:mm:ssZ}})
{{where}}
| summarize total = sum(itemCount), failures = sumif(itemCount, success == false), lastSeen = max(timestamp), p95 = percentile(duration, 95) by day = startofday(timestamp)
| extend uptime = iif(total == 0, real(null), todouble(total - failures) / todouble(total))
| project day, total, failures, uptime, lastSeen, p95
| order by day asc
""";
    }

    private static string BuildWhereClauses(IReadOnlyDictionary<string, object?> filters)
    {
        if (filters.Count == 0)
        {
            return string.Empty;
        }

        var clauses = filters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => BuildWhereClause(pair.Key, pair.Value));

        return string.Join(Environment.NewLine, clauses.Select(clause => $"| where {clause}"));
    }

    private static string BuildWhereClause(string key, object? value)
    {
        if (!key.StartsWith("customDimensions.", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Only customDimensions.* filter keys are allowed: '{key}'.", nameof(key));
        }

        var dimensionName = key["customDimensions.".Length..];
        if (string.IsNullOrWhiteSpace(dimensionName))
        {
            throw new ArgumentException("Custom dimension name cannot be empty.", nameof(key));
        }

        var filterValue = ConvertToFilterValue(value);
        foreach (var token in ForbiddenTokens)
        {
            if (filterValue.Contains(token, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Filter value contains forbidden KQL metacharacters: '{filterValue}'.", nameof(value));
            }
        }

        var dynamicArray = $"dynamic({JsonSerializer.Serialize(new[] { filterValue })})";
        return $"tostring(customDimensions[\"{dimensionName}\"]) in ({dynamicArray})";
    }

    private static string ConvertToFilterValue(object? value) => value switch
    {
        null => throw new ArgumentNullException(nameof(value)),
        bool boolValue => boolValue ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? throw new ArgumentNullException(nameof(value))
    };
}
