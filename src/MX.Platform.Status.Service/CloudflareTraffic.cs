using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MX.Platform.Status.Service;

public sealed record TrafficRoute(string Id, string Pattern, string? Script, bool? RequestLimitFailOpen);

public sealed class CloudflareTraffic(IHttpClientFactory clients, RuntimeOptions options, ILogger<CloudflareTraffic> logger)
{
    private static readonly Uri ApiRoot = new UriBuilder(Uri.UriSchemeHttps, "api.cloudflare.com", -1, "/client/v4/").Uri;

    private async Task<AuthenticationHeaderValue> AuthorizationAsync(CancellationToken ct)
    {
        var token = (await File.ReadAllTextAsync(options.TrafficTokenFile
            ?? throw new InvalidOperationException("The traffic credential is not configured."), ct)).Trim();
        if (token.Length is 0 or > 4096 || !Regex.IsMatch(token, @"\A[A-Za-z0-9._~+/-]+=*\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("The traffic credential does not have valid bearer-token formatting.");
        return new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        Func<CancellationToken, Task>? requireLease = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiRoot, path));
        request.Headers.Authorization = await AuthorizationAsync(ct);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (requireLease is not null) await requireLease(ct);
        var refused = false;
        try
        {
            using var response = await clients.CreateClient("cloudflare-traffic").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                refused = (int)response.StatusCode < 500;
                logger.LogWarning("Cloudflare traffic API refused {Method} with HTTP {Status}.", method, (int)response.StatusCode);
                throw new HttpRequestException($"Cloudflare refused the traffic operation (HTTP {(int)response.StatusCode}). Check the scoped token and retry after refreshing.");
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("result", out var result))
                throw new InvalidDataException("Cloudflare did not confirm the traffic operation.");
            return result.Clone();
        }
        catch (Exception exception) when (requireLease is not null && !refused
            && exception is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException)
        {
            throw new TrafficUncertainException("Cloudflare may still complete the dispatched mutation. Reconcile its original requested outcome before any further traffic change.", exception);
        }
    }

    public async Task<TrafficRoute[]> RoutesAsync(string zoneId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, $"zones/{zoneId}/workers/routes", null, ct);
        if (result.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Cloudflare returned an invalid route list.");
        return result.EnumerateArray().Select(route => new TrafficRoute(
            RequiredString(route, "id"), RequiredString(route, "pattern"), OptionalString(route, "script"),
            route.TryGetProperty("request_limit_fail_open", out var failure) && failure.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? failure.GetBoolean() : null)).ToArray();
    }

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) is { Length: > 0 } text ? text
            : throw new InvalidDataException($"Cloudflare omitted the required {name} value.");

    private static string? OptionalString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cloudflare returned an invalid object.");
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Cloudflare returned an invalid {name} value.");
        return property.GetString();
    }

    internal static bool AffectsHost(TrafficRoute route, string hostname)
    {
        var pattern = route.Pattern;
        var scheme = pattern.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) pattern = pattern[(scheme + 3)..];
        var hostPattern = pattern.Split('/')[0];
        return Regex.IsMatch(hostname, "\\A" + Regex.Escape(hostPattern).Replace("\\*", ".*", StringComparison.Ordinal) + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    internal static bool Owned(TrafficRoute route, TrafficHost host, TrafficConfiguration configuration) =>
        route.Pattern == host.Hostname + "/*" && route.Script is not null && configuration.Scripts.Values.Contains(route.Script);

    internal static string? Fingerprint(TrafficRoute? route) =>
        route is null ? null : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(route)));

    public async Task RequireProxiedAsync(TrafficHost host, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, $"zones/{host.ZoneId}/dns_records?name={Uri.EscapeDataString(host.Hostname)}&per_page=100", null, ct);
        if (result.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Cloudflare returned an invalid DNS record list.");
        if (!result.EnumerateArray().Any(record =>
            RequiredString(record, "name") == host.Hostname
            && RequiredString(record, "type") is "A" or "AAAA" or "CNAME"
            && record.TryGetProperty("proxied", out var proxied) && proxied.ValueKind == JsonValueKind.True))
            throw new TrafficConflictException("This exact hostname has no proxied web DNS record. DNS is not changed by the maintenance switch.");
    }

    public async Task SetAsync(TrafficHost host, TrafficRoute? previous, string mode,
        TrafficConfiguration configuration, Func<CancellationToken, Task> requireLease, CancellationToken ct)
    {
        var path = $"zones/{host.ZoneId}/workers/routes";
        var affected = (await RoutesAsync(host.ZoneId, ct)).Where(route => AffectsHost(route, host.Hostname)).ToArray();
        if (affected.Length != (previous is null ? 0 : 1)
            || previous is not null && Fingerprint(affected.SingleOrDefault()) != Fingerprint(previous))
            throw new TrafficConflictException("The live routes changed during this operation. Refresh before changing traffic.");
        if (mode == "normal")
        {
            if (previous is not null)
                await SendAsync(HttpMethod.Delete, $"{path}/{previous.Id}", null, ct, requireLease);
            return;
        }
        var pattern = host.Hostname + "/*";
        var script = configuration.Scripts[mode];
        if (MatchesTarget(previous, pattern, script)) return;
        var payload = new { pattern, script, request_limit_fail_open = false };
        if (previous is null)
        {
            var created = await SendAsync(HttpMethod.Post, path, payload, ct, requireLease);
            var id = RequiredString(created, "id");
            var installed = (await RoutesAsync(host.ZoneId, ct)).Where(route => AffectsHost(route, host.Hostname)).ToArray();
            if (installed.Length != 1 || installed[0].Id != id || installed[0].Pattern != host.Hostname + "/*"
                || installed[0].Script != configuration.Scripts[mode])
                throw new TrafficConflictException("The created route changed before fail-closed verification. No later route was overwritten.");
            // Only correct a flag that the create/read-back did not already establish.
            if (!MatchesTarget(installed[0], pattern, script))
                await SendAsync(HttpMethod.Put, $"{path}/{id}", payload, ct, requireLease);
        }
        else await SendAsync(HttpMethod.Put, $"{path}/{previous.Id}", payload, ct, requireLease);
    }

    private static bool MatchesTarget(TrafficRoute? route, string pattern, string script) =>
        route is { RequestLimitFailOpen: false } && route.Pattern == pattern && route.Script == script;

    public async Task<(bool Matches, int Status)> ProbeAsync(TrafficHost host, string mode, string statusUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host.Hostname}/");
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        using var response = await clients.CreateClient("traffic-verification").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var marker = response.Headers.TryGetValues("X-Platform-Maintenance", out var values) && values.Contains("v1");
        var status = (int)response.StatusCode;
        return (mode switch
        {
            "normal" => !marker,
            "page" => marker && status == 503,
            "redirect" => marker && status == 302 && response.Headers.Location is { IsAbsoluteUri: true } destination
                && destination.AbsoluteUri == statusUrl,
            _ => false
        }, status);
    }

    public async Task<TrafficQuota> QuotaAsync(string accountId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoot, "graphql"));
            request.Headers.Authorization = await AuthorizationAsync(ct);
            var start = DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd'T'00:00:00'Z'", System.Globalization.CultureInfo.InvariantCulture);
            request.Content = JsonContent.Create(new
            {
                query = """
                    query($account: string, $start: Time) {
                      viewer { accounts(filter: {accountTag: $account}) {
                        workersInvocationsAdaptive(limit: 1, filter: {datetime_geq: $start}) { sum { requests } }
                      } }
                    }
                    """,
                variables = new { account = accountId, start }
            });
            using var response = await clients.CreateClient("cloudflare-traffic").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new InvalidDataException("Cloudflare usage analytics returned an error.");
            var records = root.GetProperty("data").GetProperty("viewer").GetProperty("accounts")[0].GetProperty("workersInvocationsAdaptive");
            long count = 0;
            if (records.GetArrayLength() != 0)
            {
                var requests = records[0].GetProperty("sum").GetProperty("requests");
                if (requests.ValueKind != JsonValueKind.Number || !requests.TryGetInt64(out count))
                    throw new InvalidDataException("Cloudflare returned invalid account usage.");
            }
            if (count < 0) throw new InvalidDataException("Cloudflare returned invalid account usage.");
            if (count >= 80_000) logger.LogWarning("Workers account usage is approaching the free daily allowance: estimated {Requests}.", count);
            return new(count, DateTimeOffset.UtcNow, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Workers usage could not be checked; no zero-usage fallback is inferred.");
            return new(null, DateTimeOffset.UtcNow, "Workers usage could not be checked. Review account usage in Cloudflare; the free allowance is shared across all Workers.");
        }
    }
}
