using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed partial class IndependentServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "status-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _tenant = Guid.NewGuid().ToString();
    private readonly string _client = Guid.NewGuid().ToString();
    private readonly string _ingestor = Guid.NewGuid().ToString();
    private readonly SymmetricSecurityKey _signingKey = new(RandomNumberGenerator.GetBytes(32));

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
        Assert.Equal("static-shell", await client.GetStringAsync("/component/mx.sitewatch.molyneux-me"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/evidence")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/traffic")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/banners")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/ops/banners", new BannerCommand("Notice", "Message", "info", true))).StatusCode);
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
    public async Task EntraConfiguredPublicRequestsIgnoreBearerHeadersWithoutInvokingRemoteAuthentication()
    {
        await using var app = Build(configuredIdentity: true);
        var bearerRequests = 0;
        app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).Events.OnMessageReceived = _ =>
            {
                bearerRequests++;
                return Task.CompletedTask;
            };
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token("Status.Operator", "Status.Access", _ingestor, "expired"));
        foreach (var path in new[] { "/api/status", "/feed.xml", "/health/ready", "/", "/api/auth/config" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(0, bearerRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/runtime")).StatusCode);
        Assert.Equal(1, bearerRequests);
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
            Components = [new ComponentDto
            {
                Id = "web", Status = "operational",
                Checks = [new("http", "HTTP check", "http", "operational", generated), new("queue", "Queue", "telemetry", "outage", generated)]
            }]
        });
        var result = await new PublicationStore(catalog, options).ReadAsync("mx");
        Assert.Equal(generated, result.GeneratedAt);
        Assert.True(result.DataFreshness!.Stale);
        Assert.Equal("unknown", result.Summary.Status);
        Assert.Equal("unknown", result.Components.Single().Status);
        Assert.Equal("unknown", result.Components.Single().Checks![0].Status);
        Assert.Equal("outage", result.Components.Single().Checks![1].Status);
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
    public void PublishingApprovesOnlyTheCurrentUpdateAndRetainsDraftHistoryPrivately()
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new IncidentCommand("Maintenance", ["mx.sitewatch.molyneux-me"], Severity.Maintenance,
            IncidentState.Investigating, "PRIVATE_DRAFT", false, ScheduledStart: now, ScheduledEnd: now.AddHours(1));
        var record = draft.Apply(1, "mx", null, now);
        record = (draft with { Published = true, Message = "Approved first update" }).Apply(1, "mx", record, now.AddMinutes(1));
        record = (draft with { Message = "PRIVATE_REVISION" }).Apply(1, "mx", record, now.AddMinutes(2));
        record = (draft with { Published = true, Message = "Approved final update" }).Apply(1, "mx", record, now.AddMinutes(3));

        Assert.Equal(4, record.Updates.Count);
        Assert.Equal(["Approved first update", "Approved final update"], record.ToPublicIncident().Updates.Select(update => update.Body));
        Assert.Equal("Approved final update", record.ToMaintenance(now).Body);
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

    [Theory]
    [InlineData("Status.Reader", "Status.Access", "operator", false, 403)]
    [InlineData("Status.Operator", "", "operator", false, 403)]
    [InlineData("Status.Operator", "Status.Access", "operator", false, 400)]
    [InlineData("Status.Operator", "Status.Access", "ingest", true, 403)]
    [InlineData("Status.Ingest", "", "ingest", false, 403)]
    [InlineData("Status.Ingest", "", "ingest", true, 400)]
    public async Task RolesScopesAndIngestorAllowlistAreEnforcedByTheApi(
        string role, string scope, string endpoint, bool allowedCaller, int expected)
    {
        await using var app = Build(configuredIdentity: true);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(role, scope, allowedCaller ? _ingestor : Guid.NewGuid().ToString()));
        var path = endpoint == "operator" ? "/api/ops/incidents" : "/api/integrations/azure";
        var payload = endpoint == "operator"
            ? JsonContent.Create(new { title = "", components = Array.Empty<string>(), severity = "outage", state = "investigating", message = "", published = false })
            : JsonContent.Create(new { });
        Assert.Equal(expected, (int)(await client.PostAsync(path, payload)).StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    public async Task InvalidTokenBoundariesAreRejectedWithoutAffectingPublicReads(string invalid)
    {
        await using var app = Build(configuredIdentity: true);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token("Status.Operator", "Status.Access", _ingestor, invalid));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task AnUnavailableWriterProducesReadOnlyStateButDoesNotSuppressCancellation()
    {
        using var reservedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var passwordFile = Path.Combine(_directory, "app-password");
        await File.WriteAllTextAsync(passwordFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        await using var database = new PostgresStore(new RuntimeOptions
        {
            DatabaseHost = IPAddress.Loopback.ToString(),
            DatabasePort = ((IPEndPoint)reservedPort.LocalEndPoint!).Port,
            DatabasePasswordFile = passwordFile
        }, NullLogger<PostgresStore>.Instance);
        Assert.False(await database.IsWritableAsync(CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.IsWritableAsync(canceled.Token));
    }

    [Fact]
    public void WriterHostOverridesTheLocalConnectionFileWithoutRedirectingLocalReads()
    {
        var localFile = Path.Combine(_directory, "local-connection");
        File.WriteAllText(localFile, "Host=standby.invalid;Port=35432;Database=status;Username=status_app");
        var options = new RuntimeOptions
        {
            DatabaseConnectionFile = localFile,
            WriterHost = "primary.invalid",
            WriterPort = 15432
        };
        var reader = new Npgsql.NpgsqlConnectionStringBuilder(PostgresStore.ConnectionString(options, writer: false));
        var writer = new Npgsql.NpgsqlConnectionStringBuilder(PostgresStore.ConnectionString(options, writer: true));
        Assert.Equal("standby.invalid", reader.Host);
        Assert.Equal(35432, reader.Port);
        Assert.Equal("primary.invalid", writer.Host);
        Assert.Equal(15432, writer.Port);
        Assert.Equal(reader.Database, writer.Database);
        Assert.Equal(reader.Username, writer.Username);

        var writerFile = Path.Combine(_directory, "writer-connection");
        File.WriteAllText(writerFile, "Host=explicit-writer.invalid;Port=45432");
        options = options with { WriterConnectionFile = writerFile };
        writer = new(PostgresStore.ConnectionString(options, writer: true));
        Assert.Equal("explicit-writer.invalid", writer.Host);
        Assert.Equal(45432, writer.Port);
    }

    [Theory]
    [InlineData(0, "unknown")]
    [InlineData(10, "operational")]
    public void HistoricGroupsUseObservedSamplesWhileMissingChildrenKeepLiveStatusUnknown(int observations, string expected)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = new HistoryDocument
        {
            ComponentsById = new Dictionary<string, ComponentHistoryRecord>
            {
                ["observed"] = new()
                {
                    Days = observations == 0 ? [] : [new HistoryDay
                    {
                        Date = day, Status = ComponentStatus.Operational, Uptime = 1, Total = observations, Failed = 0
                    }]
                }
            }
        };
        var publication = new StatusApiResponse
        {
            Components = [new ComponentDto
            {
                Id = "group", Status = "unknown", History = [new HistoryDayDto { Date = day }],
                Children =
                [
                    new() { Id = "observed", History = [new HistoryDayDto { Date = day }] },
                    new() { Id = "missing", History = [] }
                ]
            }]
        };
        var group = Assert.Single(PublicationWorker.CorrectTodayHistory(publication, history).Components);
        Assert.Equal("unknown", group.Status);
        Assert.Equal(expected, Assert.Single(group.History).Status);
        Assert.Equal(observations, Assert.Single(group.History).Total);
        Assert.Equal(observations == 0 ? null : (double?)1, group.UptimeRatio);
    }

    private string Token(string role, string scope, string caller, string? invalid = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = invalid == "issuer" ? "https://untrusted.example" : $"https://login.microsoftonline.com/{_tenant}/v2.0",
            Audience = invalid == "audience" ? Guid.NewGuid().ToString() : _client,
            NotBefore = DateTime.UtcNow.AddHours(-1),
            Expires = invalid == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(role.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => new Claim("roles", value))
                .Concat([new("oid", Guid.NewGuid().ToString()), new("scp", scope), new("azp", caller)])),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        });

    [PostgresFact]
    public async Task ProbeDiagnosticsPersistUnderPublisherLeaseAndRemainHostScoped()
    {
        var connection = Path.Combine(_directory, "connection");
        File.WriteAllText(connection, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        var component = "fixture-" + Guid.NewGuid().ToString("N");
        var catalogPath = Path.Combine(_directory, "checks-catalog.json");
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(Catalogue().Sites.Select(site => site with
        {
            Probes = [],
            Checks = site.Site.Id == "mx"
                ? [new() { Id = component, Component = $"mx.{component}", Name = "Queue", Kind = "telemetry" }] : [],
            Components = site.Components with { Components = [.. site.Components.Components, new() { Id = $"{site.Site.Id}.{component}", Name = "Fixture" }] },
            SharedComponents = new(site.SharedComponents) { [$"{site.Site.Id}.{component}"] = component }
        }), StatusJson.Options));
        await using var app = Build(configuredIdentity: true, connection, builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Status:CatalogPath"] = catalogPath }));
        await app.StartAsync();
        var database = app.Services.GetRequiredService<PostgresStore>();
        await database.InitializeAsync(default);
        var probe = new ProbeObservation("mx", component, "Fixture website", "https://example.test/", "test-publisher",
            DateTimeOffset.UtcNow, false, 503, 13, "HTTP 503 Service Unavailable");
        try
        {
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Host = "mxstatus.io";
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Ingest", "", _ingestor));
            var path = $"/api/integrations/checks/mx/{component}";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new
            {
                site = "mx",
                checkId = component,
                observedAt = DateTimeOffset.UtcNow
            })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(path, new CheckResult("mx", component,
                ComponentStatus.Outage, DateTimeOffset.UtcNow), StatusJson.Options)).StatusCode);
            PublisherLease? lease = null;
            for (var attempt = 0; lease is null && attempt < 130; attempt++)
            {
                lease = await database.TryPublisherLockAsync(default);
                if (lease is null) await Task.Delay(TimeSpan.FromSeconds(1));
            }
            await using (lease)
            {
                Assert.NotNull(lease);
                await lease.SaveProbeEvidenceAsync(probe, default);
                var catalog = app.Services.GetRequiredService<StatusCatalog>();
                using var publisher = new PublicationWorker(catalog, app.Services.GetRequiredService<RuntimeOptions>(),
                    app.Services.GetRequiredService<PublicationStore>(), database, app.Services.GetRequiredService<IHttpClientFactory>(),
                    NullLogger<PublicationWorker>.Instance);
                await publisher.RefreshProbesAsync(lease, default);
                foreach (var site in catalog.Sites)
                {
                    var history = await database.ReadHistoryAsync(site, default);
                    var day = Assert.Single(history.ComponentsById[$"{site.Site.Id}.{component}"].Days);
                    Assert.Equal(1, day.Total);
                    Assert.Equal(1, day.Failed);
                }
            }
            Assert.Equal(probe, Assert.Single(await database.ReadProbesAsync("mx", default), record => record.Component == component));
            Assert.DoesNotContain(await database.ReadProbesAsync("xi", default), record => record.Component == component);
            client.DefaultRequestHeaders.Host = "mxstatus.io";
            client.DefaultRequestHeaders.Authorization = null;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/health")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Reader", "Status.Access", _ingestor));
            var checks = (await client.GetFromJsonAsync<ComponentHealth[]>("/api/ops/health", StatusJson.Options))!;
            Assert.All(checks, check => Assert.StartsWith("mx.", check.Id));
            client.DefaultRequestHeaders.Host = "status.xtremeidiots.com";
            checks = (await client.GetFromJsonAsync<ComponentHealth[]>("/api/ops/health", StatusJson.Options))!;
            Assert.All(checks, check => Assert.StartsWith("xi.", check.Id));
        }
        finally
        {
            await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(connection));
            await using var cleanup = source.CreateCommand("DELETE FROM status_evidence WHERE source IN ('independent-probe','independent-probe-failure','check-result') AND event_id=$1");
            cleanup.Parameters.AddWithValue($"mx/{component}");
            await cleanup.ExecuteNonQueryAsync();
            await using var counters = source.CreateCommand("DELETE FROM status_probe_days WHERE component_id IN ($1,$2)");
            counters.Parameters.AddWithValue($"mx.{component}");
            counters.Parameters.AddWithValue($"xi.{component}");
            await counters.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task ProbeSchedulingSurvivesRestartRetainsSlowFailuresAndCollectsSharedChecksOnce()
    {
        var connection = Path.Combine(_directory, "connection");
        File.WriteAllText(connection, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        var fixture = "schedule-" + Guid.NewGuid().ToString("N");
        var catalogPath = Path.Combine(_directory, "scheduling-catalog.json");
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(Catalogue().Sites.Select(site => site with
        {
            Probes = [],
            Checks =
            [
                new() { Id = $"{fixture}.fast", SharedId = $"{fixture}.fast", Component = $"{site.Site.Id}.{fixture}",
                    Name = "Fast", Target = "https://fast.example.test/", ExpectedIntervalSeconds = 30 },
                new() { Id = $"{fixture}.slow", SharedId = $"{fixture}.slow", Component = $"{site.Site.Id}.{fixture}",
                    Name = "Slow", Target = "https://slow.example.test/", ExpectedIntervalSeconds = 300 },
                .. (site.Site.Id == "mx" ? new List<CheckDefinition>
                {
                    new() { Id = $"{fixture}.one-sided", SharedId = $"{fixture}.one-sided", Component = $"mx.{fixture}.one-sided",
                        Name = "One-sided", Target = "https://fast.example.test/", ExpectedIntervalSeconds = 300 }
                } : [])
            ],
            Components = site.Components with
            {
                Components = [.. site.Components.Components,
                new() { Id = $"{site.Site.Id}.{fixture}", Name = "Fixture" },
                new() { Id = $"{site.Site.Id}.{fixture}.one-sided", Name = "One-sided fixture" }]
            },
            SharedComponents = new(site.SharedComponents)
            {
                [$"{site.Site.Id}.{fixture}"] = fixture,
                [$"{site.Site.Id}.{fixture}.one-sided"] = $"{fixture}.one-sided"
            }
        }), StatusJson.Options));
        await using var app = Build(configuredIdentity: true, connection, builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Status:CatalogPath"] = catalogPath }));
        await app.StartAsync();
        var database = app.Services.GetRequiredService<PostgresStore>();
        await database.InitializeAsync(default);
        var catalog = app.Services.GetRequiredService<StatusCatalog>();
        using var http = new SchedulingProbeFixture();
        try
        {
            PublisherLease? lease = null;
            for (var attempt = 0; lease is null && attempt < 130; attempt++)
            {
                lease = await database.TryPublisherLockAsync(default);
                if (lease is null) await Task.Delay(TimeSpan.FromSeconds(1));
            }
            await using (lease)
            {
                Assert.NotNull(lease);
                foreach (var site in catalog.Sites)
                    await lease.SaveProbeEvidenceAsync(new(site.Site.Id, $"{site.Site.Id}.{fixture}", "Slow",
                        "https://slow.example.test/", "publisher", DateTimeOffset.UtcNow.AddSeconds(-250),
                        false, 503, 10, "HTTP 503", $"{fixture}.slow"), default);
                for (var restart = 0; restart < 2; restart++)
                {
                    using var publisher = new PublicationWorker(catalog, app.Services.GetRequiredService<RuntimeOptions>(),
                        app.Services.GetRequiredService<PublicationStore>(), database, http, NullLogger<PublicationWorker>.Instance);
                    await publisher.RefreshProbesAsync(lease, default);
                    Assert.Equal(2, http.Requests);
                    var all = new Dictionary<string, Dictionary<string, MX.Platform.Status.App.Telemetry.ComponentLiveTelemetry>>();
                    foreach (var site in catalog.Sites)
                        all[site.Site.Id] = ComponentChecks.Build(site, await database.ReadProbesAsync(site.Site.Id, default), []);
                    foreach (var site in catalog.Sites)
                    {
                        var live = catalog.ProjectTelemetry(site, all);
                        var publication = new MX.Platform.Status.App.Merging.StatusMerger().Merge(site.Snapshot, live, null, [], []);
                        var component = publication.Components.Single(component => component.Id == $"{site.Site.Id}.{fixture}");
                        Assert.Equal("outage", component.Status);
                        Assert.Equal("outage", component.Checks!.Single(check => check.Id == $"{fixture}.slow").Status);
                        Assert.Equal("operational", component.Checks!.Single(check => check.Id == $"{fixture}.fast").Status);
                        var day = Assert.Single((await database.ReadHistoryAsync(site, default)).ComponentsById[component.Id].Days);
                        Assert.Equal(1, day.Total);
                        Assert.Equal(1, day.Failed);
                        var oneSided = Assert.Single((await database.ReadHistoryAsync(site, default))
                            .ComponentsById[$"{site.Site.Id}.{fixture}.one-sided"].Days);
                        Assert.Equal(1, oneSided.Total);
                        Assert.Equal(0, oneSided.Failed);
                    }
                }
                using var normal = new PublicationWorker(catalog, app.Services.GetRequiredService<RuntimeOptions>(),
                    app.Services.GetRequiredService<PublicationStore>(), database, http, NullLogger<PublicationWorker>.Instance);
                await normal.PublishAsync(lease, default);
                Assert.Equal(2, http.Requests);
                foreach (var site in catalog.Sites)
                    await lease.SaveProbeEvidenceAsync(new(site.Site.Id, $"{site.Site.Id}.{fixture}", "Fast",
                        "https://fast.example.test/", "publisher", DateTimeOffset.UtcNow.AddSeconds(-31),
                        true, 200, 5000, null, $"{fixture}.fast"), default);
                await normal.PublishAsync(lease, default);
                Assert.Equal(3, http.Requests);
            }
        }
        finally
        {
            await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(connection));
            await using var cleanup = source.CreateCommand("DELETE FROM status_evidence WHERE source IN ('independent-probe','independent-probe-failure') AND event_id IN ($1,$2,$3,$4,$5)");
            foreach (var key in new[] { $"mx/{fixture}.fast", $"mx/{fixture}.slow", $"xi/{fixture}.fast", $"xi/{fixture}.slow", $"mx/{fixture}.one-sided" })
                cleanup.Parameters.AddWithValue(key);
            await cleanup.ExecuteNonQueryAsync();
            await using var counters = source.CreateCommand("DELETE FROM status_probe_days WHERE component_id IN ($1,$2,$3,$4)");
            counters.Parameters.AddWithValue($"mx.{fixture}");
            counters.Parameters.AddWithValue($"xi.{fixture}");
            counters.Parameters.AddWithValue($"mx.{fixture}.one-sided");
            counters.Parameters.AddWithValue($"xi.{fixture}.one-sided");
            await counters.ExecuteNonQueryAsync();
        }
    }

    private sealed class SchedulingProbeFixture : HttpMessageHandler, IHttpClientFactory
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("fast.example.test", request.RequestUri!.Host);
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [PostgresFact]
    public async Task BannerApiPersistsAuditsEnforcesBrandAndRevisionAndRejectsReaderWrites()
    {
        var connection = Path.Combine(_directory, "connection");
        File.WriteAllText(connection, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        await using var app = Build(configuredIdentity: true, connection);
        await app.StartAsync();
        var database = app.Services.GetRequiredService<PostgresStore>();
        await database.InitializeAsync(default);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Reader", "Status.Access", _ingestor));
        var command = new BannerCommand("Persistence test notice", "Plain <script>text()</script>", "warning", false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/ops/banners", command)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Operator", "Status.Access", _ingestor));
        var response = await client.PostAsJsonAsync("/api/ops/banners", command);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var banner = (await response.Content.ReadFromJsonAsync<BannerRecord>(StatusJson.Options))!;
        try
        {
            client.DefaultRequestHeaders.Host = "status.xtremeidiots.com";
            var otherSite = (await client.GetFromJsonAsync<BannerRecord[]>("/api/ops/banners", StatusJson.Options))!;
            Assert.DoesNotContain(otherSite, record => record.Id == banner.Id);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}", command with { Revision = 1 })).StatusCode);
            client.DefaultRequestHeaders.Host = "mxstatus.io";
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}", command)).StatusCode);
            response = await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}", command with { Published = true, Revision = 1 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, (await response.Content.ReadFromJsonAsync<BannerRecord>(StatusJson.Options))!.Revision);
            await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(connection));
            await using var audit = source.CreateCommand("SELECT count(*) FROM status_banner_audit WHERE banner_id=$1 AND site_id='mx'");
            audit.Parameters.AddWithValue(banner.Id);
            Assert.Equal(2L, await audit.ExecuteScalarAsync());
            response = await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}", command with { Revision = 2 });
            Assert.False((await response.Content.ReadFromJsonAsync<BannerRecord>(StatusJson.Options))!.Published);
            response = await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}", command with { Revision = 3, Archived = true });
            var archived = (await response.Content.ReadFromJsonAsync<BannerRecord>(StatusJson.Options))!;
            Assert.True(archived.Archived);
            Assert.False(archived.Published);
            Assert.Equal(command.Message, archived.Message);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}",
                command with { Revision = 4, Archived = true, Published = true })).StatusCode);
            response = await client.PutAsJsonAsync($"/api/ops/banners/{banner.Id}", command with { Revision = 4 });
            var restored = (await response.Content.ReadFromJsonAsync<BannerRecord>(StatusJson.Options))!;
            Assert.False(restored.Archived);
            Assert.False(restored.Published);
            Assert.Equal(5L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(connection));
            await using var cleanup = source.CreateCommand("""
                WITH audit AS (DELETE FROM status_banner_audit WHERE banner_id=$1)
                DELETE FROM status_banners WHERE id=$1
                """);
            cleanup.Parameters.AddWithValue(banner.Id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task GitHubSourceKeepsStableIdsWithoutAuditChurnAndCannotBeEditedLocally()
    {
        var options = new RuntimeOptions
        {
            DatabaseConnectionFile = Path.Combine(_directory, "connection")
        };
        File.WriteAllText(options.DatabaseConnectionFile, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        await using var database = new PostgresStore(options, NullLogger<PostgresStore>.Instance);
        await database.InitializeAsync(default);
        const string repository = "status-fixture/persistence-tests";
        options = options with { GitHubRepository = repository };
        using var api = new GitHubApiFixture { Issues = [GitHubApiFixture.Incident(false, 0)] };
        using var worker = new GitHubIssueWorker(Catalogue(), options, database, api, NullLogger<GitHubIssueWorker>.Instance);
        var command = new IncidentCommand("GitHub persistence fixture", ["mx.sitewatch.molyneux-me"],
            Severity.Outage, IncidentState.Investigating, "Approved initial update", true);
        try
        {
            await worker.SynchronizeAsync(default);
            var first = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.False(first.Incident.Published);
            Assert.Empty(first.Incident.PublicUpdates);
            await worker.SynchronizeAsync(default);
            var repeated = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.Equal(first.Incident.Id, repeated.Incident.Id);
            Assert.Equal(first.Incident.Revision, repeated.Incident.Revision);
            Assert.Null(await database.SaveIncidentAsync("mx", first.Incident.Id, command with { Revision = first.Incident.Revision }, "operator", default));
            api.Issues = [GitHubApiFixture.Incident(true, 1)];
            await worker.SynchronizeAsync(default);
            var published = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.True(published.Incident.Published);
            Assert.Equal(first.Incident.Id, published.Incident.Id);
            Assert.Equal(["mx.sitewatch.molyneux-me"], published.Incident.Components);
            api.Status = HttpStatusCode.TooManyRequests;
            await Assert.ThrowsAsync<HttpRequestException>(() => worker.SynchronizeAsync(default));
            Assert.True(Assert.Single(await database.ReadGitHubSourcesAsync(repository, default)).Incident.Published);
            api.Status = HttpStatusCode.OK;
            api.Issues = [GitHubApiFixture.Incident(true, 2, closed: true)];
            await worker.SynchronizeAsync(default);
            var closed = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.Equal(first.Incident.Id, closed.Incident.Id);
            Assert.Equal(IncidentState.Resolved, closed.Incident.State);
            api.Issues = [GitHubApiFixture.Incident(true, 3), GitHubApiFixture.Incident(true, 2, closed: true)];
            await worker.SynchronizeAsync(default);
            var reopened = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.Equal(IncidentState.Investigating, reopened.Incident.State);
            Assert.Null(reopened.Incident.ResolvedAt);
            api.Issues = [GitHubApiFixture.Incident(true, 4, resolved: true)];
            await worker.SynchronizeAsync(default);
            var resolved = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            api.Issues = [GitHubApiFixture.Incident(true, 5, resolved: true)];
            await worker.SynchronizeAsync(default);
            var unchangedResolution = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.Equal(resolved.Incident.ResolvedAt, unchangedResolution.Incident.ResolvedAt);
            Assert.Equal(resolved.Incident.Revision, unchangedResolution.Incident.Revision);
            api.Issues = [GitHubApiFixture.Incident(false, 6, resolved: true)];
            await worker.SynchronizeAsync(default);
            var withdrawn = Assert.Single(await database.ReadGitHubSourcesAsync(repository, default));
            Assert.False(withdrawn.Incident.Published);
            Assert.DoesNotContain(await database.ReadPublicationIncidentsAsync("mx", default), record => record.Id == withdrawn.Incident.Id);
            await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(options.DatabaseConnectionFile));
            await using var audit = source.CreateCommand("SELECT count(*) FROM status_audit WHERE incident_id=$1");
            audit.Parameters.AddWithValue(first.Incident.Id);
            Assert.Equal(6L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(options.DatabaseConnectionFile));
            await using var connection = await source.OpenConnectionAsync();
            await using var remove = connection.CreateCommand();
            remove.CommandText = """
                WITH ids AS (DELETE FROM status_github_sources WHERE repository=$1 RETURNING incident_id),
                audit AS (DELETE FROM status_audit WHERE incident_id IN(SELECT incident_id FROM ids)),
                evidence AS (DELETE FROM status_evidence WHERE source='github-issues' AND event_id=$1)
                DELETE FROM status_incidents WHERE id IN(SELECT incident_id FROM ids)
                """;
            remove.Parameters.AddWithValue(repository);
            await remove.ExecuteNonQueryAsync();
        }
    }

    private Microsoft.AspNetCore.Builder.WebApplication Build(bool configuredIdentity = false, string? connectionFile = null,
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null)
    {
        var webRoot = Path.Combine(_directory, "webroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "static-shell");
        return ServiceProgram.Build(["--webroot", webRoot], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Status:CatalogPath"] = Path.Combine(AppContext.BaseDirectory, "catalog.json"),
                ["Status:DataDirectory"] = _directory,
                ["Status:DatabaseConnectionFile"] = connectionFile,
                ["Status:TenantId"] = configuredIdentity ? _tenant : null,
                ["Status:ClientId"] = configuredIdentity ? _client : null,
                ["Status:WebhookApplicationIds:0"] = _ingestor
            });
            if (configuredIdentity)
            {
                builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, settings =>
                {
                    settings.Configuration = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{_tenant}/v2.0" };
                    settings.Configuration.SigningKeys.Add(_signingKey);
                });
            }
            configure?.Invoke(builder);
        });
    }

    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION")))
            Skip = "Runs against the ephemeral primary/standby database in deployment/integration_test.py.";
    }
}
