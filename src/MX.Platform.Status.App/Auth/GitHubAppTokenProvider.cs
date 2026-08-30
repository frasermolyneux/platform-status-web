using System.Globalization;
using Azure.Security.KeyVault.Secrets;
using GitHubJwt;
using Octokit;

namespace MX.Platform.Status.App.Auth;

public sealed class GitHubAppTokenProvider : IGitHubAppTokenProvider
{
    private readonly SecretClient _secretClient;
    private readonly int _appId;
    private readonly long _installationId;
    private readonly string _pemSecretName;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public GitHubAppTokenProvider(SecretClient secretClient, string appId, string installationId, string pemSecretName)
    {
        _secretClient = secretClient;
        if (!int.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out _appId))
        {
            throw new InvalidOperationException("Configuration setting 'GitHubApp__AppId' must be a valid integer.");
        }

        if (!long.TryParse(installationId, NumberStyles.None, CultureInfo.InvariantCulture, out _installationId))
        {
            throw new InvalidOperationException("Configuration setting 'GitHubApp__InstallationId' must be a valid integer.");
        }

        if (string.IsNullOrWhiteSpace(pemSecretName))
        {
            throw new InvalidOperationException("Configuration setting 'GitHubApp__PemSecretName' must not be empty.");
        }

        _pemSecretName = pemSecretName;
    }

    public async Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return _cachedToken;
            }

            var pemSecret = await _secretClient.GetSecretAsync(_pemSecretName, cancellationToken: cancellationToken).ConfigureAwait(false);
            var pem = pemSecret.Value.Value;

            var generator = new GitHubJwtFactory(
                new StringPrivateKeySource(pem),
                new GitHubJwtFactoryOptions
                {
                    AppIntegrationId = _appId,
                    ExpirationSeconds = 540
                });
            var jwt = generator.CreateEncodedJwtToken();

            var appClient = new GitHubClient(new ProductHeaderValue("MX.Platform.Status.App"))
            {
                Credentials = new Credentials(jwt, AuthenticationType.Bearer)
            };
            var installationToken = await appClient.GitHubApps.CreateInstallationToken(_installationId).ConfigureAwait(false);

            _cachedToken = installationToken.Token;
            _tokenExpiry = installationToken.ExpiresAt;
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
