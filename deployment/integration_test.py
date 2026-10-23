"""Exercise the production images with real primary/standby PostgreSQL."""

import json
import os
from contextlib import contextmanager
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def docker(*arguments, **kwargs):
    return subprocess.run(
        ["docker", *arguments], check=True, text=True, capture_output=True, **kwargs
    ).stdout.strip()


def wait_for(check, description, seconds=180):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(2)
    raise AssertionError(f"Timed out waiting for {description}")


def database_sql(container, sql):
    return docker("exec", "--interactive", container, "sh", "-ec",
                  'PGCONNECT_TIMEOUT=5 PGPASSWORD="$(cat /run/secrets/app-password)" psql '
                  '-h 127.0.0.1 -U status_app -d status -Atq --set=ON_ERROR_STOP=1',
                  input=sql)


def verify_database_restart_recovery(primary, standby, urls):
    snapshots = {label: request(url, "/api/status")[1]["generatedAt"] for label, url in urls.items()}
    for container in (primary, standby):
        docker("restart", container)
        wait_for(lambda: database_ready(container), f"{container} restart recovery")
        assert database_sql(primary, "SELECT pg_is_in_recovery();") == "f"
        assert database_sql(standby, "SELECT pg_is_in_recovery();") == "t"
        lsn = database_sql(primary, "SELECT pg_current_wal_lsn();")
        wait_for(lambda: database_sql(standby,
                 f"SELECT pg_last_wal_replay_lsn() >= '{lsn}'::pg_lsn;") == "t",
                 f"standby replay after {container} restart")
    for label, url in urls.items():
        wait_for(lambda: published(url) and
                 request(url, "/api/status")[1]["generatedAt"] > snapshots[label],
                 f"{label} fresh publication after database restarts")


