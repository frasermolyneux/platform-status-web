namespace MX.Platform.Status.App.Telemetry;

/// <summary>
/// Enforces tenant isolation for Application Insights queries at the platform level rather than
/// relying on content authors to remember to filter by site. Every live/rollup query issued by this
/// app must pass its filters through <see cref="WithSiteId"/> so that a component's configured
/// <c>customDimensions.*</c> filter can never be widened - accidentally or otherwise - into another
/// tenant's telemetry. The producer (platform-sitewatch-func) always emits an explicit
/// <c>customDimensions.siteId</c> dimension per the shared telemetry contract, so this filter is
/// always satisfiable for on-contract data.
/// </summary>
public static class TelemetryFilters
{
    private const string SiteIdDimensionKey = "customDimensions.siteId";

    public static IReadOnlyDictionary<string, object?> WithSiteId(IReadOnlyDictionary<string, object?> filter, string siteId)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (string.IsNullOrWhiteSpace(siteId))
        {
            throw new ArgumentException("Site id cannot be empty.", nameof(siteId));
        }

        var merged = new Dictionary<string, object?>(filter, StringComparer.OrdinalIgnoreCase)
        {
            // Always enforced by the platform, overriding any content-provided value: tenant
            // isolation must not depend on convention or content-author diligence.
            [SiteIdDimensionKey] = siteId
        };

        return merged;
    }
}
