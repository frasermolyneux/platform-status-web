using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using MX.Platform.Status.App.Caching;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Sites;
using System.Security.Cryptography;
using System.Net;
using System.Text;

namespace MX.Platform.Status.App.Functions;

public sealed class InvalidateCacheFunction
{
    private readonly SecretClient _secretClient;
    private readonly SiteResolver _siteResolver;
    private readonly InMemoryCache<StatusApiResponse> _cache;

    public InvalidateCacheFunction(SecretClient secretClient, SiteResolver siteResolver, InMemoryCache<StatusApiResponse> cache)
    {
        _secretClient = secretClient;
        _siteResolver = siteResolver;
        _cache = cache;
    }

    [Function("InvalidateCache")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/invalidate")] HttpRequestData req)
    {
        var expectedSecret = await GetExpectedSecretAsync().ConfigureAwait(false);
        if (!req.Headers.TryGetValues("X-Status-Webhook-Secret", out var providedHeader) || !SecretsMatch(providedHeader.FirstOrDefault(), expectedSecret))
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var site = GetSiteQueryParameter(req.Url) ?? await ResolveSiteFromHostAsync(req).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(site))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Specify a site query parameter or configured Host header.").ConfigureAwait(false);
            return badRequest;
        }

        _cache.Remove(site);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Invalidated cache for site '{site}'.").ConfigureAwait(false);
        return response;
    }

    private async Task<string> GetExpectedSecretAsync()
    {
        var secretUri = new Uri(Environment.GetEnvironmentVariable("WEBHOOK_SECRET_URI") ?? throw new InvalidOperationException("WEBHOOK_SECRET_URI is not configured."));
        var secretName = secretUri.Segments[^1].Trim('/');
        Response<KeyVaultSecret> secret = await _secretClient.GetSecretAsync(secretName).ConfigureAwait(false);
        return secret.Value.Value;
    }

    private async Task<string?> ResolveSiteFromHostAsync(HttpRequestData req)
    {
        return req.Headers.TryGetValues("Host", out var values)
            ? await _siteResolver.ResolveSiteIdAsync(values.FirstOrDefault()).ConfigureAwait(false)
            : null;
    }

    private static string? GetSiteQueryParameter(Uri url)
    {
        if (string.IsNullOrWhiteSpace(url.Query))
        {
            return null;
        }

        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("site", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static bool SecretsMatch(string? providedSecret, string expectedSecret)
    {
        var provided = Encoding.UTF8.GetBytes(providedSecret ?? string.Empty);
        var expected = Encoding.UTF8.GetBytes(expectedSecret);

        if (provided.Length != expected.Length)
        {
            // Compare against expected to consume constant time even on length mismatch
            CryptographicOperations.FixedTimeEquals(expected, expected);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
