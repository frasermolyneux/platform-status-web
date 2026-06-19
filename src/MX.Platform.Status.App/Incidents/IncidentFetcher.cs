using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Sites;
using Octokit;

namespace MX.Platform.Status.App.Incidents;

public sealed class IncidentFetcher
{
    private readonly ContentRepoClient _contentRepoClient;
    private readonly string _repo;

    public IncidentFetcher(ContentRepoClient contentRepoClient)
    {
        _contentRepoClient = contentRepoClient;
        _repo = Environment.GetEnvironmentVariable("STATUS_CONTENT_REPO") ?? "frasermolyneux/status-pages";
    }

    public async Task<IReadOnlyList<Incident>> FetchForSiteAsync(string siteId, CancellationToken cancellationToken = default)
    {
        var client = await _contentRepoClient.GetGitHubClientAsync(cancellationToken).ConfigureAwait(false);
        var (owner, name) = SplitRepo();
        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.All,
            Labels = { $"site:{siteId}" }
        };

        var issues = await client.Issue.GetAllForRepository(owner, name, request).ConfigureAwait(false);
        var incidents = new List<Incident>();

        foreach (var issue in issues.Where(issue => issue.PullRequest is null && !HasSeverityLabel(issue, "maintenance")))
        {
            var comments = await client.Issue.Comment.GetAllForIssue(owner, name, issue.Number).ConfigureAwait(false);
            var state = ParseState(issue.Labels.Select(label => label.Name));
            var updates = new List<IncidentUpdate>
            {
                new()
                {
                    At = issue.CreatedAt,
                    State = state,
                    Body = IncidentRenderer.Render(issue.Body ?? string.Empty),
                    Author = issue.User?.Login
                }
            };

            updates.AddRange(comments.Select(comment => new IncidentUpdate
            {
                At = comment.CreatedAt,
                State = state,
                Body = IncidentRenderer.Render(comment.Body ?? string.Empty),
                Author = comment.User?.Login
            }));

            incidents.Add(new Incident
            {
                Id = issue.Number,
                Title = issue.Title,
                Url = issue.HtmlUrl,
                Components = issue.Labels.Where(label => label.Name.StartsWith("component:", StringComparison.OrdinalIgnoreCase)).Select(label => label.Name["component:".Length..]).ToArray(),
                Severity = ParseSeverity(issue.Labels.Select(label => label.Name)),
                State = state,
                CreatedAt = issue.CreatedAt,
                StartedAt = issue.CreatedAt,
                ResolvedAt = issue.ClosedAt,
                Updates = updates.OrderBy(update => update.At).ToArray()
            });
        }

        return incidents.OrderByDescending(incident => incident.CreatedAt).ToArray();
    }

    private (string Owner, string Name) SplitRepo()
    {
        var parts = _repo.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Invalid STATUS_CONTENT_REPO value '{_repo}'. Expected 'owner/repo'.");
        }

        return (parts[0], parts[1]);
    }

    private static bool HasSeverityLabel(Issue issue, string severity) =>
        issue.Labels.Any(label => string.Equals(label.Name, $"severity:{severity}", StringComparison.OrdinalIgnoreCase));

    private static Severity ParseSeverity(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            if (label.Equals("severity:outage", StringComparison.OrdinalIgnoreCase))
            {
                return Severity.Outage;
            }

            if (label.Equals("severity:maintenance", StringComparison.OrdinalIgnoreCase))
            {
                return Severity.Maintenance;
            }
        }

        return Severity.Degraded;
    }

    private static IncidentState ParseState(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            if (label.Equals("state:identified", StringComparison.OrdinalIgnoreCase))
            {
                return IncidentState.Identified;
            }

            if (label.Equals("state:monitoring", StringComparison.OrdinalIgnoreCase))
            {
                return IncidentState.Monitoring;
            }

            if (label.Equals("state:resolved", StringComparison.OrdinalIgnoreCase))
            {
                return IncidentState.Resolved;
            }
        }

        return IncidentState.Investigating;
    }
}
