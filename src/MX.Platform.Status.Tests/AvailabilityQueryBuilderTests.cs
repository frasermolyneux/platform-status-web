using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Tests;

public sealed class AvailabilityQueryBuilderTests
{
    private readonly AvailabilityQueryBuilder _sut = new();

    [Theory]
    [InlineData("abc;def")]
    [InlineData("abc|def")]
    [InlineData("abc//def")]
    public void RejectsValuesContainingKqlMetacharacters(string value)
    {
        var filters = new Dictionary<string, object?> { ["customDimensions.componentId"] = value };
        Assert.Throws<ArgumentException>(() => _sut.BuildLiveTodayQuery(filters));
    }

    [Fact]
    public void OnlyAllowsCustomDimensionsFilterKeys()
    {
        var filters = new Dictionary<string, object?> { ["name"] = "component" };
        Assert.Throws<ArgumentException>(() => _sut.BuildLiveTodayQuery(filters));
    }

    [Fact]
    public void GeneratesCorrectDynamicArray()
    {
        var query = _sut.BuildLiveTodayQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" });
        Assert.Contains("dynamic([\"mx.api\"])", query);
    }

    [Fact]
    public void UsesSumItemCountInOutput()
    {
        var query = _sut.BuildLiveTodayQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" });
        Assert.Contains("sum(itemCount)", query);
        Assert.DoesNotContain("count()", query);
    }

    [Fact]
    public void BuildLiveTodayQueryProducesExpectedStructure()
    {
        var query = _sut.BuildLiveTodayQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" });
        Assert.Contains("availabilityResults", query);
        Assert.Contains("startofday(now())", query);
        Assert.Contains("lastSeen = max(timestamp)", query);
    }

    [Fact]
    public void BuildDailyRollupQueryIncludesCorrectDateRange()
    {
        var query = _sut.BuildDailyRollupQuery(new Dictionary<string, object?> { ["customDimensions.componentId"] = "mx.api" }, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3));
        Assert.Contains("datetime(2026-01-01T00:00:00Z)", query);
        Assert.Contains("datetime(2026-01-04T00:00:00Z)", query);
    }
}
