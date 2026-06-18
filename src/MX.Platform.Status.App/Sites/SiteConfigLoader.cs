using System.Collections.Concurrent;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Yaml;

namespace MX.Platform.Status.App.Sites;

public sealed class SiteConfigLoader
{
    public static readonly string[] KnownSiteIds = ["xi", "mx", "dev"];

    private readonly ContentRepoClient _contentRepoClient;
    private readonly SiteConfigSnapshotStore _snapshotStore;
    private readonly YamlParser _yamlParser;
    private readonly TimeSpan _cacheTtl;
    private readonly string _repo;
    private readonly string _branch;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SiteConfigLoader(ContentRepoClient contentRepoClient, SiteConfigSnapshotStore snapshotStore, YamlParser yamlParser)
    {
        _contentRepoClient = contentRepoClient;
        _snapshotStore = snapshotStore;
        _yamlParser = yamlParser;
        _repo = Environment.GetEnvironmentVariable("STATUS_CONTENT_REPO") ?? "frasermolyneux/status-pages";
        _branch = Environment.GetEnvironmentVariable("STATUS_CONTENT_BRANCH") ?? "main";
        _cacheTtl = TimeSpan.FromSeconds(ParseInt("CONTENT_CACHE_TTL_SECONDS", 60));
    }

    public async Task<SiteConfigurationSnapshot> LoadSiteAsync(string siteId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(siteId, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAtUtc < _cacheTtl)
        {
            return cached.Snapshot;
        }

        try
        {
            var siteYaml = await _contentRepoClient.GetTextFileAsync(_repo, _branch, $"sites/{siteId}/site.yaml", cancellationToken).ConfigureAwait(false);
            var componentsYaml = await _contentRepoClient.GetTextFileAsync(_repo, _branch, $"sites/{siteId}/components.yaml", cancellationToken).ConfigureAwait(false);
            var snapshot = BuildSnapshot(siteYaml, componentsYaml);

            await _snapshotStore.SaveAsync(siteId, new SiteConfigSnapshotContent(siteYaml, componentsYaml), cancellationToken).ConfigureAwait(false);
            _cache[siteId] = new CacheEntry(snapshot, DateTimeOffset.UtcNow);
            return snapshot;
        }
        catch
        {
            var fallback = await _snapshotStore.LoadAsync(siteId, cancellationToken).ConfigureAwait(false);
            if (fallback is null)
            {
                throw;
            }

            var snapshot = BuildSnapshot(fallback.SiteYaml, fallback.ComponentsYaml);
            _cache[siteId] = new CacheEntry(snapshot, DateTimeOffset.UtcNow);
            return snapshot;
        }
    }

    private SiteConfigurationSnapshot BuildSnapshot(string siteYaml, string componentsYaml)
    {
        var site = _yamlParser.ParseSite(siteYaml);
        var components = _yamlParser.ParseComponents(componentsYaml);
        return new SiteConfigurationSnapshot(site, components, siteYaml, componentsYaml, DateTimeOffset.UtcNow);
    }

    private static int ParseInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) ? parsed : defaultValue;

    private sealed record CacheEntry(SiteConfigurationSnapshot Snapshot, DateTimeOffset LoadedAtUtc);
}
