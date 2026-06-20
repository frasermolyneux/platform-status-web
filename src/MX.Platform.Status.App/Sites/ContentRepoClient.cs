using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using MX.Platform.Status.App.Auth;
using Octokit;

namespace MX.Platform.Status.App.Sites;

public class ContentRepoClient
{
    private readonly IGitHubAppTokenProvider _tokenProvider;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CachedContent> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ContentRepoClient(IGitHubAppTokenProvider tokenProvider, HttpClient httpClient)
    {
        _tokenProvider = tokenProvider;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MX.Platform.Status.App", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
    }

    public virtual async Task<string> GetTextFileAsync(string repo, string branch, string path, CancellationToken cancellationToken = default)
    {
        var token = await _tokenProvider.GetInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = $"{repo}@{branch}:{path}";
        _cache.TryGetValue(cacheKey, out var cached);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildContentsUri(repo, branch, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(cached?.ETag))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            _cache[cacheKey] = cached with { RetrievedAtUtc = DateTimeOffset.UtcNow };
            return cached.Content;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"GitHub content fetch failed for '{path}' with {(int)response.StatusCode}: {body}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var etag = response.Headers.ETag?.Tag;
        _cache[cacheKey] = new CachedContent(content, etag, DateTimeOffset.UtcNow);
        return content;
    }

    public virtual async Task<GitHubClient> GetGitHubClientAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenProvider.GetInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
        return new GitHubClient(new Octokit.ProductHeaderValue("MX.Platform.Status.App"))
        {
            Credentials = new Credentials(token, AuthenticationType.Bearer)
        };
    }

    private static Uri BuildContentsUri(string repo, string branch, string path)
    {
        var escapedPath = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri($"https://api.github.com/repos/{repo}/contents/{escapedPath}?ref={Uri.EscapeDataString(branch)}");
    }

    private sealed record CachedContent(string Content, string? ETag, DateTimeOffset RetrievedAtUtc);
}
