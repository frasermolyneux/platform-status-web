using Azure.Storage.Blobs;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Caching;

public sealed class StaleCacheBlob
{
    private readonly BlobContainerClient _containerClient;

    public StaleCacheBlob(BlobServiceClient blobServiceClient)
    {
        var containerName = Environment.GetEnvironmentVariable("STALE_CACHE_BLOB_CONTAINER") ?? "stale-cache";
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task SaveAsync(string siteId, StatusApiResponse response, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var blobClient = _containerClient.GetBlobClient($"responses/{siteId}.json");
        var payload = BinaryData.FromObjectAsJson(response, StatusJson.Options);
        await blobClient.UploadAsync(payload, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StatusApiResponse?> LoadAsync(string siteId, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var blobClient = _containerClient.GetBlobClient($"responses/{siteId}.json");
        if (!await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var download = await blobClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return download.Value.Content.ToObjectFromJson<StatusApiResponse>(StatusJson.Options);
    }
}
