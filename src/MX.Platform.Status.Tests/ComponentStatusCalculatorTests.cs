using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Tests;

public sealed class ComponentStatusCalculatorTests
{
    private readonly ComponentStatusCalculator _sut = new();
    private readonly SlaDefinition _sla = new() { ExpectedIntervalSeconds = 60, DegradedBelow = 0.995, OutageBelow = 0.9 };

    [Fact]
    public void Live_WithZeroSamples_ReturnsUnknown() =>
        Assert.Equal(ComponentStatus.Unknown, _sut.ClassifyLiveStatus(0, 0, DateTimeOffset.UtcNow, _sla));

    [Fact]
    public void Live_WithStaleLastSeen_ReturnsUnknown() =>
        Assert.Equal(ComponentStatus.Unknown, _sut.ClassifyLiveStatus(10, 0, DateTimeOffset.UtcNow.AddMinutes(-4), _sla));

    [Fact]
    public void Live_WithNoFailures_ReturnsOperational() =>
        Assert.Equal(ComponentStatus.Operational, _sut.ClassifyLiveStatus(10, 0, DateTimeOffset.UtcNow, _sla));

    [Fact]
    public void Live_WithFailureRatioBelowDegradedThreshold_ReturnsOperational() =>
        Assert.Equal(ComponentStatus.Operational, _sut.ClassifyLiveStatus(1000, 4, DateTimeOffset.UtcNow, _sla));

    [Fact]
    public void Live_WithFailureRatioBetweenThresholds_ReturnsDegraded() =>
        Assert.Equal(ComponentStatus.Degraded, _sut.ClassifyLiveStatus(1000, 10, DateTimeOffset.UtcNow, _sla));

    [Fact]
    public void Live_WithFailureRatioAboveOutageThreshold_ReturnsOutage() =>
        Assert.Equal(ComponentStatus.Outage, _sut.ClassifyLiveStatus(100, 20, DateTimeOffset.UtcNow, _sla));

    [Fact]
    public void Historic_WithZeroTotal_ReturnsUnknown() =>
        Assert.Equal(ComponentStatus.Unknown, _sut.ClassifyHistoricStatus(0, null, _sla));

    [Fact]
    public void Historic_WithUptimeAboveDegradedThreshold_ReturnsOperational() =>
        Assert.Equal(ComponentStatus.Operational, _sut.ClassifyHistoricStatus(100, 0.999, _sla));

    [Fact]
    public void Historic_WithUptimeBetweenThresholds_ReturnsDegraded() =>
        Assert.Equal(ComponentStatus.Degraded, _sut.ClassifyHistoricStatus(100, 0.95, _sla));

    [Fact]
    public void Historic_WithUptimeBelowOutageThreshold_ReturnsOutage() =>
        Assert.Equal(ComponentStatus.Outage, _sut.ClassifyHistoricStatus(100, 0.80, _sla));

    [Fact]
    public void WorstOf_ReturnsCorrectPrecedence()
    {
        var result = _sut.WorstOf([ComponentStatus.Unknown, ComponentStatus.Operational, ComponentStatus.Degraded, ComponentStatus.Outage]);
        Assert.Equal(ComponentStatus.Outage, result);
    }

    [Fact]
    public void Regional_WithNoRegions_ReturnsUnknown() =>
        Assert.Equal(ComponentStatus.Unknown, _sut.ClassifyLiveStatusRegional([], _sla));

    [Fact]
    public void Regional_WithAllRegionsHealthy_ReturnsOperational()
    {
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("westeurope", 10, 0, DateTimeOffset.UtcNow)
        };

        Assert.Equal(ComponentStatus.Operational, _sut.ClassifyLiveStatusRegional(regions, _sla));
    }

    [Fact]
    public void Regional_WithOneRegionFailingAndOthersHealthy_ReturnsDegraded()
    {
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 10, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("westeurope", 10, 0, DateTimeOffset.UtcNow)
        };

        Assert.Equal(ComponentStatus.Degraded, _sut.ClassifyLiveStatusRegional(regions, _sla));
    }

    [Fact]
    public void Regional_WithAllReportingRegionsFailing_ReturnsOutage()
    {
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 10, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("eastus", 10, 10, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("westeurope", 10, 10, DateTimeOffset.UtcNow)
        };

        Assert.Equal(ComponentStatus.Outage, _sut.ClassifyLiveStatusRegional(regions, _sla));
    }

    [Fact]
    public void Regional_WithAllRegionsStale_ReturnsUnknown()
    {
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 0, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow.AddMinutes(-10))
        };

        Assert.Equal(ComponentStatus.Unknown, _sut.ClassifyLiveStatusRegional(regions, _sla));
    }

    [Fact]
    public void Regional_WithExpectedRegionMissingEntirely_DoesNotReturnOperational()
    {
        var sla = _sla with { ExpectedRegions = ["uksouth", "eastus", "westeurope"] };
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow)
            // westeurope never reported: must not be silently treated as healthy.
        };

        Assert.Equal(ComponentStatus.Degraded, _sut.ClassifyLiveStatusRegional(regions, sla));
    }

    [Fact]
    public void Regional_WithExpectedRegionsAllReportingAndHealthy_ReturnsOperational()
    {
        var sla = _sla with { ExpectedRegions = ["uksouth", "eastus"] };
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("unexpected-region", 10, 10, DateTimeOffset.UtcNow)
        };

        Assert.Equal(ComponentStatus.Operational, _sut.ClassifyLiveStatusRegional(regions, sla));
    }

    [Fact]
    public void Regional_WithOneRegionStaleAndOthersHealthy_ReturnsDegraded()
    {
        var regions = new[]
        {
            new RegionAvailabilityTelemetry("uksouth", 10, 0, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new RegionAvailabilityTelemetry("eastus", 10, 0, DateTimeOffset.UtcNow),
            new RegionAvailabilityTelemetry("westeurope", 10, 0, DateTimeOffset.UtcNow)
        };

        Assert.Equal(ComponentStatus.Degraded, _sut.ClassifyLiveStatusRegional(regions, _sla));
    }
}
