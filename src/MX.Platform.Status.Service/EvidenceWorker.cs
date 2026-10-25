using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace MX.Platform.Status.Service;

public sealed class EvidenceWorker(
    RuntimeOptions options, PostgresStore database,
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

    private async Task CollectAzureStatusAsync(CancellationToken ct)
    {
        using var response = await clients.CreateClient("vendor").GetAsync(options.AzureStatusFeed,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var items = document.Descendants("item").Take(50).Select(item => new
        {
            title = item.Element("title")?.Value,
            link = item.Element("link")?.Value,
            description = item.Element("description")?.Value,
            publishedAt = item.Element("pubDate")?.Value
        }).ToArray();
        await database.SaveEvidenceAsync("azure-public-status", "feed", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { items, coverage = "Broad incidents only; not a complete Azure health feed." }), ct);
    }
}
