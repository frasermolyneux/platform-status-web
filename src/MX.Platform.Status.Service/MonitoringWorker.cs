using Azure.Identity;
using Azure.Monitor.Query;
using System.Security.Cryptography.X509Certificates;

namespace MX.Platform.Status.Service;

public sealed class MonitoringWorker(
    StatusCatalog catalog, RuntimeOptions options, PostgresStore database, ILogger<MonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Publisher || !catalog.Sites.Any(site => site.Checks.Any(check => check.Monitoring is not null || check.Process is not null))) return;
        if (options.AzureCertificatePath is null)
        {
            logger.LogError("Monitoring collection has no reader certificate; cached observations will become stale. No workload probing fallback is available.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                if (await database.IsWritableAsync(timeout.Token)) await RefreshAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Monitoring input refresh failed; retaining cached observations without advancing their timestamps.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<int> RefreshAsync(CancellationToken ct, bool requireCompleteRegionalCoverage = false)
    {
        if (options.AzureCertificatePath is null)
            throw new InvalidOperationException("Monitoring input requires a dedicated certificate-authenticated reader.");
        await database.InitializeAsync(ct);
        using var certificate = LoadReaderCertificate(options.AzureCertificatePath);
        var credential = new ClientCertificateCredential(options.TenantId, options.AzureCollectorClientId,
            certificate);
        var client = new LogsQueryClient(credential);
        var references = catalog.Sites.SelectMany(site => site.Checks).Select(check => check.Monitoring)
            .OfType<MonitoringReference>().DistinctBy(reference => reference.Identity).ToArray();
        var processReferences = catalog.Sites.SelectMany(site => site.Checks).Select(check => check.Process)
            .OfType<ProcessReference>().DistinctBy(reference => reference.Identity).ToArray();
        var requiredAliases = references.SelectMany(reference => reference.Resources)
            .Concat(processReferences.Select(reference => reference.Resource)).ToHashSet();
        if (requiredAliases.Any(alias => !options.AzureMonitoringResources.ContainsKey(alias)))
            throw new InvalidOperationException("Monitoring resource bindings are incomplete.");
        var resources = options.AzureMonitoringResources.Where(pair => requiredAliases.Contains(pair.Key))
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        var availabilityTask = QueryResourceBatchesAsync<MonitoringObservation>(
            resources.Where(resource => resource.Any(pair => references.Any(reference => reference.Resources.Contains(pair.Key)))),
            "Availability", (resource, aliases, token) => MonitoringAvailability.QueryAsync(client, resource,
                references.Where(reference => reference.Resources.Any(aliases.Contains)), token), ct);
        var processTask = QueryResourceBatchesAsync<ProcessObservation>(
            resources.Where(resource => resource.Any(pair => processReferences.Any(reference => reference.Resource == pair.Key))),
            "Process", (resource, aliases, token) => ProcessMonitoring.QueryAsync(client, resource,
                processReferences.Where(reference => aliases.Contains(reference.Resource)).ToArray(), token), ct);
        var batches = await availabilityTask;
        var processBatches = await processTask;
        var unavailable = batches.Where(batch => !batch.Succeeded).SelectMany(batch => batch.Aliases).ToHashSet();
        var observations = batches.SelectMany(batch => batch.Rows).ToArray();
        var processObservations = processBatches.SelectMany(batch => batch.Rows).ToArray();
        var cached = new Dictionary<string, CheckResult>();
        foreach (var siteId in catalog.Sites.Select(site => site.Site.Id))
            foreach (var result in await database.ReadChecksAsync(siteId, ct))
                cached[$"{siteId}/{result.CheckId}"] = result;
        var now = DateTimeOffset.UtcNow;
        var results = catalog.Sites.SelectMany(site => site.Checks.Where(check => check.Monitoring is not null || check.Process is not null)
            .Select(check => check.Process is not null
                ? ProcessMonitoring.Project(site.Site.Id, check, processObservations, now,
                    cached.GetValueOrDefault($"{site.Site.Id}/{check.Id}"))
                : MonitoringAvailability.Project(site.Site.Id, check,
                    observations, now, cached.GetValueOrDefault($"{site.Site.Id}/{check.Id}"))))
            .OfType<CheckResult>().ToArray();
        await database.SaveMonitoringChecksAsync(results, ct);
        if (requireCompleteRegionalCoverage)
        {
            ValidateCoverage(unavailable, observations, now);
            if (processBatches.Any(batch => !batch.Succeeded))
                throw new InvalidOperationException("Process preflight could not query every required source.");
            foreach (var source in processReferences.Where(source => !processObservations.Any(observation =>
                observation.Source.Identity == source.Identity
                && now - observation.ObservedAt <= TimeSpan.FromSeconds(ProcessReference.FreshnessSeconds))))
                throw new InvalidOperationException($"Fresh process monitoring coverage is incomplete for {source.CheckId}.");
        }
        return results.Length;
    }

    internal static X509Certificate2 LoadReaderCertificate(string path) => X509Certificate2.CreateFromPemFile(path);

    private Task<(HashSet<string> Aliases, bool Succeeded, IReadOnlyList<T> Rows)[]> QueryResourceBatchesAsync<T>(
        IEnumerable<IGrouping<string, KeyValuePair<string, string>>> resources, string kind,
        Func<string, HashSet<string>, CancellationToken, Task<IReadOnlyList<T>>> query, CancellationToken ct) =>
        Task.WhenAll(resources.Select(async resource =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var aliases = resource.Select(pair => pair.Key).ToHashSet();
            try
            {
                var rows = await query(resource.Key, aliases, timeout.Token);
                return (Aliases: aliases, Succeeded: true, Rows: rows);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "{Kind} monitoring query failed for aliases {Aliases}; retaining original assessment timestamps.",
                    kind, string.Join(", ", aliases));
                return (Aliases: aliases, Succeeded: false, Rows: (IReadOnlyList<T>)[]);
            }
        }));

    private void ValidateCoverage(IReadOnlyCollection<string> unavailable,
        IReadOnlyList<MonitoringObservation> observations, DateTimeOffset now)
    {
        if (unavailable.Count > 0)
            throw new InvalidOperationException("Monitoring preflight could not query every required Application Insights resource.");
        foreach (var site in catalog.Sites)
            foreach (var check in site.Checks.Where(check => check.Monitoring is not null))
            {
                var source = check.Monitoring!;
                var result = MonitoringAvailability.Project(site.Site.Id, check, observations, now);
                if (result is null || source.Regions.Any(region => !result.Regions.Any(observation =>
                    observation.Region.Equals(region, StringComparison.OrdinalIgnoreCase)
                    && observation.LastSeen is { } at && now - at <= TimeSpan.FromSeconds(source.FreshnessSeconds))))
                    throw new InvalidOperationException($"Fresh regional monitoring coverage is incomplete for {site.Site.Id}/{check.Id}.");
            }
    }
}
