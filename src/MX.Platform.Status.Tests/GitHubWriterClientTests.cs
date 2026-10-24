using System.Security.Cryptography;
using System.Text.Json;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class GitHubWriterClientTests
{
    [Fact]
    public async Task OversizedGitHubResponsesStopReadingAtTheBoundInsteadOfBufferingTheWholeResponse()
    {
        using var stream = new CountingStream(new byte[4_200_000]);
        using var api = new GitHubWriteFixture { ResponseStream = stream };
        var github = new GitHubIncidentClient(new(), new(Path.Combine(AppContext.BaseDirectory, "catalog.json")), api);
        await Assert.ThrowsAsync<InvalidDataException>(() => github.SendAsync(HttpMethod.Get, "labels", null, default));
        Assert.InRange(stream.ReadBytes, 4_000_001, 4_016_384);
    }

    [Fact]
    public async Task AppAuthenticationRequestsOnlyTheConfiguredRepositoryAndIssueWritesAndCachesItsShortLivedToken()
    {
        var directory = Directory.CreateTempSubdirectory("status-app-auth-").FullName;
        try
        {
            using var key = RSA.Create(2048);
            var filename = Path.Combine(directory, "fixture.pem");
            await File.WriteAllTextAsync(filename, key.ExportPkcs8PrivateKeyPem());
            var options = new RuntimeOptions
            {
                GitHubPrivateKeyFile = filename, GitHubAppId = 42, GitHubInstallationId = 100,
                GitHubRepository = "status-writer-fixture/status-pages"
            };
            using var api = new GitHubWriteFixture();
            var github = new GitHubIncidentClient(options, new(Path.Combine(AppContext.BaseDirectory, "catalog.json")), api);
            using var first = await github.RequestAsync(HttpMethod.Get, "issues", null, default);
            using var second = await github.RequestAsync(HttpMethod.Get, "labels", null, default);
            Assert.Equal(1, api.TokenRequests);
            Assert.Equal("fixture-installation-token", first.Headers.Authorization?.Parameter);
            Assert.Equal(first.Headers.Authorization, second.Headers.Authorization);
            using var scope = JsonDocument.Parse(api.TokenScope!.ToJsonString());
            Assert.Equal(["status-pages"], scope.RootElement.GetProperty("repositories").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["issues"], scope.RootElement.GetProperty("permissions").EnumerateObject().Select(item => item.Name));
            Assert.Equal("write", scope.RootElement.GetProperty("permissions").GetProperty("issues").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadBytes { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += count;
            return count;
        }
    }
}
