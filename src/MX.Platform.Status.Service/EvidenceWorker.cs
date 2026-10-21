using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Azure.Identity;
using Azure.Monitor.Query;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;

namespace MX.Platform.Status.Service;

public sealed class EvidenceWorker(
    StatusCatalog catalog, RuntimeOptions options, PostgresStore database,
    IHttpClientFactory clients, ILogger<EvidenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Publisher || options.DatabaseConnectionFile is null && options.DatabaseHost is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                if (await database.IsWritableAsync(timeout.Token))
                {
                    await CollectAzureStatusAsync(timeout.Token);
                    foreach (var site in catalog.Sites) await CollectAzureTelemetryAsync(site, timeout.Token);
                    await database.PruneAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Private evidence refresh failed; public publication is independent.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task CollectAzureTelemetryAsync(SiteDefinition site, CancellationToken ct)
    {
        if (options.AzureCertificatePath is null) return;
        var credential = new ClientCertificateCredential(options.TenantId, options.AzureCollectorClientId,
            options.AzureCertificatePath);
        var availability = new AvailabilityClient(new LogsQueryClient(credential));
        foreach (var component in StatusCatalog.Flatten(site.Components.Components).Where(c => c.Source.Kind == "appInsights"))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var regions = new List<RegionAvailabilityTelemetry>();
                foreach (var resource in component.Source.EffectiveResources())
                {
                    if (!site.Site.AppInsights.TryGetValue(resource, out var configured))
                        throw new InvalidOperationException("Unknown Application Insights resource alias.");
                    regions.AddRange(await availability.QueryLiveRegionalAsync(configured.ResourceId,
                        TelemetryFilters.WithSiteId(component.Source.Filter, site.Site.Id), 15, timeout.Token));
                }
                await database.SaveEvidenceAsync("sitewatch", $"{site.Site.Id}/{component.Id}", DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(new { site = site.Site.Id, component = component.Id, regions }, StatusJson.Options), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Sitewatch evidence refresh failed for {Site}/{Component}.", site.Site.Id, component.Id);
            }
        }
    }

    private async Task CollectAzureStatusAsync(CancellationToken ct)
    {
        using var response = await clients.CreateClient("vendor").GetAsync("https://azure.status.microsoft/status/feed/",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var items = document.Descendants("item").Take(50).Select(item => new
        {
            title = item.Element("title")?.Value, link = item.Element("link")?.Value,
            description = item.Element("description")?.Value, publishedAt = item.Element("pubDate")?.Value
        }).ToArray();
        await database.SaveEvidenceAsync("azure-public-status", "feed", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { items, coverage = "Broad incidents only; not a complete Azure health feed." }), ct);
    }
}
