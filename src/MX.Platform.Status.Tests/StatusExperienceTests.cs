using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class StatusExperienceTests
{
    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-23T08:00:00Z");

    private static JsonElement Issue(string[]? labels = null, string author = "frasermolyneux", bool closed = false, string body = "We are investigating.") =>
        JsonSerializer.SerializeToElement(new
        {
            number = 42, title = "Availability incident", body,
            user = new { login = author }, created_at = Created, updated_at = Created.AddMinutes(5),
            closed_at = closed ? Created.AddMinutes(5) : (DateTimeOffset?)null,
            labels = (labels ?? ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage"])
                .Select(name => new { name })
        });

    [Fact]
    public void GitHubImportsRequirePublicationApprovalAndAttributeWorkloadsToPublicComponents()
    {
        var published = Assert.Single(GitHubIssueParser.Parse(Issue(body: "### Public update\n\nWe are investigating."), [], Catalogue(), new()));
        Assert.True(published.Published);
        Assert.Equal("We are investigating.", Assert.Single(published.PublicUpdates).Body);
        Assert.Equal(["mx.sitewatch.geo-location-web", "mx.sitewatch.geo-location-api"], published.Components);
        Assert.Equal("https://github.com/frasermolyneux/status-pages/issues/42", published.SourceUrl);
        var draft = Assert.Single(GitHubIssueParser.Parse(Issue(
            ["status:incident", "site:mx", "workload:geo-location", "severity:outage"]), [], Catalogue(), new()));
        Assert.False(draft.Published);
        Assert.Empty(draft.PublicUpdates);
        Assert.NotEmpty(draft.Updates);
        Assert.Empty(GitHubIssueParser.Parse(Issue(["unrelated"], author: "stranger"), [], Catalogue(), new()));
    }

    [Fact]
    public void GitHubUpdatesExcludeUntrustedCommentsAndInternalNotesAndEscapeHtml()
    {
        var comments = new[] {
            JsonSerializer.SerializeToElement(new { body = "UNTRUSTED_COMMENT", user = new { login = "stranger" }, created_at = Created.AddMinutes(1) }),
            JsonSerializer.SerializeToElement(new { body = "state: monitoring\nRecovered <script>unsafe()</script>.", user = new { login = "frasermolyneux" }, created_at = Created.AddMinutes(2) }),
        };
        var record = Assert.Single(GitHubIssueParser.Parse(Issue(
            ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage", "state:monitoring"],
            body: "Public description.\n<!-- internal -->\nPRIVATE_NOTE\n<!-- /internal -->"), comments, Catalogue(), new()));
        Assert.Equal([IncidentState.Investigating, IncidentState.Monitoring], record.PublicUpdates.Select(update => update.State));
        var output = JsonSerializer.Serialize(record.ToPublicIncident(), StatusJson.Options);
        Assert.DoesNotContain("UNTRUSTED_COMMENT", output);
        Assert.DoesNotContain("PRIVATE_NOTE", output);
        Assert.DoesNotContain("<script>", record.PublicUpdates[1].Body);
        Assert.Contains(WebUtility.HtmlEncode("<script>"), record.PublicUpdates[1].Body);
    }

    [Fact]
    public void ClosingAndReopeningSourceIssuesUpdatesTheIncidentLifecycle()
    {
        var closed = Assert.Single(GitHubIssueParser.Parse(Issue(closed: true), [], Catalogue(), new()));
        Assert.Equal(IncidentState.Resolved, closed.State);
        Assert.Equal(Created.AddMinutes(5), closed.ResolvedAt);
        Assert.Equal(IncidentState.Resolved, closed.PublicUpdates.Last().State);
        var reopened = Assert.Single(GitHubIssueParser.Parse(Issue(), [], Catalogue(), new()));
        Assert.Equal(IncidentState.Investigating, reopened.State);
        Assert.Null(reopened.ResolvedAt);
    }

    [Theory]
    [InlineData("site:unknown", "workload:geo-location", "severity:outage", "frasermolyneux")]
    [InlineData("site:xi", "workload:geo-location", "severity:outage", "frasermolyneux")]
    [InlineData("site:mx", "workload:unknown", "severity:outage", "frasermolyneux")]
    [InlineData("site:mx", "component:xi.sitewatch.forums-web", "severity:outage", "frasermolyneux")]
    [InlineData("site:mx", "workload:geo-location", "severity:unknown", "frasermolyneux")]
    [InlineData("site:mx", "workload:geo-location", "severity:outage", "stranger")]
    public void InvalidAttributionOrUntrustedAuthorsCannotBecomePublicIncidents(string site, string workload, string severity, string author) =>
        Assert.Throws<InvalidDataException>(() => GitHubIssueParser.Parse(
            Issue(["status:incident", "status:published", site, workload, severity], author), [], Catalogue(), new()));

    [Fact]
    public void CrossBrandIssuesMapOnlyTheirOwnComponentsAndSupportExplicitTrustedMaintainers()
    {
        var records = GitHubIssueParser.Parse(Issue(
            ["status:incident", "site:mx", "site:xi", "workload:molyneux-me", "workload:xi-forums", "severity:degraded"],
            author: "maintainer"), [], Catalogue(), new() { GitHubTrustedAuthors = ["maintainer"] });
        Assert.Equal(2, records.Count);
        Assert.Equal(["mx.sitewatch.molyneux-me"], records.Single(record => record.SiteId == "mx").Components);
        Assert.Equal(["xi.sitewatch.forums-web"], records.Single(record => record.SiteId == "xi").Components);
    }

    [Theory]
    [InlineData("unknown", 0, 1)]
    [InlineData("info", 2, 1)]
    public void BannersRejectInvalidAppearanceOrTimeWindows(string tone, int start, int end)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(new BannerCommand("Notice", "Message", tone, true,
            StartsAt: now.AddHours(start), ExpiresAt: now.AddHours(end)).Validate());
        Assert.NotNull(new BannerCommand("Notice", "Message", "info", true, ExpiresAt: now.AddMinutes(-1)).Validate());
    }

    [Fact]
    public async Task BannerSchedulingSurvivesStaleDatabaseIndependentPublications()
    {
        var directory = Path.Combine(Path.GetTempPath(), "status-banner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new PublicationStore(Catalogue(), new() { DataDirectory = directory });
            await store.WriteAsync(new()
            {
                Site = new SiteInfo { Id = "mx" }, GeneratedAt = now.AddHours(-1),
                Banners = [
                    new() { Id = 1, Title = "Current", StartsAt = now.AddMinutes(-1), ExpiresAt = now.AddHours(1) },
                    new() { Id = 2, Title = "Future", StartsAt = now.AddHours(1) },
                    new() { Id = 3, Title = "Expired", ExpiresAt = now.AddMinutes(-1) }
                ]
            });
            var read = await new PublicationStore(Catalogue(), new() { DataDirectory = directory }).ReadAsync("mx");
            Assert.True(read.DataFreshness!.Stale);
            Assert.Equal("Current", Assert.Single(read.Banners).Title);
            Assert.Empty((await store.ReadAsync("xi")).Banners);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncompleteOrRateLimitedGitHubSnapshotsFailExplicitlyBeforeChangingIncidents()
    {
        using var api = new GitHubApiFixture
        {
            Issues = Enumerable.Repeat(GitHubApiFixture.Incident(true, 0), 100).ToArray()
        };
        await using var database = new PostgresStore(new(), NullLogger<PostgresStore>.Instance);
        using var worker = new GitHubIssueWorker(Catalogue(), new(), database, api, NullLogger<GitHubIssueWorker>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ReadPagesAsync("issues?state=all", default));
        Assert.Equal(10, api.Requests);
        api.Status = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.ReadPagesAsync("issues?state=all", default));
    }
}
