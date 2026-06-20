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
}
