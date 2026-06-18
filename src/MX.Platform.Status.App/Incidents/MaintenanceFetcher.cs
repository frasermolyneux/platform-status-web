using System.Text.RegularExpressions;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Sites;
using Octokit;

namespace MX.Platform.Status.App.Incidents;

public sealed class MaintenanceFetcher
{
    private static readonly Regex FrontMatterValue = new(@"^(?<key>[A-Za-z][A-Za-z0-9-]*):\s*(?<value>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly ContentRepoClient _contentRepoClient;
    private readonly IncidentRenderer _incidentRenderer;
    private readonly string _repo;

    public MaintenanceFetcher(ContentRepoClient contentRepoClient, IncidentRenderer incidentRenderer)
    {
        _contentRepoClient = contentRepoClient;
        _incidentRenderer = incidentRenderer;
        _repo = Environment.GetEnvironmentVariable("STATUS_CONTENT_REPO") ?? "frasermolyneux/status-pages";
    }

    public async Task<IReadOnlyList<MaintenanceWindow>> FetchForSiteAsync(string siteId, CancellationToken cancellationToken = default)
    {
        var client = await _contentRepoClient.GetGitHubClientAsync(cancellationToken).ConfigureAwait(false);
        var (owner, name) = SplitRepo();
        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.All,
            Labels = { $"site:{siteId}", "severity:maintenance" }
        };

        var issues = await client.Issue.GetAllForRepository(owner, name, request).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return issues.Where(issue => issue.PullRequest is null)
            .Select(issue => ParseMaintenance(issue, now))
            .Where(maintenance => maintenance is not null)
            .Cast<MaintenanceWindow>()
            .OrderBy(maintenance => maintenance.ScheduledStart)
            .ToArray();
    }

    private MaintenanceWindow? ParseMaintenance(Issue issue, DateTimeOffset now)
    {
        var metadata = ParseFrontMatter(issue.Body ?? string.Empty);
        if (!TryParseDate(metadata, "scheduledStart", out var scheduledStart) || !TryParseDate(metadata, "scheduledEnd", out var scheduledEnd))
        {
            return null;
        }

        var state = now < scheduledStart ? "scheduled" : now <= scheduledEnd ? "in-progress" : "completed";
        return new MaintenanceWindow
        {
            Id = issue.Number,
            Title = issue.Title,
            Url = issue.HtmlUrl,
            Components = issue.Labels.Where(label => label.Name.StartsWith("component:", StringComparison.OrdinalIgnoreCase)).Select(label => label.Name["component:".Length..]).ToArray(),
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
            State = state,
            Body = _incidentRenderer.Render(issue.Body ?? string.Empty)
        };
    }

    private (string Owner, string Name) SplitRepo()
    {
        var parts = _repo.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : throw new InvalidOperationException($"Invalid STATUS_CONTENT_REPO value '{_repo}'.");
    }

    private static Dictionary<string, string> ParseFrontMatter(string body)
    {
        var trimmed = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!trimmed.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var endIndex = trimmed.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var frontMatter = trimmed[4..endIndex];
        return FrontMatterValue.Matches(frontMatter)
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseDate(IReadOnlyDictionary<string, string> metadata, string key, out DateTimeOffset value)
    {
        if (metadata.TryGetValue(key, out var raw) && DateTimeOffset.TryParse(raw, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
