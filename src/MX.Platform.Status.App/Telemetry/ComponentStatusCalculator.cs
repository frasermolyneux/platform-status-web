using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Telemetry;

public sealed class ComponentStatusCalculator
{
    public ComponentStatus ClassifyLiveStatus(int samples, int failures, DateTimeOffset? lastSeen, SlaDefinition sla)
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

    public ComponentStatus ClassifyHistoricStatus(int total, double? uptime, SlaDefinition sla)
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

    public ComponentStatus WorstOf(IEnumerable<ComponentStatus> statuses)
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