@contextmanager
def database_lock(container, key):
    process = subprocess.Popen(
        ["docker", "exec", "--interactive", container, "sh", "-ec",
         'PGCONNECT_TIMEOUT=5 PGPASSWORD="$(cat /run/secrets/app-password)" psql '
         '-h 127.0.0.1 -U status_app -d status -Atq --set=ON_ERROR_STOP=1'],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    try:
        process.stdin.write(f"SELECT pg_try_advisory_lock({key});\n")
        process.stdin.flush()
        assert process.stdout.readline().strip() == "t", f"Could not acquire test lock {key}"

        def release():
            process.stdin.write(f"SELECT pg_advisory_unlock({key});\n")
            process.stdin.flush()

        yield release
    finally:
        _, errors = process.communicate("\\q\n", timeout=15)
        assert process.returncode == 0, errors


def verify_publisher_fencing(container):
    with database_lock(container, 814233108) as release_probe:
        database_sql(container, """
            CREATE FUNCTION pause_status_probe() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(814233108);
                RETURN NEW;
            END $$;
            CREATE TRIGGER pause_status_probe BEFORE INSERT ON status_probe_days
                FOR EACH ROW EXECUTE FUNCTION pause_status_probe();
            """)
        try:
            wait_for(lambda: database_sql(container, """
                SELECT EXISTS(SELECT FROM pg_locks
                    WHERE locktype='advisory' AND objid=814233108 AND NOT granted);
                """) == "t", "publisher blocked during a probe")
            snapshot_sql = """
                SELECT coalesce(sum(total),0)::text || ':' ||
                    (SELECT string_agg(generated_at::text,',' ORDER BY site_id) FROM status_publications)
                FROM status_probe_days;
                """
            snapshot = database_sql(container, snapshot_sql)
            assert database_sql(container, """
                SELECT pg_terminate_backend(pid) FROM pg_locks
                WHERE locktype='advisory' AND objid=814233107 AND granted;
                """) == "t"
            wait_for(lambda: database_sql(container, """
                SELECT NOT EXISTS(SELECT FROM pg_locks
                    WHERE locktype='advisory' AND objid=814233107 AND granted);
                """) == "t", "lost publisher lock released")
            with database_lock(container, 814233107):
                release_probe()
                wait_for(lambda: database_sql(container, """
                    SELECT NOT EXISTS(SELECT FROM pg_locks
                        WHERE locktype='advisory' AND objid=814233108 AND NOT granted);
                    """) == "t", "interrupted probe unblocked")
                time.sleep(5)
                assert database_sql(container, snapshot_sql) == snapshot, \
                    "A publisher wrote probes or publications after losing its lock session"
        finally:
            database_sql(container, """
                DROP TRIGGER pause_status_probe ON status_probe_days;
                DROP FUNCTION pause_status_probe();
                """)


def verify_historic_thresholds(container, urls):
    day = database_sql(container, "SELECT ((now() AT TIME ZONE 'UTC')::date-1)::text;")
    expected = {
        "mx.sitewatch.molyneux-me": "degraded",
        "mx.sitewatch.geo-location-web": "operational",
        "mx.sitewatch.geo-location-api": "outage",
    }
    for component in expected:
        database_sql(container, f"""
            INSERT INTO status_probe_days VALUES('mx','{component}','{day}',100,15)
            ON CONFLICT(site_id,component_id,day) DO UPDATE SET total=100,failures=15;
            """)
    for label, url in urls.items():
        def correct_history():
            publication = request(url, "/api/status")[1]
            components = {component["id"]: component for component in publication.get("components", [])}
            for component, status in expected.items():
                days = components.get(component, {}).get("history", [])
                sample = next((sample for sample in days if sample["date"] == day), None)
                if sample is None or sample["status"] != status or sample.get("total") != 100:
                    return False
                assert sample["failed"] == 15 and sample["uptime"] == 0.85
            return True
        wait_for(correct_history, f"{label} configured and default historic SLA thresholds")


@contextmanager
def runtime_directory(containers):
    temporary = tempfile.TemporaryDirectory(prefix="status-integration-")
    root = Path(temporary.name)
    try:
        yield root
    except BaseException:
        for container in containers:
            result = subprocess.run(["docker", "logs", "--tail", "40", container], capture_output=True, text=True)
            print(f"{container}:\n{result.stdout}\n{result.stderr}")
        raise
    finally:
        for container in reversed(containers):
            subprocess.run(["docker", "rm", "--force", container], capture_output=True, check=False)
        docker("run", "--rm", "--user", "0:0", "--entrypoint", "chown",
               "--mount", f"type=bind,src={root},dst=/cleanup", "status-service:ci",
               "-R", f"{os.getuid()}:{os.getgid()}", "/cleanup")
        temporary.cleanup()


def main():
    name = f"status-ci-{os.getpid()}"
    containers = []
    network_created = False
    try:
        with runtime_directory(containers) as root:
            for directory in ("primary", "standby", "public", "replica", "secrets", "writer-node", "reader-node"):
                (root / directory).mkdir()
            credentials = {
                filename: secrets.token_hex(32)
                for filename in ("postgres-password", "app-password", "replication-password")
            }
            for filename, password in credentials.items():
                (root / "secrets" / filename).write_text(password)
                (root / "secrets" / filename).chmod(0o640)
            catalog_path = Path(__file__).resolve().parents[1] / "src" / "MX.Platform.Status.Service" / "catalog.json"
            catalog = json.loads(catalog_path.read_text())
            for component, degraded in zip(catalog[0]["components"]["components"][:2], (0.95, 0.8)):
                component["sla"] = {"outageBelow": 0.5, "degradedBelow": degraded}
            for directory, role, peer in (
                ("writer-node", "primary", "100.120.200.11"),
                ("reader-node", "standby", "100.120.200.10"),
            ):
                node = root / directory
                (node / "database-role").write_text(role)
                (node / "primary-host").write_text(f"{name}-primary")
                (node / "primary-port").write_text("5432")
                (node / "peer-address").write_text(peer)
                (node / "catalog.json").write_text(json.dumps(catalog))
                (node / "runtime.json").write_text(json.dumps({"Status": {
                    "Publisher": role == "primary", "RefreshSeconds": 30, "StaleAfterSeconds": 60,
                    "GitHubEnabled": False,
                    "DataDirectory": "/data", "DatabaseHost": f"{name}-{role}",
                    "WriterHost": f"{name}-primary", "WriterPort": 5432,
                    "CatalogPath": "/node/catalog.json",
                }}))
            docker("run", "--rm", "--user", "0:0", "--entrypoint", "sh",
                   "--mount", f"type=bind,src={root},dst=/init", "status-service:ci", "-ec",
                   "chown -R 999:10001 /init/primary /init/standby; "
                   "chown -R 10001:10001 /init/public /init/replica /init/secrets")
            docker("network", "create", "--subnet", "100.120.200.0/24", name)
            network_created = True
            assert docker("inspect", "--format", "{{json .Config.Volumes}}", "status-postgres:ci") == "null"
            restrictions = ("--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                            "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m")
            for role, address, node in (("primary", ".10", "writer-node"), ("standby", ".11", "reader-node")):
                container = f"{name}-{role}"
                containers.append(container)
                docker("run", "--detach", "--name", container, "--network", name,
                       "--ip", f"100.120.200{address}", "--publish", "127.0.0.1::5432", *restrictions,
                       "--mount", f"type=bind,src={root / role},dst=/var/lib/postgresql/data",
                       "--mount", f"type=bind,src={root / node},dst=/node,readonly",
                       "--mount", f"type=bind,src={root / 'secrets'},dst=/run/secrets,readonly",
                       "status-postgres:ci")
                wait_for(lambda: database_ready(container), role)
            urls = {}
            for label, node in (("public", "writer-node"), ("replica", "reader-node")):
                container = f"{name}-{label}"
                containers.append(container)
                docker("run", "--detach", "--name", container, "--network", name,
                       "--user", "10001:10001", *restrictions, "--publish", "127.0.0.1::8080",
                       "--env", "STATUS_RUNTIME_FILE=/node/runtime.json",
                       "--mount", f"type=bind,src={root / label},dst=/data",
                       "--mount", f"type=bind,src={root / node},dst=/node,readonly",
                       "--mount", f"type=bind,src={root / 'secrets'},dst=/run/secrets,readonly",
                       "status-service:ci")
                urls[label] = origin_url(container)
            for label, url in urls.items():
                wait_for(lambda: published(url), f"{label} persisted publication")
                assert request(url, "/api/status", "status.xtremeidiots.com")[1]["site"]["id"] == "xi"
                assert request(url, "/api/ops/evidence")[0] == 401
                assert request(url, "/ops")[0] == 200
                assert request(url, "/feed.xml")[0] == 200
                assert request(url, "/api/status", "unexpected.example")[0] == 404
            verify_database_restart_recovery(f"{name}-primary", f"{name}-standby", urls)
            verify_publisher_fencing(f"{name}-primary")
            verify_historic_thresholds(f"{name}-primary", urls)
            connection_port = docker("port", f"{name}-primary", "5432/tcp").split(":")[-1]
            integration_environment = dict(os.environ)
            integration_environment["STATUS_TEST_CONNECTION"] = (
                f"Host=127.0.0.1;Port={connection_port};Database=status;Username=status_app;"
                f"Password={credentials['app-password']}"
            )
            native_cases = ("BannerApiPersists", "GitHubSourceKeeps", "ProbeDiagnosticsPersist",
                            "TrafficControlsPersist", "TrafficFailuresRemain", "TrafficConcurrencyRejects")
            native_results = root / "native-results"
            subprocess.run([
                "dotnet", "test", "src/MX.Platform.Status.Tests/MX.Platform.Status.Tests.csproj",
                "--configuration", "Release", "--no-build", "--nologo",
                "--filter", "|".join(f"FullyQualifiedName~{name}" for name in native_cases),
                "--logger", "trx;LogFileName=native-status.trx", "--results-directory", str(native_results),
            ], check=True, env=integration_environment)
            results = ET.parse(native_results / "native-status.trx").findall(".//{*}UnitTestResult")
            assert len(results) == len(native_cases), "Every required native PostgreSQL case must execute."
            for name in native_cases:
                matches = [result for result in results if result.attrib["testName"].split(".")[-1].startswith(name)]
                assert len(matches) == 1 and matches[0].attrib["outcome"] == "Passed", name
            print(f"All {len(native_cases)} required native PostgreSQL cases executed and passed without skips.")
            command = {
                "title": "Integration incident", "components": ["mx.sitewatch.molyneux-me"],
                "severity": "outage", "state": "investigating",
                "message": "RESTRICTED_DRAFT_MARKER", "published": False,
            }
            record = json.loads(docker("exec", "--interactive", f"{name}-public", "dotnet",
                                       "MX.Platform.Status.Service.dll", "--emergency-publish", "mx",
                                       input=json.dumps(command)))
            assert "RESTRICTED_DRAFT_MARKER" not in json.dumps(request(urls["public"], "/api/status")[1])
            command.update(published=True, revision=record["revision"], message="Approved public update")
            docker("exec", "--interactive", f"{name}-replica", "dotnet", "MX.Platform.Status.Service.dll",
                   "--emergency-publish", "mx", str(record["id"]), input=json.dumps(command))
            for label, url in urls.items():
                wait_for(lambda: bool(request(url, "/api/status")[1].get("incidents")), f"{label} incident replication")
            now = datetime.now(timezone.utc)
            maintenance = {
                "title": "Integration maintenance", "components": ["mx.sitewatch.molyneux-me"],
                "severity": "maintenance", "state": "monitoring", "message": "Approved maintenance timeline",
                "published": True, "scheduledStart": (now - timedelta(minutes=5)).isoformat(),
                "scheduledEnd": (now + timedelta(minutes=30)).isoformat(),
            }
            maintenance_record = json.loads(docker("exec", "--interactive", f"{name}-public", "dotnet",
                                                  "MX.Platform.Status.Service.dll", "--emergency-publish", "mx",
                                                  input=json.dumps(maintenance)))
            for label, url in urls.items():
                wait_for(lambda: len(request(url, "/api/status")[1].get("incidents", [])) == 2,
                         f"{label} maintenance timeline publication")
                publication = request(url, "/api/status")[1]
                assert any(item["id"] == maintenance_record["id"] for item in publication["maintenance"])
                assert request(url, f"/incident/{maintenance_record['id']}")[0] == 200
                assert "Integration maintenance" in request(url, "/feed.xml")[1]
                assert "RESTRICTED_DRAFT_MARKER" not in json.dumps(publication)
            banner_expiry = datetime.now(timezone.utc) + timedelta(seconds=120)
            database_sql(f"{name}-primary", f"""
                INSERT INTO status_banners(site_id,revision,document) VALUES
                ('mx',1,jsonb_build_object('id',9001,'siteId','mx','revision',1,'title','Cached notice',
                    'message','Public integration notice','tone','info','published',true,'updatedAt',now())),
                ('mx',1,jsonb_build_object('id',9002,'siteId','mx','revision',1,'title','PRIVATE_BANNER_DRAFT',
                    'message','Private draft','tone','info','published',false,'updatedAt',now())),
                ('mx',1,jsonb_build_object('id',9003,'siteId','mx','revision',1,'title','Expiring notice',
                    'message','Temporary public notice','tone','warning','published',true,
                    'expiresAt','{banner_expiry.isoformat()}','updatedAt',now()));
                """)
            for label, url in urls.items():
                wait_for(lambda: any(banner["title"] == "Cached notice"
                         for banner in request(url, "/api/status")[1].get("banners", [])),
                         f"{label} cached public banner")
                assert "PRIVATE_BANNER_DRAFT" not in json.dumps(request(url, "/api/status")[1])
                assert any(banner["title"] == "Expiring notice" for banner in request(url, "/api/status")[1]["banners"])
                assert not request(url, "/api/status", "status.xtremeidiots.com")[1].get("banners")
            docker("exec", "--interactive", f"{name}-primary", "sh", "-ec",
                   'PGPASSWORD="$(cat /run/secrets/app-password)" psql '
                   '-h 127.0.0.1 -U status_app -d status --set=ON_ERROR_STOP=1', input=f"""
                WITH copies AS (
                    SELECT nextval(pg_get_serial_sequence('status_incidents', 'id')) AS id, n
                    FROM generate_series(1,2002) n
                ), history AS (
                    SELECT jsonb_build_array(jsonb_build_object(
                        'at', now()-interval '1 day', 'state', 'resolved',
                        'body', 'Synthetic historic recovery')) AS updates
                )
                INSERT INTO status_incidents(id,site_id,revision,document) OVERRIDING SYSTEM VALUE
                SELECT copies.id, original.site_id, 1,
                    original.document || CASE WHEN copies.n <= 1001
                    THEN jsonb_build_object('id', copies.id, 'published', false)
                    ELSE jsonb_build_object(
                        'id', copies.id, 'title', 'Historic integration incident', 'state', 'resolved',
                        'startedAt', now()-interval '2 days', 'resolvedAt', now()-interval '1 day',
                        'updates', history.updates, 'publicUpdates', history.updates) END
                FROM copies CROSS JOIN history CROSS JOIN status_incidents original
                WHERE original.id={int(record['id'])};
                """)
            for label, url in urls.items():
                wait_for(lambda: len(request(url, "/api/status")[1].get("incidents", [])) == 1002,
                         f"{label} active records preserved beyond historical and draft limits")
                publication = request(url, "/api/status")[1]
                assert {record["id"], maintenance_record["id"]}.issubset(
                    {item["id"] for item in publication["incidents"]})
                assert "RESTRICTED_DRAFT_MARKER" not in json.dumps(publication)
            snapshots = {label: request(url, "/api/status")[1]["generatedAt"] for label, url in urls.items()}
            docker("stop", f"{name}-primary", f"{name}-standby")
            for label, url in urls.items():
                docker("restart", f"{name}-{label}")
                urls[label] = url = origin_url(f"{name}-{label}")
                wait_for(lambda: request(url, "/health/ready")[0] == 200, f"{label} independent restart", seconds=30)
                publication = request(url, "/api/status")[1]
                assert any(item["title"] == "Integration incident" for item in publication["incidents"])
                assert publication["generatedAt"] >= snapshots[label]
                assert any(banner["title"] == "Cached notice" for banner in publication["banners"])
                assert request(url, "/feed.xml")[0] == 200
            time.sleep(max(65, (banner_expiry - datetime.now(timezone.utc)).total_seconds() + 1))
            for url in urls.values():
                stale = request(url, "/api/status")[1]
                assert stale["dataFreshness"]["stale"]
                assert stale["summary"]["status"] == "unknown"
                assert "Integration incident" in request(url, "/feed.xml")[1]
                assert [banner["title"] for banner in stale["banners"]] == ["Cached notice"]
            print("Publisher fencing, historic SLA thresholds, primary/standby publishing, draft privacy, "
                  "standby writes, and database-independent restart passed.")
    finally:
        if network_created:
            subprocess.run(["docker", "network", "rm", name], capture_output=True, check=False)


def database_ready(container):
    try:
        # The temporary bootstrap server listens only on loopback.
        return docker("exec", container, "sh", "-ec",
                      'PGPASSWORD="$(cat /run/secrets/app-password)" psql '
                      '-h "$1" -U status_app -d status -Atc "SELECT 1"',
                      "runtime-readiness", container) == "1"
    except subprocess.CalledProcessError:
        if docker("inspect", "--format", "{{.State.Running}}", container) != "true":
            raise RuntimeError(f"Database container exited before becoming ready: {container}")
        return False


def origin_url(container):
    port = docker("port", container, "8080/tcp").split(":")[-1]
    return f"http://127.0.0.1:{port}"


def request(url, path, host="mxstatus.io"):
    try:
        with urlopen(Request(url + path, headers={"Host": host}), timeout=5) as response:
            text = response.read().decode()
            return response.status, json.loads(text) if path == "/api/status" else text
    except HTTPError as error:
        return error.code, error.read().decode()
    except (URLError, TimeoutError, ConnectionError):
        return 0, {}


def published(url):
    status, body = request(url, "/api/status")
    return status == 200 and not body["dataFreshness"].get("stale", False) and not body["generatedAt"].startswith("1970-")


if __name__ == "__main__":
    main()
