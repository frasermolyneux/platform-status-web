using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using MX.Platform.Status.App.Caching;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.History;
using MX.Platform.Status.App.Incidents;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Sites;
using MX.Platform.Status.App.Telemetry;
using System.Net;
using System.Text.Json;

namespace MX.Platform.Status.App.Functions;

public sealed class GetStatusFunction
{
    private readonly SiteResolver _siteResolver;
    private readonly SiteConfigLoader _siteConfigLoader;
    private readonly StatusDependencies _statusDependencies;
    private readonly StatusMerger _statusMerger;
    private readonly InMemoryCache<StatusApiResponse> _cache;
    private readonly StaleCacheBlob _staleCacheBlob;
    private readonly ILogger<GetStatusFunction> _logger;

    public GetStatusFunction(
        SiteResolver siteResolver,
        SiteConfigLoader siteConfigLoader,
        StatusDependencies statusDependencies,
        StatusMerger statusMerger,
        InMemoryCache<StatusApiResponse> cache,
        StaleCacheBlob staleCacheBlob,
        ILogger<GetStatusFunction> logger)
    {
        _siteResolver = siteResolver;
        _siteConfigLoader = siteConfigLoader;
        _statusDependencies = statusDependencies;
        _statusMerger = statusMerger;
        _cache = cache;
        _staleCacheBlob = staleCacheBlob;
        _logger = logger;
    }

    [Function("GetStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequestData req)
    {
        var siteId = await ResolveSiteAsync(req).ConfigureAwait(false);
        if (siteId is null)
        {
            var response = req.CreateResponse(HttpStatusCode.NotFound);
            await response.WriteStringAsync("Host not configured for this status service.").ConfigureAwait(false);
            return response;
        }

        if (_cache.TryGetValue(siteId, out var cached) && cached is not null)
        {
            return await WriteJsonAsync(req, cached).ConfigureAwait(false);
        }

        try
        {
            var snapshot = await _siteConfigLoader.LoadSiteAsync(siteId).ConfigureAwait(false);
            var liveData = await QueryLiveDataAsync(snapshot).ConfigureAwait(false);
            var history = await _statusDependencies.HistoryReader.ReadDailyHistoryAsync(siteId).ConfigureAwait(false);
            var incidents = await _statusDependencies.IncidentFetcher.FetchForSiteAsync(siteId).ConfigureAwait(false);
            var maintenance = await _statusDependencies.MaintenanceFetcher.FetchForSiteAsync(siteId).ConfigureAwait(false);
            var response = _statusMerger.Merge(snapshot, liveData, history, incidents, maintenance);

            _cache.Set(siteId, response);
            await _staleCacheBlob.SaveAsync(siteId, response).ConfigureAwait(false);
            return await WriteJsonAsync(req, response).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to generate live status response for site '{SiteId}'.", siteId);
            var stale = await _staleCacheBlob.LoadAsync(siteId).ConfigureAwait(false);
            if (stale is not null)
            {
                var staleResponse = stale with
                {
                    DataFreshness = (stale.DataFreshness ?? new DataFreshness()) with { Stale = true },
                    GeneratedAt = DateTimeOffset.UtcNow
                };
                return await WriteJsonAsync(req, staleResponse).ConfigureAwait(false);
            }

            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Status data is currently unavailable.").ConfigureAwait(false);
            return response;
        }
    }

    private async Task<Dictionary<string, ComponentLiveTelemetry>> QueryLiveDataAsync(SiteConfigurationSnapshot snapshot)
    {
        var components = Flatten(snapshot.Components.Components)
            .Where(c => c.Kind.Equals("leaf", StringComparison.OrdinalIgnoreCase)
                && c.Source.Kind.Equals("appInsights", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Source.Resource)
                && snapshot.Site.AppInsights.ContainsKey(c.Source.Resource))
            .ToList();

        var tasks = components.Select(async component =>
        {
            var resourceKey = component.Source.Resource!;
            var resource = snapshot.Site.AppInsights[resourceKey];
            var telemetry = await _statusDependencies.AvailabilityClient.QueryLiveTodayAsync(resource.ResourceId, component.Source.Filter).ConfigureAwait(false);
            return (component.Id, telemetry);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(result => result.Id, result => result.telemetry, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveSiteAsync(HttpRequestData req)
    {
        if (req.Headers.TryGetValues("Host", out var values))
        {
            return await _siteResolver.ResolveSiteIdAsync(values.FirstOrDefault()).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, StatusApiResponse payload)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Cache-Control", "public, max-age=30, s-maxage=30");
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, StatusJson.Options)).ConfigureAwait(false);
        return response;
    }

    private static IEnumerable<Component> Flatten(IEnumerable<Component> components)
    {
        foreach (var component in components)
        {
            yield return component;
            foreach (var child in Flatten(component.Children))
            {
                yield return child;
            }
        }
    }
}

public sealed record StatusDependencies(
    AvailabilityClient AvailabilityClient,
    HistoryReader HistoryReader,
    IncidentFetcher IncidentFetcher,
    MaintenanceFetcher MaintenanceFetcher);
