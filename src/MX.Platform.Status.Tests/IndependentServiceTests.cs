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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class IndependentServiceTests : IDisposable
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
            Subject = new ClaimsIdentity([new("oid", Guid.NewGuid().ToString()), new("roles", role), new("scp", scope), new("azp", caller)]),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        });

    private Microsoft.AspNetCore.Builder.WebApplication Build(bool configuredIdentity = false)
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
        });
    }

    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
