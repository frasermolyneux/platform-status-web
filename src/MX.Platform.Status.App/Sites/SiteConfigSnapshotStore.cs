using Azure.Storage.Blobs;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Sites;

public class SiteConfigSnapshotStore
{
    private readonly BlobContainerClient _containerClient;

    public SiteConfigSnapshotStore(BlobServiceClient blobServiceClient)
    {
        var containerName = Environment.GetEnvironmentVariable("STALE_CACHE_BLOB_CONTAINER") ?? "stale-cache";
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public virtual async Task SaveAsync(string siteId, SiteConfigSnapshotContent content, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await UploadTextAsync(GetBlobPath(siteId, "site.yaml"), content.SiteYaml, cancellationToken).ConfigureAwait(false);
        await UploadTextAsync(GetBlobPath(siteId, "components.yaml"), content.ComponentsYaml, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<SiteConfigSnapshotContent?> LoadAsync(string siteId, CancellationToken cancellationToken = default)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var siteYaml = await DownloadTextIfExistsAsync(GetBlobPath(siteId, "site.yaml"), cancellationToken).ConfigureAwait(false);
        var componentsYaml = await DownloadTextIfExistsAsync(GetBlobPath(siteId, "components.yaml"), cancellationToken).ConfigureAwait(false);
        return siteYaml is null || componentsYaml is null ? null : new SiteConfigSnapshotContent(siteYaml, componentsYaml);
    }

    private async Task UploadTextAsync(string blobName, string content, CancellationToken cancellationToken)
    {
        var blobClient = _containerClient.GetBlobClient(blobName);
        await blobClient.UploadAsync(BinaryData.FromString(content), overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> DownloadTextIfExistsAsync(string blobName, CancellationToken cancellationToken)
    {
        var blobClient = _containerClient.GetBlobClient(blobName);
        if (!await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var download = await blobClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return download.Value.Content.ToString();
    }

    private static string GetBlobPath(string siteId, string fileName) => $"config-snapshots/{siteId}/{fileName}";
}
