using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Tests;

public sealed class AvailabilityQueryBuilderTests
{
    [Theory]
    [InlineData("abc;def")]
    [InlineData("abc|def")]
    [InlineData("abc//def")]
    public void RejectsValuesContainingKqlMetacharacters(string value)
    {
        var filters = new Dictionary<string, object?> { ["customDimensions.componentId"] = value };
        Assert.Throws<ArgumentException>(() => AvailabilityQueryBuilder.BuildLiveRegionalQuery(filters, 15));
    }

    [Fact]
    public void OnlyAllowsCustomDimensionsFilterKeys()
    {
        var filters = new Dictionary<string, object?> { ["name"] = "component" };
        Assert.Throws<ArgumentException>(() => AvailabilityQueryBuilder.BuildLiveRegionalQuery(filters, 15));
    }

    [Fact]
    public void GeneratesCorrectDynamicArray()
    {
        var query = AvailabilityQueryBuilder.BuildLiveRegionalQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" }, 15);
        Assert.Contains("dynamic([\"mx.api\"])", query);
    }

    [Fact]
    public void UsesSumItemCountInOutput()
    {
        var query = AvailabilityQueryBuilder.BuildLiveRegionalQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" }, 15);
        Assert.Contains("sum(itemCount)", query);
        Assert.DoesNotContain("count()", query);
    }

    [Fact]
    public void BuildLiveRegionalQueryProducesExpectedStructure()
    {
        var query = AvailabilityQueryBuilder.BuildLiveRegionalQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" }, 15);
        Assert.Contains("availabilityResults", query);
        Assert.Contains("ago(15m)", query);
        Assert.DoesNotContain("startofday(now())", query);
        Assert.Contains("lastSeen = max(timestamp)", query);
        Assert.Contains("by region = tostring(customDimensions[\"region\"])", query);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveLookbackMinutes(int lookbackMinutes)
    {
        var filters = new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" };
        Assert.Throws<ArgumentOutOfRangeException>(() => AvailabilityQueryBuilder.BuildLiveRegionalQuery(filters, lookbackMinutes));
    }

    [Fact]
    public void BuildDailyRollupQueryIncludesCorrectDateRange()
    {
        var query = AvailabilityQueryBuilder.BuildDailyRollupQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" }, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3));
        Assert.Contains("datetime(2026-01-01T00:00:00Z)", query);
        Assert.Contains("datetime(2026-01-04T00:00:00Z)", query);
    }
}
