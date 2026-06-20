namespace MX.Platform.Status.App.Auth;

public interface IGitHubAppTokenProvider
{
    Task<string> GetInstallationTokenAsync(CancellationToken cancellationToken = default);
}
