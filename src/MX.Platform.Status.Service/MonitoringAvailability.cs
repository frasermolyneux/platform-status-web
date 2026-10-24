using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonitoringReference
{
    public string SiteId { get; init; } = "";
    public string ComponentId { get; init; } = "";
    public string CheckId { get; init; } = "";
    public string[] Resources { get; init; } = [];
    public string[] Regions { get; init; } = [];
    public int IntervalSeconds { get; init; } = 30;

    internal string Identity => $"{SiteId}/{ComponentId}/{CheckId}";
    internal int FreshnessSeconds => Math.Max(180, IntervalSeconds * 3);
}

internal sealed record MonitoringObservation(
    string SiteId, string ComponentId, string CheckId, string Region, DateTimeOffset ObservedAt, bool Success, string? Message);

internal static class MonitoringAvailability
{
    internal static string BuildQuery(IEnumerable<MonitoringReference> references)
    {
        var predicates = references.DistinctBy(reference => reference.Identity).Select(reference =>
            $"(siteId == {JsonSerializer.Serialize(reference.SiteId)}"
            + $" and componentId == {JsonSerializer.Serialize(reference.ComponentId)}"
            + $" and checkId == {JsonSerializer.Serialize(reference.CheckId)})").ToArray();
        if (predicates.Length == 0) throw new InvalidOperationException("Monitoring queries require explicit source identities.");
        return """
            availabilityResults
            | where timestamp >= ago(30m)
            | extend siteId=tostring(customDimensions.siteId),
                componentId=tostring(customDimensions.componentId),
                checkId=tostring(customDimensions.checkId),
                region=tostring(customDimensions.region)
            | where
            """ + " " + string.Join(" or ", predicates) + """

            | summarize arg_max(timestamp, success, message) by siteId, componentId, checkId, region
            | project siteId, componentId, checkId, region, observedAt=timestamp, success, message
            """;
    }

    internal static async Task<IReadOnlyList<MonitoringObservation>> QueryAsync(
        LogsQueryClient client, string resourceId, IEnumerable<MonitoringReference> references, CancellationToken ct)
    {
        var response = await client.QueryResourceAsync(new ResourceIdentifier(resourceId), BuildQuery(references),
            new QueryTimeRange(TimeSpan.FromMinutes(30)), cancellationToken: ct);
        if (response.Value.Status != LogsQueryResultStatus.Success)
            throw new InvalidOperationException("Application Insights returned incomplete monitoring results.");
        var table = response.Value.Table;
        var indices = table.Columns.Select((column, index) => (column.Name, Index: index))
            .ToDictionary(column => column.Name, column => column.Index, StringComparer.Ordinal);
        return table.Rows.Select(row => new MonitoringObservation(
            RequiredString(row, indices["siteId"]), RequiredString(row, indices["componentId"]),
            RequiredString(row, indices["checkId"]), RequiredString(row, indices["region"]),
            row[indices["observedAt"]] switch
            {
                DateTimeOffset at => at,
                DateTime at => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
                _ => throw new InvalidDataException("Monitoring observation timestamp is invalid.")
            },
            row[indices["success"]] is bool success ? success
                : throw new InvalidDataException("Monitoring observation outcome is invalid."),
            row[indices["message"]] as string)).ToArray();
    }

    private static string RequiredString(LogsTableRow row, int index) => row[index] is string { Length: > 0 } value
        ? value : throw new InvalidDataException("Monitoring observation identity is missing.");

    internal static CheckResult? Project(string site, CheckDefinition check,
        IEnumerable<MonitoringObservation> observations, DateTimeOffset now)
    {
        var source = check.Monitoring ?? throw new InvalidOperationException("A monitoring reference is required.");
        var latest = observations.Where(observation => observation.SiteId == source.SiteId
                && observation.ComponentId == source.ComponentId && observation.CheckId == source.CheckId
                && source.Regions.Contains(observation.Region, StringComparer.OrdinalIgnoreCase))
            .GroupBy(observation => observation.Region, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(observation => observation.ObservedAt)!)
            .OrderBy(observation => observation.Region, StringComparer.Ordinal).ToArray();
        if (latest.Length == 0) return null;
        if (latest.Any(observation => observation.ObservedAt > now.AddMinutes(5)))
            throw new InvalidDataException("Monitoring observation timestamp is in the future.");
        var fresh = latest.Where(observation => now - observation.ObservedAt
            <= TimeSpan.FromSeconds(source.FreshnessSeconds)).ToArray();
        var failed = fresh.Where(observation => !observation.Success).ToArray();
        var missing = source.Regions.Where(region => !fresh.Any(observation =>
            observation.Region.Equals(region, StringComparison.OrdinalIgnoreCase))).ToArray();
        var status = fresh.Length == 0 ? ComponentStatus.Unknown
            : failed.Length == fresh.Length ? ComponentStatus.Outage
            : failed.Length > 0 || missing.Length > 0 ? ComponentStatus.Degraded : ComponentStatus.Operational;
        var detail = $"Sitewatch via Application Insights: {fresh.Length}/{source.Regions.Length} regions reporting."
            + (failed.Length == 0 ? "" : $" Failing: {string.Join(", ", failed.Select(observation => observation.Region
                + (string.IsNullOrWhiteSpace(observation.Message) ? "" : $" ({observation.Message[..Math.Min(180, observation.Message.Length)]})")))}.")
            + (missing.Length == 0 ? "" : $" Missing/stale: {string.Join(", ", missing)}.");
        return new(site, check.Id, status, latest.Max(observation => observation.ObservedAt), detail)
        {
            Component = check.Component,
            Name = check.Name,
            Monitoring = source,
            ObservationKey = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(latest.Select(
                observation => new { observation.Region, observation.ObservedAt, observation.Success })))),
            Regions = latest.Select(observation => new RegionAvailabilityTelemetry(
                observation.Region, 1, observation.Success ? 0 : 1, observation.ObservedAt)).ToArray()
        };
    }
}
