using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

namespace MX.Platform.Status.App;

internal static class Program
{
    public static async Task Main()
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults(worker =>
            {
                worker.Serializer = new JsonObjectSerializer(StatusJson.Options);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(StatusJson.Options);
                services.AddSingleton<DefaultAzureCredential>();
                services.AddSingleton(sp =>
                {
                    var storageAccountName = GetRequiredEnvironmentVariable("STORAGE_ACCOUNT_NAME");
                    var credential = sp.GetRequiredService<DefaultAzureCredential>();
                    return new BlobServiceClient(new Uri($"https://{storageAccountName}.blob.core.windows.net"), credential);
                });
                services.AddSingleton(sp =>
                {
                    var secretUri = GetRequiredEnvironmentVariable("GITHUB_PAT_SECRET_URI");
                    return new SecretClient(GetVaultUri(secretUri), sp.GetRequiredService<DefaultAzureCredential>());
                });
                services.AddSingleton(sp => new LogsQueryClient(sp.GetRequiredService<DefaultAzureCredential>()));
                services.AddSingleton(new HttpClient());

                services.AddSingleton<YamlParser>();
                services.AddSingleton<SiteResolver>();
                services.AddSingleton<SiteConfigLoader>();
                services.AddSingleton<SiteConfigSnapshotStore>();
                services.AddSingleton<ContentRepoClient>();
                services.AddSingleton<AvailabilityQueryBuilder>();
                services.AddSingleton<AvailabilityClient>();
                services.AddSingleton<ComponentStatusCalculator>();
                services.AddSingleton<IncidentFetcher>();
                services.AddSingleton<MaintenanceFetcher>();
                services.AddSingleton<HistoryReader>();
                services.AddSingleton<StatusDependencies>();
                services.AddSingleton(sp => new InMemoryCache<StatusApiResponse>(TimeSpan.FromSeconds(GetIntEnvironmentVariable("LIVE_CACHE_TTL_SECONDS", 30))));
                services.AddSingleton<StaleCacheBlob>();
                services.AddSingleton<StatusMerger>();
                services.AddSingleton<DailyRollupService>();
                services.AddSingleton<YearlyAggregator>();
                services.AddSingleton<BackfillService>();
                services.AddSingleton<OverrideApplier>();
            })
            .Build();

        await host.RunAsync().ConfigureAwait(false);
    }

    private static Uri GetVaultUri(string secretUri)
    {
        var uri = new Uri(secretUri);
        return new Uri($"{uri.Scheme}://{uri.Host}");
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required environment variable '{name}'.");

    private static int GetIntEnvironmentVariable(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }
}
