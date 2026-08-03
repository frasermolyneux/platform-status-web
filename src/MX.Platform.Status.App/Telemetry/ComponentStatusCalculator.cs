using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Telemetry;

public static class ComponentStatusCalculator
{
    public static ComponentStatus ClassifyLiveStatus(int samples, int failures, DateTimeOffset? lastSeen, SlaDefinition sla)
    {
        if (samples == 0)
        {
            return ComponentStatus.Unknown;
        }

        var stalenessThreshold = TimeSpan.FromSeconds(Math.Max(1, sla.ExpectedIntervalSeconds) * 3d);
        if (lastSeen is null || DateTimeOffset.UtcNow - lastSeen.Value > stalenessThreshold)
        {
            return ComponentStatus.Unknown;
        }

        if (failures <= 0)
        {
            return ComponentStatus.Operational;
        }

        var successRatio = 1d - (double)failures / samples;
        if (successRatio < sla.OutageBelow)
        {
            return ComponentStatus.Outage;
        }

        if (successRatio < sla.DegradedBelow)
        {
            return ComponentStatus.Degraded;
        }

        return ComponentStatus.Operational;
    }

    /// <summary>
    /// Classifies a component's live status from independent per-region samples.
    /// <para>
    /// Rules: a region is "reporting" when it has recent (non-stale) samples; a reporting region is
    /// "failing" when its success ratio drops below <see cref="SlaDefinition.OutageBelow"/>. No
    /// reporting regions at all -&gt; <see cref="ComponentStatus.Unknown"/>. All reporting regions
    /// failing -&gt; <see cref="ComponentStatus.Outage"/>. No reporting region failing and no expected
    /// region missing -&gt; <see cref="ComponentStatus.Operational"/>. Anything else (a subset failing,
    /// or an expected region missing/stale while others are healthy) -&gt;
    /// <see cref="ComponentStatus.Degraded"/>, so missing/stale telemetry is never presented as
    /// healthy.
    /// </para>
    /// </summary>
    public static ComponentStatus ClassifyLiveStatusRegional(IReadOnlyList<RegionAvailabilityTelemetry> regions, SlaDefinition sla)
    {
        var consideredRegions = sla.ExpectedRegions is { Count: > 0 } expected
            ? expected
            : regions.Select(region => region.Region).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (consideredRegions.Count == 0)
        {
            return ComponentStatus.Unknown;
        }

        var stalenessThreshold = TimeSpan.FromSeconds(Math.Max(1, sla.ExpectedIntervalSeconds) * 3d);
        var now = DateTimeOffset.UtcNow;
        var byRegion = regions
            .GroupBy(region => region.Region, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var reportingCount = 0;
        var failingCount = 0;
        var missingCount = 0;

        foreach (var regionName in consideredRegions)
        {
            if (!byRegion.TryGetValue(regionName, out var sample)
                || sample.Samples == 0
                || sample.LastSeen is null
                || now - sample.LastSeen.Value > stalenessThreshold)
            {
                missingCount++;
                continue;
            }

            reportingCount++;
            var successRatio = 1d - (double)sample.Failures / sample.Samples;
            if (successRatio < sla.OutageBelow)
            {
                failingCount++;
            }
        }

        if (reportingCount == 0)
        {
            return ComponentStatus.Unknown;
        }

        if (failingCount == reportingCount)
        {
            return ComponentStatus.Outage;
        }

        if (failingCount == 0 && missingCount == 0)
        {
            return ComponentStatus.Operational;
        }

        return ComponentStatus.Degraded;
    }

    public static ComponentStatus ClassifyHistoricStatus(int total, double? uptime, SlaDefinition sla)
    {
        if (total == 0 || uptime is null)
        {
            return ComponentStatus.Unknown;
        }

        if (uptime.Value < sla.OutageBelow)
        {
            return ComponentStatus.Outage;
        }

        if (uptime.Value < sla.DegradedBelow)
        {
            return ComponentStatus.Degraded;
        }

        return ComponentStatus.Operational;
    }

    public static ComponentStatus WorstOf(IEnumerable<ComponentStatus> statuses)
    {
        var bestRank = statuses.Select(GetRank).DefaultIfEmpty(GetRank(ComponentStatus.Unknown)).Max();
        return bestRank switch
        {
            4 => ComponentStatus.Outage,
            3 => ComponentStatus.Degraded,
            2 => ComponentStatus.Operational,
            _ => ComponentStatus.Unknown
        };
    }

    private static int GetRank(ComponentStatus status) => status switch
    {
        ComponentStatus.Outage => 4,
        ComponentStatus.Degraded => 3,
        ComponentStatus.Operational => 2,
        _ => 1
    };
}
