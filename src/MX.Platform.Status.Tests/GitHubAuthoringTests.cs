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
            Assert.False(await worker.RunOnceAsync(default));
            api.Issue!["body"] = api.Issue["body"]!.GetValue<string>()
                .Replace(command.Message, "Edited after the lost creation reply", StringComparison.Ordinal);
            Assert.False(await worker.RunOnceAsync(default));
            record = await Read();
            Assert.Equal("review", record.GitHub!.State);
            Assert.Null(record.SourceUrl);
            await Retry();
            Assert.EndsWith("/issues/42", (await Read()).SourceUrl);
            Assert.True(await worker.RunOnceAsync(default));
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
            Assert.False(await worker.RunOnceAsync(default));
            api.Comments[^1]["body"] = api.Comments[^1]["body"]!.GetValue<string>()
                .Replace(command.Message, "Edited before acknowledgment", StringComparison.Ordinal);
            Assert.False(await worker.RunOnceAsync(default));
            Assert.Equal("review", (await Read()).GitHub!.State);
            await Retry();
            Assert.True(await worker.RunOnceAsync(default));
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
            Assert.True(await database.RetryGitHubAsync(job, new(job.Incident.Revision, remote.Version, true),
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
            Assert.False(await worker.RunOnceAsync(default));
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
            Assert.False(await worker.RunOnceAsync(default));
            Assert.Equal("uncertain", (await Read()).GitHub!.State);
            Assert.True(await worker.RunOnceAsync(default));
            record = await Read();
            Assert.Equal("synced", record.GitHub!.State);
            Assert.Equal(1, api.Creates);
            Assert.EndsWith("/issues/42", record.SourceUrl);
            Assert.Equal(record.Id, Assert.Single(await database.ReadGitHubSourcesAsync(options.GitHubRepository, default)).Incident.Id);
            Assert.DoesNotContain("PRIVATE", string.Join('\n', api.Bodies));
            using var importer = new GitHubIssueWorker(Catalogue(), options, database, api, NullLogger<GitHubIssueWorker>.Instance, github);
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
            Assert.False(await worker.RunOnceAsync(default));
            Assert.Equal("conflict", (await Read()).GitHub!.State);
            Assert.Equal("External correction at the same source timestamp", api.Issue["body"]!.GetValue<string>());
            var job = (await database.ReadGitHubReviewAsync("mx", record.Id, default))!;
            var remote = await github.ReadAsync(42, default);
            Assert.False(await database.RetryGitHubAsync(job, new(record.Revision - 1, remote.Version, true), remote, options.GitHubRepository, "operator", default));
            Assert.True(await database.RetryGitHubAsync(job, new(record.Revision, remote.Version, true), remote, options.GitHubRepository, "operator", default));
            api.LoseCommentReply = true;
            Assert.False(await worker.RunOnceAsync(default));
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Single(api.Comments);
            Assert.Equal("External correction at the same source timestamp", api.Issue["body"]!.GetValue<string>());
            await importer.SynchronizeAsync(default);
            record = await Read();
            Assert.Single(record.PublicUpdates, update => update.OperationId == command.OperationId);

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
            Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(record.PublicUpdates, StatusJson.Options));
        }
        finally { await CleanupGitHubFixtureAsync(options, record.Id); }

        async Task<IncidentRecord> Read() =>
            (await database.ReadIncidentsAsync("mx", default)).Single(item => item.Id == record.Id);
    }

    private static async Task CleanupGitHubFixtureAsync(RuntimeOptions options, int id)
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(File.ReadAllText(options.DatabaseConnectionFile!));
        await using var cleanup = source.CreateCommand("""
            DELETE FROM status_github_outbox WHERE incident_id=$1;
            DELETE FROM status_github_sources WHERE repository=$2;
            DELETE FROM status_audit WHERE incident_id=$1;
            DELETE FROM status_evidence WHERE source='github-issues' AND event_id=$2;
            DELETE FROM status_incidents WHERE id=$1;
            """);
        cleanup.Parameters.AddWithValue(id);
        cleanup.Parameters.AddWithValue(options.GitHubRepository);
        await cleanup.ExecuteNonQueryAsync();
    }
}

internal sealed class GitHubWriteFixture : HttpMessageHandler, IHttpClientFactory
{
    public JsonObject? Issue { get; private set; }
    public List<JsonObject> Comments { get; } = [];
    public List<string> Bodies { get; } = [];
    public bool Offline { get; set; }
    public bool LoseCreateReply { get; set; }
    public bool LoseCommentReply { get; set; }
    public int Creates { get; private set; }
    public int Requests { get; private set; }
    public int TokenRequests { get; private set; }
    public JsonObject? TokenScope { get; private set; }
    public Stream? ResponseStream { get; set; }
    private readonly HashSet<string> labels = [];
    private DateTimeOffset time = DateTimeOffset.UtcNow;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    private string NextTime()
    {
        var now = DateTimeOffset.UtcNow;
        return (time = now > time ? now : time.AddTicks(1)).ToString("O");
    }

    public void Touch() => Issue!["updated_at"] = NextTime();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (ResponseStream is not null) return new(HttpStatusCode.OK) { Content = new StreamContent(ResponseStream) };
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
                label!["name"]!.GetValue<string>() != "status:published").Select(label => label!.DeepClone()).ToArray());
            Issue["updated_at"] = NextTime();
            return Reply(Issue["labels"]);
        }
        throw new InvalidOperationException($"Unexpected fixture request: {request.Method} {path}");
    }

    private static JsonArray LabelNodes(JsonNode assigned) => new(assigned.AsArray()
        .Select(label => (JsonNode)new JsonObject { ["name"] = label!.GetValue<string>() }).ToArray());
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
