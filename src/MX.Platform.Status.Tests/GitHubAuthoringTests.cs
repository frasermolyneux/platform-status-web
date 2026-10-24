using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.Service;

namespace MX.Platform.Status.Tests;

public sealed partial class IndependentServiceTests
{
    [PostgresFact]
    public async Task GitHubOutboxReviewsEditedLostWritesAndReusesTheKnownIssueOnExplicitRetry()
    {
        var options = new RuntimeOptions
        {
            DatabaseConnectionFile = Path.Combine(_directory, "connection"),
            GitHubTokenFile = Path.Combine(_directory, "github-token"),
            GitHubRepository = $"status-writer-fixture/test-{Guid.NewGuid():N}"
        };
        await File.WriteAllTextAsync(options.DatabaseConnectionFile, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        await File.WriteAllTextAsync(options.GitHubTokenFile, "test-token");
        await using var database = new PostgresStore(options, NullLogger<PostgresStore>.Instance);
        await database.InitializeAsync(default);
        using var api = new GitHubWriteFixture { LoseCreateReply = true };
        var github = new GitHubIncidentClient(options, Catalogue(), api);
        using var worker = new GitHubOutboxWorker(options, database, github, NullLogger<GitHubOutboxWorker>.Instance);
        using var importer = new GitHubIssueWorker(Catalogue(), options, database, api, NullLogger<GitHubIssueWorker>.Instance, github);
        var command = new IncidentCommand("Lost creation fixture", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "Approved creation", true, Delivery: "github", OperationId: Guid.NewGuid());
        var record = (await database.SaveIncidentAsync("mx", null, command, "operator", default))!;
        try
        {
            Assert.True(await worker.RunOnceAsync(default));
            api.Issue!["body"] = api.Issue["body"]!.GetValue<string>()
                .Replace(command.Message, "Edited after the lost creation reply", StringComparison.Ordinal);
            Assert.True(await worker.RunOnceAsync(default));
            record = await Read();
            Assert.Equal("review", record.GitHub!.State);
            Assert.Null(record.SourceUrl);
            await Retry();
            Assert.EndsWith("/issues/42", (await Read()).SourceUrl);
            Assert.True(await worker.RunOnceAsync(default), (await Read()).GitHub?.Error);
            Assert.Equal(1, api.Creates);
            Assert.Single(api.Comments);
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Equal("Approved creation", Assert.Single(record.PublicUpdates).Body);
            Assert.Equal("synced", record.GitHub!.State);

            api.Comments[0]["body"] = api.Comments[0]["body"]!.GetValue<string>()
                .Replace(command.Message, "Maintainer correction", StringComparison.Ordinal);
            api.Touch();
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Equal("Maintainer correction", record.PublicUpdates[^1].Body);
            var revision = record.Revision;
            await importer.SynchronizeAsync(default);
            Assert.Equal(revision, (await Read()).Revision);

            command = command with
            {
                State = IncidentState.Monitoring,
                Message = "Approved recovery",
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            };
            record = (await database.SaveIncidentAsync("mx", record.Id, command, "operator", default))!;
            api.LoseCommentReply = true;
            Assert.True(await worker.RunOnceAsync(default));
            api.Comments[^1]["body"] = api.Comments[^1]["body"]!.GetValue<string>()
                .Replace(command.Message, "Edited before acknowledgment", StringComparison.Ordinal);
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Equal("review", (await Read()).GitHub!.State);
            await Retry();
            Assert.True(await worker.RunOnceAsync(default), (await Read()).GitHub?.Error);
            Assert.Equal(1, api.Creates);
            Assert.Equal(3, api.Comments.Count);
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Equal("synced", record.GitHub!.State);
            Assert.Equal(command.Message, record.PublicUpdates[^1].Body);
            Assert.DoesNotContain(record.PublicUpdates, update => update.Body == "Edited before acknowledgment");
            Assert.Single(record.PublicUpdates, update => update.OperationId == command.OperationId);
        }
        finally { await CleanupGitHubFixtureAsync(options, record.Id); }

        async Task<IncidentRecord> Read() =>
            (await database.ReadIncidentsAsync("mx", default)).Single(item => item.Id == record.Id);

        async Task Retry()
        {
            var job = (await database.ReadGitHubReviewAsync("mx", record.Id, default))!;
            var remote = await github.ReadAsync(42, default);
            Assert.True(await database.RetryGitHubAsync(job, new(job.Incident.Revision, remote.Version, true, job.OperationId),
                remote, options.GitHubRepository, "operator", default));
        }
    }

    [PostgresFact]
    public async Task GitHubOutboxPublishesLocallyReconcilesLostRepliesAndProtectsRemoteEditsAndPrivateDrafts()
    {
        var options = new RuntimeOptions
        {
            DatabaseConnectionFile = Path.Combine(_directory, "connection"),
            GitHubTokenFile = Path.Combine(_directory, "github-token"),
            GitHubRepository = $"status-writer-fixture/test-{Guid.NewGuid():N}"
        };
        await File.WriteAllTextAsync(options.DatabaseConnectionFile, Environment.GetEnvironmentVariable("STATUS_TEST_CONNECTION"));
        await File.WriteAllTextAsync(options.GitHubTokenFile, "test-token");
        await using var database = new PostgresStore(options, NullLogger<PostgresStore>.Instance);
        await database.InitializeAsync(default);
        using var api = new GitHubWriteFixture();
        var github = new GitHubIncidentClient(options, Catalogue(), api);
        using var worker = new GitHubOutboxWorker(options, database, github, NullLogger<GitHubOutboxWorker>.Instance);
        var command = new IncidentCommand("Authoring fixture", ["mx.sitewatch.geo-location-web"], Severity.Outage,
            IncidentState.Investigating, "PRIVATE_DRAFT", false,
            Evidence: new("fixture", "PRIVATE_EVIDENCE", DateTimeOffset.UtcNow, "PRIVATE_CONTEXT"),
            Delivery: "github", OperationId: Guid.NewGuid());
        var record = (await database.SaveIncidentAsync("mx", null, command, "operator", default))!;
        try
        {
            Assert.False(await worker.RunOnceAsync(default));
            Assert.Equal(0, api.Requests);
            command = command with { Published = true, Message = "Approved update", Revision = record.Revision, OperationId = Guid.NewGuid() };
            record = (await database.SaveIncidentAsync("mx", record.Id, command, "operator", default))!;
            var repeated = await database.SaveIncidentAsync("mx", record.Id, command, "operator", default);
            Assert.Equal(record.Id, repeated!.Id);
            Assert.Equal(record.Revision, repeated.Revision);
            Assert.Null(await database.SaveIncidentAsync("mx", record.Id, command with { Message = "Different request" }, "operator", default));
            Assert.Null(await database.SaveIncidentAsync("xi", record.Id, command, "operator", default));
            Assert.Null(await database.SaveIncidentAsync("mx", null, command, "operator", default));
            Assert.Null(await database.SaveIncidentAsync("mx", record.Id + 100000, command, "operator", default));
            Assert.Null(await database.SaveIncidentAsync("mx", record.Id,
                command with { Delivery = "local", Revision = record.Revision, OperationId = null }, "operator", default));
            api.Offline = true;
            Assert.True(await worker.RunOnceAsync(default));
            Assert.True((await Read()).Published);
            Assert.Equal("pending", (await Read()).GitHub!.State);
            await using (var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(options.DatabaseConnectionFile)))
            {
                await using var retry = source.CreateCommand("UPDATE status_github_outbox SET next_attempt_at=now() WHERE incident_id=$1");
                retry.Parameters.AddWithValue(record.Id);
                await retry.ExecuteNonQueryAsync();
            }
            api.Offline = false;
            api.LoseCreateReply = true;
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Equal("uncertain", (await Read()).GitHub!.State);
            Assert.True(await worker.RunOnceAsync(default));
            record = await Read();
            Assert.Equal("synced", record.GitHub!.State);
            Assert.Equal(1, api.Creates);
            Assert.EndsWith("/issues/42", record.SourceUrl);
            Assert.Equal(record.Id, Assert.Single(await database.ReadGitHubSourcesAsync(options.GitHubRepository, default)).Incident.Id);
            Assert.DoesNotContain("PRIVATE", string.Join('\n', api.Bodies));
            using var importer = new GitHubIssueWorker(Catalogue(), options, database, api, NullLogger<GitHubIssueWorker>.Instance, github);
            api.Issue!["labels"]!.AsArray().Add(new JsonObject { ["name"] = "site:xi" });
            api.Issue["labels"]!.AsArray().Add(new JsonObject { ["name"] = "component:xi.sitewatch.geo-location-web" });
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Contains(record.Updates, update => update.Body == "PRIVATE_DRAFT");
            Assert.Single(record.PublicUpdates);
            Assert.Equal(command.OperationId, record.PublicUpdates[0].OperationId);

            command = command with
            {
                Message = "Recovery observed",
                State = IncidentState.Monitoring,
                Severity = Severity.Degraded,
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            };
            record = (await database.SaveIncidentAsync("mx", record.Id, command, "operator", default))!;
            api.Issue!["body"] = "External correction at the same source timestamp";
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Equal("conflict", (await Read()).GitHub!.State);
            Assert.Equal("External correction at the same source timestamp", api.Issue["body"]!.GetValue<string>());
            var job = (await database.ReadGitHubReviewAsync("mx", record.Id, default))!;
            var remote = await github.ReadAsync(42, default);
            Assert.False(await database.RetryGitHubAsync(job, new(record.Revision - 1, remote.Version, true, job.OperationId), remote, options.GitHubRepository, "operator", default));
            Assert.False(await database.RetryGitHubAsync(job, new(record.Revision, remote.Version, true, Guid.NewGuid()), remote, options.GitHubRepository, "operator", default));
            Assert.Equal("conflict", (await Read()).GitHub!.State);
            Assert.True(await database.RetryGitHubAsync(job, new(record.Revision, remote.Version, true, job.OperationId), remote, options.GitHubRepository, "operator", default));
            api.LoseCommentReply = true;
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Equal("uncertain", (await Read()).GitHub!.State);
            Assert.True(await worker.RunOnceAsync(default), (await Read()).GitHub?.Error);
            Assert.Single(api.Comments);
            Assert.Equal("External correction at the same source timestamp", api.Issue["body"]!.GetValue<string>());
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Single(record.PublicUpdates, update => update.OperationId == command.OperationId);

            var schedule = DateTimeOffset.UtcNow.AddMinutes(-1);
            var maintenance = command with
            {
                Message = "Maintenance announced",
                Severity = Severity.Maintenance,
                ScheduledStart = schedule,
                ScheduledEnd = schedule.AddHours(1),
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            };
            record = (await database.SaveIncidentAsync("mx", record.Id, maintenance, "operator", default))!;
            var peer = (await database.ReadGitHubSourcesAsync(options.GitHubRepository, default)).Single(source => source.SiteId == "xi").Incident;
            Assert.Equal(schedule, peer.ScheduledStart);
            Assert.Equal(maintenance.ScheduledEnd, peer.ScheduledEnd);
            Assert.NotNull(peer.ToMaintenance(DateTimeOffset.UtcNow));
            var adjusted = maintenance with
            {
                Components = ["xi.sitewatch.geo-location-web", "xi.sitewatch.server-events"],
                Message = "Schedule adjusted",
                ScheduledStart = schedule.AddMinutes(-1),
                Revision = peer.Revision,
                OperationId = Guid.NewGuid()
            };
            peer = (await database.SaveIncidentAsync("xi", peer.Id, adjusted, "operator", default))!;
            Assert.True(await worker.RunOnceAsync(default));
            record = await Read();
            Assert.Equal("pending", record.GitHub!.State);
            Assert.Equal("xi", record.GitHub.SiteId);
            Assert.Equal(peer.Id, record.GitHub.IncidentId);
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Equal(adjusted.ScheduledStart, record.ScheduledStart);
            Assert.Equal("Schedule adjusted", record.PublicUpdates[^1].Body);
            Assert.True(await worker.RunOnceAsync(default));
            record = await Read();
            Assert.Equal("synced", record.GitHub!.State);

            await VerifySiteWithdrawalAsync(importer, maintenance);
            await VerifyPrivatePeerScopeAsync(importer, maintenance);
            var labels = api.Issue!["labels"]!.AsArray();
            labels.Remove(labels.Single(label => label!["name"]!.GetValue<string>() == "status:incident"));
            api.Touch();
            await importer.SynchronizeAsync(default);
            Assert.False((await Read()).Published);
            labels.Add(new JsonObject { ["name"] = "status:incident" });
            await importer.SynchronizeAsync(default);
            Assert.True((await Read()).Published);
            var approvedBody = api.Issue["body"]!.GetValue<string>();
            api.Issue["body"] = $"{GitHubIncidentDocument.Marker(Guid.NewGuid())}\n<!-- mxstatus-update:invalid -->\nUnapproved malformed metadata";
            labels.Remove(labels.Single(label => label!["name"]!.GetValue<string>() == "status:published"));
            api.Touch();
            await importer.SynchronizeAsync(default);
            Assert.False((await Read()).Published);
            api.Issue["body"] = approvedBody;
            labels.Add(new JsonObject { ["name"] = "status:published" });
            api.Touch();
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.True(record.Published);

            api.Issue!["labels"]!.AsArray().Single(label => label!["name"]!.GetValue<string>() == "status:published")!["name"] = "Status:Published";
            api.Touch();
            await importer.SynchronizeAsync(default);
            record = await Read();
            command = command with
            {
                Message = "PRIVATE_WITHDRAWAL",
                Published = false,
                Title = "PRIVATE_TITLE",
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            };
            record = (await database.SaveIncidentAsync("mx", record.Id, command, "operator", default))!;
            Assert.True(await worker.RunOnceAsync(default));
            Assert.DoesNotContain("PRIVATE", string.Join('\n', api.Bodies));
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.False(record.Published);
            Assert.Equal("draft", record.GitHub!.State);
            Assert.Equal("PRIVATE_TITLE", record.Title);
            Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(record.PublicUpdates, StatusJson.Options));
            await VerifyStaleDraftImportAsync(importer);
            api.Issue!["labels"]!.AsArray().Add(new JsonObject { ["name"] = "status:published" });
            api.Touch();
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.True(record.Published);
            Assert.Equal("synced", record.GitHub!.State);
            Assert.Equal("Authoring fixture", record.Title);
            Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(record.PublicUpdates, StatusJson.Options));
            await VerifyIndependentGitHubJobsDrainAsync(database, options);
        }
        finally { await CleanupGitHubFixtureAsync(options, record.Id); }

        async Task<IncidentRecord> Read() =>
            (await database.ReadIncidentsAsync("mx", default)).Single(item => item.Id == record.Id);

        async Task VerifySiteWithdrawalAsync(GitHubIssueWorker importer, IncidentCommand maintenance)
        {
            var labels = api.Issue!["labels"]!.AsArray();
            var removed = labels.Where(label => label!["name"]!.GetValue<string>() == "site:xi"
                || label!["name"]!.GetValue<string>().StartsWith("component:xi.", StringComparison.Ordinal)).ToArray();
            foreach (var label in removed) labels.Remove(label);
            api.Touch();
            await importer.SynchronizeAsync(default);
            var peer = (await database.ReadGitHubSourcesAsync(options.GitHubRepository, default))
                .Single(source => source.SiteId == "xi").Incident;
            Assert.False(peer.Published);
            Assert.True(peer.GitHubSiteWithdrawn);
            Assert.Contains("xi.sitewatch.server-events", peer.Components);
            record = await Read();
            api.Offline = true;
            record = (await database.SaveIncidentAsync("mx", record.Id, maintenance with
            {
                Message = "Approved MX-only update",
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            }, "operator", default))!;
            peer = (await database.ReadIncidentsAsync("xi", default)).Single(item => item.Id == peer.Id);
            Assert.False(peer.Published);
            Assert.True(peer.GitHubSiteWithdrawn);
            api.Offline = false;
            Assert.True(await worker.RunOnceAsync(default));
            await importer.SynchronizeAsync(default);
            labels = api.Issue!["labels"]!.AsArray();
            foreach (var label in removed) labels.Add(label);
            api.Touch();
            await importer.SynchronizeAsync(default);
            peer = (await database.ReadIncidentsAsync("xi", default)).Single(item => item.Id == peer.Id);
            Assert.True(peer.Published);
            Assert.False(peer.GitHubSiteWithdrawn);
            record = await Read();
        }

        async Task VerifyStaleDraftImportAsync(GitHubIssueWorker importer)
        {
            var sources = await database.ReadGitHubSourcesAsync(options.GitHubRepository, default);
            var remote = await github.ReadAsync(42, default);
            var incoming = GitHubIssueParser.Parse(remote.Issue, remote.Comments, Catalogue(), options,
                sources.Select(source => source.Incident).ToArray()).Single(item => item.SiteId == "mx");
            record = (await database.SaveIncidentAsync("mx", record.Id, command with
            {
                Message = "PRIVATE_NEWER_NOTE",
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            }, "operator", default))!;
            Assert.True(await worker.RunOnceAsync(default));
            var revision = record.Revision;
            await using (var lease = await database.TryPublisherLockAsync(default)
                ?? throw new InvalidOperationException("Fixture publisher is busy."))
                await database.SaveGitHubIncidentAsync(lease, options.GitHubRepository, 42, remote.UpdatedAt, incoming, default);
            record = await Read();
            Assert.Equal(revision, record.Revision);
            Assert.Contains(record.Updates, update => update.Body == "PRIVATE_NEWER_NOTE");
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Contains(record.Updates, update => update.Body == "PRIVATE_NEWER_NOTE");
            Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(record.PublicUpdates, StatusJson.Options));
        }

        async Task VerifyPrivatePeerScopeAsync(GitHubIssueWorker importer, IncidentCommand maintenance)
        {
            var peer = (await database.ReadGitHubSourcesAsync(options.GitHubRepository, default))
                .Single(source => source.SiteId == "xi").Incident;
            peer = (await database.SaveIncidentAsync("xi", peer.Id, maintenance with
            {
                Components = ["xi.sitewatch.geo-location-web"],
                Revision = peer.Revision,
                OperationId = Guid.NewGuid()
            }, "operator", default))!;
            Assert.True(await worker.RunOnceAsync(default));
            await importer.SynchronizeAsync(default);
            peer = (await database.ReadIncidentsAsync("xi", default)).Single(item => item.Id == peer.Id);
            peer = (await database.SaveIncidentAsync("xi", peer.Id, maintenance with
            {
                Title = "PRIVATE_XI_TITLE",
                Message = "PRIVATE_XI_SCOPE",
                Components = ["xi.sitewatch.server-events"],
                Published = false,
                Revision = peer.Revision,
                OperationId = Guid.NewGuid()
            }, "operator", default))!;
            Assert.True(await worker.RunOnceAsync(default));
            await importer.SynchronizeAsync(default);
            peer = (await database.ReadIncidentsAsync("xi", default)).Single(item => item.Id == peer.Id);
            var impacts = peer.ImpactHistory.Count;
            record = await Read();
            record = (await database.SaveIncidentAsync("mx", record.Id, maintenance with
            {
                Message = "Approved MX republication",
                Revision = record.Revision,
                OperationId = Guid.NewGuid()
            }, "operator", default))!;
            peer = (await database.ReadIncidentsAsync("xi", default)).Single(item => item.Id == peer.Id);
            Assert.False(peer.Published);
            Assert.Equal("PRIVATE_XI_TITLE", peer.Title);
            Assert.Equal(impacts, peer.ImpactHistory.Count);
            Assert.True(await worker.RunOnceAsync(default));
            await importer.SynchronizeAsync(default);
            peer = (await database.ReadIncidentsAsync("xi", default)).Single(item => item.Id == peer.Id);
            Assert.True(peer.Published);
            Assert.Equal(["xi.sitewatch.geo-location-web"], peer.Components);
            record = await Read();
        }
    }

    private static async Task VerifyIndependentGitHubJobsDrainAsync(PostgresStore database, RuntimeOptions options)
    {
        options = options with { GitHubRepository = $"status-writer-fixture/drain-{Guid.NewGuid():N}" };
        using var api = new GitHubWriteFixture { TransientFailures = 2 };
        using var worker = new GitHubOutboxWorker(options, database, new(options, Catalogue(), api),
            NullLogger<GitHubOutboxWorker>.Instance);
        var queued = new List<IncidentRecord>();
        try
        {
            for (var index = 0; index < 3; index++)
                queued.Add((await database.SaveIncidentAsync("mx", null,
                    new($"Independent incident {index}", ["mx.sitewatch.geo-location-web"], Severity.Outage,
                        IncidentState.Investigating, "Approved update", true, Delivery: "github", OperationId: Guid.NewGuid()),
                    "operator", default))!);
            await worker.DrainAsync(default);
            var records = (await database.ReadIncidentsAsync("mx", default)).ToDictionary(record => record.Id);
            Assert.Equal(["pending", "pending", "synced"], queued.Select(record => records[record.Id].GitHub!.State));
            Assert.Equal(1, api.Creates);
        }
        finally
        {
            foreach (var record in queued) await CleanupGitHubFixtureAsync(options, record.Id);
        }
    }

    private static async Task CleanupGitHubFixtureAsync(RuntimeOptions options, int id)
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(options.DatabaseConnectionFile!));
        var ids = new List<int> { id };
        await using (var read = source.CreateCommand("SELECT id FROM status_incidents WHERE document->>'sourceUrl' LIKE $1"))
        {
            read.Parameters.AddWithValue($"https://github.com/{options.GitHubRepository}/issues/%");
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        }
        await using var cleanup = source.CreateCommand("""
            DELETE FROM status_github_outbox WHERE incident_id=ANY(@ids);
            DELETE FROM status_github_sources WHERE repository=@repository;
            DELETE FROM status_audit WHERE incident_id=ANY(@ids);
            DELETE FROM status_evidence WHERE source='github-issues' AND event_id=@repository;
            DELETE FROM status_incidents WHERE id=ANY(@ids);
            """);
        cleanup.Parameters.AddWithValue("ids", ids.Distinct().ToArray());
        cleanup.Parameters.AddWithValue("repository", options.GitHubRepository);
        await cleanup.ExecuteNonQueryAsync();
    }
}

