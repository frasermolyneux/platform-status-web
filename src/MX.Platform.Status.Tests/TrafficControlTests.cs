using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed partial class IndependentServiceTests
{
    private TrafficConfiguration TrafficConfig() => new()
    {
        Version = 1,
        AccountId = new('a', 32),
        Scripts = new() { ["page"] = "platform-maintenance-page", ["redirect"] = "platform-maintenance-redirect" },
        Hosts = [
            new("bishopsbees.co.uk", "mx", new('b', 32)),
            new("www.xtremeidiots.com", "xi", new('c', 32))
        ]
    };

    [Theory]
    [InlineData("mxstatus.io")]
    [InlineData("status.xtremeidiots.com")]
    [InlineData("*.xtremeidiots.com")]
    [InlineData("example.com/path")]
    public void TrafficRegistryRejectsStatusHostsAndNonExactNames(string hostname)
    {
        var configuration = TrafficConfig() with { Hosts = [new(hostname, "mx", new('b', 32))] };
        Assert.Throws<InvalidDataException>(() => configuration.Validate(Catalogue()));
    }

    [Theory]
    [InlineData("*xtremeidiots.com/*", "www.xtremeidiots.com", true)]
    [InlineData("https://www.xtremeidiots.com/admin/*", "www.xtremeidiots.com", true)]
    [InlineData("maps.xtremeidiots.com/*", "www.xtremeidiots.com", false)]
    [InlineData("*.xtremeidiots.com/*", "xtremeidiots.com", false)]
    public void ExistingWorkerConflictsIncludeProtocolAndPathSpecificRoutes(string pattern, string hostname, bool affects) =>
        Assert.Equal(affects, CloudflareTraffic.AffectsHost(new("id", pattern, "foreign", false), hostname));

    [Theory]
    [InlineData("Status.Reader", "Status.Access", 403)]
    [InlineData("Status.Operator", "Status.Access", 403)]
    [InlineData("Status.TrafficOperator", "Status.Access", 403)]
    [InlineData("Status.Operator Status.TrafficOperator", "", 403)]
    [InlineData("Status.Operator Status.TrafficOperator", "Status.Access", 400)]
    public async Task TrafficWritesRequireTheDedicatedUserRoleAndDelegatedScope(string roles, string scope, int expected)
    {
        await using var app = Build(configuredIdentity: true);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(roles, scope, _ingestor));
        var response = await client.PostAsJsonAsync("/api/ops/traffic/bishopsbees.co.uk",
            new TrafficCommand("page", "Operator request", "wrong-host"));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task TrafficClientRefusesUnproxiedHostsAndDoesNotSendCredentialsToVerificationTargets()
    {
        using var edge = new TrafficHttp { Proxied = false };
        var tokenFile = Path.Combine(_directory, "traffic-token");
        await File.WriteAllTextAsync(tokenFile, "unit-test-credential");
        var api = new CloudflareTraffic(edge, new() { TrafficTokenFile = tokenFile }, NullLogger<CloudflareTraffic>.Instance);
        await Assert.ThrowsAsync<TrafficConflictException>(() => api.RequireProxiedAsync(TrafficConfig().Hosts[0], default));
        Assert.Equal(0, edge.Mutations);
        await api.ProbeAsync(TrafficConfig().Hosts[0], "normal", "https://mxstatus.io/", default);
        Assert.True(edge.VerificationWasAnonymous);
    }

    [Fact]
    public async Task PartialTrafficConfigurationFailsPrivatelyWithoutStoppingPublicPublications()
    {
        await using var app = Build(configuredIdentity: true, configure: builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Status:TrafficTokenFile"] = "configured-without-registry"
            }));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Reader", "Status.Access", _ingestor));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/ops/traffic")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    [PostgresFact]
    public async Task TrafficControlsPersistAuditsFenceOperationsAndFollowLiveCloudflareState()
    {
        using var edge = new TrafficHttp();
        await using var app = BuildTraffic(edge);
        await app.StartAsync();
        var database = app.Services.GetRequiredService<PostgresStore>();
        await database.InitializeAsync(default);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Operator Status.TrafficOperator", "Status.Access", _ingestor));
        const string hostname = "bishopsbees.co.uk";
        var command = new TrafficCommand("page", "PRIVATE_TRAFFIC_AUDIT_REASON", hostname);
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/ops/traffic/www.xtremeidiots.com",
                command with { Confirmation = "www.xtremeidiots.com" })).StatusCode);
            var response = await client.PostAsJsonAsync("/api/ops/traffic/" + hostname, command);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var active = (await response.Content.ReadFromJsonAsync<TrafficRow>(StatusJson.Options))!;
            Assert.True(active.Verified);
            Assert.Equal("active", active.State);
            Assert.Equal(1, active.Revision);
            Assert.Equal(2, edge.Mutations);
            Assert.False(edge.Routes.Single().Value.RequestLimitFailOpen);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/ops/traffic/" + hostname, command)).StatusCode);
            var restore = command with
            {
                Mode = "normal",
                Revision = active.Revision,
                RouteId = active.RouteId,
                RouteFingerprint = active.RouteFingerprint
            };
            await using (var lease = await database.TryTrafficLockAsync(hostname, default))
            {
                Assert.NotNull(lease);
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/ops/traffic/" + hostname, restore)).StatusCode);
            }
            client.DefaultRequestHeaders.Host = "status.xtremeidiots.com";
            var other = (await client.GetFromJsonAsync<TrafficWorkspace>("/api/ops/traffic", StatusJson.Options))!;
            Assert.Single(other.Hosts);
            Assert.Equal("normal", other.Hosts[0].Mode);
            client.DefaultRequestHeaders.Host = "mxstatus.io";
            edge.StaleNormalResponses = 1;
            response = await client.PostAsJsonAsync("/api/ops/traffic/" + hostname, restore);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(edge.Routes);
            var normal = (await response.Content.ReadFromJsonAsync<TrafficRow>(StatusJson.Options))!;
            Assert.Equal(2, normal.Revision);
            await using var source = Npgsql.NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION")!);
            await using var audit = source.CreateCommand("SELECT count(*) FROM status_traffic_audit WHERE hostname=$1");
            audit.Parameters.AddWithValue(hostname);
            Assert.Equal(4L, await audit.ExecuteScalarAsync());
            edge.Routes.Add("foreign", new("foreign", "https://bishopsbees.co.uk/admin/*", "another-worker", false));
            var conflict = (await client.GetFromJsonAsync<TrafficWorkspace>("/api/ops/traffic", StatusJson.Options))!.Hosts[0];
            Assert.Equal("conflict", conflict.State);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/ops/traffic/" + hostname,
                command with { Revision = 2 })).StatusCode);
            Assert.Equal("another-worker", edge.Routes["foreign"].Script);
            var publication = await client.GetStringAsync("/api/status");
            Assert.DoesNotContain("PRIVATE_TRAFFIC_AUDIT_REASON", publication);
            Assert.DoesNotContain("traffic", publication, StringComparison.OrdinalIgnoreCase);
        }
        finally { await CleanupTrafficAsync(); }
    }

    [Theory]
    [InlineData("""{"success":true,"result":{}}""")]
    [InlineData("""{"success":true,"result":[{}]}""")]
    [InlineData("""{"success":true,"result":[{"id":"route","pattern":"example.com/*","script":123}]}""")]
    public async Task TrafficClientRejectsMalformedSuccessfulResponses(string response)
    {
        using var edge = new TrafficHttp { OverrideResponse = response };
        var tokenFile = Path.Combine(_directory, "traffic-token");
        await File.WriteAllTextAsync(tokenFile, "unit-test-credential");
        var api = new CloudflareTraffic(edge, new() { TrafficTokenFile = tokenFile }, NullLogger<CloudflareTraffic>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => api.RoutesAsync(new('b', 32), default));
        Assert.Equal(0, edge.Mutations);
    }

    [Fact]
    public async Task TrafficVerificationRejectsRelativeRedirectDestinationsWithoutThrowing()
    {
        using var edge = new TrafficHttp { RelativeRedirect = true };
        edge.Routes.Add("route", new("route", "bishopsbees.co.uk/*", "platform-maintenance-redirect", false));
        var api = new CloudflareTraffic(edge, new(), NullLogger<CloudflareTraffic>.Instance);
        var result = await api.ProbeAsync(TrafficConfig().Hosts[0], "redirect", "https://mxstatus.io/", default);
        Assert.False(result.Matches);
        Assert.Equal(302, result.Status);
    }

    [Theory]
    [InlineData(79_999, false)]
    [InlineData(80_000, true)]
    public async Task TrafficQuotaWarnsAtTheSharedAccountThreshold(long requests, bool warning)
    {
        using var edge = new TrafficHttp { Requests = requests };
        var tokenFile = Path.Combine(_directory, "traffic-token");
        await File.WriteAllTextAsync(tokenFile, "unit-test-credential");
        var api = new CloudflareTraffic(edge, new() { TrafficTokenFile = tokenFile }, NullLogger<CloudflareTraffic>.Instance);
        var quota = await api.QuotaAsync(new('a', 32), default);
        Assert.Equal(requests, quota.Requests);
        Assert.Equal(warning, quota.Warning);
        Assert.Null(quota.Error);
    }

    [Fact]
    public async Task MalformedTrafficQuotaIsUnavailableRatherThanZeroOrFatal()
    {
        using var edge = new TrafficHttp
        {
            OverrideResponse = """{"data":{"viewer":{"accounts":[{"workersInvocationsAdaptive":[{"sum":{"requests":1.5}}]}]}}}"""
        };
        var tokenFile = Path.Combine(_directory, "traffic-token");
        await File.WriteAllTextAsync(tokenFile, "unit-test-credential");
        var api = new CloudflareTraffic(edge, new() { TrafficTokenFile = tokenFile }, NullLogger<CloudflareTraffic>.Instance);
        var quota = await api.QuotaAsync(new('a', 32), default);
        Assert.Null(quota.Requests);
        Assert.NotNull(quota.Error);
    }

    [PostgresFact]
    public async Task TrafficFailuresRemainAuditedWithoutAutomaticallyReopeningTheOrigin()
    {
        using var edge = new TrafficHttp { ExposeFailureMode = false };
        await using var app = BuildTraffic(edge);
        await app.StartAsync();
        var database = app.Services.GetRequiredService<PostgresStore>();
        await database.InitializeAsync(default);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Operator Status.TrafficOperator", "Status.Access", _ingestor));
        try
        {
            var command = new TrafficCommand("page", "Failure mode verification", "bishopsbees.co.uk");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/ops/traffic/bishopsbees.co.uk", command)).StatusCode);
            Assert.Single(edge.Routes);
            var record = (await database.ReadTrafficAsync("mx", default))["bishopsbees.co.uk"];
            Assert.Equal("failed", record.Phase);
            Assert.Equal(1, record.Revision);
            var row = (await client.GetFromJsonAsync<TrafficWorkspace>("/api/ops/traffic", StatusJson.Options))!.Hosts[0];
            Assert.Equal("unsafe", row.State);
            edge.ExposeFailureMode = true;
            var restored = await client.PostAsJsonAsync("/api/ops/traffic/bishopsbees.co.uk",
                command with
                {
                    Mode = "normal",
                    Revision = 1,
                    RouteId = edge.Routes.Single().Key,
                    RouteFingerprint = CloudflareTraffic.Fingerprint(edge.Routes.Single().Value)
                });
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Empty(edge.Routes);
            var pending = new TrafficRecord(command.Confirmation, "mx", 3, "page", "enabling",
                _ingestor.ToString(), "Interrupted operation", null, DateTimeOffset.UtcNow);
            await using (var lease = await database.TryTrafficLockAsync(command.Confirmation, default))
                await lease!.SaveAsync(pending, default);
            var mutations = edge.Mutations;
            var retry = await client.PostAsJsonAsync("/api/ops/traffic/" + command.Confirmation, command with { Revision = 3 });
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Contains("reconciliation window", await retry.Content.ReadAsStringAsync());
            Assert.Equal(mutations, edge.Mutations);
            await using (var lease = await database.TryTrafficLockAsync(command.Confirmation, default))
                await lease!.SaveAsync(pending with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3) }, default);
            var resumed = await client.PostAsJsonAsync("/api/ops/traffic/" + command.Confirmation, command with { Revision = 3 });
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        }
        finally { await CleanupTrafficAsync(); }
    }

    [Fact]
    public async Task MalformedTrafficCredentialsArePrivateUnavailabilityWithoutAnApiRequest()
    {
        using var edge = new TrafficHttp();
        var tokenFile = Path.Combine(_directory, "traffic-token");
        await File.WriteAllTextAsync(tokenFile, "malformed\ncredential");
        var api = new CloudflareTraffic(edge, new() { TrafficTokenFile = tokenFile }, NullLogger<CloudflareTraffic>.Instance);
        var quota = await api.QuotaAsync(new('a', 32), default);
        Assert.Null(quota.Requests);
        Assert.NotNull(quota.Error);
        Assert.Equal(0, edge.ApiCalls);
    }

    [PostgresFact]
    public async Task TrafficConcurrencyRejectsDashboardEditsAndStopsAfterLeaseSessionLoss()
    {
        using var edge = new TrafficHttp();
        await using var app = BuildTraffic(edge);
        await app.StartAsync();
        var database = app.Services.GetRequiredService<PostgresStore>();
        await database.InitializeAsync(default);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "mxstatus.io";
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("Status.Operator Status.TrafficOperator", "Status.Access", _ingestor));
        const string host = "bishopsbees.co.uk";
        var command = new TrafficCommand("page", "Concurrency regression", host);
        try
        {
            var enabled = await client.PostAsJsonAsync("/api/ops/traffic/" + host, command);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            var active = (await enabled.Content.ReadFromJsonAsync<TrafficRow>(StatusJson.Options))!;
            edge.Routes[active.RouteId!] = edge.Routes[active.RouteId!] with { Script = "platform-maintenance-redirect" };
            var stale = command with
            {
                Mode = "normal",
                Revision = active.Revision,
                RouteId = active.RouteId,
                RouteFingerprint = active.RouteFingerprint
            };
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/ops/traffic/" + host, stale)).StatusCode);
            Assert.Equal("platform-maintenance-redirect", edge.Routes[active.RouteId!].Script);
            Assert.Equal(2, edge.Mutations);

            var refreshed = (await client.GetFromJsonAsync<TrafficWorkspace>("/api/ops/traffic", StatusJson.Options))!.Hosts[0];
            edge.BeforeApi = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/dns_records", StringComparison.Ordinal))
                {
                    edge.Routes[active.RouteId!] = edge.Routes[active.RouteId!] with
                    { Pattern = "unregistered.example/*", Script = "foreign-worker" };
                    edge.BeforeApi = null;
                }
                return Task.CompletedTask;
            };
            var changed = await client.PostAsJsonAsync("/api/ops/traffic/" + host,
                command with { Revision = refreshed.Revision, RouteId = refreshed.RouteId, RouteFingerprint = refreshed.RouteFingerprint });
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("foreign-worker", edge.Routes[active.RouteId!].Script);
            Assert.Equal(2, edge.Mutations);

            edge.Routes.Clear();
            enabled = await client.PostAsJsonAsync("/api/ops/traffic/" + host, command with { Revision = 2 });
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            active = (await enabled.Content.ReadFromJsonAsync<TrafficRow>(StatusJson.Options))!;
            edge.BeforeApi = async request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/workers/routes", StringComparison.Ordinal)) return;
                var pending = (await database.ReadTrafficAsync("mx", default))[host];
                if (pending.Phase != "restoring") return;
                edge.BeforeApi = null;
                await using var source = Npgsql.NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION")!);
                await using var kill = source.CreateCommand("""
                    SELECT pg_terminate_backend(pid,5000) FROM pg_locks
                    WHERE locktype='advisory' AND classid=814233110::oid AND objsubid=2
                    AND objid=(hashtext($1)::bigint & 4294967295)::oid AND pid<>pg_backend_pid()
                    """);
                kill.Parameters.AddWithValue(host);
                Assert.True((bool)(await kill.ExecuteScalarAsync())!);
                await using var newer = await database.TryTrafficLockAsync(host, default);
                Assert.NotNull(newer);
                await newer.SaveAsync(pending with
                {
                    Revision = pending.Revision + 1,
                    Phase = "active",
                    Mode = "page",
                    Reason = "Newer operation after interrupted session",
                    UpdatedAt = DateTimeOffset.UtcNow
                }, default);
            };
            var mutations = edge.Mutations;
            var interrupted = await client.PostAsJsonAsync("/api/ops/traffic/" + host,
                command with
                {
                    Mode = "normal",
                    Revision = active.Revision,
                    RouteId = active.RouteId,
                    RouteFingerprint = active.RouteFingerprint
                });
            Assert.Equal(HttpStatusCode.Conflict, interrupted.StatusCode);
            Assert.Single(edge.Routes);
            Assert.Equal(mutations, edge.Mutations);
            var retained = (await database.ReadTrafficAsync("mx", default))[host];
            Assert.Equal(5, retained.Revision);
            Assert.Equal("active", retained.Phase);
            Assert.Equal("Newer operation after interrupted session", retained.Reason);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
            var live = (await client.GetFromJsonAsync<TrafficWorkspace>("/api/ops/traffic", StatusJson.Options))!.Hosts[0];
            var restored = await client.PostAsJsonAsync("/api/ops/traffic/" + host,
                command with { Mode = "normal", Revision = live.Revision, RouteId = live.RouteId, RouteFingerprint = live.RouteFingerprint });
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Empty(edge.Routes);
        }
        finally { await CleanupTrafficAsync(); }
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildTraffic(TrafficHttp edge)
    {
        var connection = Path.Combine(_directory, "traffic-connection");
        File.WriteAllText(connection, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        var configuration = Path.Combine(_directory, "traffic-configuration");
        File.WriteAllText(configuration, JsonSerializer.Serialize(TrafficConfig(), StatusJson.Options));
        var token = Path.Combine(_directory, "traffic-token");
        File.WriteAllText(token, "unit-test-credential");
        return Build(configuredIdentity: true, connection, builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Status:TrafficConfigurationFile"] = configuration,
                ["Status:TrafficTokenFile"] = token
            });
            builder.Services.AddSingleton<IHttpClientFactory>(edge);
        });
    }

    private static async Task CleanupTrafficAsync()
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION")!);
        await using var cleanup = source.CreateCommand("""
            WITH audit AS (DELETE FROM status_traffic_audit WHERE hostname='bishopsbees.co.uk')
            DELETE FROM status_traffic WHERE hostname='bishopsbees.co.uk'
            """);
        await cleanup.ExecuteNonQueryAsync();
    }

    private sealed class TrafficHttp : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, TrafficRoute> Routes { get; } = [];
        public int Mutations { get; private set; }
        public bool Proxied { get; init; } = true;
        public bool ExposeFailureMode { get; set; } = true;
        public bool VerificationWasAnonymous { get; private set; }
        public string? OverrideResponse { get; init; }
        public bool RelativeRedirect { get; init; }
        public long Requests { get; init; } = 123;
        public int StaleNormalResponses { get; set; }
        public int ApiCalls { get; private set; }
        public Func<HttpRequestMessage, Task>? BeforeApi { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(2) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host != "api.cloudflare.com")
            {
                VerificationWasAnonymous = request.Headers.Authorization is null;
                Assert.True(VerificationWasAnonymous);
                var route = Routes.Values.SingleOrDefault(route => route.Pattern == uri.Host + "/*");
                if (route is null && StaleNormalResponses > 0)
                {
                    StaleNormalResponses--;
                    route = new("stale-edge", uri.Host + "/*", "platform-maintenance-page", false);
                }
                var response = new HttpResponseMessage(route is null ? HttpStatusCode.OK
                    : route.Script!.EndsWith("-page", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Found);
                if (route is not null)
                {
                    response.Headers.Add("X-Platform-Maintenance", "v1");
                    if (response.StatusCode == HttpStatusCode.Found)
                        response.Headers.Location = RelativeRedirect ? new Uri("/status", UriKind.Relative) : new Uri(uri.Host.EndsWith("xtremeidiots.com", StringComparison.Ordinal)
                            ? "https://status.xtremeidiots.com/" : "https://mxstatus.io/");
                }
                return response;
            }
            ApiCalls++;
            Assert.Equal("unit-test-credential", request.Headers.Authorization?.Parameter);
            if (BeforeApi is not null) await BeforeApi(request);
            if (OverrideResponse is not null)
                return new(HttpStatusCode.OK) { Content = new StringContent(OverrideResponse) };
            if (uri.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal))
                return Json(new { data = new { viewer = new { accounts = new[] { new { workersInvocationsAdaptive = new[] { new { sum = new { requests = Requests } } } } } } } });
            if (uri.AbsolutePath.EndsWith("/dns_records", StringComparison.Ordinal))
                return Json(new { success = true, result = new[] { new { name = "bishopsbees.co.uk", type = "A", proxied = Proxied } } });
            if (request.Method == HttpMethod.Get)
            {
                var zoneId = uri.AbsolutePath.Split('/')[4];
                var rows = Routes.Values.Where(route => zoneId == new string('b', 32) || route.Pattern.StartsWith("www.xtremeidiots.com", StringComparison.Ordinal));
                return Json(new
                {
                    success = true,
                    result = rows.Select(route =>
                {
                    var value = new Dictionary<string, object?> { ["id"] = route.Id, ["pattern"] = route.Pattern, ["script"] = route.Script };
                    if (ExposeFailureMode) value["request_limit_fail_open"] = route.RequestLimitFailOpen;
                    return value;
                })
                });
            }
            Mutations++;
            if (request.Method == HttpMethod.Delete)
            {
                Routes.Remove(uri.Segments.Last().Trim('/'));
                return Json(new { success = true, result = new { } });
            }
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var id = request.Method == HttpMethod.Post ? Guid.NewGuid().ToString("N") : uri.Segments.Last().Trim('/');
            Routes[id] = new(id, payload.GetProperty("pattern").GetString()!, payload.GetProperty("script").GetString(),
                payload.GetProperty("request_limit_fail_open").GetBoolean());
            return Json(new { success = true, result = new { id } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
