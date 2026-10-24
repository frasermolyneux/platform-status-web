using System.Text.Json;
using MX.Platform.Status.App.Models;
using Npgsql;
using NpgsqlTypes;

namespace MX.Platform.Status.Service;

public sealed record GitHubRetryCommand(int Revision, string? RemoteVersion, bool Confirmed);

public sealed partial class PostgresStore
{
    public async Task<GitHubJob?> ReadGitHubReviewAsync(string site, int id, CancellationToken ct)
    {
        await using var read = Writer.CreateCommand("""
            SELECT operation_id,i.document::text,command::text,expected_at,attempted,state,expected_version
            FROM status_github_outbox o JOIN status_incidents i ON i.id=o.incident_id
            WHERE o.site_id=$1 AND o.incident_id=$2 AND o.state NOT IN ('synced','draft')
            ORDER BY o.created_at LIMIT 1
            """);
        read.Parameters.AddWithValue(site);
        read.Parameters.AddWithValue(id);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), JsonSerializer.Deserialize<IncidentRecord>(reader.GetString(1), StatusJson.Options)!,
            JsonSerializer.Deserialize<IncidentCommand>(reader.GetString(2), StatusJson.Options)!,
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task<bool> RetryGitHubAsync(GitHubJob job, GitHubRetryCommand request, GitHubIssueSnapshot? remote,
        string repository, string actor, CancellationToken ct)
    {
        await using var connection = await Writer.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var lease = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(814233112)", connection, transaction);
        if (!(bool)(await lease.ExecuteScalarAsync(ct))!) return false;
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814233110)", connection, transaction);
        await gate.ExecuteNonQueryAsync(ct);
        await using var current = new NpgsqlCommand("SELECT revision FROM status_incidents WHERE id=$1 AND site_id=$2 FOR UPDATE", connection, transaction);
        current.Parameters.AddWithValue(job.Incident.Id);
        current.Parameters.AddWithValue(job.Incident.SiteId);
        if ((int?)(await current.ExecuteScalarAsync(ct)) != request.Revision || !request.Confirmed
            || request.RemoteVersion != remote?.Version) return false;
        await using var write = new NpgsqlCommand("""
            UPDATE status_github_outbox SET state='pending',error=NULL,attempted=false,next_attempt_at=now(),expected_at=$1,
                expected_version=$2,predecessor=NULL WHERE operation_id=$3 AND state IN ('conflict','review','uncertain','pending');
            """, connection, transaction);
        write.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)remote?.UpdatedAt ?? DBNull.Value);
        write.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)remote?.Version ?? DBNull.Value);
        write.Parameters.AddWithValue(job.OperationId);
        if (await write.ExecuteNonQueryAsync(ct) != 1) return false;
        if (remote is not null)
            await BindGitHubSourceAsync(connection, transaction, job, (repository, remote.Number, remote.UpdatedAt, remote.Version), ct);
        await RefreshGitHubDeliveryAsync(connection, transaction, job.Incident with
        {
            SourceUrl = remote is null ? job.Incident.SourceUrl : $"https://github.com/{repository}/issues/{remote.Number}"
        }, "pending", ct);
        await using var audit = new NpgsqlCommand("""
            INSERT INTO status_audit(site_id,incident_id,actor,revision) VALUES($1,$2,$3,$4)
            """, connection, transaction);
        audit.Parameters.AddWithValue(job.Incident.SiteId);
        audit.Parameters.AddWithValue(job.Incident.Id);
        audit.Parameters.AddWithValue($"github-review:{actor}");
        audit.Parameters.AddWithValue(request.Revision);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}

public static class GitHubReconciliation
{
    public static Task<GitHubIssueSnapshot?> ReadRemoteAsync(GitHubJob job, RuntimeOptions options, GitHubIncidentClient github, CancellationToken ct) =>
        GitHubIncidentClient.IssueNumber(job.Incident.SourceUrl, options.GitHubRepository) is { } number
            ? ReadKnownAsync(github, number, ct) : github.FindCreatedAsync(job.OperationId, ct);

    private static async Task<GitHubIssueSnapshot?> ReadKnownAsync(GitHubIncidentClient github, int number, CancellationToken ct) =>
        await github.ReadAsync(number, ct);

    public static async Task<IResult> ReviewAsync(string site, int id, PostgresStore database, GitHubIncidentClient github,
        RuntimeOptions options, CancellationToken ct)
    {
        var job = await database.ReadGitHubReviewAsync(site, id, ct);
        if (job is null) return Results.Conflict(new { error = "No pending GitHub change exists. Refresh the workspace." });
        var remote = await ReadRemoteAsync(job, options, github, ct);
        return Results.Ok(new
        {
            operationId = job.OperationId,
            revision = job.Incident.Revision,
            state = job.State,
            localMessage = job.Command.Published ? job.Command.Message : "Withdraw the status publication; private draft text will not be sent.",
            remoteVersion = remote?.Version,
            remote = remote is null ? null : new
            {
                number = remote.Number,
                title = remote.Issue.GetProperty("title").GetString(),
                body = remote.Issue.GetProperty("body").GetString(),
                labels = remote.ReadLabels(),
                updatedAt = remote.UpdatedAt,
                comments = remote.Comments.TakeLast(10).Select(comment => comment.GetProperty("body").GetString())
            }
        });
    }
}
