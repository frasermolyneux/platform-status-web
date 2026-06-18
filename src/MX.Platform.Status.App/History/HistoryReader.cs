using Azure.Storage.Blobs;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.History;

public sealed class HistoryReader
{
    private readonly BlobContainerClient _containerClient;

    public HistoryReader(BlobServiceClient blobServiceClient)
    {
        var containerName = Environment.GetEnvironmentVariable("HISTORY_BLOB_CONTAINER") ?? "history";
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public Task<HistoryDocument?> ReadDailyHistoryAsync(string siteId, CancellationToken cancellationToken = default) =>
        ReadAsync<HistoryDocument>($"history/{siteId}.json", cancellationToken);

    public Task<YearlyHistoryDocument?> ReadYearlyHistoryAsync(string siteId, CancellationToken cancellationToken = default) =>
        ReadAsync<YearlyHistoryDocument>($"history/{siteId}-yearly.json", cancellationToken);

    private async Task<T?> ReadAsync<T>(string blobName, CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var blobClient = _containerClient.GetBlobClient(blobName);
        if (!await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        var download = await blobClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return download.Value.Content.ToObjectFromJson<T>(StatusJson.Options);
    }
}