internal sealed class GitHubWriteFixture : HttpMessageHandler, IHttpClientFactory
{
    public JsonObject? Issue { get; private set; }
    public List<JsonObject> Comments { get; } = [];
    public List<string> Bodies { get; } = [];
    public bool Offline { get; set; }
    public int TransientFailures { get; set; }
    public bool LoseCreateReply { get; set; }
    public bool LoseCommentReply { get; set; }
    public int Creates { get; private set; }
    public int Requests { get; private set; }
    public int TokenRequests { get; private set; }
    public JsonObject? TokenScope { get; private set; }
    public Stream? ResponseStream { get; set; }
    private readonly HashSet<string> labels = [];
    private DateTimeOffset time = DateTimeOffset.MinValue;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    private string NextTime()
    {
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        return (time = now > time ? now : time.AddMicroseconds(1)).ToString("O");
    }

    public void Touch() => Issue!["updated_at"] = NextTime();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (ResponseStream is not null) return new(HttpStatusCode.OK) { Content = new StreamContent(ResponseStream) };
        if (TransientFailures > 0)
        {
            TransientFailures--;
            return new(HttpStatusCode.ServiceUnavailable);
        }
        if (Offline) return new(HttpStatusCode.ServiceUnavailable);
        var path = string.Join('/', request.RequestUri!.AbsolutePath.Split('/').Skip(4));
        JsonObject? body = null;
        if (request.Content is not null)
        {
            var text = await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(text);
            body = JsonNode.Parse(text)!.AsObject();
        }
        if (request.RequestUri.AbsolutePath == "/app/installations/100/access_tokens")
        {
            TokenRequests++;
            TokenScope = body;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Reply(new { token = "fixture-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) });
        }
        if (path == "labels" && request.Method == HttpMethod.Get)
            return Reply(labels.Select(name => new { name }));
        if (path == "labels" && request.Method == HttpMethod.Post)
        {
            labels.Add(body!["name"]!.GetValue<string>());
            return Reply(new { name = body["name"]!.GetValue<string>() });
        }
        if (path == "issues" && request.Method == HttpMethod.Get) return Reply(Issue is null ? [] : new[] { Issue });
        if (path == "issues" && request.Method == HttpMethod.Post)
        {
            Creates++;
            var at = NextTime();
            Issue = new JsonObject
            {
                ["number"] = 42,
                ["title"] = body!["title"]!.DeepClone(),
                ["body"] = body["body"]!.DeepClone(),
                ["labels"] = LabelNodes(body["labels"]!),
                ["state"] = "open",
                ["closed_at"] = null,
                ["created_at"] = at,
                ["updated_at"] = at,
                ["comments"] = 0,
                ["user"] = new JsonObject { ["login"] = "status-writer-fixture" }
            };
            if (LoseCreateReply) { LoseCreateReply = false; throw new HttpRequestException("Injected lost create reply."); }
            return Reply(Issue);
        }
        if (path == "issues/42/comments" && request.Method == HttpMethod.Get) return Reply(Comments);
        if (path == "issues/42" && request.Method == HttpMethod.Get) return Reply(Issue);
        if (path == "issues/42" && request.Method == HttpMethod.Patch)
        {
            if (body!["title"] is { } title) Issue!["title"] = title.DeepClone();
            if (body["labels"] is { } assigned) Issue!["labels"] = LabelNodes(assigned);
            if (body["state"] is { } state)
            {
                Issue!["state"] = state.DeepClone();
                Issue["closed_at"] = state.GetValue<string>() == "closed" ? NextTime() : null;
            }
            Issue!["updated_at"] = NextTime();
            return Reply(Issue);
        }
        if (path == "issues/42/comments" && request.Method == HttpMethod.Post)
        {
            var at = NextTime();
            var comment = new JsonObject
            {
                ["id"] = Comments.Count + 1,
                ["body"] = body!["body"]!.DeepClone(),
                ["created_at"] = at,
                ["updated_at"] = at,
                ["user"] = new JsonObject { ["login"] = "status-writer-fixture" }
            };
            Comments.Add(comment);
            Issue!["comments"] = Comments.Count;
            Issue["updated_at"] = at;
            if (LoseCommentReply) { LoseCommentReply = false; throw new HttpRequestException("Injected lost comment reply."); }
            return Reply(comment);
        }
        if (path == "issues/42/labels/status%3Apublished" && request.Method == HttpMethod.Delete)
        {
            Issue!["labels"] = new JsonArray(Issue["labels"]!.AsArray().Where(label =>
                !label!["name"]!.GetValue<string>().Equals("status:published", StringComparison.OrdinalIgnoreCase))
                .Select(label => label!.DeepClone()).ToArray());
            Issue["updated_at"] = NextTime();
            return Reply(Issue["labels"]);
        }
        throw new InvalidOperationException($"Unexpected fixture request: {request.Method} {path}");
    }

    private static JsonArray LabelNodes(JsonNode assigned) => new(assigned.AsArray()
        .Select(label => (JsonNode)new JsonObject { ["name"] = label!.GetValue<string>() }).ToArray());
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
