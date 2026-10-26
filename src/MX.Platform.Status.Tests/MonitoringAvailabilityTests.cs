using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.Query;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;
using NSubstitute;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MX.Platform.Status.Tests;

public sealed class MonitoringAvailabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessObservationsDecodeThroughTheRealSdkAndRejectRestampedAssessments(bool restamped)
    {
        var check = new StatusCatalog(Path.Combine(AppContext.BaseDirectory, "catalog.json"))
            .Get("xi").Checks.Single(check => check.Id == "xi.process.server-agent.fleet");
        var source = check.Process!;
        var now = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new LogsResponseHandler(JsonSerializer.Serialize(new
        {
            tables = new[]
            {
                new
                {
                    name = "PrimaryResult",
                    columns = new[]
                    {
                        new { name = "siteId", type = "string" }, new { name = "componentId", type = "string" },
                        new { name = "checkId", type = "string" }, new { name = "role", type = "string" },
                        new { name = "timestamp", type = "datetime" }, new { name = "measuredAt", type = "datetime" },
                        new { name = "status", type = "string" }, new { name = "expected", type = "int" },
                        new { name = "healthy", type = "int" }, new { name = "failed", type = "int" },
                        new { name = "unknown", type = "int" }
                    },
                    rows = new object[][] { [source.SiteId, source.ComponentId, source.CheckId, source.Role, now,
                        restamped ? now.AddMinutes(-10) : now, "degraded", 2, 1, 1, 0] }
                }
            }
        })));
        var query = ProcessMonitoring.QueryAsync(OutcomeClient(http),
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/fixture/providers/Microsoft.Insights/components/fixture",
            [source], default);
        if (restamped) await Assert.ThrowsAsync<InvalidDataException>(() => query);
        else
        {
            var row = Assert.Single(await query);
            Assert.Equal(ComponentStatus.Degraded, row.Status);
            Assert.Equal(now, row.ObservedAt);
            Assert.Equal(2, row.Expected);
        }
    }

    [Theory]
    [InlineData("string", "1", true)]
    [InlineData("string", "0", false)]
    [InlineData("bool", true, true)]
    [InlineData("bool", false, false)]
    public async Task DecodesLegacyAndBooleanAvailabilityOutcomesThroughTheRealLogsSdk(
        string columnType, object outcome, bool expected)
    {
        using var http = OutcomeHttpClient(columnType, outcome);
        var rows = await MonitoringAvailability.QueryAsync(OutcomeClient(http),
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/fixture/providers/Microsoft.Insights/components/fixture",
            [Check.Monitoring!], CancellationToken.None);
        Assert.Equal(expected, Assert.Single(rows).Success);
    }

    [Theory]
    [InlineData("string", null)]
    [InlineData("string", "unexpected")]
    [InlineData("long", 2L)]
    public async Task InvalidAvailabilityOutcomesFailClosedInsteadOfBecomingHealthyOrOutage(
        string columnType, object? outcome)
    {
        using var http = OutcomeHttpClient(columnType, outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => MonitoringAvailability.QueryAsync(OutcomeClient(http),
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/fixture/providers/Microsoft.Insights/components/fixture",
            [Check.Monitoring!], CancellationToken.None));
    }

    [Fact]
    public void ReaderLoadsAnExtensionlessPemBundleWithAUsableMatchingPrivateKey()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=status-reader-test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"status-reader-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());
            using var loaded = MonitoringWorker.LoadReaderCertificate(path);
            using var privateKey = loaded.GetRSAPrivateKey();
            Assert.NotNull(privateKey);
            byte[] assertion = [1, 2, 3];
            var signature = privateKey.SignData(assertion, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Assert.True(key.VerifyData(assertion, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CatalogueMatchesTheProducerOwnedBindingContractWithoutEndpointCollectionCapability()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "contract", "status-monitoring-bindings.json")));
        var regions = fixture.RootElement.GetProperty("regions").Deserialize<string[]>()!;
        var expected = fixture.RootElement.GetProperty("checks").Deserialize<MonitoringReference[]>(StatusJson.Options)!;
        var catalog = new StatusCatalog(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
        var actual = catalog.Sites.SelectMany(site => site.Checks).Select(check => check.Monitoring)
            .OfType<MonitoringReference>().ToArray();
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
        Id = "public-alias",
        Kind = "telemetry",
        Monitoring = new()
        {
            SiteId = "mx",
            ComponentId = "mx.shared",
            CheckId = "mx.shared.routing",
            Resources = ["platform"],
            Regions = ["uksouth", "eastus", "westeurope"],
            IntervalSeconds = 300
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
    public void PrivateEvidenceRetainsPublicComponentScopeAndCanonicalMonitoringProvenance()
    {
        var check = Check with { Component = "xi.shared", Name = "Routing" };
        var result = MonitoringAvailability.Project("xi", check,
            [Row("uksouth", false), Row("eastus", true), Row("westeurope", true)], Now)!;
        Assert.Equal(check.Component, result.Component);
        Assert.Equal(check.Name, result.Name);
        Assert.Equal(Check.Monitoring, result.Monitoring);
        Assert.Contains("uksouth (HTTP 503", result.Detail);
        Assert.Equal(3, result.Regions.Count);
    }

    [Fact]
    public void MonitoringOwnedChecksRejectMissingOrReboundSourceProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        var check = Check with { Component = "mx.shared" };
        var site = new SiteDefinition { Site = new() { Id = "mx" }, Checks = [check] };
        var valid = MonitoringAvailability.Project("mx", check,
            [Row("uksouth", true, now), Row("eastus", true, now), Row("westeurope", true, now)], now)!;
        Assert.Equal(ComponentStatus.Operational, ComponentChecks.Build(site, [], [valid])["mx.shared"].Checks.Single().Status);
        foreach (var invalid in new[]
        {
            valid with { Monitoring = null },
            valid with { Regions = [] },
            valid with { Monitoring = valid.Monitoring! with { CheckId = "previous-source" } }
        })
            Assert.Equal(ComponentStatus.Unknown, ComponentChecks.Build(site, [], [invalid])["mx.shared"].Checks.Single().Status);
    }

    [Fact]
    public void HistoricalMeasurementOutcomesRemainSampleableAfterLiveExpiry()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-16);
        var check = Check with { Component = "mx.shared" };
        var site = new SiteDefinition { Site = new() { Id = "mx" }, Checks = [check] };
        var cached = MonitoringAvailability.Project("mx", check,
            [Row("uksouth", false, at), Row("eastus", false, at), Row("westeurope", false, at)], at)!;
        Assert.Equal(0, ComponentChecks.Build(site, [], [cached])["mx.shared"].Samples);
        var historical = ComponentChecks.Build(site, [], [cached], at)["mx.shared"];
        Assert.Equal(1, historical.Samples);
        Assert.Equal(1, historical.Failures);
        Assert.Equal(at, historical.LastSeen);
    }

    [Fact]
    public void HistoricalAggregationKeepsMissingConfiguredChecksUnknown()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-16);
        var check = Check with { Component = "mx.shared" };
        var site = new SiteDefinition
        {
            Site = new() { Id = "mx" },
            Checks = [check, check with { Id = "missing", Monitoring = null }]
        };
        var cached = MonitoringAvailability.Project("mx", check,
            [Row("uksouth", true, at), Row("eastus", true, at), Row("westeurope", true, at)], at)!;
        var historical = ComponentChecks.Build(site, [], [cached], at)["mx.shared"];
        Assert.Equal(0, historical.Samples);
        Assert.Contains(historical.Checks, result => result.Id == "missing" && result.Status == ComponentStatus.Unknown);
    }

    [Fact]
    public void QueryWindowEvictionRetainsObservationIdentityWithoutHidingRegionalExpiryOrNewMeasurements()
    {
        var first = MonitoringAvailability.Project("mx", Check,
            [Row("uksouth", true), Row("eastus", true), Row("westeurope", false, Now.AddMinutes(-29))], Now)!;
        MonitoringObservation[] remaining = [Row("uksouth", true), Row("eastus", true)];
        var repolled = MonitoringAvailability.Project("mx", Check, remaining, Now.AddMinutes(2), first)!;
        Assert.Equal(first.ObservationKey, repolled.ObservationKey);
        Assert.Equal(first.ObservedAt, repolled.ObservedAt);
        Assert.Equal(ComponentStatus.Degraded, repolled.Status);
        Assert.Equal(3, repolled.Regions.Count);
        Assert.Equal(ComponentStatus.Unknown,
            MonitoringAvailability.Project("mx", Check, [], Now.AddMinutes(16), repolled)!.Status);
        var updated = MonitoringAvailability.Project("mx", Check,
            [.. remaining, Row("westeurope", true, Now.AddMinutes(2))], Now.AddMinutes(2), repolled)!;
        Assert.NotEqual(first.ObservationKey, updated.ObservationKey);
        Assert.Equal(ComponentStatus.Operational, updated.Status);
        Assert.Equal(Now.AddMinutes(2), updated.ObservedAt);
        Assert.Equal(2, MonitoringAvailability.Project("mx", Check, remaining, Now.AddMinutes(2))!.Regions.Count);
    }

    [Fact]
    public void PublicationReclassifiesIndividualRegionalExpiryWithoutRestampingTheMeasurement()
    {
        var now = DateTimeOffset.UtcNow;
        var measuredAt = now.AddSeconds(-20);
        var check = Check with { Component = "xi.shared", Monitoring = Check.Monitoring! with { IntervalSeconds = 30 } };
        var cached = MonitoringAvailability.Project("xi", check,
            [Row("uksouth", true, measuredAt), Row("eastus", true, measuredAt.AddSeconds(-170)),
             Row("westeurope", true, measuredAt.AddSeconds(-170))], measuredAt)!;
        Assert.Equal(ComponentStatus.Operational, cached.Status);
        var site = new SiteDefinition { Site = new() { Id = "xi" }, Checks = [check] };
        var published = ComponentChecks.Build(site, [], [cached])["xi.shared"].Checks.Single();
        Assert.Equal(ComponentStatus.Degraded, published.Status);
        Assert.Equal(measuredAt, published.ObservedAt);
        Assert.Equal(ComponentStatus.Operational, cached.Status);
    }

    [Fact]
    public void CachedUnavailableRegionsDoNotHideNewFailuresFromSuccessfulResources()
    {
        var cached = MonitoringAvailability.Project("xi", Check,
            [Row("uksouth", true, Now.AddSeconds(-30)), Row("eastus", true, Now.AddSeconds(-30)),
             Row("westeurope", true, Now.AddSeconds(-30))], Now)!;
        var combined = MonitoringAvailability.Project("xi", Check,
            MonitoringAvailability.RecoverCached(Check.Monitoring!, cached).Append(Row("eastus", false)), Now)!;
        Assert.Equal(ComponentStatus.Degraded, combined.Status);
        Assert.Equal(Now.AddSeconds(-30), combined.Regions.Single(region => region.Region == "uksouth").LastSeen);
        Assert.Equal(Now, combined.Regions.Single(region => region.Region == "eastus").LastSeen);
        Assert.Contains("eastus (HTTP 503", combined.Detail);
        Assert.Equal(ComponentStatus.Unknown, MonitoringAvailability.CurrentStatus(Check.Monitoring!,
            combined.Regions, Now.AddMinutes(16)));
        Assert.Empty(MonitoringAvailability.RecoverCached(
            Check.Monitoring! with { CheckId = "different-source" }, cached));
    }

    [Fact]
    public void DelayedSamplesUseHistoricalMaintenanceScopeAndResolutionBoundaries()
    {
        var site = new SiteDefinition
        {
            Components = new()
            {
                Components =
            [
                new() { Id = "mx.group", Kind = "group", Children =
                [
                    new() { Id = "mx.old" }, new() { Id = "mx.new" }
                ]}
            ]
            }
        };
        var incident = new IncidentRecord
        {
            Published = true,
            State = IncidentState.Resolved,
            Severity = Severity.Maintenance,
            ResolvedAt = Now,
            ImpactHistory =
            [
                new(Now.AddMinutes(-3), IncidentState.Monitoring, Severity.Maintenance, ["mx.group"],
                    Now.AddMinutes(-5), Now.AddMinutes(5)),
                new(Now.AddMinutes(-1), IncidentState.Monitoring, Severity.Maintenance, ["mx.new"],
                    Now.AddMinutes(-5), Now.AddMinutes(5)),
                new(Now, IncidentState.Resolved, Severity.Maintenance, ["mx.new"],
                    Now.AddMinutes(-5), Now.AddMinutes(5))
            ]
        };
        Assert.True(PublicationWorker.IsMaintained(site, "mx.old", Now.AddMinutes(-2), [incident]));
        Assert.False(PublicationWorker.IsMaintained(site, "mx.old", Now.AddSeconds(-30), [incident]));
        Assert.True(PublicationWorker.IsMaintained(site, "mx.new", Now.AddSeconds(-30), [incident]));
        Assert.False(PublicationWorker.IsMaintained(site, "mx.new", Now, [incident]));
        Assert.False(PublicationWorker.IsMaintained(site, "mx.new", Now.AddMinutes(-4), [incident]));
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

    private static LogsQueryClient OutcomeClient(HttpClient http)
    {
        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new AccessToken("fixture", DateTimeOffset.UtcNow.AddHours(1))));
        return new LogsQueryClient(credential, new LogsQueryClientOptions { Transport = new HttpClientTransport(http) });
    }

    private static HttpClient OutcomeHttpClient(string columnType, object? outcome) => new(new LogsResponseHandler(
        JsonSerializer.Serialize(new
        {
            tables = new[]
            {
                new
                {
                    name = "PrimaryResult",
                    columns = new[]
                    {
                        new { name = "siteId", type = "string" },
                        new { name = "componentId", type = "string" },
                        new { name = "checkId", type = "string" },
                        new { name = "region", type = "string" },
                        new { name = "observedAt", type = "datetime" },
                        new { name = "success", type = columnType },
                        new { name = "message", type = "string" }
                    },
                    rows = new object?[][] { ["mx", "mx.shared", "mx.shared.routing", "uksouth", Now, outcome, "Native availability"] }
                }
            }
        })));

    private sealed class LogsResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
