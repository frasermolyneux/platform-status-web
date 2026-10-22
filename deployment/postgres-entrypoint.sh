#!/bin/bash
set -euo pipefail
umask 077

role="$(cat /node/database-role)"
primary="$(cat /node/primary-host)"
primary_port="$(cat /node/primary-port)"
peer="$(cat /node/peer-address)"
if [[ "$role" != primary && "$role" != standby ]]; then
    echo "Database role must be primary or standby." >&2
    exit 78
fi
if [[ ! "$primary" =~ ^[a-zA-Z0-9.-]+$ || ! "$peer" =~ ^100\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Invalid database peer configuration." >&2
    exit 78
fi
if [[ ! "$primary_port" =~ ^[0-9]+$ ]] || (( primary_port < 1 || primary_port > 65535 )); then
    echo "Invalid primary database port." >&2
    exit 78
fi
for file in postgres-password app-password replication-password; do
    test -s "/run/secrets/$file"
done
chmod 0700 "$PGDATA"

# Keep the bootstrap server and normal server compatible with standby recovery.
postgres_options=(
    -c unix_socket_directories=/tmp
    -c wal_level=replica -c max_wal_senders=10 -c max_replication_slots=2
    -c hot_standby=on -c shared_buffers=128MB
)

export PGUSER=status_admin
export PGPASSWORD
PGPASSWORD="$(cat /run/secrets/postgres-password)"

if [[ ! -s "$PGDATA/PG_VERSION" ]]; then
    if [[ "$role" == standby ]]; then
        printf '%s:%s:replication:status_replicator:%s\n' \
            "$primary" "$primary_port" "$(cat /run/secrets/replication-password)" >/tmp/status-pgpass
        chmod 0600 /tmp/status-pgpass
        unset PGPASSWORD
        export PGPASSFILE=/tmp/status-pgpass
        pg_basebackup --host="$primary" --port="$primary_port" --username=status_replicator \
            --pgdata="$PGDATA" --wal-method=stream --write-recovery-conf \
            --slot=status_standby --no-password
    else
        initdb --username=status_admin --pwfile=/run/secrets/postgres-password \
            --auth-local=scram-sha-256 --auth-host=scram-sha-256
    fi
fi

if [[ "$role" == standby ]]; then
    if [[ ! -f "$PGDATA/standby.signal" ]]; then
        echo "Refusing to start a promoted database as a standby; follow the recovery runbook." >&2
        exit 78
    fi
    printf '%s:%s:replication:status_replicator:%s\n' \
        "$primary" "$primary_port" "$(cat /run/secrets/replication-password)" >/tmp/status-pgpass
    chmod 0600 /tmp/status-pgpass
    export PGPASSFILE=/tmp/status-pgpass
else
    if [[ -f "$PGDATA/standby.signal" ]]; then
        echo "Refusing implicit database promotion; fence the old writer and promote explicitly." >&2
        exit 78
    fi
fi

cat >"$PGDATA/pg_hba.conf" <<EOF
local all all scram-sha-256
host all status_admin 127.0.0.1/32 scram-sha-256
host status status_app 0.0.0.0/0 scram-sha-256
host replication status_replicator $peer/32 scram-sha-256
EOF

if [[ "$role" == primary ]]; then
    pg_ctl --pgdata="$PGDATA" --wait --options="-c listen_addresses=127.0.0.1 ${postgres_options[*]}" start
    trap 'pg_ctl --pgdata="$PGDATA" --mode=fast --wait stop' EXIT
    psql --host=127.0.0.1 --dbname=postgres --no-password --set=ON_ERROR_STOP=1 <<'SQL'
SET log_min_error_statement=panic;
\set app_password `cat /run/secrets/app-password`
\set replication_password `cat /run/secrets/replication-password`
SELECT 'CREATE ROLE status_app LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='status_app') \gexec
SELECT format('ALTER ROLE status_app PASSWORD %L', :'app_password') \gexec
SELECT 'CREATE ROLE status_replicator LOGIN REPLICATION' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='status_replicator') \gexec
SELECT format('ALTER ROLE status_replicator PASSWORD %L', :'replication_password') \gexec
SELECT 'CREATE DATABASE status OWNER status_app' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='status') \gexec
SELECT pg_create_physical_replication_slot('status_standby') WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name='status_standby');
SQL
    pg_ctl --pgdata="$PGDATA" --mode=fast --wait stop
    trap - EXIT
fi
unset PGPASSWORD
exec postgres -D "$PGDATA" \
    -c listen_addresses='*' "${postgres_options[@]}" -c max_slot_wal_keep_size=1GB
