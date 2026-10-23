using System.Text.Json;
using MX.Platform.Status.App.Contracts;
using MX.Platform.Status.App.Models;
using MX.Platform.Status.App.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace MX.Platform.Status.Service;

public sealed partial class PostgresStore(RuntimeOptions options, ILogger<PostgresStore> logger) : IAsyncDisposable
{
    internal const string EvidenceUpsert = """
        INSERT INTO status_evidence VALUES($1,$2,$3,$4)
        ON CONFLICT(source,event_id) DO UPDATE SET observed_at=excluded.observed_at,document=excluded.document
        WHERE status_evidence.observed_at <= excluded.observed_at
        """;
    private readonly Lazy<NpgsqlDataSource> _source = new(() => NpgsqlDataSource.Create(ConnectionString(options, writer: false)));
    private readonly Lazy<NpgsqlDataSource> _writer = new(() => NpgsqlDataSource.Create(ConnectionString(options, writer: true)));
    private NpgsqlDataSource Source => _source.Value;
    private NpgsqlDataSource Writer => options.WriterConnectionFile is null && options.WriterHost is null ? Source : _writer.Value;

    internal static string ConnectionString(RuntimeOptions options, bool writer)
    {
        if (writer && options.WriterConnectionFile is not null)
            return File.ReadAllText(options.WriterConnectionFile).Trim();
        NpgsqlConnectionStringBuilder connection;
        if (options.DatabaseConnectionFile is not null)
            connection = new NpgsqlConnectionStringBuilder(File.ReadAllText(options.DatabaseConnectionFile).Trim());
        else
            connection = new NpgsqlConnectionStringBuilder
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
            };
        if (writer && options.WriterHost is not null)
        {
            connection.Host = options.WriterHost;
            connection.Port = options.WriterPort;
        }
        return connection.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await Source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(814233109);
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
            CREATE TABLE IF NOT EXISTS status_banners (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                site_id text NOT NULL, revision integer NOT NULL, document jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS status_banner_audit (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                at timestamptz NOT NULL DEFAULT now(), site_id text NOT NULL,
                banner_id integer NOT NULL, actor text NOT NULL, revision integer NOT NULL);
            CREATE TABLE IF NOT EXISTS status_github_sources (
                repository text NOT NULL, issue_number integer NOT NULL, site_id text NOT NULL,
                incident_id integer NOT NULL REFERENCES status_incidents(id),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY(repository,issue_number,site_id));
            CREATE TABLE IF NOT EXISTS status_traffic (
                hostname text PRIMARY KEY, site_id text NOT NULL, document jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS status_traffic_audit (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, at timestamptz NOT NULL DEFAULT now(),
                hostname text NOT NULL, site_id text NOT NULL, document jsonb NOT NULL);
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        try
        {
            await using var command = Writer.CreateCommand("SELECT NOT pg_is_in_recovery()");
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }
        catch (NpgsqlException exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "The status writer is unavailable; restricted reads may continue from the local database.");
            return false;
        }
        catch (TimeoutException exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "The status writer timed out; restricted reads may continue from the local database.");
            return false;
        }
    }

    public async Task<PublisherLease?> TryPublisherLockAsync(CancellationToken ct)
    {
        var connection = await Source.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(814233107)", connection);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return new PublisherLease(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    public Task<IReadOnlyList<IncidentRecord>> ReadIncidentsAsync(string site, CancellationToken ct) =>
        ReadIncidentDocumentsAsync(site, "SELECT document::text FROM status_incidents WHERE site_id=$1 ORDER BY id DESC LIMIT 1000", ct);

    public async Task<IReadOnlyList<IncidentRecord>> ReadIncidentsAsync(StatusCatalog catalog, SiteDefinition site,
        bool publication, CancellationToken ct)
    {
        var records = new List<IncidentRecord>();
        foreach (var sourceId in catalog.Sites.Select(definition => definition.Site.Id))
            records.AddRange(publication || sourceId != site.Site.Id
                ? await ReadPublicationIncidentsAsync(sourceId, ct)
                : await ReadIncidentsAsync(sourceId, ct));
        return catalog.ProjectIncidents(site, records);
    }

    public Task<IReadOnlyList<IncidentRecord>> ReadPublicationIncidentsAsync(string site, CancellationToken ct) =>
        ReadIncidentDocumentsAsync(site, """
            SELECT document::text FROM (
                SELECT id, document FROM status_incidents
                WHERE site_id=$1 AND document->>'published'='true' AND document->>'state'<>'resolved'
                UNION ALL
                (SELECT id, document FROM status_incidents
                 WHERE site_id=$1 AND document->>'published'='true' AND document->>'state'='resolved'
                 ORDER BY id DESC LIMIT 1000)
            ) records ORDER BY id DESC
            """, ct);

    private async Task<IReadOnlyList<IncidentRecord>> ReadIncidentDocumentsAsync(string site, string sql, CancellationToken ct)
    {
        await using var command = Source.CreateCommand(sql);
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
            if (previous.Revision != request.Revision || previous.SourceUrl is not null) return null;
        }
        else
        {
            await using var insert = new NpgsqlCommand("INSERT INTO status_incidents(site_id,revision,document) VALUES($1,0,'{}') RETURNING id", connection, transaction);
            insert.Parameters.AddWithValue(site);
            id = (int)(await insert.ExecuteScalarAsync(ct))!;
        }
        var record = request.Apply(id.Value, site, previous, DateTimeOffset.UtcNow);
        await WriteIncidentAsync(connection, transaction, record, actor, ct);
        await transaction.CommitAsync(ct);
        return record;
    }

    private static async Task WriteIncidentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IncidentRecord record, string actor, CancellationToken ct)
    {
        await using var write = new NpgsqlCommand("UPDATE status_incidents SET revision=$1,document=$2 WHERE id=$3 AND site_id=$4", connection, transaction);
        write.Parameters.AddWithValue(record.Revision);
        write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(record, StatusJson.Options));
        write.Parameters.AddWithValue(record.Id);
        write.Parameters.AddWithValue(record.SiteId);
        await write.ExecuteNonQueryAsync(ct);
        await using var audit = new NpgsqlCommand("INSERT INTO status_audit(site_id,incident_id,actor,revision) VALUES($1,$2,$3,$4)", connection, transaction);
        audit.Parameters.AddWithValue(record.SiteId);
        audit.Parameters.AddWithValue(record.Id);
        audit.Parameters.AddWithValue(actor);
        audit.Parameters.AddWithValue(record.Revision);
        await audit.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BannerRecord>> ReadBannersAsync(string site, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("SELECT document::text FROM status_banners WHERE site_id=$1 ORDER BY id DESC");
        command.Parameters.AddWithValue(site);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<BannerRecord>();
        while (await reader.ReadAsync(ct))
            result.Add(JsonSerializer.Deserialize<BannerRecord>(reader.GetString(0), StatusJson.Options)!);
        return result;
    }

    public async Task<BannerRecord?> SaveBannerAsync(string site, int? id, BannerCommand request, string actor, CancellationToken ct)
    {
        await using var connection = await Writer.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        BannerRecord? previous = null;
        if (id.HasValue)
        {
            await using var read = new NpgsqlCommand("SELECT document::text FROM status_banners WHERE id=$1 AND site_id=$2 FOR UPDATE", connection, transaction);
            read.Parameters.AddWithValue(id.Value);
            read.Parameters.AddWithValue(site);
            var json = await read.ExecuteScalarAsync(ct) as string;
            if (json is null) return null;
            previous = JsonSerializer.Deserialize<BannerRecord>(json, StatusJson.Options)!;
            if (previous.Revision != request.Revision) return null;
        }
        else
        {
            await using var insert = new NpgsqlCommand("INSERT INTO status_banners(site_id,revision,document) VALUES($1,0,'{}') RETURNING id", connection, transaction);
            insert.Parameters.AddWithValue(site);
            id = (int)(await insert.ExecuteScalarAsync(ct))!;
        }
        var record = request.Apply(id.Value, site, previous, DateTimeOffset.UtcNow);
        await using var write = new NpgsqlCommand("UPDATE status_banners SET revision=$1,document=$2 WHERE id=$3 AND site_id=$4", connection, transaction);
        write.Parameters.AddWithValue(record.Revision);
        write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(record, StatusJson.Options));
        write.Parameters.AddWithValue(record.Id);
        write.Parameters.AddWithValue(site);
        await write.ExecuteNonQueryAsync(ct);
        await using var audit = new NpgsqlCommand("INSERT INTO status_banner_audit(site_id,banner_id,actor,revision) VALUES($1,$2,$3,$4)", connection, transaction);
        audit.Parameters.AddWithValue(site);
        audit.Parameters.AddWithValue(record.Id);
        audit.Parameters.AddWithValue(actor);
        audit.Parameters.AddWithValue(record.Revision);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<GitHubSource>> ReadGitHubSourcesAsync(string repository, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("""
            SELECT issue_number,s.site_id,updated_at,i.document::text
            FROM status_github_sources s JOIN status_incidents i ON i.id=s.incident_id
            WHERE repository=$1
            """);
        command.Parameters.AddWithValue(repository);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<GitHubSource>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                JsonSerializer.Deserialize<IncidentRecord>(reader.GetString(3), StatusJson.Options)!));
        return result;
    }

    public async Task SaveGitHubIncidentAsync(PublisherLease publisher, string repository, int number, DateTimeOffset updatedAt,
        IncidentRecord incoming, CancellationToken ct)
    {
        var connection = publisher.Connection;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814233110)", connection, transaction);
        await gate.ExecuteNonQueryAsync(ct);
        await using var read = new NpgsqlCommand("""
            SELECT i.document::text FROM status_github_sources s
            JOIN status_incidents i ON i.id=s.incident_id
            WHERE repository=$1 AND issue_number=$2 AND s.site_id=$3 FOR UPDATE OF i
            """, connection, transaction);
        read.Parameters.AddWithValue(repository);
        read.Parameters.AddWithValue(number);
        read.Parameters.AddWithValue(incoming.SiteId);
        var json = await read.ExecuteScalarAsync(ct) as string;
        IncidentRecord record;
        var changed = true;
        if (json is null)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO status_incidents(site_id,revision,document) VALUES($1,0,'{}') RETURNING id", connection, transaction);
            insert.Parameters.AddWithValue(incoming.SiteId);
            record = incoming with { Id = (int)(await insert.ExecuteScalarAsync(ct))!, Revision = 1 };
        }
        else
        {
            var previous = JsonSerializer.Deserialize<IncidentRecord>(json, StatusJson.Options)!;
            record = incoming with { Id = previous.Id, Revision = previous.Revision + 1 };
            changed = JsonSerializer.Serialize(previous with { Revision = 0 }, StatusJson.Options)
                != JsonSerializer.Serialize(record with { Revision = 0 }, StatusJson.Options);
        }
        if (changed) await WriteIncidentAsync(connection, transaction, record, $"github:{repository}#{number}", ct);
        await using var source = new NpgsqlCommand("""
            INSERT INTO status_github_sources VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(repository,issue_number,site_id)
            DO UPDATE SET updated_at=excluded.updated_at
            """, connection, transaction);
        source.Parameters.AddWithValue(repository);
        source.Parameters.AddWithValue(number);
        source.Parameters.AddWithValue(incoming.SiteId);
        source.Parameters.AddWithValue(record.Id);
        source.Parameters.AddWithValue(updatedAt);
        await source.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<StatusApiResponse?> ReadPublicationAsync(string site, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("SELECT document::text FROM status_publications WHERE site_id=$1");
        command.Parameters.AddWithValue(site);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<StatusApiResponse>(json, StatusJson.Options);
    }

    public async Task<HistoryDocument> ReadHistoryAsync(SiteDefinition site, CancellationToken ct)
    {
        var components = StatusCatalog.Flatten(site.Components.Components).ToDictionary(component => component.Id);
        var history = new HistoryDocument { Site = site.Site.Id, GeneratedAt = DateTimeOffset.UtcNow };
        await using var command = Source.CreateCommand("SELECT component_id,day,total,failures FROM status_probe_days WHERE site_id=$1 AND day >= (now() AT TIME ZONE 'UTC')::date-89 ORDER BY day");
        command.Parameters.AddWithValue(site.Site.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var component = reader.GetString(0);
            if (!components.TryGetValue(component, out var definition)) continue;
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
                Status = ComponentStatusCalculator.ClassifyHistoricStatus(total, uptime, definition.Sla)
            });
        }
        return history;
    }

    public async Task SaveEvidenceAsync(string source, string id, DateTimeOffset observedAt, JsonElement evidence, CancellationToken ct)
    {
        await using var command = Writer.CreateCommand(EvidenceUpsert);
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

    public async Task<IReadOnlyList<ProbeObservation>> ReadProbesAsync(string site, CancellationToken ct)
    {
        await using var command = Source.CreateCommand("""
            SELECT document::text FROM status_evidence
            WHERE source IN ('independent-probe','independent-probe-failure') AND document->>'site'=$1
            """);
        command.Parameters.AddWithValue(site);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ProbeObservation>();
        while (await reader.ReadAsync(ct))
            result.Add(JsonSerializer.Deserialize<ProbeObservation>(reader.GetString(0), StatusJson.Options)!);
        return result.Distinct().ToArray();
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
    internal NpgsqlConnection Connection => connection;

    public async Task SaveProbeEvidenceAsync(ProbeObservation observation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(PostgresStore.EvidenceUpsert, connection);
        command.Parameters.AddWithValue("independent-probe");
        command.Parameters.AddWithValue($"{observation.Site}/{observation.Component}");
        command.Parameters.AddWithValue(observation.ObservedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(observation, StatusJson.Options));
        await command.ExecuteNonQueryAsync(ct);
        if (!observation.Success)
        {
            command.Parameters[0].Value = "independent-probe-failure";
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task SavePublicationAsync(StatusApiResponse publication, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO status_publications VALUES($1,$2,$3)
            ON CONFLICT(site_id) DO UPDATE SET generated_at=excluded.generated_at,document=excluded.document
            """, connection);
        command.Parameters.AddWithValue(publication.Site.Id);
        command.Parameters.AddWithValue(publication.GeneratedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(publication, StatusJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordProbeAsync(string site, string component, bool success, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO status_probe_days VALUES($1,$2,(now() AT TIME ZONE 'UTC')::date,1,$3)
            ON CONFLICT(site_id,component_id,day) DO UPDATE
            SET total=status_probe_days.total+1,failures=status_probe_days.failures+excluded.failures
            """, connection);
        command.Parameters.AddWithValue(site);
        command.Parameters.AddWithValue(component);
        command.Parameters.AddWithValue(success ? 0 : 1);
        await command.ExecuteNonQueryAsync(ct);
    }

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
