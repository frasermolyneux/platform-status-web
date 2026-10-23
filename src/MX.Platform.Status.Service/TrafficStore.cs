using System.Text.Json;
using MX.Platform.Status.App.Models;
using Npgsql;
using NpgsqlTypes;

namespace MX.Platform.Status.Service;

public sealed partial class PostgresStore
{
    public async Task<Dictionary<string, TrafficRecord>> ReadTrafficAsync(string site, CancellationToken ct)
    {
        await using var command = Writer.CreateCommand("SELECT document::text FROM status_traffic WHERE site_id=$1");
        command.Parameters.AddWithValue(site);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, TrafficRecord>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize<TrafficRecord>(reader.GetString(0), StatusJson.Options)!;
            result.Add(record.Hostname, record);
        }
        return result;
    }

    public async Task<TrafficLease?> TryTrafficLockAsync(string hostname, CancellationToken ct)
    {
        var connection = await Writer.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(814233110,hashtext($1))", connection);
            command.Parameters.AddWithValue(hostname);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return new TrafficLease(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    public sealed class TrafficLease(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async Task RequireOwnershipAsync(TrafficRecord pending, CancellationToken ct)
        {
            if (connection.State != System.Data.ConnectionState.Open)
                throw new TrafficConflictException("The traffic lease connection was lost. No further mutation is permitted.");
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(SELECT FROM pg_locks
                    WHERE locktype='advisory' AND granted AND pid=pg_backend_pid()
                    AND classid=814233110::oid AND objsubid=2
                    AND objid=(hashtext($1)::bigint & 4294967295)::oid)
                AND EXISTS(SELECT FROM status_traffic WHERE hostname=$1
                    AND (document->>'revision')::integer=$2 AND document->>'phase'=$3)
                """, connection);
            command.Parameters.AddWithValue(pending.Hostname);
            command.Parameters.AddWithValue(pending.Revision);
            command.Parameters.AddWithValue(pending.Phase);
            if (!(bool)(await command.ExecuteScalarAsync(ct))!)
                throw new TrafficConflictException("The traffic lease or intent changed. No stale operation may mutate the edge.");
        }

        public async Task<TrafficRecord?> ReadAsync(string hostname, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("SELECT document::text FROM status_traffic WHERE hostname=$1", connection);
            command.Parameters.AddWithValue(hostname);
            var json = await command.ExecuteScalarAsync(ct) as string;
            return json is null ? null : JsonSerializer.Deserialize<TrafficRecord>(json, StatusJson.Options);
        }

        public async Task SaveAsync(TrafficRecord record, CancellationToken ct)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var json = JsonSerializer.Serialize(record, StatusJson.Options);
            await using var write = new NpgsqlCommand("""
                INSERT INTO status_traffic(hostname,site_id,document) VALUES($1,$2,$3)
                ON CONFLICT(hostname) DO UPDATE SET document=excluded.document
                """, connection, transaction);
            write.Parameters.AddWithValue(record.Hostname);
            write.Parameters.AddWithValue(record.Site);
            write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json);
            await write.ExecuteNonQueryAsync(ct);
            await using var audit = new NpgsqlCommand("INSERT INTO status_traffic_audit(hostname,site_id,document) VALUES($1,$2,$3)", connection, transaction);
            audit.Parameters.AddWithValue(record.Hostname);
            audit.Parameters.AddWithValue(record.Site);
            audit.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json);
            await audit.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            // Session locks must not survive return to the connection pool.
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", connection);
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally { await connection.DisposeAsync(); }
        }
    }
}
