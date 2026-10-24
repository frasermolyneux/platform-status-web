using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;
using System.Text.Json;

namespace MX.Platform.Status.Tests;

public sealed class MonitoringAvailabilityTests
{
    [Fact]
    public void CatalogueMatchesTheProducerOwnedBindingContractWithoutEndpointCollectionCapability()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "contract", "status-monitoring-bindings.json")));
        var regions = fixture.RootElement.GetProperty("regions").Deserialize<string[]>()!;
        var expected = fixture.RootElement.GetProperty("checks").Deserialize<MonitoringReference[]>(StatusJson.Options)!;
        var catalog = new StatusCatalog(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
        var actual = catalog.Sites.SelectMany(site => site.Checks).Select(check => check.Monitoring!).ToArray();
        Assert.Equal(expected.Select(reference => reference.Identity).Order(),
            actual.Select(reference => reference.Identity).Distinct().Order());
        foreach (var reference in actual)
        {
            var definition = Assert.Single(expected, entry => entry.Identity == reference.Identity);
            Assert.Equal(definition.Resources.Order(), reference.Resources.Order());
            Assert.Equal(regions.Order(), reference.Regions.Order());
            Assert.Equal(definition.IntervalSeconds, reference.IntervalSeconds);
        }
        Assert.Null(typeof(PublicationWorker).Assembly.GetType("MX.Platform.Status.Service.IndependentProbe"));
        Assert.DoesNotContain(typeof(PublicationWorker).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(HttpClient) || parameter.ParameterType == typeof(IHttpClientFactory));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly CheckDefinition Check = new()
    {
        Id = "public-alias", Kind = "telemetry",
        Monitoring = new()
        {
            SiteId = "mx", ComponentId = "mx.shared", CheckId = "mx.shared.routing",
            Resources = ["platform"], Regions = ["uksouth", "eastus", "westeurope"], IntervalSeconds = 300
        }
    };

    [Theory]
    [InlineData(true, true, true, ComponentStatus.Operational)]
    [InlineData(true, false, true, ComponentStatus.Degraded)]
    [InlineData(false, false, false, ComponentStatus.Outage)]
    public void ProjectsLatestIndependentRegionsWithoutAveragingAwayFreshFailures(
        bool uk, bool us, bool eu, ComponentStatus expected)
    {
        var result = MonitoringAvailability.Project("xi", Check,
            [Row("uksouth", uk), Row("eastus", us), Row("westeurope", eu), Row("eastus", true, Now.AddMinutes(-1))], Now)!;
        Assert.Equal(expected, result.Status);
        Assert.Equal("xi", result.Site);
        Assert.Equal(Check.Id, result.CheckId);
    }

    [Fact]
    public void MissingAndStaleRegionsCannotProduceHealthyStatus()
    {
        Assert.Equal(ComponentStatus.Degraded,
            MonitoringAvailability.Project("mx", Check, [Row("uksouth", true)], Now)!.Status);
        Assert.Equal(ComponentStatus.Unknown,
            MonitoringAvailability.Project("mx", Check, [Row("uksouth", true, Now.AddMinutes(-16))], Now)!.Status);
        Assert.Null(MonitoringAvailability.Project("mx", Check,
            [Row("uksouth", true) with { SiteId = "xi" }, Row("eastus", true) with { CheckId = "unrelated" }], Now));
    }

    [Fact]
    public void ObservationIdentitySurvivesRepollingAndSharedBrandProjectionButChangesWithANewMeasurement()
    {
        MonitoringObservation[] rows = [Row("uksouth", true), Row("eastus", true), Row("westeurope", true)];
        var first = MonitoringAvailability.Project("mx", Check, rows, Now)!;
        var repolled = MonitoringAvailability.Project("xi", Check, rows.Reverse(), Now.AddMinutes(1))!;
        Assert.Equal(first.ObservationKey, repolled.ObservationKey);
        Assert.Equal(first.ObservedAt, repolled.ObservedAt);
        Assert.NotEqual(first.ObservationKey, MonitoringAvailability.Project("mx", Check,
            [.. rows, Row("eastus", false, Now.AddSeconds(30))], Now.AddSeconds(30))!.ObservationKey);
    }

    [Fact]
    public void QueriesAreExplicitlyScopedAndChooseLatestMeasurementsRatherThanWindowRatios()
    {
        var query = MonitoringAvailability.BuildQuery([Check.Monitoring!]);
        Assert.Contains("siteId == \"mx\"", query);
        Assert.Contains("componentId == \"mx.shared\"", query);
        Assert.Contains("checkId == \"mx.shared.routing\"", query);
        Assert.Contains("arg_max(timestamp, success, message)", query);
        Assert.DoesNotContain("summarize total", query);
        Assert.Throws<InvalidOperationException>(() => MonitoringAvailability.BuildQuery([]));
    }

    private static MonitoringObservation Row(string region, bool success, DateTimeOffset? at = null) =>
        new("mx", "mx.shared", "mx.shared.routing", region, at ?? Now, success, success ? "OK" : "HTTP 503 (5xx)");
}
