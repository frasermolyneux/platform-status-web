using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcessReference
{
    public string SiteId { get; init; } = "";
    public string ComponentId { get; init; } = "";
    public string CheckId { get; init; } = "";
    public string Resource { get; init; } = "";
    public string Role { get; init; } = "";
    public int Version { get; init; } = 1;
    internal string Identity => $"{SiteId}/{ComponentId}/{CheckId}/{Role}/{Version}";
    internal static int FreshnessSeconds => ProcessMonitoring.Policy.GetProperty("freshnessSeconds").GetInt32();
}

internal sealed record ProcessObservation(ProcessReference Source, DateTimeOffset ObservedAt,
    ComponentStatus Status, int Expected, int Healthy, int Failed, int Unknown);

internal static class ProcessMonitoring
{
    internal static JsonElement Policy { get; } = LoadPolicy();

    private static JsonElement LoadPolicy()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "process-health-policy.json")));
        if (document.RootElement.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported process monitoring policy.");
        return document.RootElement.Clone();
    }

    internal static bool IsApproved(ProcessReference reference) => reference.Version == Policy.GetProperty("version").GetInt32()
        && Policy.GetProperty("bindings").EnumerateArray().Any(binding =>
            binding.GetProperty("siteId").GetString() == reference.SiteId
            && binding.GetProperty("componentId").GetString() == reference.ComponentId
            && binding.GetProperty("checkId").GetString() == reference.CheckId
            && binding.GetProperty("resource").GetString() == reference.Resource
            && binding.GetProperty("role").GetString() == reference.Role);

    internal static string BuildQuery(IEnumerable<ProcessReference> references)
    {
        var sources = references.DistinctBy(source => source.Identity).ToArray();
        if (sources.Length == 0 || sources.Any(source => !IsApproved(source)))
            throw new InvalidOperationException("Process queries require approved strategic source identities.");
        var predicates = sources.Select(source =>
            $"(siteId == {JsonSerializer.Serialize(source.SiteId)}"
            + $" and componentId == {JsonSerializer.Serialize(source.ComponentId)}"
            + $" and checkId == {JsonSerializer.Serialize(source.CheckId)}"
            + $" and cloud_RoleName == {JsonSerializer.Serialize(source.Role)})");
        return """
            customEvents
            | where timestamp >= ago(30m)
            | where name == "ProcessHealthSnapshot" and tostring(customDimensions.contractVersion) == "1"
            | extend siteId=tostring(customDimensions.siteId), componentId=tostring(customDimensions.componentId),
                checkId=tostring(customDimensions.checkId), status=tostring(customDimensions.status),
                expected=toint(customDimensions.expected), healthy=toint(customDimensions.healthy),
                failed=toint(customDimensions.failed), unknown=toint(customDimensions.unknown),
                measuredAt=todatetime(customDimensions.observedAt)
            | where
            """ + " " + string.Join(" or ", predicates) + """

            | summarize arg_max(timestamp, measuredAt, status, expected, healthy, failed, unknown)
                by siteId, componentId, checkId, cloud_RoleName
            | project siteId, componentId, checkId, role=cloud_RoleName, timestamp, measuredAt,
                status, expected, healthy, failed, unknown
            """;
    }

    internal static async Task<IReadOnlyList<ProcessObservation>> QueryAsync(
        LogsQueryClient client, string resource, IReadOnlyList<ProcessReference> sources, CancellationToken ct)
    {
        var response = await client.QueryResourceAsync(new ResourceIdentifier(resource), BuildQuery(sources),
            new QueryTimeRange(TimeSpan.FromMinutes(30)), cancellationToken: ct);
        if (response.Value.Status != LogsQueryResultStatus.Success)
            throw new InvalidOperationException("Application Insights returned incomplete process observations.");
        var table = response.Value.Table;
        var columns = table.Columns.Select((column, index) => (column.Name, Index: index)).ToDictionary(column => column.Name, column => column.Index);
        return table.Rows.Select(row =>
        {
            string Text(string name) => row[columns[name]] as string ?? throw new InvalidDataException($"Missing process field {name}.");
            int Number(string name) => row[columns[name]] switch
            {
                int value => value,
                long value => checked((int)value),
                _ => throw new InvalidDataException($"Invalid process count {name}.")
            };
            DateTimeOffset At(string name) => row[columns[name]] switch
            {
                DateTimeOffset value => value,
                DateTime value => new(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
                _ => throw new InvalidDataException($"Invalid process timestamp {name}.")
            };
            var source = sources.Single(item => item.SiteId == Text("siteId") && item.ComponentId == Text("componentId")
                && item.CheckId == Text("checkId") && item.Role == Text("role"));
            if ((At("timestamp") - At("measuredAt")).Duration() > TimeSpan.FromSeconds(1))
                throw new InvalidDataException("Process assessment time does not match its original telemetry timestamp.");
            var status = Text("status") switch
            {
                "operational" => ComponentStatus.Operational,
                "degraded" => ComponentStatus.Degraded,
                "outage" => ComponentStatus.Outage,
                "unknown" => ComponentStatus.Unknown,
                _ => throw new InvalidDataException("Invalid classified process status.")
            };
            var observation = new ProcessObservation(source, At("measuredAt"), status,
                Number("expected"), Number("healthy"), Number("failed"), Number("unknown"));
            Validate(observation, DateTimeOffset.UtcNow);
            return observation;
        }).ToArray();
    }

    internal static void Validate(ProcessObservation observation, DateTimeOffset now)
    {
        if (!IsApproved(observation.Source) || observation.ObservedAt == default || observation.ObservedAt > now.AddMinutes(5)
            || observation.Status is not (ComponentStatus.Operational or ComponentStatus.Degraded or ComponentStatus.Outage or ComponentStatus.Unknown)
            || observation.Expected < 0 || observation.Healthy < 0 || observation.Failed < 0 || observation.Unknown < 0
            || (long)observation.Healthy + observation.Failed + observation.Unknown != observation.Expected
            || observation.Status == ComponentStatus.Operational && (observation.Expected == 0 || observation.Healthy != observation.Expected)
            || observation.Status == ComponentStatus.Degraded && observation.Failed == 0
            || observation.Status == ComponentStatus.Outage && (observation.Expected == 0 || observation.Failed != observation.Expected)
            || observation.Status == ComponentStatus.Unknown && observation.Expected > 0
                && (observation.Unknown == 0 || observation.Failed > 0))
            throw new InvalidDataException("Invalid process provenance, assessment time or inventory counts.");
    }

    internal static CheckResult? Project(string site, CheckDefinition check, IEnumerable<ProcessObservation> rows,
        DateTimeOffset now, CheckResult? cached = null)
    {
        var source = check.Process ?? throw new InvalidOperationException("A process reference is required.");
        var latest = rows.Where(row => row.Source.Identity == source.Identity).MaxBy(row => row.ObservedAt);
        if (latest is null || cached?.Process?.Identity == source.Identity && cached.ObservedAt > latest.ObservedAt)
            return cached?.Process?.Identity == source.Identity ? cached : null;
        Validate(latest, now);
        return new(site, check.Id, latest.Status, latest.ObservedAt,
            $"Strategic process health: {latest.Healthy}/{latest.Expected} healthy; {latest.Failed} affected; {latest.Unknown} awaiting evidence.")
        {
            Component = check.Component,
            Name = check.Name,
            Process = source,
            ObservationKey = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(latest)))
        };
    }
}
