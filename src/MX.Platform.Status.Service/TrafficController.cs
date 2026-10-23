namespace MX.Platform.Status.Service;

public sealed class TrafficController(RuntimeOptions options, StatusCatalog catalog, PostgresStore database,
    CloudflareTraffic cloudflare, ILogger<TrafficController> logger)
{
    private const string Normal = "normal";
    private const string Active = "active";
    private const string Uncertain = "uncertain";
    private const string Enabling = "enabling";
    private const string Restoring = "restoring";
    private TrafficConfiguration? Configuration => TrafficConfiguration.Read(options, catalog);

    private string StatusUrl(TrafficHost host) => $"https://{catalog.Get(host.Site).Site.Domains[0]}/";

    public async Task<TrafficWorkspace> ReadAsync(string site, CancellationToken ct)
    {
        var configuration = Configuration;
        if (configuration is null) return new(false, [], null);
        var hosts = configuration.Hosts.Where(host => host.Site == site).ToArray();
        var records = await database.ReadTrafficAsync(site, ct);
        var routes = await Task.WhenAll(hosts.Select(host => host.ZoneId).Distinct()
            .Select(async zone => (Zone: zone, Routes: await cloudflare.RoutesAsync(zone, ct))));
        var rows = await Task.WhenAll(hosts.Select(host => RowAsync(host,
            routes.Single(zone => zone.Zone == host.ZoneId).Routes, records.GetValueOrDefault(host.Hostname), configuration, ct)));
        return new(true, rows, await cloudflare.QuotaAsync(configuration.AccountId, ct));
    }

    internal async Task<TrafficRow> RowAsync(TrafficHost host, TrafficRoute[] routes, TrafficRecord? record,
        TrafficConfiguration configuration, CancellationToken ct)
    {
        var affected = routes.Where(route => CloudflareTraffic.AffectsHost(route, host.Hostname)).ToArray();
        var owned = affected.Where(route => CloudflareTraffic.Owned(route, host, configuration)).ToArray();
        if (affected.Length != owned.Length || owned.Length > 1)
            return new(host.Hostname, "conflict", "conflict", false, null, record?.Revision ?? 0, StatusUrl(host), null,
                "Another Worker route affects this hostname. No foreign route will be changed; review it in Cloudflare.", record);
        var route = owned.SingleOrDefault();
        var mode = route is null ? Normal : configuration.Scripts.Single(script => script.Value == route.Script).Key;
        if (route is not null && route.RequestLimitFailOpen != false)
            return new(host.Hostname, mode, "unsafe", false, route.Id, record?.Revision ?? 0, StatusUrl(host), null,
                "Fail-closed behaviour is not confirmed. Review the route failure mode in Cloudflare before relying on maintenance.", record,
                CloudflareTraffic.Fingerprint(route));
        var (verified, status, error) = await ObserveAsync(host, route, mode, ct);
        var state = route is null ? Normal : Active;
        if (!verified) state = "unverified";
        if (record?.Phase is Enabling or Restoring)
        {
            state = DateTimeOffset.UtcNow - record.UpdatedAt < TimeSpan.FromMinutes(2) ? record.Phase : Uncertain;
            if (state == Uncertain) error ??= "A previous operation did not record completion. Reconcile its original requested outcome before making another change.";
        }
        if (record?.Phase == "failed") error ??= record.Error;
        if (record?.Phase == Uncertain)
        {
            state = Uncertain;
            error = record.Error;
        }
        return new(host.Hostname, mode, state, verified, route?.Id, record?.Revision ?? 0, StatusUrl(host), status, error, record,
            CloudflareTraffic.Fingerprint(route));
    }

    private async Task<(bool Verified, int? Status, string? Error)> ObserveAsync(
        TrafficHost host, TrafficRoute? route, string mode, CancellationToken ct)
    {
        if (route is null) return (true, null, null);
        try
        {
            var probe = await cloudflare.ProbeAsync(host, mode, StatusUrl(host), ct);
            return (probe.Matches, probe.Status, probe.Matches ? null
                : "Cloudflare has the maintenance route, but the expected public response is not yet confirmed.");
        }
        catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Maintenance verification failed for {Hostname}.", host.Hostname);
            return (false, null, "The public response could not be verified. The Cloudflare route remains installed.");
        }
    }

    public async Task<TrafficRow> ChangeAsync(string site, string hostname, TrafficCommand command, string actor, CancellationToken ct)
    {
        if (command.Validate(hostname) is { } validation) throw new TrafficConflictException(validation);
        var configuration = Configuration ?? throw new TrafficConflictException("Traffic controls are not configured.");
        var host = configuration.Hosts.SingleOrDefault(host => host.Hostname == hostname && host.Site == site)
            ?? throw new TrafficConflictException("This hostname is not approved for this status site.");
        await using var lease = await database.TryTrafficLockAsync(hostname, ct)
            ?? throw new TrafficConflictException("A traffic change for this hostname is already in progress. Refresh before retrying.");
        var previous = await lease.ReadAsync(hostname, ct);
        if (command.Revision != (previous?.Revision ?? 0))
            throw new TrafficConflictException("The traffic control changed. Refresh before submitting another operation.");
        if (previous?.Phase is Enabling or Restoring
            && DateTimeOffset.UtcNow - previous.UpdatedAt < TimeSpan.FromMinutes(2))
            throw new TrafficConflictException("A previous traffic operation is still within its bounded reconciliation window. Refresh before retrying.");
        var current = await RequireCurrentRouteAsync(host, configuration, command, ct);
        if (previous?.Phase is Uncertain or Enabling or Restoring)
            return await ReconcileAsync(host, configuration, command, previous, lease, actor, ct);
        var pending = new TrafficRecord(hostname, site, (previous?.Revision ?? 0) + 1, command.Mode,
            command.Mode == Normal ? Restoring : Enabling, actor, command.Reason.Trim(), current?.Id, DateTimeOffset.UtcNow);
        await lease.SaveAsync(pending, ct);
        // After the durable intent, finish bounded reconciliation even if the browser disconnects.
        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (command.Mode != Normal) await cloudflare.RequireProxiedAsync(host, operation.Token);
            await cloudflare.SetAsync(host, current, command.Mode, configuration,
                token => lease.RequireOwnershipAsync(pending, token), operation.Token);
            var result = await VerifyAsync(host, command.Mode, configuration, operation.Token);
            var completed = pending with { Phase = command.Mode == Normal ? Normal : Active, RouteId = result.RouteId, UpdatedAt = DateTimeOffset.UtcNow };
            await lease.RequireOwnershipAsync(pending, operation.Token);
            await lease.SaveAsync(completed, operation.Token);
            return result with { Revision = completed.Revision, LastOperation = completed };
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or InvalidDataException
            or UnauthorizedAccessException or TrafficConflictException or System.Text.Json.JsonException or Npgsql.NpgsqlException)
        {
            logger.LogWarning(exception, "Traffic operation failed for {Hostname}; no automatic reopening is permitted.", hostname);
            await RecordFailureAsync(lease, pending, exception);
            throw new TrafficConflictException("The traffic change could not be confirmed. Refresh to inspect the live route. Maintenance was not automatically removed.");
        }
    }

    private async Task<TrafficRoute?> RequireCurrentRouteAsync(
        TrafficHost host, TrafficConfiguration configuration, TrafficCommand command, CancellationToken ct)
    {
        var routes = await cloudflare.RoutesAsync(host.ZoneId, ct);
        var affected = routes.Where(route => CloudflareTraffic.AffectsHost(route, host.Hostname)).ToArray();
        if (affected.Any(route => !CloudflareTraffic.Owned(route, host, configuration)) || affected.Length > 1)
            throw new TrafficConflictException("Another Worker route affects this hostname. It will not be overwritten.");
        var current = affected.SingleOrDefault();
        if (command.RouteId != current?.Id || command.RouteFingerprint != CloudflareTraffic.Fingerprint(current))
            throw new TrafficConflictException("The live Cloudflare route changed outside this view. Refresh before changing traffic.");
        return current;
    }

    private async Task<TrafficRow> VerifyAsync(TrafficHost host, string mode, TrafficConfiguration configuration, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var updated = await cloudflare.RoutesAsync(host.ZoneId, ct);
            var result = await RowAsync(host, updated, null, configuration, ct);
            if (result.State is "unsafe" or "conflict") throw new InvalidDataException(result.Error);
            if (result.Mode == Normal && mode == Normal)
            {
                var probe = await cloudflare.ProbeAsync(host, Normal, StatusUrl(host), ct);
                result = result with { Verified = probe.Matches, HttpStatus = probe.Status };
            }
            if (result.Mode == mode && result.Verified && result.State is Normal or Active) return result;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new InvalidDataException("The requested edge response is not confirmed. No automatic rollback or reopening was performed; refresh to inspect the live route.");
    }

    private async Task<TrafficRow> ReconcileAsync(TrafficHost host, TrafficConfiguration configuration, TrafficCommand command,
        TrafficRecord previous, PostgresStore.TrafficLease lease, string actor, CancellationToken ct)
    {
        if (command.Mode != previous.Mode)
            throw new TrafficConflictException("An earlier edge mutation has an unknown outcome. Reconcile its original requested mode before making another change.");
        TrafficRow result;
        try { result = await VerifyAsync(host, previous.Mode, configuration, ct); }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException
            || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Uncertain traffic operation remains fenced for {Hostname}; reconciliation sent no new mutation.", host.Hostname);
            throw new TrafficConflictException("The earlier requested outcome is still unconfirmed. The fence remains; no new edge mutation was sent. Inspect Cloudflare before reconciling again.");
        }
        await lease.RequireOwnershipAsync(previous, ct);
        var reconciled = previous with
        {
            Revision = previous.Revision + 1,
            Phase = previous.Mode == Normal ? Normal : Active,
            RouteId = result.RouteId,
            Actor = actor,
            Reason = command.Reason.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        };
        await lease.SaveAsync(reconciled, ct);
        return result with { Revision = reconciled.Revision, LastOperation = reconciled };
    }

    private async Task RecordFailureAsync(PostgresStore.TrafficLease lease, TrafficRecord pending, Exception exception)
    {
        using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await lease.RequireOwnershipAsync(pending, auditTimeout.Token);
            await lease.SaveAsync(pending with
            {
                Phase = exception is TrafficUncertainException ? Uncertain : "failed",
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = exception is TrafficConflictException or InvalidDataException or TrafficUncertainException ? exception.Message
                    : "The traffic change could not be confirmed. Refresh to inspect the live Cloudflare route; no automatic reopening was performed."
            }, auditTimeout.Token);
        }
        catch (Exception auditFailure) when (auditFailure is Npgsql.NpgsqlException or TrafficConflictException
            or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning(auditFailure, "Traffic failure completion could not be recorded for {Hostname}; the durable intent remains unconfirmed and no newer operation is overwritten.", pending.Hostname);
        }
    }
}
