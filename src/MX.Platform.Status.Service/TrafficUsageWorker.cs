using System.Text.Json;
using Npgsql;

namespace MX.Platform.Status.Service;

public sealed class TrafficUsageWorker(RuntimeOptions options, StatusCatalog catalog, CloudflareTraffic cloudflare,
    PostgresStore database, ILogger<TrafficUsageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Publisher || options.TrafficConfigurationFile is null && options.TrafficTokenFile is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var configuration = TrafficConfiguration.Read(options, catalog)
                    ?? throw new InvalidDataException("Traffic controls are not configured.");
                var quota = await cloudflare.QuotaAsync(configuration.AccountId, stoppingToken);
                var status = quota.Warning ? "warning" : "healthy";
                if (quota.Error is not null) status = "unavailable";
                if (await database.IsWritableAsync(stoppingToken))
                    await database.SaveEvidenceAsync("cloudflare-workers-usage", configuration.AccountId, quota.ObservedAt,
                        JsonSerializer.SerializeToElement(new
                        {
                            status,
                            quota.Requests,
                            quota.FreeDailyAllowance,
                            summary = quota.Error ?? (quota.Warning
                                ? "Workers account usage is approaching or exceeding the shared free daily allowance. Maintenance routes must remain fail closed."
                                : "Workers account usage is below the warning threshold."),
                            coverage = "Estimated account-wide requests since midnight UTC; all Workers share the allowance."
                        }), stoppingToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or NpgsqlException or TimeoutException or InvalidOperationException)
            {
                logger.LogWarning(exception, "Workers quota evidence could not be refreshed; public publications remain independent.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
