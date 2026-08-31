using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using MX.Platform.Status.App.Auth;
using NSubstitute;

namespace MX.Platform.Status.Tests.Auth;

public class GitHubAppTokenProviderTests
{
    [Fact]
    public void Constructor_WithValidParameters_DoesNotThrow()
    {
        var credential = Substitute.For<TokenCredential>();
        var secretClient = Substitute.For<SecretClient>(new Uri("https://example.vault.azure.net/"), credential);
        var provider = new GitHubAppTokenProvider(secretClient, "12345", "67890", "github-app-pem");
        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData("invalid", "67890", "GitHubApp__AppId")]
    [InlineData("12345", "invalid", "GitHubApp__InstallationId")]
    public void Constructor_WithInvalidId_ThrowsClearError(string appId, string installationId, string settingName)
    {
        var credential = Substitute.For<TokenCredential>();
        var secretClient = Substitute.For<SecretClient>(new Uri("https://example.vault.azure.net/"), credential);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new GitHubAppTokenProvider(secretClient, appId, installationId, "github-app-pem"));

        Assert.Contains(settingName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidPemSecretName_ThrowsClearError(string pemSecretName)
    {
        var credential = Substitute.For<TokenCredential>();
        var secretClient = Substitute.For<SecretClient>(new Uri("https://example.vault.azure.net/"), credential);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new GitHubAppTokenProvider(secretClient, "12345", "67890", pemSecretName));

        Assert.Contains("GitHubApp__PemSecretName", exception.Message, StringComparison.Ordinal);
    }
}
