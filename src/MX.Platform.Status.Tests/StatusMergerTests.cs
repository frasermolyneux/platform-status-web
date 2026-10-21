using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Tests;

public sealed class StatusMergerTests
{
    private readonly StatusMerger _sut = new();

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

    [Fact]
    public void UsesRegionalClassificationWhenRegionsPresent()
    {
        // One of two reporting regions is fully failing while the other is healthy: aggregate ratio
        // (10 failures / 20 samples = 50%) would read as Outage under the old aggregate classifier,
        // but regional semantics require a subset failing (not all) to classify as Degraded.
        var snapshot = CreateSnapshot();
        var telemetry = new ComponentLiveTelemetry(20, 10, DateTimeOffset.UtcNow, null)
        {
            Regions =
            [
                new RegionAvailabilityTelemetry("uksouth", 10, 10, DateTimeOffset.UtcNow),
                new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow)
            ]
        };

        var response = _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry> { ["mx.api"] = telemetry }, null, [], []);
        Assert.Equal("degraded", response.Components.Single().Status);
    }

    [Fact]
    public void UnknownChildrenPreventAHealthyGroupAndSummary()
    {
        var snapshot = CreateSnapshot() with
        {
            Components = new ComponentsDocument
            {
                Components = [new Component
                {
                    Id = "group", Kind = "group", Children =
                    [
                        new() { Id = "known", Source = new ComponentSource { Kind = "static", Status = "operational" } },
                        new() { Id = "missing", Source = new ComponentSource { Kind = "probe" } }
                    ]
                }]
            }
        };
        var result = _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry>(), null, [], []);
        Assert.Equal("unknown", result.Components.Single().Status);
        Assert.Equal("unknown", result.Summary.Status);
    }

    [Fact]
    public void HiddenSubtreesAndTheirIncidentsAreNotPublished()
    {
        var snapshot = CreateSnapshot(new Component
        {
            Id = "private",
            Kind = "group",
            Hidden = true,
            Children = [new() { Id = "private-child" }]
        });
        var incident = new Incident { Id = 2, Title = "Restricted", Components = ["private-child"] };
        var result = _sut.Merge(snapshot, new Dictionary<string, ComponentLiveTelemetry>(), null, [incident], []);
        Assert.Single(result.Components);
        Assert.Empty(result.Incidents);
    }

    [Fact]
    public void HistoricHealthySamplesCannotReplaceMissingLiveObservations()
    {
        var history = new HistoryDocument
        {
            ComponentsById = new Dictionary<string, ComponentHistoryRecord>
            {
                ["mx.api"] = new()
                {
                    Days = [new HistoryDay { Date = DateOnly.FromDateTime(DateTime.UtcNow), Status = ComponentStatus.Operational, Uptime = 1, Total = 10, Failed = 0 }]
                }
            }
        };
        var result = _sut.Merge(CreateSnapshot(), new Dictionary<string, ComponentLiveTelemetry>(), history, [], []);
        Assert.Equal("unknown", result.Components.Single().Status);
        Assert.Equal("operational", result.Components.Single().History.Last().Status);
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
