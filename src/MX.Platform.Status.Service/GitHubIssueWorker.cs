using System.Net;
using System.Text.Json;
using MX.Platform.Status.App.Incidents;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record GitHubSource(int Number, string SiteId, DateTimeOffset UpdatedAt, IncidentRecord Incident);

public static class GitHubIssueParser
{
    internal const string LoginProperty = "login";
    private const string CreatedAtProperty = "created_at";
    private sealed record UpdateEvent(DateTimeOffset At, string Body, IncidentState? State = null);

    public static bool IsTrusted(string author, RuntimeOptions options) =>
        author.Equals(options.GitHubRepository.Split('/')[0], StringComparison.OrdinalIgnoreCase)
        || options.GitHubTrustedAuthors.Contains(author, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IncidentRecord> Parse(JsonElement issue, IReadOnlyList<JsonElement> comments,
        StatusCatalog catalog, RuntimeOptions options, IReadOnlyList<IncidentRecord>? previous = null)
    {
        var labels = issue.GetProperty("labels").EnumerateArray()
            .Select(label => label.GetProperty("name").GetString()!.ToLowerInvariant()).ToHashSet();
        if (!labels.Contains("status:incident")) return [];
        if (!IsTrusted(issue.GetProperty("user").GetProperty(LoginProperty).GetString()!, options))
            throw new InvalidDataException("Incident authors must be the repository owner or an explicitly trusted maintainer.");
        var targets = ReadTargets(labels, catalog);
        var severity = ReadSeverity(labels);
        var closedAt = issue.GetProperty("closed_at").ValueKind == JsonValueKind.Null
            ? (DateTimeOffset?)null : issue.GetProperty("closed_at").GetDateTimeOffset();
        var published = labels.Contains("status:published");
        var body = issue.GetProperty("body").GetString() ?? "";
        var sourceBody = GitHubIncidentDocument.Read(body);
        var title = issue.GetProperty("title").GetString() ?? "";
        var createdAt = issue.GetProperty(CreatedAtProperty).GetDateTimeOffset();
        var updatedAt = issue.GetProperty("updated_at").GetDateTimeOffset();
        var sourceUpdates = new List<SourceUpdateSnapshot>();
        var sourceTimeline = ReadUpdates(issue, comments, options, snapshots: sourceUpdates);
        var state = ReadState(labels, closedAt, sourceTimeline[^1].State);
        var approved = ApprovedHistory(previous?.OrderByDescending(record => record.PublicUpdates.Count)
            .ThenByDescending(record => record.PublicUpdates.LastOrDefault()?.At).FirstOrDefault());
        var aliases = previous?.SelectMany(record => record.PublicAliases.Append(record.Id))
            .Where(id => id > 0).Distinct().ToArray() ?? [];
        var schedule = LatestMetadata(issue, comments, options);
        if (severity != Severity.Maintenance) schedule = null;
        var records = new List<IncidentRecord>();
        foreach (var (site, components) in targets)
        {
            var own = previous?.SingleOrDefault(record => record.SiteId == site.Site.Id);
            var previousIncident = own ?? approved;
            var resolvedAt = ResolutionTime(state, closedAt, updatedAt, previousIncident);
            var (updates, stateUpdates) = ReconcileUpdates(issue, comments, options, state, closedAt, previousIncident);
            updates = PreserveHistory(updates, sourceUpdates, previousIncident, severity, updatedAt);
            var publicUpdates = previousIncident?.PublicUpdates ?? [];
            var publicStateUpdates = previousIncident?.PublicSourceStateUpdates ?? [];
            if (published)
            {
                var (current, transitions) = ReconcileUpdates(issue, comments, options, state, closedAt, approved);
                publicUpdates = PreserveHistory(current, sourceUpdates, approved, severity, updatedAt);
                publicStateUpdates = transitions;
            }
            var command = new IncidentCommand(title, components, severity, state, sourceBody.Text, published,
                ScheduledStart: schedule?.ScheduledStart, ScheduledEnd: schedule?.ScheduledEnd);
            if (command.Validate(site) is { } error) throw new InvalidDataException(error);
            records.Add(new()
            {
                SiteId = site.Site.Id,
                Title = title.Trim(),
                Components = components,
                Severity = severity,
                State = state,
                Published = published,
                StartedAt = previousIncident?.StartedAt ?? sourceBody.Metadata?.StartedAt ?? createdAt,
                ResolvedAt = resolvedAt,
                ScheduledStart = command.ScheduledStart,
                ScheduledEnd = command.ScheduledEnd,
                Updates = updates,
                PublicUpdates = publicUpdates,
                SourceStateUpdates = stateUpdates,
                SourceUpdates = sourceUpdates,
                PublicSourceUpdates = published ? sourceUpdates : previousIncident?.PublicSourceUpdates ?? [],
                PublicSourceStateUpdates = publicStateUpdates,
                PublicSourceSeverity = published ? severity : previousIncident?.PublicSourceSeverity,
                PublicAliases = aliases.Where(id => id != own?.Id).ToArray(),
                SourceUrl = $"https://github.com/{options.GitHubRepository}/issues/{issue.GetProperty("number").GetInt32()}",
                GitHub = previousIncident?.GitHub,
                GitHubVersion = GitHubIncidentClient.Version(issue, comments.Where(comment =>
                    IsTrusted(comment.GetProperty("user").GetProperty(LoginProperty).GetString()!, options)).ToArray()),
                GitHubBaseVersion = own?.GitHubVersion,
                Evidence = own?.Evidence
            });
        }
        return records;
    }

    private static GitHubUpdateEnvelope? LatestMetadata(JsonElement issue, IReadOnlyList<JsonElement> comments, RuntimeOptions options) =>
        new[] { issue }.Concat(comments.Where(comment => IsTrusted(comment.GetProperty("user").GetProperty(LoginProperty).GetString()!, options)))
            .Select(item => GitHubIncidentDocument.Read(item.GetProperty("body").GetString() ?? "").Metadata)
            .Where(metadata => metadata is not null).OrderBy(metadata => metadata!.At).LastOrDefault();

    private static IncidentRecord? ApprovedHistory(IncidentRecord? previous)
    {
        if (previous is not { PublicUpdates.Count: > 0 }) return null;
        return previous with
        {
            Updates = previous.PublicUpdates,
            SourceUpdates = previous.PublicSourceUpdates,
            SourceStateUpdates = previous.PublicSourceStateUpdates.Count > 0 ? previous.PublicSourceStateUpdates
                : previous.SourceStateUpdates.Where(source => previous.PublicUpdates.Any(approved =>
                    approved.At == source.At && approved.Body == source.Body && approved.State == source.State)).ToArray(),
            Severity = previous.PublicSourceSeverity ?? previous.Severity,
            State = previous.PublicUpdates[^1].State
        };
    }

    private static (SiteDefinition Site, string[] Components)[] ReadTargets(HashSet<string> labels, StatusCatalog catalog)
    {
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
        return sites.Select(site =>
        {
            var publicIds = StatusCatalog.PublicComponents(site.Components.Components).Select(c => c.Id).ToHashSet();
            return (site, workloads.Where(site.Workloads.ContainsKey).SelectMany(workload => site.Workloads[workload])
                .Concat(componentIds.Where(publicIds.Contains)).Distinct().ToArray());
        }).ToArray();
    }

    private static Severity ReadSeverity(HashSet<string> labels)
    {
        var severities = labels.Where(label => label.StartsWith("severity:", StringComparison.Ordinal)).ToArray();
        if (severities.Length != 1 || severities[0] is not ("severity:degraded" or "severity:outage" or "severity:maintenance"))
            throw new InvalidDataException("Choose exactly one severity:degraded, severity:outage or severity:maintenance label.");
        return Enum.Parse<Severity>(severities[0]["severity:".Length..], true);
    }

    private static IncidentState ReadState(HashSet<string> labels, DateTimeOffset? closedAt, IncidentState latest)
    {
        var states = labels.Where(label => label.StartsWith("state:", StringComparison.Ordinal)).Select(label => label[6..]).ToArray();
        if (states.Length > 1 || states.Any(state => state is not ("investigating" or "identified" or "monitoring" or "resolved")))
            throw new InvalidDataException("Choose at most one known incident-state label.");
        if (closedAt.HasValue) return IncidentState.Resolved;
        if (states.Length == 1) return Enum.Parse<IncidentState>(states[0], true);
        return latest == IncidentState.Resolved ? IncidentState.Investigating : latest;
    }

    private static DateTimeOffset? ResolutionTime(IncidentState state, DateTimeOffset? closedAt, DateTimeOffset updatedAt, IncidentRecord? previous)
    {
        if (state != IncidentState.Resolved) return null;
        if (previous?.State == IncidentState.Resolved && previous.ResolvedAt is { } resolvedAt) return resolvedAt;
        return closedAt ?? updatedAt;
    }

    private static (IReadOnlyList<IncidentUpdate> Updates, IReadOnlyList<IncidentUpdate> StateUpdates) ReconcileUpdates(
        JsonElement issue, IReadOnlyList<JsonElement> comments, RuntimeOptions options, IncidentState state,
        DateTimeOffset? closedAt, IncidentRecord? previous)
    {
        var stateUpdates = previous?.SourceStateUpdates.ToList() ?? [];
        if (closedAt is { } closed && previous?.State != IncidentState.Resolved)
            stateUpdates.Add(StateUpdate(closed, IncidentState.Resolved));
        var updates = ReadUpdates(issue, comments, options, stateUpdates);
        if (updates[^1].State != state)
        {
            stateUpdates.Add(StateUpdate(issue.GetProperty("updated_at").GetDateTimeOffset(), state));
            updates = ReadUpdates(issue, comments, options, stateUpdates);
        }
        if (updates[^1].State != state)
            throw new InvalidDataException("The issue snapshot predates its durable state history; synchronization will retry.");
        return (updates, stateUpdates);
    }

    private static IncidentUpdate StateUpdate(DateTimeOffset at, IncidentState state) =>
        new() { At = at, State = state, Body = $"Incident marked {state.ToApiString()} in GitHub." };

    private static IReadOnlyList<IncidentUpdate> PreserveHistory(IReadOnlyList<IncidentUpdate> incoming,
        IReadOnlyList<SourceUpdateSnapshot> sourceUpdates, IncidentRecord? previous, Severity severity, DateTimeOffset updatedAt)
    {
        if (previous is null)
            return incoming.Select(update => update with { Severity = update.Severity ?? (update.At == updatedAt ? severity : null) }).ToArray();
        var history = previous.Updates.ToList();
        foreach (var update in incoming)
        {
            if (UnchangedUpdate(update, sourceUpdates, previous)) continue;
            var edited = history.Any(known => known.At == update.At);
            history.Add(update with
            {
                At = edited ? updatedAt : update.At,
                State = edited ? incoming[^1].State : update.State,
                Severity = update.Severity ?? (edited || update.At == updatedAt ? severity : null)
            });
        }
        if (previous.Severity != severity)
            history.Add(new()
            {
                At = updatedAt,
                State = incoming[^1].State,
                Severity = severity,
                Body = $"Incident impact changed to {severity.ToApiString()} in GitHub."
            });
        return history.Distinct().OrderBy(update => update.At).ToArray();
    }

    private static bool UnchangedUpdate(IncidentUpdate update, IReadOnlyList<SourceUpdateSnapshot> sourceUpdates, IncidentRecord previous)
    {
        if (update.OperationId is { } operation && !previous.SourceUpdates.Any(source => source.At == update.At)
            && previous.Updates.Any(known => known.OperationId == operation && known.Body == update.Body
                && known.State == update.State && known.Severity == update.Severity)) return true;
        var current = sourceUpdates.FirstOrDefault(source => source.At == update.At && source.Body == update.Body);
        if (previous.SourceUpdates.Count > 0 && current is not null)
            return previous.SourceUpdates.Any(source => source.At == current.At && source.Body == current.Body
                && source.ExplicitState == current.ExplicitState);
        return previous.Updates.Any(known => known.At == update.At && known.Body == update.Body && known.State == update.State);
    }

    private static List<IncidentUpdate> ReadUpdates(JsonElement issue, IReadOnlyList<JsonElement> comments, RuntimeOptions options,
        IReadOnlyList<IncidentUpdate>? stateUpdates = null, List<SourceUpdateSnapshot>? snapshots = null)
    {
        var events = new List<UpdateEvent>
        {
            new(issue.GetProperty(CreatedAtProperty).GetDateTimeOffset(), issue.GetProperty("body").GetString() ?? "")
        };
        events.AddRange(comments.Where(comment => IsTrusted(comment.GetProperty("user").GetProperty(LoginProperty).GetString()!, options))
            .Select(comment => new UpdateEvent(comment.GetProperty(CreatedAtProperty).GetDateTimeOffset(), comment.GetProperty("body").GetString() ?? "")));
        events.AddRange((stateUpdates ?? []).Select(update => new UpdateEvent(update.At, update.Body, update.State)));
        var updates = new List<IncidentUpdate>();
        var sources = events.Select((entry, index) =>
            (Entry: entry, Metadata: GitHubIncidentDocument.Read(entry.Body).Metadata, Index: index));
        // An explicitly reviewed retry can restore an edited operation in a new comment.
        foreach (var entry in sources.GroupBy(source =>
                (Operation: source.Metadata?.OperationId, Unique: source.Metadata is null ? source.Index : 0))
            .Select(group => group.Last()).OrderBy(source => source.Metadata?.At ?? source.Entry.At).Select(source => source.Entry))
        {
            if (entry.State is { } state) updates.Add(new() { At = entry.At, State = state, Body = entry.Body });
            else AddUpdate(updates, entry.Body, entry.At, updates.LastOrDefault()?.State ?? IncidentState.Investigating, snapshots);
        }
        if (updates.Count == 0)
            throw new InvalidDataException("An incident needs a public description, not only internal notes.");
        return updates;
    }

    private static void AddUpdate(List<IncidentUpdate> updates, string content, DateTimeOffset at, IncidentState fallback,
        List<SourceUpdateSnapshot>? snapshots)
    {
        var state = fallback;
        IncidentState? explicitState = null;
        var document = GitHubIncidentDocument.Read(content);
        var text = IncidentRenderer.Render(document.Text);
        if (document.Metadata is { } metadata)
        {
            at = metadata.At;
            state = metadata.State;
            explicitState = metadata.State;
        }
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
            explicitState = state;
            text = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..].Trim() : "";
        }
        if (text.Length > 20000) throw new InvalidDataException("Incident updates cannot exceed 20000 characters.");
        if (text.Length > 0)
        {
            var update = new IncidentUpdate
            {
                At = at,
                State = state,
                Severity = document.Metadata?.Severity,
                OperationId = document.Metadata?.OperationId,
                Body = WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal)
            };
            updates.Add(update);
            snapshots?.Add(new(at, update.Body, explicitState));
        }
    }
}

