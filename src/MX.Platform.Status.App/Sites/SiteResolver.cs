using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.App.Sites;

public sealed class SiteResolver
{
    private readonly SiteConfigLoader _siteConfigLoader;

    public SiteResolver(SiteConfigLoader siteConfigLoader)
    {
        _siteConfigLoader = siteConfigLoader;
    }

    public async Task<string?> ResolveSiteIdAsync(string? hostHeader, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return null;
        }

        var normalizedHost = NormalizeHost(hostHeader);
        var configuredSites = await LoadDomainMapAsync(cancellationToken).ConfigureAwait(false);
        return configuredSites.TryGetValue(normalizedHost, out var siteId) ? siteId : null;
    }

    public async Task<IReadOnlyList<string>> GetConfiguredDomainsAsync(CancellationToken cancellationToken = default)
    {
        var domainMap = await LoadDomainMapAsync(cancellationToken).ConfigureAwait(false);
        return domainMap.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<Dictionary<string, string>> LoadDomainMapAsync(CancellationToken cancellationToken)
    {
        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var siteId in SiteConfigLoader.KnownSiteIds)
        {
            try
            {
                var snapshot = await _siteConfigLoader.LoadSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
                foreach (var domain in snapshot.Site.Domains)
                {
                    domains[domain] = snapshot.Site.Id;
                }
            }
            catch
            {
            }
        }

        return domains;
    }

    private static string NormalizeHost(string hostHeader)
    {
        var host = hostHeader.Trim();
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
        {
            host = host.Split(':', 2, StringSplitOptions.TrimEntries)[0];
        }

        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }
}
