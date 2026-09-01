using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MX.Platform.Status.App;
using MX.Platform.Status.App.Auth;
using MX.Platform.Status.App.Caching;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Functions;
using MX.Platform.Status.App.History;
using MX.Platform.Status.App.Incidents;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Rollup;
using MX.Platform.Status.App.Sites;
using MX.Platform.Status.App.Telemetry;
using MX.Platform.Status.App.Yaml;
using System.Text.Json;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace MX.Platform.Status.Tests;

/// <summary>
/// Exercises the startup dependency-injection graph configured in <see cref="Program"/>
/// (accessible via <c>InternalsVisibleTo</c>) so a runtime/package incompatibility that only
/// surfaces during host construction (for example an external client whose constructor
/// signature changed, or a service depending on a type that stopped being registered) fails a
/// fast unit test instead of only being caught during a deployed Functions host startup.
/// </summary>
public sealed class ProgramServiceRegistrationTests
{
    [Fact]
    public void ConfigureServices_ResolvesEveryRegisteredService()
    {
        using var scope = new EnvironmentVariableScope(
            ("STORAGE_ACCOUNT_NAME", "teststorageaccount"),
            ("WEBHOOK_SECRET_URI", "https://test-vault.vault.azure.net/secrets/webhook-secret"),
            ("GitHubApp__AppId", "12345"),
            ("GitHubApp__InstallationId", "67890"),
            ("GitHubApp__PemSecretName", "github-app-pem"),
            ("LIVE_WINDOW_MINUTES", "15"),
            ("LIVE_CACHE_TTL_SECONDS", "30"));

        var services = new ServiceCollection();
        services.AddLogging();
        Program.ConfigureServices(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        Assert.IsType<JsonSerializerOptions>(provider.GetRequiredService<JsonSerializerOptions>());
        Assert.IsType<DefaultAzureCredential>(provider.GetRequiredService<DefaultAzureCredential>());
        Assert.IsType<BlobServiceClient>(provider.GetRequiredService<BlobServiceClient>());
        Assert.IsType<SecretClient>(provider.GetRequiredService<SecretClient>());
        Assert.IsAssignableFrom<IGitHubAppTokenProvider>(provider.GetRequiredService<IGitHubAppTokenProvider>());
        Assert.IsType<LogsQueryClient>(provider.GetRequiredService<LogsQueryClient>());
        Assert.IsType<HttpClient>(provider.GetRequiredService<HttpClient>());

        Assert.IsType<YamlParser>(provider.GetRequiredService<YamlParser>());
        Assert.IsType<SiteResolver>(provider.GetRequiredService<SiteResolver>());
        Assert.IsType<SiteConfigLoader>(provider.GetRequiredService<SiteConfigLoader>());
        Assert.IsType<SiteConfigSnapshotStore>(provider.GetRequiredService<SiteConfigSnapshotStore>());
        Assert.IsType<ContentRepoClient>(provider.GetRequiredService<ContentRepoClient>());
        Assert.IsType<AvailabilityClient>(provider.GetRequiredService<AvailabilityClient>());
        Assert.Equal(15, provider.GetRequiredService<LiveWindowOptions>().Minutes);
        Assert.IsType<IncidentFetcher>(provider.GetRequiredService<IncidentFetcher>());
        Assert.IsType<MaintenanceFetcher>(provider.GetRequiredService<MaintenanceFetcher>());
        Assert.IsType<HistoryReader>(provider.GetRequiredService<HistoryReader>());
        Assert.IsType<StatusDependencies>(provider.GetRequiredService<StatusDependencies>());
        Assert.IsType<InMemoryCache<StatusApiResponse>>(provider.GetRequiredService<InMemoryCache<StatusApiResponse>>());
        Assert.IsType<StaleCacheBlob>(provider.GetRequiredService<StaleCacheBlob>());
        Assert.IsType<StatusMerger>(provider.GetRequiredService<StatusMerger>());
        Assert.IsType<DailyRollupService>(provider.GetRequiredService<DailyRollupService>());
        Assert.IsType<YearlyAggregator>(provider.GetRequiredService<YearlyAggregator>());
        Assert.IsType<BackfillService>(provider.GetRequiredService<BackfillService>());
        Assert.IsType<OverrideApplier>(provider.GetRequiredService<OverrideApplier>());

        // HttpClient must be singleton so the Functions host reuses one instance per invocation
        // batch instead of re-creating outbound handlers per request.
        Assert.Same(provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<HttpClient>());
    }

    [Fact]
    public void ConfigureServices_ThrowsWhenRequiredEnvironmentVariableIsMissing()
    {
        using var scope = new EnvironmentVariableScope(("STORAGE_ACCOUNT_NAME", null));

        var services = new ServiceCollection();
        services.AddLogging();
        Program.ConfigureServices(services);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<BlobServiceClient>());
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _previousValues;

        public EnvironmentVariableScope(params (string Name, string? Value)[] values)
        {
            _previousValues = new (string, string?)[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                _previousValues[i] = (values[i].Name, Environment.GetEnvironmentVariable(values[i].Name));
                Environment.SetEnvironmentVariable(values[i].Name, values[i].Value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
