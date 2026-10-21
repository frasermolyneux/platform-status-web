using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class IndependentServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "status-tests-" + Guid.NewGuid().ToString("N"));

    public IndependentServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PublicRequestsAndFeedsWorkWithoutAzureGithubEntraOrDatabase()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        var response = await client.GetAsync("/api/status");
        var publication = JsonSerializer.Deserialize<StatusApiResponse>(await response.Content.ReadAsStringAsync(), StatusJson.Options)!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mx", publication.Site.Id);
        Assert.Equal("unknown", publication.Summary.Status);
        Assert.True(publication.DataFreshness!.Stale);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/feed.xml")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/evidence")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/integrations/azure", JsonContent.Create(new { }))).StatusCode);
    }

    [Fact]
    public async Task UnknownHostsCannotUseForwardedHeadersToSelectAnotherSite()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "unconfigured.example";
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "mxstatus.io");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/status")).StatusCode);
        client.DefaultRequestHeaders.Host = "status.xtremeidiots.com";
        var publication = JsonSerializer.Deserialize<StatusApiResponse>(await client.GetStringAsync("/api/status"), StatusJson.Options)!;
        Assert.Equal("xi", publication.Site.Id);
    }

    [Fact]
    public async Task LocalPublicationSurvivesRestartAndRetainsItsOriginalTimestampWhenStale()
    {
        var options = new RuntimeOptions { DataDirectory = _directory };
        var catalog = Catalogue();
        var generated = DateTimeOffset.UtcNow.AddHours(-1);
        await new PublicationStore(catalog, options).WriteAsync(new StatusApiResponse
        {
            Site = new SiteInfo { Id = "mx" },
            GeneratedAt = generated,
            Summary = new Summary { Status = "operational" },
            Components = [new ComponentDto { Id = "web", Status = "operational" }]
        });
        var result = await new PublicationStore(catalog, options).ReadAsync("mx");
        Assert.Equal(generated, result.GeneratedAt);
        Assert.True(result.DataFreshness!.Stale);
        Assert.Equal("unknown", result.Summary.Status);
        Assert.Equal("unknown", result.Components.Single().Status);
        Assert.Throws<ArgumentException>(() => catalog.Get("../private"));
    }

    [Fact]
    public void IncidentUpdatesRetainTheirOwnStatesAndEscapeOperatorText()
    {
        var now = DateTimeOffset.UtcNow;
        var command = new IncidentCommand("Issue", ["mx.sitewatch.molyneux-me"], Severity.Outage,
            IncidentState.Investigating, "<script>private()</script>", false);
        var first = command.Apply(1, "mx", null, now);
        var second = (command with { State = IncidentState.Resolved, Message = "Recovered", Published = true, Revision = 1 })
            .Apply(1, "mx", first, now.AddMinutes(1));
        Assert.Equal(IncidentState.Investigating, second.Updates[0].State);
        Assert.Equal(IncidentState.Resolved, second.Updates[1].State);
        Assert.DoesNotContain("<script>", second.Updates[0].Body);
        Assert.Equal(2, second.Revision);
        Assert.Equal(now.AddMinutes(1), second.ResolvedAt);
        Assert.Null((command with { Components = ["xi.sitewatch.forums-web"] }).Validate(Catalogue().Get("xi")));
        Assert.NotNull(command.Validate(Catalogue().Get("xi")));
    }

    [Theory]
    [InlineData("Fired", "firedDateTime")]
    [InlineData("Resolved", "resolvedDateTime")]
    public void AzureEventsUseTheSourceLifecycleTimestamp(string condition, string field)
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-2);
        var payload = JsonSerializer.SerializeToElement(new
        {
            schemaId = "azureMonitorCommonAlertSchema",
            data = new
            {
                essentials = new Dictionary<string, object>
                {
                    ["alertId"] = "event-1",
                    ["monitorCondition"] = condition,
                    [field] = timestamp
                }
            }
        });
        Assert.True(AzureAlert.TryParse(payload, out var alert));
        Assert.Equal(timestamp, alert.ObservedAt);
        Assert.False(AzureAlert.TryParse(JsonSerializer.SerializeToElement(new { schemaId = 42 }), out _));
    }

    [Fact]
    public void PublicIncidentsCannotTargetChildrenOfRestrictedGroups()
    {
        var site = Catalogue().Get("mx") with
        {
            Components = new ComponentsDocument
            {
                Components = [new Component { Id = "private", Hidden = true, Children = [new() { Id = "child" }] }]
            }
        };
        var command = new IncidentCommand("Title", ["child"], Severity.Outage, IncidentState.Investigating, "Update", true);
        Assert.NotNull(command.Validate(site));
        Assert.Null((command with { Published = false }).Validate(site));
    }

    private Microsoft.AspNetCore.Builder.WebApplication Build() => ServiceProgram.Build([], builder =>
    {
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Status:CatalogPath"] = Path.Combine(AppContext.BaseDirectory, "catalog.json"),
            ["Status:DataDirectory"] = _directory
        });
    });

    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
