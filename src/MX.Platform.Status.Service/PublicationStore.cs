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
                null, [], [], stale: true) with { GeneratedAt = DateTimeOffset.UnixEpoch };
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var publication = await JsonSerializer.DeserializeAsync<StatusApiResponse>(stream, StatusJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Invalid status publication.");
        if (publication.Site.Id != site)
            throw new InvalidDataException("Publication site does not match its catalogue.");
        var stale = DateTimeOffset.UtcNow - publication.GeneratedAt > TimeSpan.FromSeconds(options.StaleAfterSeconds);
        if (!stale) return publication;
        return publication with
        {
            DataFreshness = (publication.DataFreshness ?? new()) with { Stale = true },
            Components = publication.Components.Select(MarkStale).ToArray(),
            Summary = new Summary { Status = "unknown", Message = "Live status information is stale. Incident updates are shown below." }
        };
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
        Children = component.Children?.Select(MarkStale).ToArray()
    };
}
