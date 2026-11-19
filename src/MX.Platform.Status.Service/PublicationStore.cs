using System.Text.Json;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Merging;

namespace MX.Platform.Status.Service;

public sealed class PublicationStore(StatusCatalog catalog, RuntimeOptions options)
{
    private readonly string _directory = Path.GetFullPath(options.DataDirectory);

    public async Task<StatusApiResponse> ReadAsync(string site, CancellationToken cancellationToken = default)
    {
        var definition = catalog.Get(site);
        var path = Path.Combine(_directory, $"{site}.json");
        if (!File.Exists(path))
            return new StatusMerger().Merge(definition.Snapshot, new Dictionary<string, App.Telemetry.ComponentLiveTelemetry>(),
                null, [], [], stale: true) with
            { GeneratedAt = DateTimeOffset.UnixEpoch };
        var publication = await ReadFileAsync(site, path, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        publication = publication with
        {
            Banners = publication.Banners.Where(banner =>
                (!banner.StartsAt.HasValue || banner.StartsAt <= now)
                && (!banner.ExpiresAt.HasValue || banner.ExpiresAt > now)).ToArray()
        };
        if (!IsStale(publication, now)) return publication;
        return publication with
        {
            DataFreshness = (publication.DataFreshness ?? new()) with { Stale = true },
            Components = publication.Components.Select(MarkStale).ToArray(),
            Summary = new Summary { Status = "unknown", Message = "Live status information is stale. Incident updates are shown below." }
        };
    }

    public async Task<string?> GetNotReadyReasonAsync(ILogger<PublicationStore> logger, CancellationToken ct)
    {
        var stale = false;
        foreach (var site in catalog.Sites)
        {
            var id = site.Site.Id;
            StatusApiResponse publication;
            try
            {
                publication = await ReadFileAsync(id, Path.Combine(_directory, $"{id}.json"), ct);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return "publication-unavailable";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                logger.LogWarning(exception, "Cannot read local status publication for {Site}.", id);
                return "publication-unavailable";
            }
            var now = DateTimeOffset.UtcNow;
            if (publication.GeneratedAt <= DateTimeOffset.UnixEpoch
                || publication.GeneratedAt - now > TimeSpan.FromSeconds(options.RefreshSeconds))
                return "publication-unavailable";
            stale |= IsStale(publication, now);
        }
        return stale ? "stale-publication" : null;
    }

    private bool IsStale(StatusApiResponse publication, DateTimeOffset now) =>
        now - publication.GeneratedAt > TimeSpan.FromSeconds(options.StaleAfterSeconds);

    private static async Task<StatusApiResponse> ReadFileAsync(string site, string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var publication = await JsonSerializer.DeserializeAsync<StatusApiResponse>(stream, StatusJson.Options, ct);
        if (publication is null || publication.Site?.Id != site || publication.Summary is null
            || publication.Components is null || publication.Incidents is null
            || publication.Maintenance is null || publication.Banners is null || publication.Banners.Any(banner => banner is null))
            throw new InvalidDataException("Invalid status publication or publication site does not match its catalogue.");
        return publication;
    }

    public async Task WriteAsync(StatusApiResponse publication, CancellationToken cancellationToken = default)
    {
        catalog.Get(publication.Site.Id);
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, $"{publication.Site.Id}.json");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, publication, StatusJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ComponentDto MarkStale(ComponentDto component) => component with
    {
        Status = component.Status == "operational" ? "unknown" : component.Status,
        Checks = component.Checks?.Select(check => check with { Status = check.Status == "operational" ? "unknown" : check.Status }).ToArray(),
        Children = component.Children?.Select(MarkStale).ToArray()
    };
}
