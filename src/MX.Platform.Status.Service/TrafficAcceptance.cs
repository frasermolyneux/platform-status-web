namespace MX.Platform.Status.Service;

public static class TrafficAcceptance
{
    public static async Task RunAsync(RuntimeOptions options, StatusCatalog catalog, TrafficController controller,
        IHttpClientFactory clients, bool restoreOnly, CancellationToken ct)
    {
        var configuration = TrafficConfiguration.Read(options, catalog)
            ?? throw new InvalidOperationException("Traffic controls are not configured.");
        var canary = configuration.Hosts.Single(host => host.Canary);
        if (catalog.Sites.SelectMany(site => site.Probes.Values).Any(target => new Uri(target).Host == canary.Hostname))
            throw new InvalidOperationException("Acceptance cannot intercept a monitored production application.");
        var workspace = await controller.ReadAsync(canary.Site, ct);
        if (!restoreOnly && workspace.Quota?.Requests is null)
            throw new InvalidDataException(workspace.Quota?.Error ?? "The live account usage estimate is unavailable.");
        var current = workspace.Hosts.Single(host => host.Hostname == canary.Hostname);
        if (!restoreOnly && current.Mode != "normal")
            throw new InvalidOperationException("The isolated canary is already intercepted. Inspect it and explicitly restore before acceptance.");
        if (!restoreOnly) await RequireBaselineAsync();
        foreach (var mode in restoreOnly ? new[] { "normal" } : new[] { "page", "redirect", "normal" })
        {
            current = await controller.ChangeAsync(canary.Site, canary.Hostname,
                new(mode, "Isolated maintenance canary acceptance", canary.Hostname, current.Revision, current.RouteId, current.RouteFingerprint),
                "acceptance:local-administrator", ct);
            if (mode != "normal")
            {
                foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Post })
                {
                    using var request = new HttpRequestMessage(method, $"https://{canary.Hostname}/acceptance-package.pk3?sample=not-forwarded");
                    request.Headers.Accept.ParseAdd("*/*");
                    using var response = await clients.CreateClient("traffic-verification").SendAsync(request, ct);
                    if ((int)response.StatusCode != 503 || !response.Headers.TryGetValues("X-Platform-Maintenance", out var marker)
                        || !marker.Contains("v1") || response.Headers.Location is not null
                        || method == HttpMethod.Head && (await response.Content.ReadAsByteArrayAsync(ct)).Length != 0)
                        throw new InvalidDataException("The isolated canary did not block a non-browser request correctly. No real application was intercepted.");
                }
                using var htmlFetch = new HttpRequestMessage(HttpMethod.Get, $"https://{canary.Hostname}/acceptance-fragment");
                htmlFetch.Headers.Accept.ParseAdd("text/html");
                htmlFetch.Headers.Add("Sec-Fetch-Mode", "cors");
                htmlFetch.Headers.Add("Sec-Fetch-Dest", "empty");
                using var fetched = await clients.CreateClient("traffic-verification").SendAsync(htmlFetch, ct);
                if ((int)fetched.StatusCode != 503 || fetched.Headers.Location is not null)
                    throw new InvalidDataException("An HTML subrequest was redirected instead of blocked on the isolated canary.");
            }
            else await RequireBaselineAsync();
            Console.WriteLine($"Isolated maintenance canary: {mode}, verified={current.Verified}.");
        }

        async Task RequireBaselineAsync()
        {
            using var response = await clients.CreateClient("traffic-verification").GetAsync($"https://{canary.Hostname}/", ct);
            if ((int)response.StatusCode != 200 || response.Headers.Contains("X-Platform-Maintenance")
                || !response.Headers.TryGetValues("X-Platform-Maintenance-Canary", out var marker) || !marker.Contains("v1"))
                throw new InvalidDataException("The isolated canary's normal Worker response is not confirmed. No production application was intercepted.");
        }
    }
}
