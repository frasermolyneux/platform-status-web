using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using GitHubJwt;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record GitHubUpdateEnvelope(Guid OperationId, DateTimeOffset At, IncidentState State, Severity Severity,
    DateTimeOffset StartedAt, DateTimeOffset? ScheduledStart, DateTimeOffset? ScheduledEnd);

public sealed record GitHubIssueSnapshot(JsonElement Issue, IReadOnlyList<JsonElement> Comments)
{
    public int Number => Issue.GetProperty("number").GetInt32();
    public DateTimeOffset UpdatedAt => Issue.GetProperty("updated_at").GetDateTimeOffset();
    public string Version => GitHubIncidentClient.Version(Issue, Comments);
    public string[] ReadLabels() => Issue.GetProperty("labels").EnumerateArray().Select(label => label.GetProperty("name").GetString()!).ToArray();
}

public sealed class GitHubIncidentConflictException(string message) : Exception(message);
public sealed class GitHubOutcomeUnknownException(string message) : Exception(message);

public static class GitHubIncidentDocument
{
    public static string Marker(Guid operation) => $"<!-- mxstatus-operation:{operation:D} -->";

    public static string Body(GitHubJob job)
    {
        var update = job.Incident.Updates.Single(update => update.OperationId == job.OperationId);
        var metadata = new GitHubUpdateEnvelope(job.OperationId, update.At, job.Command.State, job.Command.Severity,
            job.Incident.StartedAt, job.Command.ScheduledStart, job.Command.ScheduledEnd);
        return $"{Marker(job.OperationId)}\n<!-- mxstatus-update:{Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata, StatusJson.Options))} -->\n{job.Command.Message.Trim()}";
    }

    public static (string Text, GitHubUpdateEnvelope? Metadata) Read(string body)
    {
        var lines = body.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || !lines[0].StartsWith("<!-- mxstatus-operation:", StringComparison.Ordinal)
            || !lines[1].StartsWith("<!-- mxstatus-update:", StringComparison.Ordinal)) return (body, null);
        try
        {
            var encoded = lines[1]["<!-- mxstatus-update:".Length..].Replace(" -->", "", StringComparison.Ordinal);
            var metadata = JsonSerializer.Deserialize<GitHubUpdateEnvelope>(Convert.FromBase64String(encoded), StatusJson.Options)
                ?? throw new InvalidDataException("GitHub status update metadata is empty.");
            if (lines[0] != Marker(metadata.OperationId) || metadata.OperationId == Guid.Empty || metadata.At == default
                || metadata.StartedAt == default || metadata.StartedAt > metadata.At || metadata.At > DateTimeOffset.UtcNow.AddMinutes(5)
                || metadata.ScheduledStart.HasValue != metadata.ScheduledEnd.HasValue
                || metadata.ScheduledEnd <= metadata.ScheduledStart
                || (metadata.Severity == Severity.Maintenance) != metadata.ScheduledStart.HasValue
                || !Enum.IsDefined(metadata.State) || !Enum.IsDefined(metadata.Severity))
                throw new InvalidDataException("GitHub status update metadata is invalid.");
            return (string.Join('\n', lines.Skip(2)), metadata);
        }
        catch (FormatException exception) { throw new InvalidDataException("GitHub status metadata is not valid base64.", exception); }
        catch (JsonException exception) { throw new InvalidDataException("GitHub status metadata is not valid JSON.", exception); }
    }

    public static string[] Labels(StatusCatalog catalog, string siteId, IncidentCommand command, string[] previous)
    {
        var otherSites = previous.Where(label => label.StartsWith("site:", StringComparison.Ordinal)).Select(label => label[5..])
            .Where(id => id != siteId).Select(id => catalog.Sites.SingleOrDefault(site => site.Site.Id == id)
                ?? throw new GitHubIncidentConflictException("The GitHub issue names an unknown status site. Review its site labels before retrying.")).ToArray();
        var otherComponents = otherSites.SelectMany(site =>
            previous.Where(label => label.StartsWith("component:", StringComparison.Ordinal)).Select(label => label[10..])
                .Where(id => StatusCatalog.PublicComponents(site.Components.Components).Any(component => component.Id == id))
            .Concat(previous.Where(label => label.StartsWith("workload:", StringComparison.Ordinal)).Select(label => label[9..])
                .Where(site.Workloads.ContainsKey).SelectMany(workload => site.Workloads[workload]))).Distinct().ToArray();
        return [.. previous.Where(label => label is not ("status:incident" or "status:published")
            && !new[] { "site:", "workload:", "component:", "state:", "severity:" }.Any(prefix => label.StartsWith(prefix, StringComparison.Ordinal))),
            "status:incident", "status:published", $"severity:{command.Severity.ToApiString()}", $"state:{command.State.ToApiString()}",
            $"site:{siteId}", .. otherSites.Select(site => $"site:{site.Site.Id}"),
            .. command.Components.Concat(otherComponents).Distinct().Select(id => $"component:{id}")];
    }
}

