using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using MX.Platform.Status.App.Incidents;
using MX.Platform.Status.App.Sites;
using System.Net;
using System.ServiceModel.Syndication;
using System.Xml;

namespace MX.Platform.Status.App.Functions;

public sealed class GetFeedFunction
{
    private readonly SiteResolver _siteResolver;
    private readonly SiteConfigLoader _siteConfigLoader;
    private readonly IncidentFetcher _incidentFetcher;

    public GetFeedFunction(SiteResolver siteResolver, SiteConfigLoader siteConfigLoader, IncidentFetcher incidentFetcher)
    {
        _siteResolver = siteResolver;
        _siteConfigLoader = siteConfigLoader;
        _incidentFetcher = incidentFetcher;
    }

    [Function("GetFeed")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feed.xml")] HttpRequestData req)
    {
        var host = req.Headers.TryGetValues("Host", out var values) ? values.FirstOrDefault() : null;
        var siteId = await _siteResolver.ResolveSiteIdAsync(host).ConfigureAwait(false);
        if (siteId is null)
        {
            var response = req.CreateResponse(HttpStatusCode.NotFound);
            await response.WriteStringAsync("Unconfigured host.").ConfigureAwait(false);
            return response;
        }

        var snapshot = await _siteConfigLoader.LoadSiteAsync(siteId).ConfigureAwait(false);
        if (!snapshot.Site.Feed.RssEnabled)
        {
            var response = req.CreateResponse(HttpStatusCode.NotFound);
            await response.WriteStringAsync("RSS feed is disabled for this site.").ConfigureAwait(false);
            return response;
        }

        var incidents = await _incidentFetcher.FetchForSiteAsync(siteId).ConfigureAwait(false);
        var feed = new SyndicationFeed(
            snapshot.Site.DisplayName,
            snapshot.Site.Tagline,
            new Uri($"https://{(snapshot.Site.Domains.Count > 0 ? snapshot.Site.Domains[0] : host ?? "localhost")}/api/feed.xml"),
            incidents.Take(Math.Max(1, snapshot.Site.Feed.MaxItems)).Select(incident =>
                new SyndicationItem(
                    incident.Title,
                    incident.Updates.Count == 0 ? string.Empty : incident.Updates[^1].Body,
                    new Uri(incident.Url),
                    incident.Id.ToString(),
                    incident.CreatedAt)));

        var responseData = req.CreateResponse(HttpStatusCode.OK);
        responseData.Headers.Add("Content-Type", "application/rss+xml; charset=utf-8");
        using var writer = XmlWriter.Create(responseData.Body, new XmlWriterSettings { Async = true, Indent = true });
        var formatter = new Rss20FeedFormatter(feed);
        formatter.WriteTo(writer);
        await writer.FlushAsync().ConfigureAwait(false);
        return responseData;
    }
}
