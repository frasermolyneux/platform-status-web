"""Compare all image inputs with the actual source of the last successful publication."""

import json
import os
from pathlib import Path
import re
import subprocess


def gh_json(endpoint):
    return json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))


def last_published_revision(repository):
    page = 1
    while True:
        runs = gh_json(
            f"repos/{repository}/actions/workflows/deploy-prd.yml/runs"
            f"?branch=main&status=success&per_page=100&page={page}"
        )["workflow_runs"]
        if not runs:
            return None
        for run in runs:
            artifacts = gh_json(f"repos/{repository}/actions/runs/{run['id']}/artifacts?per_page=100")
            if artifacts["total_count"] > 100:
                raise RuntimeError("Unexpected publication artifact count; review source provenance.")
            revisions = []
            legacy = False
            for artifact in artifacts["artifacts"]:
                if artifact["expired"]:
                    continue
                match = re.fullmatch(r"status-image-digests-([0-9a-f]{40})", artifact["name"])
                if match:
                    revisions.append(match[1])
                legacy |= artifact["name"] == "status-image-digests"
            if len(revisions) > 1:
                raise RuntimeError("A publication run records multiple source revisions.")
            if revisions:
                return revisions[0]
            if legacy:
                return None
        page += 1


def should_publish(repository, pattern):
    previous = last_published_revision(repository)
    if previous is None:
        print("No retained source publication record; performing a complete image publication.")
        return True
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", previous, "HEAD"], text=True
    ).splitlines()
    return any(re.search(pattern, path) for path in changed)


if __name__ == "__main__":
    pattern = (Path(__file__).resolve().parents[1] / ".github" / "image-input-pattern.txt").read_text().strip()
    if not pattern:
        raise RuntimeError("Image input pattern cannot be empty.")
    re.compile(pattern)
    publish = os.environ["FORCE_PUBLISH"] == "true" or should_publish(os.environ["GITHUB_REPOSITORY"], pattern)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"publish={str(publish).lower()}\n")
    if not publish:
        print("All image inputs match the last successful publication; retaining its digests.")
