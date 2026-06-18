using Azure.Storage.Blobs;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Sites;
using MX.Platform.Status.App.Yaml;

namespace MX.Platform.Status.Tests;

public sealed class SiteResolverTests
{
    private static readonly string SiteYaml = """
version: 1
id: mx
displayName: MX
domains:
  - status.example.com
appInsights: {}
""";

    private static readonly string ComponentsYaml = """
version: 1
components:
  - id: mx.api
    name: API
    source:
      kind: static
      status: operational
""";

    [Fact]
    public async Task ResolvesKnownDomainToSiteId()
    {
        var sut = CreateSubject();
        Assert.Equal("mx", await sut.ResolveSiteIdAsync("status.example.com"));
    }

    [Fact]
    public async Task StripsPortFromHostHeader()
    {
        var sut = CreateSubject();
        Assert.Equal("mx", await sut.ResolveSiteIdAsync("status.example.com:7071"));
    }

    [Fact]
    public async Task ReturnsNullForUnknownDomain()
    {
        var sut = CreateSubject();
        Assert.Null(await sut.ResolveSiteIdAsync("unknown.example.com"));
    }

    [Fact]
    public async Task MatchingIsCaseInsensitive()
    {
        var sut = CreateSubject();
        Assert.Equal("mx", await sut.ResolveSiteIdAsync("STATUS.EXAMPLE.COM"));
    }

    private static SiteResolver CreateSubject()
    {
        var loader = new SiteConfigLoader(new StubContentRepoClient(SiteYaml, ComponentsYaml), new StubSnapshotStore(), new YamlParser());
        return new SiteResolver(loader);
    }

    private sealed class StubContentRepoClient : ContentRepoClient
    {
        private readonly string _siteYaml;
        private readonly string _componentsYaml;

        public StubContentRepoClient(string siteYaml, string componentsYaml)
            : base(null!, new HttpClient())
        {
            _siteYaml = siteYaml;
            _componentsYaml = componentsYaml;
        }

        public override Task<string> GetTextFileAsync(string repo, string branch, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(path.EndsWith("site.yaml", StringComparison.OrdinalIgnoreCase) ? _siteYaml : _componentsYaml);
    }

    private sealed class StubSnapshotStore : SiteConfigSnapshotStore
    {
        public StubSnapshotStore() : base(new BlobServiceClient("UseDevelopmentStorage=true")) { }

        public override Task SaveAsync(string siteId, SiteConfigSnapshotContent content, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task<SiteConfigSnapshotContent?> LoadAsync(string siteId, CancellationToken cancellationToken = default) => Task.FromResult<SiteConfigSnapshotContent?>(null);
    }
}
