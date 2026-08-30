using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Tests;

public sealed class TelemetryFiltersTests
{
    [Fact]
    public void AddsSiteIdWhenNotPresent()
    {
        var filter = new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" };
        var result = TelemetryFilters.WithSiteId(filter, "mx");

        Assert.Equal("mx", result["customDimensions.siteId"]);
        Assert.Equal("mx.api", result["customDimensions.componentId"]);
    }

    [Fact]
    public void OverridesAnyContentProvidedSiteId()
    {
        // A component filter should never be able to widen queries into another tenant's data,
        // even if content authors mistakenly (or maliciously) hardcode a different siteId.
        var filter = new Dictionary<string, object?>
        {
            ["customDimensions.componentId"] = "mx.api",
            ["customDimensions.siteId"] = "xi"
        };

        var result = TelemetryFilters.WithSiteId(filter, "mx");

        Assert.Equal("mx", result["customDimensions.siteId"]);
    }

    [Fact]
    public void DoesNotMutateInputFilter()
    {
        var filter = new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" };
        TelemetryFilters.WithSiteId(filter, "mx");

        Assert.False(filter.ContainsKey("customDimensions.siteId"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ThrowsForInvalidSiteId(string? siteId)
    {
        var filter = new Dictionary<string, object?>();
        Assert.Throws<ArgumentException>(() => TelemetryFilters.WithSiteId(filter, siteId!));
    }
}
