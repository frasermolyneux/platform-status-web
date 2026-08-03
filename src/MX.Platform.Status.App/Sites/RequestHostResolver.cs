using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker.Http;

namespace MX.Platform.Status.App.Sites;

/// <summary>
/// Resolves the original public hostname for a request that reaches this Function App through the
/// Azure Static Web Apps "bring your own Function App" (BYOFA) linked-backend proxy.
/// <para>
/// The <c>Host</c> header seen by a Function App behind any Azure-managed reverse proxy (Static Web
/// Apps, Front Door, Application Gateway) reflects the internal hop between the proxy and this app,
/// not the custom domain the browser actually requested. The platform edge conveys the originally
/// requested hostname via the <c>X-Forwarded-Host</c> request header, consistent with the standard
/// <c>X-Forwarded-*</c> header family used across Azure's edge/proxy products
/// (see https://learn.microsoft.com/en-us/azure/architecture/best-practices/host-name-preservation
/// and https://learn.microsoft.com/en-us/azure/static-web-apps/functions-bring-your-own for the BYOFA
/// proxy relationship this app relies on). This resolver prefers a validated first-hop value from
/// that header and falls back to <c>Host</c> when it is absent, empty, or implausible.
/// </para>
/// <para>
/// Multi-site routing (see <c>SiteResolver</c>) depends entirely on the resolved hostname, so this
/// class deliberately does NOT trust arbitrary/spoofable header content: only the first header
/// instance and its first comma-separated hop are considered, and the candidate must look like a
/// plausible host[:port] before it is used.
/// </para>
/// </summary>
public static class RequestHostResolver
{
    private const string ForwardedHostHeaderName = "X-Forwarded-Host";
    private const string HostHeaderName = "Host";
    private const int MaxHostLength = 253;

    // Permissive host[:port] shape: DNS labels or bracketed IPv6, optional port. Rejects whitespace,
    // control characters, and other characters that have no place in a hostname, without trying to
    // fully validate DNS syntax (that's SiteResolver/domain-map matching's job).
    private static readonly Regex PlausibleHostPattern = new(
        @"^\[[0-9a-fA-F:]+\](:\d{1,5})?$|^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*\.?(:\d{1,5})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns the resolved host header value to use for site/tenant resolution, or <c>null</c> when
    /// neither a valid forwarded host nor a <c>Host</c> header is present.
    /// </summary>
    public static string? Resolve(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Resolve(name => request.Headers.TryGetValues(name, out var values) ? values : null);
    }

    /// <summary>
    /// Core resolution logic, decoupled from <see cref="HttpRequestData"/> so it can be exercised
    /// directly in tests without a functions-worker request fake. <paramref name="headerValues"/>
    /// should behave like <c>HttpHeadersCollection.TryGetValues</c>: returning all values for the
    /// given header name (in receipt order), or <c>null</c>/empty when the header was not present.
    /// </summary>
    public static string? Resolve(Func<string, IEnumerable<string>?> headerValues)
    {
        ArgumentNullException.ThrowIfNull(headerValues);

        var forwardedHost = TryGetFirstForwardedHop(headerValues);
        if (forwardedHost is not null && IsPlausibleHost(forwardedHost))
        {
            return forwardedHost;
        }

        return headerValues(HostHeaderName)?.FirstOrDefault();
    }

    private static string? TryGetFirstForwardedHop(Func<string, IEnumerable<string>?> headerValues)
    {
        // Only the first header instance is honored. A downstream/misbehaving hop repeating the
        // header would otherwise let an attacker append their own value; Azure's edge sets this
        // header exactly once for a BYOFA-linked request.
        var first = headerValues(ForwardedHostHeaderName)?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        // A forwarded-host chain lists the original client-requested host first, followed by any
        // intermediate hop values appended by further proxies. Only the leftmost hop reflects what
        // the client actually requested; never trust or concatenate the rest.
        var firstHop = first.Split(',')[0].Trim();
        return string.IsNullOrWhiteSpace(firstHop) ? null : firstHop;
    }

    private static bool IsPlausibleHost(string candidate) =>
        candidate.Length <= MaxHostLength && PlausibleHostPattern.IsMatch(candidate);
}
