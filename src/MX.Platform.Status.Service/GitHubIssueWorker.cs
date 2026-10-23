using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MX.Platform.Status.App.Incidents;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record GitHubSource(int Number, string SiteId, DateTimeOffset UpdatedAt, IncidentRecord Incident);

public static class GitHubIssueParser
{
    public static bool IsTrusted(string author, RuntimeOptions options) =>
        author.Equals(options.GitHubRepository.Split('/')[0], StringComparison.OrdinalIgnoreCase)
        || options.GitHubTrustedAuthors.Contains(author, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IncidentRecord> Parse(JsonElement issue, IReadOnlyList<JsonElement> comments,
        StatusCatalog catalog, RuntimeOptions options)
    {
        var labels = issue.GetProperty("labels").EnumerateArray()
            .Select(label => label.GetProperty("name").GetString()!.ToLowerInvariant()).ToHashSet();
        if (!labels.Contains("status:incident")) return [];
        if (!IsTrusted(issue.GetProperty("user").GetProperty("login").GetString()!, options))
            throw new InvalidDataException("Incident authors must be the repository owner or an explicitly trusted maintainer.");
        var siteIds = labels.Where(label => label.StartsWith("site:", StringComparison.Ordinal)).Select(label => label[5..]).ToArray();
        if (siteIds.Length == 0 || siteIds.Any(id => !catalog.Sites.Any(site => site.Site.Id == id)))
            throw new InvalidDataException("Choose known site labels before importing an incident.");
        var sites = siteIds.Select(catalog.Get).ToArray();
        var workloads = labels.Where(label => label.StartsWith("workload:", StringComparison.Ordinal)).Select(label => label[9..]).ToArray();
        var componentIds = labels.Where(label => label.StartsWith("component:", StringComparison.Ordinal)).Select(label => label[10..]).ToArray();
        var knownIds = sites.SelectMany(site => StatusCatalog.PublicComponents(site.Components.Components)).Select(c => c.Id).ToHashSet();
        if (workloads.Any(workload => !sites.Any(site => site.Workloads.ContainsKey(workload)))
            || componentIds.Any(id => !knownIds.Contains(id)))
            throw new InvalidDataException("Workload and component labels must match the selected public catalogue.");
        var severities = labels.Where(label => label.StartsWith("severity:", StringComparison.Ordinal)).ToArray();
        if (severities.Length != 1 || severities[0] is not ("severity:degraded" or "severity:outage"))
            throw new InvalidDataException("Choose exactly one severity:degraded or severity:outage label.");
        var states = labels.Where(label => label.StartsWith("state:", StringComparison.Ordinal)).Select(label => label[6..]).ToArray();
        if (states.Length > 1 || states.Any(state => state is not ("investigating" or "identified" or "monitoring" or "resolved")))
            throw new InvalidDataException("Choose at most one known incident-state label.");
        var closedAt = issue.GetProperty("closed_at").ValueKind == JsonValueKind.Null
            ? (DateTimeOffset?)null : issue.GetProperty("closed_at").GetDateTimeOffset();
        var state = closedAt.HasValue ? IncidentState.Resolved
            : states.Length == 1 ? Enum.Parse<IncidentState>(states[0], true) : IncidentState.Investigating;
        var severity = severities[0] == "severity:outage" ? Severity.Outage : Severity.Degraded;
        var published = labels.Contains("status:published");
        var body = issue.GetProperty("body").GetString() ?? "";
        var title = issue.GetProperty("title").GetString() ?? "";
        var createdAt = issue.GetProperty("created_at").GetDateTimeOffset();
        var updates = new List<IncidentUpdate>();
        AddUpdate(updates, body, createdAt, IncidentState.Investigating);
        foreach (var comment in comments.OrderBy(comment => comment.GetProperty("created_at").GetDateTimeOffset()))
        {
            if (!IsTrusted(comment.GetProperty("user").GetProperty("login").GetString()!, options)) continue;
            AddUpdate(updates, comment.GetProperty("body").GetString() ?? "",
                comment.GetProperty("created_at").GetDateTimeOffset(), updates.LastOrDefault()?.State ?? IncidentState.Investigating);
        }
        if (updates.Count == 0)
            throw new InvalidDataException("An incident needs a public description, not only internal notes.");
        if (!closedAt.HasValue && states.Length == 0)
            state = updates[^1].State == IncidentState.Resolved ? IncidentState.Investigating : updates[^1].State;
        if (updates[^1].State != state)
            updates.Add(new()
            {
                At = closedAt ?? issue.GetProperty("updated_at").GetDateTimeOffset(),
                State = state,
                Body = $"Incident marked {state.ToApiString()} in GitHub."
            });
        var records = new List<IncidentRecord>();
        foreach (var site in sites)
        {
            var publicIds = StatusCatalog.PublicComponents(site.Components.Components).Select(c => c.Id).ToHashSet();
            var components = workloads.Where(site.Workloads.ContainsKey).SelectMany(workload => site.Workloads[workload])
                .Concat(componentIds.Where(publicIds.Contains)).Distinct().ToArray();
            var command = new IncidentCommand(title, components, severity, state, body, published);
            if (command.Validate(site) is { } error) throw new InvalidDataException(error);
            records.Add(new()
            {
                SiteId = site.Site.Id,
                Title = title.Trim(),
                Components = components,
                Severity = severity,
                State = state,
                Published = published,
                StartedAt = createdAt,
                ResolvedAt = state == IncidentState.Resolved
                    ? closedAt ?? issue.GetProperty("updated_at").GetDateTimeOffset() : null,
                Updates = updates,
                PublicUpdates = published ? updates : [],
                SourceUrl = $"https://github.com/{options.GitHubRepository}/issues/{issue.GetProperty("number").GetInt32()}"
            });
        }
        return records;
    }

    private static void AddUpdate(List<IncidentUpdate> updates, string content, DateTimeOffset at, IncidentState fallback)
    {
        var state = fallback;
        var text = IncidentRenderer.Render(content);
        const string formHeading = "### Public update\n";
        if (text.StartsWith(formHeading, StringComparison.Ordinal))
            text = text[formHeading.Length..].TrimStart();
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.StartsWith("state:", StringComparison.OrdinalIgnoreCase))
        {
            var value = firstLine[6..].Trim().ToLowerInvariant();
            if (value is not ("investigating" or "identified" or "monitoring" or "resolved"))
                throw new InvalidDataException("Comment state headers must use investigating, identified, monitoring or resolved.");
            state = Enum.Parse<IncidentState>(value, true);
            text = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..].Trim() : "";
        }
        if (text.Length > 20000) throw new InvalidDataException("Incident updates cannot exceed 20000 characters.");
        if (text.Length > 0)
            updates.Add(new() { At = at, State = state, Body = WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal) });
    }
}

