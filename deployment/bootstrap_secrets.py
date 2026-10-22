"""Create missing database credentials without rotating an existing status database."""

import json
from pathlib import Path
import secrets
import subprocess
import tempfile


SECRET_NAMES = ("postgres-password", "app-password", "replication-password")


def az_json(*arguments):
    return json.loads(subprocess.check_output(
        ["az", *arguments, "--only-show-errors", "--output", "json"], text=True
    ))


def discover_vault():
    vaults = az_json("keyvault", "list", "--resource-group", "rg-platform-status-web-prd-swedencentral")
    matches = [
        vault["name"] for vault in vaults
        if (vault.get("tags") or {}).get("Workload") == "platform-status-web"
        and (vault.get("tags") or {}).get("Environment") == "prd"
        and (vault.get("tags") or {}).get("Purpose") == "workload-secrets"
    ]
    if len(matches) != 1:
        raise RuntimeError(f"Expected exactly one platform-owned status runtime vault; found {len(matches)}.")
    return matches[0]


def set_secret(vault, name, value, *, expires=None, tags=()):
    with tempfile.TemporaryDirectory(prefix="status-secret-") as directory:
        filename = Path(directory) / "value"
        filename.write_text(value, encoding="utf-8")
        filename.chmod(0o600)
        command = ["az", "keyvault", "secret", "set", "--vault-name", vault, "--name", name,
                   "--file", str(filename), "--only-show-errors", "--output", "none"]
        if expires is not None:
            command.extend(["--expires", expires])
        if tags:
            command.extend(["--tags", *tags])
        subprocess.run(command, check=True)


def seed_database(vault):
    metadata = {entry["name"]: entry for entry in az_json("keyvault", "secret", "list", "--vault-name", vault)}
    for name in set(SECRET_NAMES) & metadata.keys():
        attributes = metadata[name]["attributes"]
        if attributes.get("enabled") is not True or attributes.get("expires") is not None:
            raise RuntimeError(f"Existing credential {name} is disabled or expiring; explicit recovery is required.")
    for name in SECRET_NAMES:
        if name in metadata:
            print(f"Retained existing {name}.")
        else:
            set_secret(vault, name, secrets.token_urlsafe(48))
            print(f"Created {name}.")


def reviewed_vault():
    vault = discover_vault()
    policy = json.loads((Path(__file__).resolve().parents[1] / ".github" / "validation" / "policy.json").read_text())
    bindings = {entry["id"]: (entry["vaultName"], entry["vaultSecret"]) for entry in policy["allowedSecrets"]}
    if any(bindings.get(name) != (vault, name) for name in SECRET_NAMES):
        raise RuntimeError("Discovered status vault does not match the reviewed workload secret bindings.")
    return vault


if __name__ == "__main__":
    seed_database(reviewed_vault())
