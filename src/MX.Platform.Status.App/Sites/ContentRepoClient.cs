using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Octokit;

namespace MX.Platform.Status.App.Sites;

public class ContentRepoClient
{
    private readonly SecretClient _secretClient;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CachedContent> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private GitHubClient? _gitHubClient;
    private string? _token;

    public ContentRepoClient(SecretClient secretClient, HttpClient httpClient)
    {
        _secretClient = secretClient;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MX.Platform.Status.App", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
    }

    public virtual async Task<string> GetTextFileAsync(string repo, string branch, string path, CancellationToken cancellationToken = default)
    {
        var token = await GetGitHubTokenAsync(cancellationToken).ConfigureAwait(false);
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
        if (_gitHubClient is not null)
        {
            return _gitHubClient;
        }

        var token = await GetGitHubTokenAsync(cancellationToken).ConfigureAwait(false);
        _gitHubClient = new GitHubClient(new Octokit.ProductHeaderValue("MX.Platform.Status.App"))
        {
            Credentials = new Credentials(token)
        };
        return _gitHubClient;
    }

    private async Task<string> GetGitHubTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token))
            {
                return _token;
            }

            var secretUriRaw = Environment.GetEnvironmentVariable("GITHUB_PAT_SECRET_URI");
            if (!string.IsNullOrWhiteSpace(secretUriRaw))
            {
                var secretUri = new Uri(secretUriRaw);
                var secretName = secretUri.Segments.Last().Trim('/');
                Response<KeyVaultSecret> secret = await _secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken).ConfigureAwait(false);
                _token = secret.Value.Value;
            }
            else
            {
                _token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            }

            return _token ?? throw new InvalidOperationException("No GitHub token is configured.");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static Uri BuildContentsUri(string repo, string branch, string path)
    {
        var escapedPath = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri($"https://api.github.com/repos/{repo}/contents/{escapedPath}?ref={Uri.EscapeDataString(branch)}");
    }

    private sealed record CachedContent(string Content, string? ETag, DateTimeOffset RetrievedAtUtc);
}