public sealed class GitHubIssueWorker(
    StatusCatalog catalog, RuntimeOptions options, PostgresStore database,
    IHttpClientFactory clients, ILogger<GitHubIssueWorker> logger) : BackgroundService
{
    private readonly Dictionary<int, (DateTimeOffset UpdatedAt, DateTimeOffset CheckedAt, List<JsonElement> Items)> commentCache = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Publisher || options.DatabaseConnectionFile is null && options.DatabaseHost is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                await database.InitializeAsync(timeout.Token);
                await SynchronizeAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "GitHub incident synchronization failed; retaining the last durable incident records.");
                try
                {
                    await database.SaveEvidenceAsync("github-issues", options.GitHubRepository, DateTimeOffset.UtcNow,
                        JsonSerializer.SerializeToElement(new
                        {
                            status = "error",
                            repository = options.GitHubRepository,
                            summary = "Issue synchronization failed. Existing incidents remain available; check service logs."
                        }), stoppingToken);
                }
                catch (Exception evidenceError) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(evidenceError, "Unable to record the GitHub synchronization failure in private evidence.");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.GitHubRefreshSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task SynchronizeAsync(CancellationToken ct)
    {
        var issues = await ReadPagesAsync("issues?state=all&sort=updated&direction=desc", ct);
        var sources = await database.ReadGitHubSourcesAsync(options.GitHubRepository, ct);
        var observed = new HashSet<(int, string)>();
        var changes = new List<(int Number, DateTimeOffset UpdatedAt, IncidentRecord Incident)>();
        var problems = new List<object>();
        var errors = 0;
        var imported = 0;
        foreach (var issue in issues.Where(issue => !issue.TryGetProperty("pull_request", out _)))
        {
            var number = issue.GetProperty("number").GetInt32();
            var updatedAt = issue.GetProperty("updated_at").GetDateTimeOffset();
            var previous = sources.Where(source => source.Number == number).ToArray();
            try
            {
                var relevant = issue.GetProperty("labels").EnumerateArray()
                    .Any(label => string.Equals(label.GetProperty("name").GetString(), "status:incident", StringComparison.OrdinalIgnoreCase));
                List<JsonElement> comments = [];
                if (relevant && issue.GetProperty("comments").GetInt32() > 0)
                {
                    if (commentCache.TryGetValue(number, out var cached) && cached.UpdatedAt == updatedAt
                        && cached.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-30))
                        comments = cached.Items;
                    else
                    {
                        comments = await ReadPagesAsync($"issues/{number}/comments?", ct);
                        commentCache[number] = (updatedAt, DateTimeOffset.UtcNow, comments);
                    }
                }
                var records = GitHubIssueParser.Parse(issue, comments, catalog, options);
                foreach (var record in records)
                {
                    changes.Add((number, updatedAt, record));
                    observed.Add((number, record.SiteId));
                    imported++;
                }
            }
            catch (InvalidDataException exception)
            {
                errors++;
                if (problems.Count < 20) problems.Add(new { issue = number, summary = exception.Message });
                var published = issue.GetProperty("labels").EnumerateArray()
                    .Any(label => string.Equals(label.GetProperty("name").GetString(), "status:published", StringComparison.OrdinalIgnoreCase));
                var trusted = GitHubIssueParser.IsTrusted(issue.GetProperty("user").GetProperty("login").GetString()!, options);
                foreach (var source in previous)
                {
                    observed.Add((number, source.SiteId));
                    if ((!published || !trusted) && source.Incident.Published)
                        changes.Add((number, updatedAt, source.Incident with { Published = false }));
                }
                logger.LogWarning(exception, "GitHub issue #{Number} needs publication metadata or maintainer corrections.", number);
            }
        }
        var fetchedNumbers = issues.Select(issue => issue.GetProperty("number").GetInt32()).ToHashSet();
        foreach (var source in sources.Where(source => source.Incident.Published && !observed.Contains((source.Number, source.SiteId))))
        {
            if (!fetchedNumbers.Contains(source.Number))
            {
                using var verification = await RequestAsync($"issues/{source.Number}", ct);
                using var response = await clients.CreateClient("github").SendAsync(verification, ct);
                if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
                {
                    response.EnsureSuccessStatusCode();
                    errors++;
                    if (problems.Count < 20) problems.Add(new { issue = source.Number, summary = "Issue moved during pagination; retrying the next snapshot." });
                    logger.LogWarning("GitHub issue #{Number} moved during pagination; retaining its publication until the next complete snapshot.", source.Number);
                    continue;
                }
            }
            changes.Add((source.Number, DateTimeOffset.UtcNow, source.Incident with { Published = false }));
        }
        if (changes.Count > 0)
        {
            PublisherLease? lease = null;
            for (var attempt = 0; attempt < 30 && lease is null; attempt++)
            {
                lease = await database.TryPublisherLockAsync(ct);
                if (lease is null) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            await using var publisher = lease ?? throw new InvalidOperationException("Publisher is busy; GitHub changes will retry without blocking public reads.");
            foreach (var change in changes)
                await database.SaveGitHubIncidentAsync(publisher, options.GitHubRepository, change.Number, change.UpdatedAt, change.Incident, ct);
        }
        await database.SaveEvidenceAsync("github-issues", options.GitHubRepository, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new
            {
                status = errors == 0 ? "healthy" : "error",
                repository = options.GitHubRepository,
                summary = errors == 0 ? "GitHub issues are synchronized." : $"{errors} issue(s) need metadata or maintainer corrections.",
                imported,
                errors,
                problems,
                pollSeconds = options.GitHubRefreshSeconds
            }), ct);
    }

    internal async Task<List<JsonElement>> ReadPagesAsync(string path, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        for (var page = 1; page <= 10; page++)
        {
            using var request = await RequestAsync($"{path}&per_page=100&page={page}", ct);
            using var response = await clients.CreateClient("github").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsByteArrayAsync(ct);
            if (content.Length > 4_000_000) throw new InvalidDataException("GitHub response exceeded the bounded import size.");
            var items = JsonSerializer.Deserialize<List<JsonElement>>(content)
                ?? throw new InvalidDataException("GitHub returned an invalid issue collection.");
            result.AddRange(items);
            if (items.Count < 100) return result;
        }
        throw new InvalidDataException("GitHub import exceeded ten pages; no missing-source withdrawals were applied.");
    }

    private async Task<HttpRequestMessage> RequestAsync(string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{options.GitHubRepository}/{path}");
        request.Headers.UserAgent.ParseAdd("MXStatus/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (options.GitHubTokenFile is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(options.GitHubTokenFile, ct)).Trim());
        return request;
    }
}
