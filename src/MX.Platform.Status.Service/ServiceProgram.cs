using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public static class ServiceProgram
{
    private const string ReaderPolicy = "reader";
    private const string OperatorPolicy = "operator";

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--healthcheck"])
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var healthUri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), 8080, "/health/ready").Uri;
            try { return (await client.GetAsync(healthUri)).IsSuccessStatusCode ? 0 : 1; }
            catch (HttpRequestException) { return 1; }
            catch (TaskCanceledException) { return 1; }
        }
        if (args.Length is 2 or 3 && args[0] == "--emergency-publish")
        {
            await using var application = Build([]);
            var catalog = application.Services.GetRequiredService<StatusCatalog>();
            var command = JsonSerializer.Deserialize<IncidentCommand>(await Console.In.ReadToEndAsync(), StatusJson.Options)
                ?? throw new InvalidDataException("An incident command is required on standard input.");
            var site = catalog.Get(args[1]);
            var validation = command.Validate(site);
            if (validation is not null) throw new InvalidDataException(validation);
            var database = application.Services.GetRequiredService<PostgresStore>();
            var id = args.Length == 3 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
            var record = await database.SaveIncidentAsync(site.Site.Id, id, command, "emergency:local-administrator", CancellationToken.None)
                ?? throw new InvalidOperationException("Incident revision conflict.");
            Console.WriteLine(JsonSerializer.Serialize(record, StatusJson.Options));
            return 0;
        }
        await Build(args).RunAsync();
        return 0;
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var runtimeFile = Environment.GetEnvironmentVariable("STATUS_RUNTIME_FILE");
        if (runtimeFile is not null) builder.Configuration.AddJsonFile(runtimeFile, optional: false);
        configure?.Invoke(builder);
        var options = builder.Configuration.GetSection("Status").Get<RuntimeOptions>() ?? new();
        options.Validate();
        var catalogPath = Path.IsPathRooted(options.CatalogPath) ? options.CatalogPath
            : Path.Combine(builder.Environment.ContentRootPath, options.CatalogPath);
        var catalog = new StatusCatalog(catalogPath);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<PublicationStore>();
        builder.Services.AddSingleton<PostgresStore>();
        builder.Services.AddHostedService<PublicationWorker>();
        builder.Services.AddHostedService<EvidenceWorker>();
        builder.Services.AddHostedService<GitHubIssueWorker>();
        builder.Services.AddHttpClient("probe", client => client.DefaultRequestHeaders.UserAgent.ParseAdd("MXStatus/1.0"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddHttpClient("vendor", client => client.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddHttpClient("github", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 4_000_000;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.ConfigureHttpJsonOptions(settings =>
        {
            foreach (var converter in StatusJson.Options.Converters) settings.SerializerOptions.Converters.Add(converter);
        });
        ConfigureIdentity(builder.Services, options);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 131072);
        var app = builder.Build();
        app.UseExceptionHandler(error => error.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "Status operation unavailable. Public publications remain independent." });
        }));
        UseRequestBoundary(app, catalog);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (PublicationStore files, CancellationToken ct) =>
        {
            foreach (var site in catalog.Sites) await files.ReadAsync(site.Site.Id, ct);
            return Results.Ok(new { status = "ready" });
        });
        app.MapGet("/api/status", async (HttpContext context, PublicationStore files, CancellationToken ct) =>
        {
            var site = catalog.FindHost(context.Request.Host.Host)!;
            return Results.Json(await files.ReadAsync(site.Site.Id, ct), StatusJson.Options);
        });
        app.MapGet("/api/auth/config", () => Results.Ok(new
        {
            enabled = options.ClientId is not null,
            tenantId = options.TenantId,
            clientId = options.ClientId,
            scope = options.ClientId is null ? null : $"api://{options.ClientId}/Status.Access"
        }));
        app.MapGet("/api/ops/incidents", async (HttpContext context, PostgresStore database, CancellationToken ct) =>
            Results.Json(await database.ReadIncidentsAsync(catalog.FindHost(context.Request.Host.Host)!.Site.Id, ct), StatusJson.Options))
            .RequireAuthorization(ReaderPolicy);
        app.MapGet("/api/ops/evidence", async (PostgresStore database, CancellationToken ct) =>
            Results.Ok(await database.ReadEvidenceAsync(ct))).RequireAuthorization(ReaderPolicy);
        app.MapGet("/api/ops/health", async (HttpContext context, PublicationStore files, PostgresStore database, CancellationToken ct) =>
            Results.Ok(await HealthDiagnostics.ReadAsync(
                catalog.FindHost(context.Request.Host.Host)!, files, database, ct))).RequireAuthorization(ReaderPolicy);
        app.MapGet("/api/ops/runtime", async (HttpContext context, PostgresStore database, CancellationToken ct) =>
            Results.Ok(new
            {
                publisher = options.Publisher,
                writable = await database.IsWritableAsync(ct),
                github = new { enabled = options.GitHubEnabled, repository = options.GitHubRepository, pollSeconds = options.GitHubRefreshSeconds },
                workloads = catalog.FindHost(context.Request.Host.Host)!.Workloads
            }))
            .RequireAuthorization(ReaderPolicy);
        app.MapGet("/api/ops/banners", async (HttpContext context, PostgresStore database, CancellationToken ct) =>
            Results.Json(await database.ReadBannersAsync(catalog.FindHost(context.Request.Host.Host)!.Site.Id, ct), StatusJson.Options))
            .RequireAuthorization(ReaderPolicy);
        app.MapPost("/api/ops/banners", (HttpContext context, BannerCommand command, PostgresStore database, CancellationToken ct) =>
            SaveBannerAsync(context, command, null, catalog, database, ct)).RequireAuthorization(OperatorPolicy);
        app.MapPut("/api/ops/banners/{id:int}", (int id, HttpContext context, BannerCommand command, PostgresStore database, CancellationToken ct) =>
            SaveBannerAsync(context, command, id, catalog, database, ct)).RequireAuthorization(OperatorPolicy);
        app.MapPost("/api/ops/incidents", (HttpContext context, IncidentCommand command, PostgresStore database, CancellationToken ct) =>
            SaveIncidentAsync(context, command, null, catalog, database, ct)).RequireAuthorization(OperatorPolicy);
        app.MapPut("/api/ops/incidents/{id:int}", (int id, HttpContext context, IncidentCommand command, PostgresStore database, CancellationToken ct) =>
            SaveIncidentAsync(context, command, id, catalog, database, ct)).RequireAuthorization(OperatorPolicy);
        app.MapPost("/api/integrations/azure", async (JsonElement payload, PostgresStore database, CancellationToken ct) =>
        {
            if (!AzureAlert.TryParse(payload, out var alert)) return Results.BadRequest(new { error = "Unsupported Azure common alert payload." });
            if (!await database.IsWritableAsync(ct)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            await database.SaveEvidenceAsync("azure-alert", alert.Id, alert.ObservedAt, payload, ct);
            return Results.Accepted();
        }).RequireAuthorization("ingest");
        app.MapGet("/feed.xml", WriteFeedAsync);
        app.MapGet("/api/feed.xml", WriteFeedAsync);
        app.Map("/api/{**path}", () => Results.NotFound());
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("/component/{id}", "index.html");
        app.MapFallbackToFile("index.html");
        return app;
    }

    private static void UseRequestBoundary(WebApplication app, StatusCatalog catalog)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.CacheControl = IsRestrictedApi(context.Request.Path) ? "private, no-store" : "no-store";
            if (!context.Request.Path.StartsWithSegments("/health")
                && catalog.FindHost(context.Request.Host.Host) is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }

    private static void ConfigureIdentity(IServiceCollection services, RuntimeOptions options)
    {
        var authentication = services.AddAuthentication("request-boundary")
            .AddScheme<AuthenticationSchemeOptions, UnconfiguredAuthenticationHandler>("unconfigured", _ => { })
            .AddPolicyScheme("request-boundary", null, settings =>
            {
                settings.ForwardDefaultSelector = context =>
                    options.TenantId is not null && IsRestrictedApi(context.Request.Path)
                        ? JwtBearerDefaults.AuthenticationScheme : "unconfigured";
            });
        if (options.TenantId is not null)
        {
            authentication.AddJwtBearer(settings =>
            {
                settings.Authority = $"https://login.microsoftonline.com/{options.TenantId}/v2.0";
                settings.Audience = options.ClientId;
                settings.MapInboundClaims = false;
                settings.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidIssuer = $"https://login.microsoftonline.com/{options.TenantId}/v2.0",
                    RoleClaimType = "roles",
                    NameClaimType = "oid",
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });
        }
        services.AddAuthorization(auth =>
        {
            auth.AddPolicy(OperatorPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context => HasUserScope(context.User) && context.User.IsInRole("Status.Operator")));
            auth.AddPolicy(ReaderPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context => HasUserScope(context.User)
                    && (context.User.IsInRole("Status.Reader") || context.User.IsInRole("Status.Operator"))));
            auth.AddPolicy("ingest", policy => policy.RequireAuthenticatedUser().RequireRole("Status.Ingest")
                .RequireAssertion(context => options.WebhookApplicationIds.Contains(
                    context.User.FindFirstValue("azp") ?? context.User.FindFirstValue("appid"), StringComparer.OrdinalIgnoreCase)));
        });
    }

    private static bool IsRestrictedApi(PathString path) =>
        path.StartsWithSegments("/api/ops") || path.StartsWithSegments("/api/integrations");

    private static bool HasUserScope(ClaimsPrincipal user) =>
        user.FindFirstValue("scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Status.Access") == true;

    private static async Task<IResult> SaveIncidentAsync(HttpContext context, IncidentCommand command, int? id,
        StatusCatalog catalog, PostgresStore database, CancellationToken ct)
    {
        var site = catalog.FindHost(context.Request.Host.Host)!;
        var error = command.Validate(site);
        if (error is not null) return Results.BadRequest(new { error });
        if (!await database.IsWritableAsync(ct))
            return Results.Json(new { error = "This origin is read-only. Retry on the publisher; promotion requires fencing the former writer." }, statusCode: 503);
        var record = await database.SaveIncidentAsync(site.Site.Id, id, command,
            context.User.FindFirstValue("oid") ?? throw new InvalidOperationException("Operator identity is missing."), ct);
        var statusCode = id is null ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        return record is null ? Results.Conflict(new { error = "Incident changed, is managed in GitHub, or does not exist. Reload before editing." })
            : Results.Json(record, StatusJson.Options, statusCode: statusCode);
    }

    private static async Task<IResult> SaveBannerAsync(HttpContext context, BannerCommand command, int? id,
        StatusCatalog catalog, PostgresStore database, CancellationToken ct)
    {
        if (command.Validate() is { } error) return Results.BadRequest(new { error });
        if (!await database.IsWritableAsync(ct))
            return Results.Json(new { error = "Banner publishing is temporarily read-only because the writer is unavailable." }, statusCode: 503);
        var record = await database.SaveBannerAsync(catalog.FindHost(context.Request.Host.Host)!.Site.Id, id, command,
            context.User.FindFirstValue("oid") ?? throw new InvalidOperationException("Operator identity is missing."), ct);
        var statusCode = id.HasValue ? StatusCodes.Status200OK : StatusCodes.Status201Created;
        return record is null ? Results.Conflict(new { error = "Banner changed or does not exist. Reload before editing." })
            : Results.Json(record, StatusJson.Options, statusCode: statusCode);
    }

    private static async Task WriteFeedAsync(HttpContext context, StatusCatalog catalog, PublicationStore files, CancellationToken ct)
    {
        var site = catalog.FindHost(context.Request.Host.Host)!;
        var publication = await files.ReadAsync(site.Site.Id, ct);
        var feed = new SyndicationFeed(site.Site.DisplayName, site.Site.Tagline,
            new Uri($"https://{site.Site.Domains[0]}/"), publication.Incidents.Select(incident =>
                new SyndicationItem(incident.Title, WebUtility.HtmlDecode((incident.Updates.LastOrDefault()?.Body ?? "")
                        .Replace("<br>", "\n", StringComparison.Ordinal)),
                    new Uri($"https://{site.Site.Domains[0]}{incident.Url}"), $"{site.Site.Id}:{incident.Id}", incident.Updates.LastOrDefault()?.At ?? incident.CreatedAt)));
        context.Response.ContentType = "application/rss+xml; charset=utf-8";
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { CloseOutput = false }))
            new Rss20FeedFormatter(feed).WriteTo(writer);
        await context.Response.Body.WriteAsync(buffer.ToArray(), ct);
    }
}

public sealed class UnconfiguredAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}

public sealed record AzureAlert(string Id, DateTimeOffset ObservedAt)
{
    public static bool TryParse(JsonElement payload, out AzureAlert alert)
    {
        alert = new("", DateTimeOffset.MinValue);
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("schemaId", out var schema) || schema.ValueKind != JsonValueKind.String
            || schema.GetString() != "azureMonitorCommonAlertSchema"
            || !payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("essentials", out var essentials) || essentials.ValueKind != JsonValueKind.Object
            || !essentials.TryGetProperty("alertId", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString())
            || !essentials.TryGetProperty("monitorCondition", out var condition) || condition.ValueKind != JsonValueKind.String
            || condition.GetString() is not ("Fired" or "Resolved"))
            return false;
        var field = condition.GetString() == "Resolved" ? "resolvedDateTime" : "firedDateTime";
        if (!essentials.TryGetProperty(field, out var at) || at.ValueKind != JsonValueKind.String
            || !at.TryGetDateTimeOffset(out var observedAt) || observedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return false;
        alert = new(id.GetString()!, observedAt);
        return true;
    }
}
