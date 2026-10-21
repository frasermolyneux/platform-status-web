using System.Text.Json;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace MX.Platform.Status.Service;

public sealed class PostgresStore(RuntimeOptions options) : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source = new(() => NpgsqlDataSource.Create(ConnectionString(options, writer: false)));
    private readonly Lazy<NpgsqlDataSource> _writer = new(() => NpgsqlDataSource.Create(ConnectionString(options, writer: true)));
    private NpgsqlDataSource Source => _source.Value;
    private NpgsqlDataSource Writer => options.WriterConnectionFile is null && options.WriterHost is null ? Source : _writer.Value;

    private static string ConnectionString(RuntimeOptions options, bool writer)
    {
        var file = writer ? options.WriterConnectionFile ?? options.DatabaseConnectionFile : options.DatabaseConnectionFile;
        if (file is not null) return File.ReadAllText(file).Trim();
        return new NpgsqlConnectionStringBuilder
        {
            Host = (writer ? options.WriterHost ?? options.DatabaseHost : options.DatabaseHost)
                ?? throw new InvalidOperationException("Database is not configured."),
            Port = writer && options.WriterHost is not null ? options.WriterPort : options.DatabasePort,
            Database = "status",
            Username = "status_app",
            Password = File.ReadAllText(options.DatabasePasswordFile).Trim(),
            Timeout = 5,
            CommandTimeout = 15,
            MaxPoolSize = 10,
            SslMode = SslMode.Disable
        }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var command = Source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS status_incidents (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                site_id text NOT NULL, revision integer NOT NULL, document jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS status_audit (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                at timestamptz NOT NULL DEFAULT now(), site_id text NOT NULL,
                incident_id integer NOT NULL, actor text NOT NULL, revision integer NOT NULL);
            CREATE TABLE IF NOT EXISTS status_publications (
                site_id text PRIMARY KEY, generated_at timestamptz NOT NULL, document jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS status_probe_days (
                site_id text NOT NULL, component_id text NOT NULL, day date NOT NULL,
                total integer NOT NULL, failures integer NOT NULL,
                PRIMARY KEY (site_id, component_id, day));
            CREATE TABLE IF NOT EXISTS status_evidence (
                source text NOT NULL, event_id text NOT NULL, observed_at timestamptz NOT NULL,
                document jsonb NOT NULL, PRIMARY KEY (source, event_id));
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        await using var command = Writer.CreateCommand("SELECT NOT pg_is_in_recovery()");
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<PublisherLease?> TryPublisherLockAsync(CancellationToken ct)
    {
        var connection = await Source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(814233107)", connection);
        if ((bool)(await command.ExecuteScalarAsync(ct))!) return new PublisherLease(connection);
        await connection.DisposeAsync();
        return null;
    }

    public async Task<IReadOnlyList<IncidentRecord>> ReadIncidentsAsync(string site, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("SELECT document::text FROM status_incidents WHERE site_id=$1 ORDER BY id DESC LIMIT 1000");
        command.Parameters.AddWithValue(site);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<IncidentRecord>();
        while (await reader.ReadAsync(ct))
            result.Add(JsonSerializer.Deserialize<IncidentRecord>(reader.GetString(0), StatusJson.Options)!);
        return result;
    }

    public async Task<IncidentRecord?> SaveIncidentAsync(string site, int? id, IncidentCommand request, string actor, CancellationToken ct)
    {
        await using var connection = await Writer.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        IncidentRecord? previous = null;
        if (id.HasValue)
        {
            await using var read = new NpgsqlCommand("SELECT document::text FROM status_incidents WHERE id=$1 AND site_id=$2 FOR UPDATE", connection, transaction);
            read.Parameters.AddWithValue(id.Value);
            read.Parameters.AddWithValue(site);
            var json = await read.ExecuteScalarAsync(ct) as string;
            if (json is null) return null;
            previous = JsonSerializer.Deserialize<IncidentRecord>(json, StatusJson.Options)!;
            if (previous.Revision != request.Revision) return null;
        }
        else
        {
            await using var insert = new NpgsqlCommand("INSERT INTO status_incidents(site_id,revision,document) VALUES($1,0,'{}') RETURNING id", connection, transaction);
            insert.Parameters.AddWithValue(site);
            id = (int)(await insert.ExecuteScalarAsync(ct))!;
        }
        var record = request.Apply(id.Value, site, previous, DateTimeOffset.UtcNow);
        await using var write = new NpgsqlCommand("UPDATE status_incidents SET revision=$1,document=$2 WHERE id=$3 AND site_id=$4", connection, transaction);
        write.Parameters.AddWithValue(record.Revision);
        write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(record, StatusJson.Options));
        write.Parameters.AddWithValue(record.Id);
        write.Parameters.AddWithValue(site);
        await write.ExecuteNonQueryAsync(ct);
        await using var audit = new NpgsqlCommand("INSERT INTO status_audit(site_id,incident_id,actor,revision) VALUES($1,$2,$3,$4)", connection, transaction);
        audit.Parameters.AddWithValue(site);
        audit.Parameters.AddWithValue(record.Id);
        audit.Parameters.AddWithValue(actor);
        audit.Parameters.AddWithValue(record.Revision);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return record;
    }

    public async Task SavePublicationAsync(StatusApiResponse publication, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("""
            INSERT INTO status_publications VALUES($1,$2,$3)
            ON CONFLICT(site_id) DO UPDATE SET generated_at=excluded.generated_at,document=excluded.document
            """);
        command.Parameters.AddWithValue(publication.Site.Id);
        command.Parameters.AddWithValue(publication.GeneratedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(publication, StatusJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<StatusApiResponse?> ReadPublicationAsync(string site, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("SELECT document::text FROM status_publications WHERE site_id=$1");
        command.Parameters.AddWithValue(site);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<StatusApiResponse>(json, StatusJson.Options);
    }

    public async Task RecordProbeAsync(string site, string component, bool success, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("""
            INSERT INTO status_probe_days VALUES($1,$2,(now() AT TIME ZONE 'UTC')::date,1,$3)
            ON CONFLICT(site_id,component_id,day) DO UPDATE
            SET total=status_probe_days.total+1,failures=status_probe_days.failures+excluded.failures
            """);
        command.Parameters.AddWithValue(site);
        command.Parameters.AddWithValue(component);
        command.Parameters.AddWithValue(success ? 0 : 1);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<HistoryDocument> ReadHistoryAsync(string site, CancellationToken ct)
    {
        var history = new HistoryDocument { Site = site, GeneratedAt = DateTimeOffset.UtcNow };
        await using var command = Source.CreateCommand("SELECT component_id,day,total,failures FROM status_probe_days WHERE site_id=$1 AND day >= (now() AT TIME ZONE 'UTC')::date-89 ORDER BY day");
        command.Parameters.AddWithValue(site);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var component = reader.GetString(0);
            if (!history.ComponentsById.TryGetValue(component, out var record))
                history.ComponentsById[component] = record = new ComponentHistoryRecord();
            var total = reader.GetInt32(2);
            var failures = reader.GetInt32(3);
            var uptime = 1d - (double)failures / total;
            record.Days.Add(new HistoryDay
            {
                Date = reader.GetFieldValue<DateOnly>(1),
                Total = total,
                Failed = failures,
                Uptime = uptime,
                Status = ComponentStatusCalculator.ClassifyHistoricStatus(total, uptime, new SlaDefinition())
            });
        }
        return history;
    }

    public async Task SaveEvidenceAsync(string source, string id, DateTimeOffset observedAt, JsonElement evidence, CancellationToken ct)
    {
        await using var command = Writer.CreateCommand("""
            INSERT INTO status_evidence VALUES($1,$2,$3,$4)
            ON CONFLICT(source,event_id) DO UPDATE SET observed_at=excluded.observed_at,document=excluded.document
            WHERE status_evidence.observed_at <= excluded.observed_at
            """);
        command.Parameters.AddWithValue(source);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(observedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, evidence.GetRawText());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<JsonElement>> ReadEvidenceAsync(CancellationToken ct)
    {
        await using var command = Source.CreateCommand("SELECT jsonb_build_object('source',source,'id',event_id,'observedAt',observed_at,'evidence',document)::text FROM status_evidence ORDER BY observed_at DESC LIMIT 200");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<JsonElement>();
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return result;
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        await using var command = Source.CreateCommand("""
            DELETE FROM status_probe_days WHERE day < (now() AT TIME ZONE 'UTC')::date-365;
            DELETE FROM status_evidence WHERE observed_at < now()-interval '30 days';
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source.IsValueCreated) await _source.Value.DisposeAsync();
        if (_writer.IsValueCreated) await _writer.Value.DisposeAsync();
    }
}

public sealed class PublisherLease(NpgsqlConnection connection) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(814233107)", connection);
                command.CommandTimeout = 5;
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { await connection.DisposeAsync(); }
    }
}
