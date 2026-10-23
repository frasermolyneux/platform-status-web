using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Merging;
using MX.Platform.Status.App.Telemetry;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed class StatusExperienceTests
{
    private static StatusCatalog Catalogue() => new(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-23T08:00:00Z");

    [Theory]
    [InlineData(Severity.Outage, "outage")]
    [InlineData(Severity.Degraded, "degraded")]
    [InlineData(Severity.Maintenance, "maintenance")]
    public void SharedIncidentsOverrideHealthyChecksOnBothBrandsWithoutSharingPrivateDrafts(Severity severity, string expected)
    {
        var catalog = Catalogue();
        var now = DateTimeOffset.UtcNow;
        var command = new IncidentCommand("GeoLocation disruption", ["mx.sitewatch.geo-location-web"], severity,
            IncidentState.Identified, "Public update", true,
            ScheduledStart: severity == Severity.Maintenance ? now.AddMinutes(-1) : null,
            ScheduledEnd: severity == Severity.Maintenance ? now.AddMinutes(10) : null);
        var source = command.Apply(27, "mx", null, now);
        foreach (var site in catalog.Sites)
        {
            var projected = Assert.Single(catalog.ProjectIncidents(site, [source]));
            Assert.Equal([$"{site.Site.Id}.sitewatch.geo-location-web"], projected.Components);
            var live = site.Probes.ToDictionary(probe => probe.Key, _ => new ComponentLiveTelemetry(1, 0, now, null));
            var result = new StatusMerger().Merge(site.Snapshot, live, null, [projected.ToPublicIncident()],
                severity == Severity.Maintenance ? [projected.ToMaintenance(now)] : []);
            var component = Assert.Single(result.Components, component => component.Id == projected.Components[0]);
            Assert.Equal(expected, component.Status);
            Assert.Contains(27, component.OpenIncidentIds!);
            Assert.Contains("GeoLocation Website", result.Summary.Message);
        }
        Assert.Empty(catalog.ProjectIncidents(catalog.Get("xi"), [source with { Published = false }]));
        Assert.Empty(catalog.ProjectIncidents(catalog.Get("xi"), [source with { Components = ["mx.sitewatch.bishopsbees"] }]));
        var reverse = source with { SiteId = "xi", Components = ["xi.sitewatch.geo-location-web"] };
        Assert.Equal(["mx.sitewatch.geo-location-web"], Assert.Single(catalog.ProjectIncidents(catalog.Get("mx"), [reverse])).Components);
    }

    [Fact]
    public void SharedSourceIncidentsAreDeduplicatedAndDoNotExposePrivateUpdates()
    {
        var catalog = Catalogue();
        var first = new IncidentCommand("Shared incident", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Approved description", true).Apply(30, "mx", null, Created);
        first = first with
        {
            SourceUrl = "https://github.com/frasermolyneux/status-pages/issues/42",
            Updates = [new() { At = Created, Body = "PRIVATE_DRAFT" }],
            Evidence = new("source", "private", Created, "PRIVATE_CONTEXT")
        };
        var projected = Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [first]));
        Assert.Null(projected.Evidence);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(projected, StatusJson.Options));
        var local = first with { Id = 31, SiteId = "xi", Components = ["xi.sitewatch.geo-location-web"] };
        Assert.Equal(31, Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [first, local])).Id);
        Assert.True(Assert.Single(catalog.ProjectIncidents(catalog.Get("xi"), [first, local with { Published = false }])).Published);
        var apiOnly = first with { Components = ["mx.sitewatch.geo-location-api"] };
        var combined = Assert.Single(catalog.ProjectIncidents(catalog.Get("mx"), [apiOnly, local]));
        Assert.Contains("mx.sitewatch.geo-location-api", combined.Components);
        Assert.Contains("mx.sitewatch.geo-location-web", combined.Components);
    }

    [Fact]
    public void IncidentHistoryRetainsEachPublishedStateImpactAndDescription()
    {
        var command = new IncidentCommand("Incident", ["mx.sitewatch.geo-location-web"], Severity.Degraded,
            IncidentState.Investigating, "Investigating", true);
        var first = command.Apply(3, "mx", null, Created);
        var second = (command with { State = IncidentState.Identified, Severity = Severity.Outage, Message = "Impact confirmed" })
            .Apply(3, "mx", first, Created.AddMinutes(1));
        Assert.Equal([Severity.Degraded, Severity.Outage], second.PublicUpdates.Select(update => update.Severity));
        Assert.Equal([IncidentState.Investigating, IncidentState.Identified], second.PublicUpdates.Select(update => update.State));
        Assert.Equal(["Investigating", "Impact confirmed"], second.PublicUpdates.Select(update => update.Body));
    }

    [Fact]
    public void GitHubDescriptionAndSeverityEditsRemainInHistoryWithoutRepeatingOnEverySync()
    {
        var catalog = Catalogue();
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(body: "Original description"), [], catalog, new()));
        Assert.Null(first.PublicUpdates[0].Severity);
        var labels = new[] { "status:incident", "status:published", "site:mx", "workload:geo-location", "severity:degraded" };
        var edited = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Revised description", updatedMinutes: 10),
            [], catalog, new(), [first]));
        Assert.Contains(edited.PublicUpdates, update => update.Body == "Original description");
        Assert.Contains(edited.PublicUpdates, update => update.Body == "Revised description" && update.At == Created.AddMinutes(10));
        Assert.Contains(edited.PublicUpdates, update => update.Severity == Severity.Degraded);
        var repeated = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Revised description", updatedMinutes: 15, title: "Renamed"),
            [], catalog, new(), [edited]));
        Assert.Equal(edited.PublicUpdates, repeated.PublicUpdates);
        Assert.Equal(first.SourceUrl, repeated.ToPublicIncident().SourceUrl);
        var reverted = Assert.Single(GitHubIssueParser.Parse(Issue(labels, body: "Original description", updatedMinutes: 20),
            [], catalog, new(), [repeated]));
        Assert.Equal("Original description", reverted.PublicUpdates[^1].Body);
        Assert.Equal(Created.AddMinutes(20), reverted.PublicUpdates[^1].At);
        var identifiedLabels = labels.Append("state:identified").ToArray();
        var identified = Assert.Single(GitHubIssueParser.Parse(Issue(identifiedLabels, body: "Original description", updatedMinutes: 25),
            [], catalog, new(), [reverted]));
        var editedAfterIdentification = Assert.Single(GitHubIssueParser.Parse(Issue(identifiedLabels, body: "Identified cause", updatedMinutes: 30),
            [], catalog, new(), [identified]));
        Assert.Equal(IncidentState.Identified, editedAfterIdentification.PublicUpdates[^1].State);
        Assert.Equal("Identified cause", editedAfterIdentification.PublicUpdates[^1].Body);
    }

    [Fact]
    public void PublicationUsesTheLatestObservationWithoutMakingPersistedEvidenceAppearFresh()
    {
        var site = Catalogue().Get("mx");
        const string id = "mx.sitewatch.geo-location-web";
        var first = new ProbeObservation("mx", id, "GeoLocation Website", site.Probes[id], "publisher",
            Created, false, 503, 10, "HTTP 503");
        var latest = first with { ObservedAt = Created.AddMinutes(1), Success = true, HttpStatus = 200, Error = null };
        var result = PublicationWorker.LatestTelemetry(site, [latest, first,
            latest with { Site = "xi" }, latest with { Component = "removed-component" }]);
        var observation = Assert.Single(result).Value;
        Assert.Equal(latest.ObservedAt, observation.LastSeen);
        Assert.Equal(0, observation.Failures);
    }

    [Fact]
    public void EvidencePromotionKeepsItsSourcePrivatelyAcrossEditsAndNeverAddsItToPublicUpdates()
    {
        var source = new IncidentEvidence("azure-alert", "PRIVATE_ALERT_ID", Created, "PRIVATE_MONITOR_DIAGNOSTICS");
        var command = new IncidentCommand("Website availability", ["mx.sitewatch.molyneux-me"], Severity.Degraded,
            IncidentState.Investigating, "We are investigating intermittent failures.", true, Evidence: source);
        Assert.Null(command.Validate(Catalogue().Get("mx")));
        var first = command.Apply(5, "mx", null, Created);
        var edited = (command with { Evidence = null, Revision = 1, State = IncidentState.Monitoring })
            .Apply(5, "mx", first, Created.AddMinutes(1));
        Assert.Equal(source, edited.Evidence);
        var publication = JsonSerializer.Serialize(edited.ToPublicIncident(), StatusJson.Options);
        Assert.DoesNotContain("PRIVATE_ALERT_ID", publication);
        Assert.DoesNotContain("PRIVATE_MONITOR_DIAGNOSTICS", publication);
        Assert.NotNull((command with { Evidence = source with { Source = "" } }).Validate(Catalogue().Get("mx")));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(302, true)]
    [InlineData(503, false)]
    public async Task IndependentChecksRetainTheHttpResultAndTargetForOperators(int status, bool success)
    {
        using var client = new HttpClient(new ProbeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var result = await IndependentProbe.ObserveAsync(client, "xi", "xi.sitewatch.forums-web",
            "Community Forums", "https://www.xtremeidiots.com/", default);
        Assert.Equal(success, result.Success);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal("https://www.xtremeidiots.com/", result.Target);
        Assert.Equal("xi.sitewatch.forums-web", result.Component);
        if (success) Assert.Null(result.Error);
        else Assert.Contains("HTTP 503", result.Error);
    }

    [Fact]
    public async Task ProbeNetworkErrorsStayInspectableAndShutdownIsNotRecordedAsAnOutage()
    {
        using var client = new HttpClient(new ProbeHandler((_, _) =>
            throw new HttpRequestException("Name resolution failed.")));
        var result = await IndependentProbe.ObserveAsync(client, "mx", "web", "Website", "https://example.test/", default);
        Assert.False(result.Success);
        Assert.Null(result.HttpStatus);
        Assert.Equal("Name resolution failed.", result.Error);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IndependentProbe.ObserveAsync(client, "mx", "web", "Website", "https://example.test/", canceled.Token));
    }

    [Fact]
    public void HealthDiagnosticsSeparateTheProbeFromIncidentOverridesAndKeepBrandsIsolated()
    {
        const string id = "xi.sitewatch.forums-web";
        var probe = new ProbeObservation("xi", id, "Forums", "https://example.test/", "publisher",
            Created, true, 200, 10, null);
        var publication = new StatusApiResponse
        {
            Components = [new ComponentDto { Id = id, Status = "degraded", LastSampleAt = Created, OpenIncidentIds = [7] }],
            Incidents = [new IncidentDto { Id = 7, Title = "Investigating intermittent failures", Severity = "degraded" }]
        };
        var checks = HealthDiagnostics.Build(Catalogue().Get("xi"), publication,
            [probe, probe with { Site = "mx", Component = "mx.sitewatch.molyneux-me" }]);
        var forums = Assert.Single(checks, check => check.Id == id);
        Assert.Equal("degraded", forums.Status);
        Assert.True(forums.Probe!.Success);
        Assert.True(forums.MatchesPublication);
        Assert.Equal(7, Assert.Single(forums.Incidents).Id);
        Assert.DoesNotContain(checks, check => check.Id.StartsWith("mx.", StringComparison.Ordinal));
        Assert.DoesNotContain(checks, check => check.Id == "xi.server-agent");
        var failure = probe with { Success = false, HttpStatus = 503, Error = "HTTP 503", ObservedAt = Created.AddMinutes(-1) };
        var nextCycle = HealthDiagnostics.Build(Catalogue().Get("xi"), publication,
            [failure, probe with { ObservedAt = Created.AddMinutes(1) }]);
        var recovered = Assert.Single(nextCycle, check => check.Id == id);
        Assert.False(recovered.MatchesPublication);
        Assert.True(recovered.Probe!.Success);
        Assert.Equal(failure, recovered.LastFailure);
    }

    [Fact]
    public void GeoLocationAndTheServerPipelineHaveApprovedBrandSpecificWorkloadMappings()
    {
        var both = GitHubIssueParser.Parse(Issue([
            "status:incident", "status:published", "site:mx", "site:xi", "workload:geo-location", "severity:outage"
        ]), [], Catalogue(), new());
        Assert.Equal(2, both.Count);
        Assert.Equal(["xi.sitewatch.geo-location-web"], Assert.Single(both, record => record.SiteId == "xi").Components);
        var xi = Catalogue().Get("xi");
        foreach (var workload in new[] { "portal-servers-integration", "portal-server-events" })
        {
            var record = Assert.Single(GitHubIssueParser.Parse(Issue([
                "status:incident", "status:published", "site:xi", $"workload:{workload}", "severity:degraded"
            ]), [], Catalogue(), new()));
            Assert.Equal(xi.Workloads[workload], record.Components);
            Assert.All(record.Components, component => Assert.StartsWith("xi.", component));
        }
    }

    [Theory]
    [InlineData("scheduled", "operational", 0)]
    [InlineData("in-progress", "maintenance", 1)]
    [InlineData("completed", "operational", 0)]
    public void MaintenanceIsExplainedOnlyWhileItActuallyOverridesAvailability(string phase, string status, int overrides)
    {
        const string id = "mx.sitewatch.molyneux-me";
        var publication = new StatusApiResponse
        {
            Components = [new ComponentDto { Id = id, Status = status, OpenIncidentIds = [99] }],
            Incidents = [new IncidentDto { Id = 99, Severity = "maintenance", Components = [id] }],
            Maintenance = [new MaintenanceDto { Id = 99, Components = [id], State = phase }]
        };
        var health = Assert.Single(HealthDiagnostics.Build(Catalogue().Get("mx"), publication, []), check => check.Id == id);
        Assert.Empty(health.Incidents);
        Assert.Equal(overrides, health.Maintenance.Count);
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);
    }

    private static JsonElement Issue(string[]? labels = null, string author = "frasermolyneux", bool closed = false,
        string body = "We are investigating.", int updatedMinutes = 5, string title = "Availability incident") =>
        JsonSerializer.SerializeToElement(new
        {
            number = 42,
            title,
            body,
            user = new { login = author },
            created_at = Created,
            updated_at = Created.AddMinutes(updatedMinutes),
            closed_at = closed ? Created.AddMinutes(updatedMinutes) : (DateTimeOffset?)null,
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

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\t")]
    public void GitHubUpdatesExcludeUntrustedCommentsAndInternalNotesAndEscapeHtml(string indentation)
    {
        var comments = new[] {
            JsonSerializer.SerializeToElement(new { body = "UNTRUSTED_COMMENT", user = new { login = "stranger" }, created_at = Created.AddMinutes(1) }),
            JsonSerializer.SerializeToElement(new { body = "state: monitoring\nRecovered <script>unsafe()</script>.", user = new { login = "frasermolyneux" }, created_at = Created.AddMinutes(2) }),
        };
        var record = Assert.Single(GitHubIssueParser.Parse(Issue(
            ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage"],
            body: $"Public description.\n{indentation}<!-- internal -->\nPRIVATE_NOTE\n{indentation}<!-- /internal -->"), comments, Catalogue(), new()));
        Assert.Equal([IncidentState.Investigating, IncidentState.Monitoring], record.PublicUpdates.Select(update => update.State));
        Assert.Equal(IncidentState.Monitoring, record.State);
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
        var resolvedComment = JsonSerializer.SerializeToElement(new
        {
            body = "state: resolved\nPreviously recovered.",
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(4)
        });
        var reopened = Assert.Single(GitHubIssueParser.Parse(Issue(), [resolvedComment], Catalogue(), new()));
        Assert.Equal(IncidentState.Investigating, reopened.State);
        Assert.Null(reopened.ResolvedAt);
    }

    [Fact]
    public void ResolvedLabelsKeepTheirTransitionTimeAfterTitleEditsAndFollowupComments()
    {
        string[] labels = ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage", "state:resolved"];
        var first = Assert.Single(GitHubIssueParser.Parse(Issue(labels), [], Catalogue(), new()));
        var edited = Assert.Single(GitHubIssueParser.Parse(
            Issue(labels, updatedMinutes: 10, title: "Corrected title"), [], Catalogue(), new(), [first]));
        Assert.Equal(first.ResolvedAt, edited.ResolvedAt);
        Assert.Equal(first.Updates.Select(update => update.At), edited.Updates.Select(update => update.At));
        var comment = JsonSerializer.SerializeToElement(new
        {
            body = "Follow-up details after recovery.",
            user = new { login = "frasermolyneux" },
            created_at = Created.AddMinutes(12)
        });
        var followed = Assert.Single(GitHubIssueParser.Parse(
            Issue(labels, updatedMinutes: 15, title: "Corrected title"), [comment], Catalogue(), new(), [edited]));
        Assert.Equal(first.ResolvedAt, followed.ResolvedAt);
        Assert.Equal(3, followed.PublicUpdates.Count);
        Assert.Equal(first.ResolvedAt, followed.PublicUpdates[1].At);
        Assert.Equal(IncidentState.Resolved, followed.PublicUpdates[^1].State);
        Assert.Equal(Created.AddMinutes(12), followed.PublicUpdates[^1].At);
    }

    [Fact]
    public void ActiveStateLabelsRetainTheirHistoryAcrossUnrelatedEditsUntrustedCommentsAndReopening()
    {
        string[] identifiedLabels = ["status:incident", "status:published", "site:mx", "workload:geo-location", "severity:outage", "state:identified"];
        var identified = Assert.Single(GitHubIssueParser.Parse(Issue(identifiedLabels), [], Catalogue(), new()));
        var monitoringLabels = identifiedLabels.Select(label => label == "state:identified" ? "state:monitoring" : label).ToArray();
        var monitoring = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 10), [], Catalogue(), new(), [identified]));
        Assert.Equal([IncidentState.Investigating, IncidentState.Identified, IncidentState.Monitoring],
            monitoring.PublicUpdates.Select(update => update.State));
        var edited = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 15, title: "Corrected title"), [], Catalogue(), new(), [monitoring]));
        Assert.Equal(monitoring.PublicUpdates.Select(update => update.At), edited.PublicUpdates.Select(update => update.At));
        var comment = JsonSerializer.SerializeToElement(new
        {
            body = "state: resolved\nUntrusted update.",
            user = new { login = "stranger" },
            created_at = Created.AddMinutes(18)
        });
        var afterUntrusted = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 20, title: "Corrected title"), [comment], Catalogue(), new(), [edited]));
        Assert.Equal(JsonSerializer.Serialize(edited, StatusJson.Options), JsonSerializer.Serialize(afterUntrusted, StatusJson.Options));
        var closed = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, closed: true, updatedMinutes: 25), [], Catalogue(), new(), [afterUntrusted]));
        var reopened = Assert.Single(GitHubIssueParser.Parse(
            Issue(monitoringLabels, updatedMinutes: 30), [], Catalogue(), new(), [closed]));
        Assert.Equal([IncidentState.Investigating, IncidentState.Identified, IncidentState.Monitoring, IncidentState.Resolved, IncidentState.Monitoring],
            reopened.PublicUpdates.Select(update => update.State));
        Assert.Null(reopened.ResolvedAt);
    }

    [Fact]
    public async Task DisabledGitHubIngestionDoesNotReadTheDatabaseOrContactGitHub()
    {
        var options = new RuntimeOptions { Publisher = true, GitHubEnabled = false, DatabaseConnectionFile = "not-a-real-connection-file" };
        using var api = new GitHubApiFixture();
        await using var database = new PostgresStore(options, NullLogger<PostgresStore>.Instance);
        using var worker = new GitHubIssueWorker(Catalogue(), options, database, api, NullLogger<GitHubIssueWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, api.Requests);
    }

    [Theory]
    [InlineData("site:unknown", "workload:geo-location", "severity:outage", "frasermolyneux")]
    [InlineData("site:xi", "workload:molyneux-me", "severity:outage", "frasermolyneux")]
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
                Site = new SiteInfo { Id = "mx" },
                GeneratedAt = now.AddHours(-1),
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
