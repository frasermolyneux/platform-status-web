using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using MX.Platform.Status.App.Models;

namespace MX.Platform.Status.Service;

public sealed record GitHubJob(Guid OperationId, IncidentRecord Incident, IncidentCommand Command,
    DateTimeOffset? ExpectedAt, bool Attempted, string State, string? ExpectedVersion = null);

public sealed partial class PostgresStore
{
    private static async Task<(bool Found, IncidentRecord? Record)> ReadRepeatedGitHubCommandAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid operation, string site, int? requestedId,
        IncidentCommand request, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("""
            SELECT o.command::text,i.document::text,o.requested_incident_id FROM status_github_outbox o
            JOIN status_incidents i ON i.id=o.incident_id WHERE operation_id=$1
            """, connection, transaction);
        read.Parameters.AddWithValue(operation);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, null);
        var previous = JsonSerializer.Deserialize<IncidentCommand>(reader.GetString(0), StatusJson.Options)!;
        var record = JsonSerializer.Deserialize<IncidentRecord>(reader.GetString(1), StatusJson.Options)!;
        var target = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var same = record.SiteId == site && target == requestedId && JsonSerializer.Serialize(previous, StatusJson.Options)
            == JsonSerializer.Serialize(request with { Evidence = null }, StatusJson.Options);
        return (true, same ? record : null);
    }

    private static async Task QueueGitHubAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IncidentRecord record, int? requestedId, IncidentCommand request, string actor, CancellationToken ct)
    {
        await using var queue = new NpgsqlCommand("""
            INSERT INTO status_github_outbox(operation_id,incident_id,site_id,revision,command,expected_at,state,expected_version,requested_incident_id,predecessor)
            VALUES($1,$2,$3,$4,$5,(SELECT max(updated_at) FROM status_github_sources WHERE incident_id=$2),$6,$7,$9,
                (SELECT o.operation_id FROM status_github_outbox o JOIN status_incidents i ON i.id=o.incident_id
                 WHERE (o.incident_id=$2 OR ($8<>'' AND i.document->>'sourceUrl'=$8))
                 AND o.state NOT IN ('synced','draft') ORDER BY o.created_at DESC LIMIT 1))
            """, connection, transaction);
        queue.Parameters.AddWithValue(request.OperationId!.Value);
        queue.Parameters.AddWithValue(record.Id);
        queue.Parameters.AddWithValue(record.SiteId);
        queue.Parameters.AddWithValue(record.Revision);
        queue.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(request with { Evidence = null }, StatusJson.Options));
        queue.Parameters.AddWithValue(record.GitHub!.State);
        queue.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)record.GitHubVersion ?? DBNull.Value);
        queue.Parameters.AddWithValue(record.SourceUrl ?? "");
        queue.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)requestedId ?? DBNull.Value);
        await queue.ExecuteNonQueryAsync(ct);
        if (record.SourceUrl is null)
        {
            await RefreshGitHubDeliveryAsync(connection, transaction, record, "draft", ct);
            return;
        }
        await using var peers = new NpgsqlCommand("""
            SELECT document::text FROM status_incidents
            WHERE document->>'sourceUrl'=$1 AND id<>$2 FOR UPDATE
            """, connection, transaction);
        peers.Parameters.AddWithValue(record.SourceUrl);
        peers.Parameters.AddWithValue(record.Id);
        var records = new List<IncidentRecord>();
        await using (var reader = await peers.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                records.Add(JsonSerializer.Deserialize<IncidentRecord>(reader.GetString(0), StatusJson.Options)!);
        foreach (var peer in records)
            await WriteIncidentAsync(connection, transaction, (peer with
            {
                Title = record.Title,
                Severity = record.Severity,
                State = record.State,
                Published = record.Published,
                ResolvedAt = record.ResolvedAt,
                ScheduledStart = record.ScheduledStart,
                ScheduledEnd = record.ScheduledEnd,
                GitHub = record.GitHub,
                PublicUpdates = record.PublicUpdates,
                Revision = peer.Revision + 1
            }).ObserveImpact(peer, DateTimeOffset.UtcNow), actor, ct);
        await RefreshGitHubDeliveryAsync(connection, transaction, record, "draft", ct);
    }

    public Task<PublisherLease?> TryGitHubLockAsync(CancellationToken ct) => TryLockAsync(814233112, ct);

    public static async Task<GitHubJob?> ReadGitHubJobAsync(PublisherLease lease, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("""
            SELECT o.operation_id,i.document::text,o.command::text,
                COALESCE((SELECT remote_at FROM status_github_outbox WHERE operation_id=o.predecessor AND state='synced'),o.expected_at),
                o.attempted,o.state,
                COALESCE((SELECT remote_version FROM status_github_outbox WHERE operation_id=o.predecessor AND state='synced'),o.expected_version)
            FROM status_github_outbox o JOIN status_incidents i ON i.id=o.incident_id
            WHERE o.state IN ('pending','uncertain') AND o.next_attempt_at<=now() AND NOT EXISTS(
                SELECT FROM status_github_outbox p JOIN status_incidents peer ON peer.id=p.incident_id
                WHERE (p.incident_id=o.incident_id OR
                    (i.document->>'sourceUrl' IS NOT NULL AND peer.document->>'sourceUrl'=i.document->>'sourceUrl'))
                AND p.created_at<o.created_at AND p.state NOT IN ('synced','draft'))
            ORDER BY o.created_at,o.revision LIMIT 1
            """, lease.Connection);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), JsonSerializer.Deserialize<IncidentRecord>(reader.GetString(1), StatusJson.Options)!,
            JsonSerializer.Deserialize<IncidentCommand>(reader.GetString(2), StatusJson.Options)!,
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public static async Task MarkGitHubAttemptAsync(PublisherLease lease, Guid operation, CancellationToken ct)
    {
        await using var write = new NpgsqlCommand("UPDATE status_github_outbox SET attempted=true WHERE operation_id=$1", lease.Connection);
        write.Parameters.AddWithValue(operation);
        await write.ExecuteNonQueryAsync(ct);
    }

    public static async Task SetGitHubProblemAsync(PublisherLease lease, GitHubJob job, string state, string error, CancellationToken ct)
    {
        await using var transaction = await lease.Connection.BeginTransactionAsync(ct);
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814233110)", lease.Connection, transaction);
        await gate.ExecuteNonQueryAsync(ct);
        await using var write = new NpgsqlCommand("""
            UPDATE status_github_outbox SET state=$1,error=$2,
                next_attempt_at=now()+CASE WHEN $1='pending' THEN interval '30 seconds' ELSE interval '0 seconds' END
            WHERE operation_id=$3
            """, lease.Connection, transaction);
        write.Parameters.AddWithValue(state);
        write.Parameters.AddWithValue(error);
        write.Parameters.AddWithValue(job.OperationId);
        await write.ExecuteNonQueryAsync(ct);
        await RefreshGitHubDeliveryAsync(lease.Connection, transaction, job.Incident, state, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task SetGitHubDeliveryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IncidentRecord incident, GitHubDelivery delivery, CancellationToken ct)
    {
        await using var write = new NpgsqlCommand("""
            UPDATE status_incidents SET document=jsonb_set(document,'{gitHub}',$1::jsonb)
            WHERE id=$2 OR ($3<>'' AND document->>'sourceUrl'=$3)
            """, connection, transaction);
        write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(delivery, StatusJson.Options));
        write.Parameters.AddWithValue(incident.Id);
        write.Parameters.AddWithValue(incident.SourceUrl ?? "");
        await write.ExecuteNonQueryAsync(ct);
    }

    private static async Task RefreshGitHubDeliveryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IncidentRecord incident, string fallback, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("""
            SELECT o.state,o.error,o.site_id,o.incident_id FROM status_github_outbox o
            JOIN status_incidents i ON i.id=o.incident_id
            WHERE (o.incident_id=$1 OR ($2<>'' AND i.document->>'sourceUrl'=$2))
                AND o.state NOT IN ('synced','draft')
            ORDER BY o.created_at,o.revision LIMIT 1
            """, connection, transaction);
        read.Parameters.AddWithValue(incident.Id);
        read.Parameters.AddWithValue(incident.SourceUrl ?? "");
        var delivery = new GitHubDelivery(fallback);
        await using (var reader = await read.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                delivery = new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        await SetGitHubDeliveryAsync(connection, transaction, incident, delivery, ct);
    }

    public async Task CompleteGitHubJobAsync(PublisherLease lease, GitHubJob job, string repository, int number,
        DateTimeOffset remoteAt, string remoteVersion, CancellationToken ct)
    {
        await using var transaction = await lease.Connection.BeginTransactionAsync(ct);
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814233110)", lease.Connection, transaction);
        await gate.ExecuteNonQueryAsync(ct);
        await BindGitHubSourceAsync(lease.Connection, transaction, job, (repository, number, remoteAt, remoteVersion), ct);
        await using var acknowledge = new NpgsqlCommand("""
            UPDATE status_github_outbox SET state='synced',remote_at=$2,remote_version=$3,error=NULL WHERE operation_id=$1
            """, lease.Connection, transaction);
        acknowledge.Parameters.AddWithValue(job.OperationId);
        acknowledge.Parameters.AddWithValue(remoteAt);
        acknowledge.Parameters.AddWithValue(remoteVersion);
        await acknowledge.ExecuteNonQueryAsync(ct);
        await using var activate = new NpgsqlCommand("""
            UPDATE status_github_outbox SET state='pending' WHERE incident_id=$1 AND state='draft'
                AND revision=(SELECT max(revision) FROM status_github_outbox WHERE incident_id=$1)
                AND revision>(SELECT revision FROM status_github_outbox WHERE operation_id=$2)
            """, lease.Connection, transaction);
        activate.Parameters.AddWithValue(job.Incident.Id);
        activate.Parameters.AddWithValue(job.OperationId);
        await activate.ExecuteNonQueryAsync(ct);
        var delivery = job.Command.Published ? "synced" : "draft";
        await RefreshGitHubDeliveryAsync(lease.Connection, transaction,
            job.Incident with { SourceUrl = $"https://github.com/{repository}/issues/{number}" },
            delivery, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task BindGitHubSourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        GitHubJob job, (string Repository, int Number, DateTimeOffset At, string Version) source, CancellationToken ct)
    {
        var url = $"https://github.com/{source.Repository}/issues/{source.Number}";
        await using var duplicates = new NpgsqlCommand("""
            UPDATE status_incidents SET document=jsonb_set(document,'{published}','false')
            WHERE id IN (SELECT incident_id FROM status_github_sources
                WHERE repository=$1 AND issue_number=$2 AND site_id=$3 AND incident_id<>$4)
            RETURNING id
            """, connection, transaction);
        duplicates.Parameters.AddWithValue(source.Repository);
        duplicates.Parameters.AddWithValue(source.Number);
        duplicates.Parameters.AddWithValue(job.Incident.SiteId);
        duplicates.Parameters.AddWithValue(job.Incident.Id);
        var aliases = new List<int>();
        await using (var reader = await duplicates.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) aliases.Add(reader.GetInt32(0));
        foreach (var alias in aliases)
        {
            await using var preserve = new NpgsqlCommand("""
                UPDATE status_incidents SET document=jsonb_set(document,'{publicAliases}',
                    COALESCE(document->'publicAliases','[]'::jsonb)||jsonb_build_array($1::integer)) WHERE id=$2
                """, connection, transaction);
            preserve.Parameters.AddWithValue(alias);
            preserve.Parameters.AddWithValue(job.Incident.Id);
            await preserve.ExecuteNonQueryAsync(ct);
        }
        await using var bind = new NpgsqlCommand("""
            INSERT INTO status_github_sources VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(repository,issue_number,site_id) DO UPDATE
            SET incident_id=excluded.incident_id,updated_at=excluded.updated_at
            """, connection, transaction);
        bind.Parameters.AddWithValue(source.Repository);
        bind.Parameters.AddWithValue(source.Number);
        bind.Parameters.AddWithValue(job.Incident.SiteId);
        bind.Parameters.AddWithValue(job.Incident.Id);
        bind.Parameters.AddWithValue(source.At);
        await bind.ExecuteNonQueryAsync(ct);
        await using var version = new NpgsqlCommand("""
            UPDATE status_incidents SET document=jsonb_set(jsonb_set(document,'{sourceUrl}',to_jsonb($2::text)),
                '{gitHubVersion}',to_jsonb($3::text)) WHERE id=$1 OR document->>'sourceUrl'=$2
            """, connection, transaction);
        version.Parameters.AddWithValue(job.Incident.Id);
        version.Parameters.AddWithValue(url);
        version.Parameters.AddWithValue(source.Version);
        await version.ExecuteNonQueryAsync(ct);
        await using var updated = new NpgsqlCommand("""
            UPDATE status_github_sources SET updated_at=$3 WHERE repository=$1 AND issue_number=$2
            """, connection, transaction);
        updated.Parameters.AddWithValue(source.Repository);
        updated.Parameters.AddWithValue(source.Number);
        updated.Parameters.AddWithValue(source.At);
        await updated.ExecuteNonQueryAsync(ct);
    }
}