public sealed class GitHubIncidentClient(RuntimeOptions options, StatusCatalog catalog, IHttpClientFactory clients)
{
    public static string Version(JsonElement issue, IReadOnlyList<JsonElement> comments) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = issue.GetProperty("title").GetString(),
            body = issue.GetProperty("body").GetString(),
            closed = issue.GetProperty("closed_at").GetRawText(),
            labels = issue.GetProperty("labels").EnumerateArray().Select(label => label.GetProperty("name").GetString()).Order(),
            comments = comments.Select(comment => new
            {
                at = comment.GetProperty("created_at").GetDateTimeOffset(),
                author = comment.GetProperty("user").GetProperty(GitHubIssueParser.LoginProperty).GetString(),
                body = comment.GetProperty("body").GetString()
            })
        })));

    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? installationToken;
    private DateTimeOffset expiresAt;

    public async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"https://api.github.com/repos/{options.GitHubRepository}/{path}");
        Configure(request);
        var token = await TokenAsync(ct);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: StatusJson.Options);
        return request;
    }

    private static void Configure(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("MXStatus/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (options.GitHubTokenFile is not null) return (await File.ReadAllTextAsync(options.GitHubTokenFile, ct)).Trim();
        if (options.GitHubPrivateKeyFile is null) return null;
        await tokenLock.WaitAsync(ct);
        try
        {
            if (installationToken is not null && expiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return installationToken;
            var pem = await File.ReadAllTextAsync(options.GitHubPrivateKeyFile, ct);
            using var signingKey = RSA.Create();
            signingKey.ImportFromPem(pem);
            // StringPrivateKeySource supplies the PEM envelope around PKCS#1 key data.
            var jwt = new GitHubJwtFactory(new StringPrivateKeySource(Convert.ToBase64String(signingKey.ExportRSAPrivateKey())),
                new GitHubJwtFactoryOptions { AppIntegrationId = options.GitHubAppId!.Value, ExpirationSeconds = 540 }).CreateEncodedJwtToken();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.github.com/app/installations/{options.GitHubInstallationId}/access_tokens");
            Configure(request);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Content = JsonContent.Create(new
            {
                repositories = new[] { options.GitHubRepository.Split('/')[1] },
                permissions = new { issues = "write" }
            });
            var token = await SendAsync(request, ct);
            installationToken = token.GetProperty("token").GetString()!;
            expiresAt = token.GetProperty("expires_at").GetDateTimeOffset();
            return installationToken;
        }
        finally { tokenLock.Release(); }
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await clients.CreateClient("github").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub operation returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 4_000_000) throw new InvalidDataException("GitHub response exceeds the bounded size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return JsonSerializer.Deserialize<JsonElement>(output.ToArray());
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = await RequestAsync(method, path, body, ct);
        return await SendAsync(request, ct);
    }

    public async Task<IReadOnlyList<JsonElement>> PagesAsync(string path, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        for (var page = 1; page <= 10; page++)
        {
            var result = await SendAsync(HttpMethod.Get, $"{path}&per_page=100&page={page}", null, ct);
            var current = result.EnumerateArray().Select(item => item.Clone()).ToArray();
            items.AddRange(current);
            if (current.Length < 100) return items;
        }
        throw new InvalidDataException("GitHub collection exceeds ten pages; reconciliation was not applied.");
    }

    public async Task<GitHubIssueSnapshot> ReadAsync(int number, CancellationToken ct)
    {
        var issue = await SendAsync(HttpMethod.Get, $"issues/{number}", null, ct);
        var comments = issue.GetProperty("comments").GetInt32() == 0 ? []
            : await PagesAsync($"issues/{number}/comments?", ct);
        var latest = await SendAsync(HttpMethod.Get, $"issues/{number}", null, ct);
        if (latest.GetProperty("updated_at").GetDateTimeOffset() != issue.GetProperty("updated_at").GetDateTimeOffset()
            || latest.GetProperty("comments").GetInt32() != comments.Count
            || Version(latest, comments) != Version(issue, comments))
            throw new GitHubIncidentConflictException("The GitHub issue changed while it was read. Review the current version before retrying.");
        return new(latest, comments.Where(comment =>
            GitHubIssueParser.IsTrusted(comment.GetProperty("user").GetProperty(GitHubIssueParser.LoginProperty).GetString()!, options)).ToArray());
    }

    public async Task<GitHubIssueSnapshot?> FindCreatedAsync(Guid operation, CancellationToken ct)
    {
        var issues = await PagesAsync("issues?state=all&sort=updated&direction=desc", ct);
        var matching = issues.Where(issue => !issue.TryGetProperty("pull_request", out _)
            && (issue.GetProperty("body").GetString() ?? "").Contains(GitHubIncidentDocument.Marker(operation), StringComparison.Ordinal)
            && GitHubIssueParser.IsTrusted(issue.GetProperty("user").GetProperty(GitHubIssueParser.LoginProperty).GetString()!, options)).ToArray();
        if (matching.Length > 1) throw new GitHubIncidentConflictException("More than one GitHub issue has this operation marker. Review the repository before continuing.");
        return matching.Length == 0 ? null : await ReadAsync(matching[0].GetProperty("number").GetInt32(), ct);
    }

    public static int? IssueNumber(string? url, string repository) =>
        url?.StartsWith($"https://github.com/{repository}/issues/", StringComparison.Ordinal) == true
            && int.TryParse(url.Split('/')[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    public bool ContainsUpdate(GitHubIssueSnapshot snapshot, GitHubJob job)
    {
        var latest = new[] { snapshot.Issue }.Concat(snapshot.Comments)
            .Where(item => GitHubIssueParser.IsTrusted(item.GetProperty("user").GetProperty(GitHubIssueParser.LoginProperty).GetString()!, options))
            .Select(item => item.GetProperty("body").GetString() ?? "")
            .LastOrDefault(body => GitHubIncidentDocument.Read(body).Metadata?.OperationId == job.OperationId);
        return latest is not null && latest.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()
            == GitHubIncidentDocument.Body(job).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    public string[] DesiredLabels(GitHubJob job, GitHubIssueSnapshot? snapshot) =>
        GitHubIncidentDocument.Labels(catalog, job.Incident.SiteId, job.Command, snapshot?.ReadLabels() ?? []);

    public bool Matches(GitHubJob job, GitHubIssueSnapshot snapshot)
    {
        if (!job.Command.Published) return !snapshot.ReadLabels().Contains("status:published");
        return snapshot.Issue.GetProperty("title").GetString() == job.Command.Title.Trim()
            && snapshot.Issue.GetProperty("state").GetString() == (job.Command.State == IncidentState.Resolved ? "closed" : "open")
            && DesiredLabels(job, snapshot).ToHashSet().SetEquals(snapshot.ReadLabels());
    }

    public async Task EnsureLabelsAsync(IEnumerable<string> labels, CancellationToken ct)
    {
        var existing = (await PagesAsync("labels?", ct)).Select(label => label.GetProperty("name").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels.Where(label => !existing.Contains(label)))
        {
            try { await SendAsync(HttpMethod.Post, "labels", new { name = label, color = "0969da" }, ct); }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                await SendAsync(HttpMethod.Get, $"labels/{Uri.EscapeDataString(label)}", null, ct);
            }
        }
    }
}
