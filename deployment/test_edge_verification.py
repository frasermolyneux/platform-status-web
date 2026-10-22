import io
import json
import os
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.error import HTTPError


class EdgeVerificationTests(unittest.TestCase):
    def test_http_failures_always_retain_endpoint_and_status(self):
        workflow = (Path(__file__).resolve().parents[1] / ".github" / "workflows" / "status-edge.yml").read_text()
        body = workflow.split("          python3 - <<'PY'\n", 1)[1].split("\n          PY", 1)[0]
        code = compile("\n".join(line[10:] for line in body.splitlines()), "edge-verification", "exec")
        edge = {"account_id": "account", "pool_id": "pool", "monitor_id": "monitor"}
        for payload, expected in (
            (b"", "No structured JSON error details"),
            (b"<html>Not Found</html>", "No structured JSON error details"),
            (b'{"errors":[{"code":1002,"message":"Not found"}]}', "1002"),
            (b"[]", "not an object"),
        ):
            with self.subTest(payload=payload):
                error = HTTPError("https://api.cloudflare.com/test", 404, "Not Found", {}, io.BytesIO(payload))
                with (patch.dict(os.environ, {"CLOUDFLARE_API_TOKEN": "test-token"}),
                      patch("subprocess.check_output", return_value=json.dumps(edge)),
                      patch("urllib.request.urlopen", side_effect=error),
                      self.assertRaisesRegex(RuntimeError, "/pools/pool: HTTP 404; .*" + expected)):
                    exec(code, {})
