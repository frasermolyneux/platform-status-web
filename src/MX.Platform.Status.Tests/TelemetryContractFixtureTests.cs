using System.Text.Json;

namespace MX.Platform.Status.Tests;

/// <summary>
/// Guards the cross-repository telemetry contract fixture (<c>contract/availability-telemetry-contract.json</c>,
/// duplicated verbatim in platform-sitewatch-func). This test asserts the dimension names this
/// consumer filters/aggregates on (<see cref="TelemetryFiltersTests"/>, <see cref="AvailabilityQueryBuilderTests"/>)
/// exactly match the fixture's declared <c>customDimensions</c> keys, so a rename here that isn't
/// mirrored in the fixture (and in platform-sitewatch-func's copy/tests) fails CI instead of silently
/// drifting in production.
/// </summary>
public sealed class TelemetryContractFixtureTests
{
    private static readonly string[] ConsumedDimensionNames = ["componentId", "siteId", "region"];

    [Fact]
    public void ConsumedDimensionNames_MatchContractFixture()
    {
        using var document = JsonDocument.Parse(ReadContractFixture());
        var declaredDimensions = document.RootElement
            .GetProperty("customDimensions")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(ConsumedDimensionNames.OrderBy(name => name, StringComparer.Ordinal), declaredDimensions.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ContractFixture_MarksAllDimensionsRequired()
    {
        using var document = JsonDocument.Parse(ReadContractFixture());
        foreach (var property in document.RootElement.GetProperty("customDimensions").EnumerateObject())
        {
            Assert.True(property.Value.GetProperty("required").GetBoolean(), $"Dimension '{property.Name}' should be marked required in the contract fixture.");
        }
    }

    private static string ReadContractFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contract", "availability-telemetry-contract.json");
        return File.ReadAllText(path);
    }
}
