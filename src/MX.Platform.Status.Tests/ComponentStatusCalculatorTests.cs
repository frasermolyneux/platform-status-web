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
}
