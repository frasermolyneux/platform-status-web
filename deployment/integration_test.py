"""Exercise the production images with real primary/standby PostgreSQL."""

import json
import os
from contextlib import contextmanager
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
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
            for filename in ("postgres-password", "app-password", "replication-password"):
                (root / "secrets" / filename).write_text(secrets.token_hex(32))
                (root / "secrets" / filename).chmod(0o640)
            for directory, role, peer in (
                ("writer-node", "primary", "100.120.200.11"),
                ("reader-node", "standby", "100.120.200.10"),
            ):
                node = root / directory
                (node / "database-role").write_text(role)
                (node / "primary-host").write_text(f"{name}-primary")
                (node / "primary-port").write_text("5432")
                (node / "peer-address").write_text(peer)
                (node / "runtime.json").write_text(json.dumps({"Status": {
                    "Publisher": role == "primary", "RefreshSeconds": 30, "StaleAfterSeconds": 60,
                    "DataDirectory": "/data", "DatabaseHost": f"{name}-{role}",
                    "WriterHost": f"{name}-primary", "WriterPort": 5432,
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
                       "--ip", f"100.120.200{address}", "--user", "999:10001", *restrictions,
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
                port = docker("port", container, "8080/tcp").split(":")[-1]
                urls[label] = f"http://127.0.0.1:{port}"
            for label, url in urls.items():
                wait_for(lambda: published(url), f"{label} persisted publication")
                assert request(url, "/api/status", "status.xtremeidiots.com")[1]["site"]["id"] == "xi"
                assert request(url, "/api/ops/evidence")[0] == 401
                assert request(url, "/ops")[0] == 200
                assert request(url, "/feed.xml")[0] == 200
                assert request(url, "/api/status", "unexpected.example")[0] == 404
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
            snapshots = {label: request(url, "/api/status")[1]["generatedAt"] for label, url in urls.items()}
            docker("stop", f"{name}-primary", f"{name}-standby")
            for label, url in urls.items():
                docker("restart", f"{name}-{label}")
                wait_for(lambda: request(url, "/health/ready")[0] == 200, f"{label} independent restart", seconds=30)
                publication = request(url, "/api/status")[1]
                assert publication["incidents"][0]["title"] == "Integration incident"
                assert publication["generatedAt"] >= snapshots[label]
                assert request(url, "/feed.xml")[0] == 200
            time.sleep(65)
            for url in urls.values():
                stale = request(url, "/api/status")[1]
                assert stale["dataFreshness"]["stale"]
                assert stale["summary"]["status"] == "unknown"
                assert "Integration incident" in request(url, "/feed.xml")[1]
            print("Primary/standby publishing, draft privacy, standby writes, and database-independent restart passed.")
    finally:
        if network_created:
            subprocess.run(["docker", "network", "rm", name], capture_output=True, check=False)


def database_ready(container):
    try:
        return docker("exec", container, "sh", "-ec",
                      'PGPASSWORD="$(cat /run/secrets/app-password)" psql '
                      '-h 127.0.0.1 -U status_app -d status -Atc "SELECT 1"') == "1"
    except subprocess.CalledProcessError:
        return False


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
    return status == 200 and not body["dataFreshness"]["stale"] and not body["generatedAt"].startswith("1970-")


if __name__ == "__main__":
    main()
