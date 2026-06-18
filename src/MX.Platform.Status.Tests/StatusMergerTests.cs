using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Tests;

public sealed class StatusMergerTests
{
    private readonly StatusMerger _sut = new(new ComponentStatusCalculator());

    [Fact]
    public void WorstOfOperationalAndDegradedIncident_ReturnsDegraded()
    {
        var response = MergeWith(Severity.Degraded, ComponentStatus.Operational);
        Assert.Equal("degraded", response.Components.Single().Status);
    }

    [Fact]
    public void WorstOfDegradedAndOutageIncident_ReturnsOutage()
    {
        var response = MergeWith(Severity.Outage, ComponentStatus.Degraded);
        Assert.Equal("outage", response.Components.Single().Status);
    }

    [Fact]
    public void MaintenanceOverridesComponentStatus()
    {
        var snapshot = CreateSnapshot();
        var response = _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry> { ["mx.api"] = new(100, 0, DateTimeOffset.UtcNow, 10) }, null, [], [new MaintenanceWindow { Id = 1, Title = "Maint", Url = "https://example.com", Components = ["mx.api"], ScheduledStart = DateTimeOffset.UtcNow.AddHours(-1), ScheduledEnd = DateTimeOffset.UtcNow.AddHours(1), State = "in-progress" }]);
        Assert.Equal("maintenance", response.Components.Single().Status);
    }

    [Fact]
    public void SummaryReflectsWorstComponentStatus()
    {
        var snapshot = CreateSnapshot(new Component { Id = "mx.web", Name = "Web", Source = new ComponentSource { Kind = "static", Status = "operational" }, Sla = new SlaDefinition() });
        var response = _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry> { ["mx.api"] = new(100, 20, DateTimeOffset.UtcNow, 10) }, null, [], []);
        Assert.Equal("outage", response.Summary.Status);
    }

    [Fact]
    public void PrecedenceIsOutageThenDegradedThenOperationalThenUnknown()
    {
        var snapshot = CreateSnapshot(
            new Component { Id = "mx.degraded", Name = "Degraded", Source = new ComponentSource { Kind = "static", Status = "degraded" }, Sla = new SlaDefinition() },
            new Component { Id = "mx.unknown", Name = "Unknown", Source = new ComponentSource { Kind = "static", Status = "unknown" }, Sla = new SlaDefinition() });

        var response = _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry> { ["mx.api"] = new(100, 20, DateTimeOffset.UtcNow, 10) }, null, [], []);
        Assert.Equal("outage", response.Summary.Status);
    }

    private MX.Platform.Status.App.Contracts.StatusApiResponse MergeWith(Severity severity, ComponentStatus liveStatus)
    {
        var snapshot = CreateSnapshot();
        var telemetry = liveStatus switch
        {
            ComponentStatus.Operational => new ComponentLiveTelemetry(100, 0, DateTimeOffset.UtcNow, 10),
            ComponentStatus.Degraded => new ComponentLiveTelemetry(100, 10, DateTimeOffset.UtcNow, 10),
            _ => new ComponentLiveTelemetry(100, 30, DateTimeOffset.UtcNow, 10)
        };

        return _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry> { ["mx.api"] = telemetry }, null,
        [new Incident { Id = 1, Title = "Issue", Url = "https://example.com", Components = ["mx.api"], Severity = severity, State = IncidentState.Investigating, CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow, Updates = [] }], []);
    }

    private static SiteConfigurationSnapshot CreateSnapshot(params Component[] additionalComponents)
    {
        var components = new List<Component>
        {
            new()
            {
                Id = "mx.api",
                Name = "API",
                Kind = "leaf",
                Source = new ComponentSource { Kind = "appInsights", Resource = "sitewatch", Filter = new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" } },
                Sla = new SlaDefinition { WindowDays = 7, ExpectedIntervalSeconds = 60, DegradedBelow = 0.995, OutageBelow = 0.9 }
            }
        };
        components.AddRange(additionalComponents);

        return new SiteConfigurationSnapshot(
            new Site { Id = "mx", DisplayName = "MX", Domains = ["status.example.com"], AppInsights = new Dictionary<string, AppInsightsResource> { ["sitewatch"] = new() { ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Insights/components/ai" } } },
            new ComponentsDocument { Components = components },
            "siteYaml",
            "componentsYaml",
            DateTimeOffset.UtcNow);
    }
}