public sealed class GitHubIssueWorker(
    StatusCatalog catalog, RuntimeOptions options, PostgresStore database,
    IHttpClientFactory clients, ILogger<GitHubIssueWorker> logger, GitHubIncidentClient? github = null) : BackgroundService
{
    private readonly GitHubIncidentClient githubClient = github ?? new(options, catalog, clients);
    private const string NumberProperty = "number";
    private readonly Dictionary<int, (DateTimeOffset UpdatedAt, DateTimeOffset CheckedAt, List<JsonElement> Items)> commentCache = [];
    private sealed record IssueImport(int Number, DateTimeOffset UpdatedAt, IReadOnlyList<IncidentRecord> Records, string? Problem = null);
    private sealed record SyncProblem(int Issue, string Summary);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.GitHubEnabled || !options.Publisher || options.DatabaseConnectionFile is null && options.DatabaseHost is null) return;
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
        var imports = new List<IssueImport>();
        foreach (var issue in issues.Where(issue => !issue.TryGetProperty("pull_request", out _))
            .GroupBy(issue => issue.GetProperty(NumberProperty).GetInt32())
            .Select(group => group.OrderByDescending(issue => issue.GetProperty("updated_at").GetDateTimeOffset()).First()))
        {
            var number = issue.GetProperty(NumberProperty).GetInt32();
            imports.Add(await ReadImportAsync(issue, sources.Where(source => source.Number == number).ToArray(), ct));
        }
        var changes = imports.SelectMany(import => import.Records.Select(record =>
            (import.Number, import.UpdatedAt, Incident: record))).ToList();
        var observed = changes.Select(change => (change.Number, change.Incident.SiteId)).ToHashSet();
        var problems = new List<SyncProblem>();
        foreach (var import in imports)
            if (import.Problem is { } problem) problems.Add(new(import.Number, problem));
        var fetchedNumbers = issues.Select(issue => issue.GetProperty(NumberProperty).GetInt32()).ToHashSet();
        foreach (var source in sources.Where(source => source.Incident.Published && !observed.Contains((source.Number, source.SiteId))))
        {
            if (await CanWithdrawAsync(source.Number, fetchedNumbers.Contains(source.Number), ct))
                changes.Add((source.Number, DateTimeOffset.UtcNow,
                    source.Incident with { Published = false, GitHubBaseVersion = source.Incident.GitHubVersion }));
            else problems.Add(new(source.Number, "Issue moved during pagination; retrying the next snapshot."));
        }
        await PersistChangesAsync(changes, ct);
        var errors = problems.Count;
        await database.SaveEvidenceAsync("github-issues", options.GitHubRepository, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new
            {
                status = errors == 0 ? "healthy" : "error",
                repository = options.GitHubRepository,
                summary = errors == 0 ? "GitHub issues are synchronized." : $"{errors} issue(s) need metadata or maintainer corrections.",
                imported = imports.Where(import => import.Problem is null).Sum(import => import.Records.Count),
                errors,
                problems = problems.Take(20),
                pollSeconds = options.GitHubRefreshSeconds
            }, StatusJson.Options), ct);
    }

    private async Task<IssueImport> ReadImportAsync(JsonElement issue, IReadOnlyList<GitHubSource> previous, CancellationToken ct)
    {
        var number = issue.GetProperty(NumberProperty).GetInt32();
        var updatedAt = issue.GetProperty("updated_at").GetDateTimeOffset();
        try
        {
            var comments = await ReadCommentsAsync(issue, ct);
            return new(number, updatedAt, GitHubIssueParser.Parse(issue, comments, catalog, options,
                previous.Select(source => source.Incident).ToArray()));
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "GitHub issue #{Number} needs publication metadata or maintainer corrections.", number);
            var approved = HasLabel(issue, "status:published")
                && GitHubIssueParser.IsTrusted(issue.GetProperty("user").GetProperty("login").GetString()!, options);
            return new(number, updatedAt, previous.Select(source =>
                source.Incident with
                {
                    Published = source.Incident.Published && approved,
                    GitHubBaseVersion = source.Incident.GitHubVersion
                }).ToArray(), exception.Message);
        }
    }

    private async Task<List<JsonElement>> ReadCommentsAsync(JsonElement issue, CancellationToken ct)
    {
        if (!HasLabel(issue, "status:incident") || issue.GetProperty("comments").GetInt32() == 0
            || !GitHubIssueParser.IsTrusted(issue.GetProperty("user").GetProperty("login").GetString()!, options)) return [];
        var number = issue.GetProperty(NumberProperty).GetInt32();
        var updatedAt = issue.GetProperty("updated_at").GetDateTimeOffset();
        if (commentCache.TryGetValue(number, out var cached) && cached.UpdatedAt == updatedAt
            && cached.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-30)) return cached.Items;
        var comments = await ReadPagesAsync($"issues/{number}/comments?", ct);
        commentCache[number] = (updatedAt, DateTimeOffset.UtcNow, comments);
        return comments;
    }

    private async Task<bool> CanWithdrawAsync(int number, bool inSnapshot, CancellationToken ct)
    {
        if (inSnapshot) return true;
        using var verification = await RequestAsync($"issues/{number}", ct);
        using var response = await clients.CreateClient("github").SendAsync(verification, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return true;
        response.EnsureSuccessStatusCode();
        logger.LogWarning("GitHub issue #{Number} moved during pagination; retaining its publication until the next complete snapshot.", number);
        return false;
    }

    private async Task PersistChangesAsync(IReadOnlyList<(int Number, DateTimeOffset UpdatedAt, IncidentRecord Incident)> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return;
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

    private static bool HasLabel(JsonElement issue, string name) => issue.GetProperty("labels").EnumerateArray()
        .Any(label => string.Equals(label.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));

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

    private Task<HttpRequestMessage> RequestAsync(string path, CancellationToken ct) =>
        githubClient.RequestAsync(HttpMethod.Get, path, null, ct);
}
