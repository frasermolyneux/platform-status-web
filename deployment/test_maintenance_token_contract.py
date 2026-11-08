import json
from pathlib import Path
import unittest


class MaintenanceTokenContractTests(unittest.TestCase):
    def test_workload_and_policy_bind_the_maintenance_token(self):
        root = Path(__file__).resolve().parents[1]
        manifest = json.loads((root / "workload.json").read_text())
        policy = json.loads((root / ".github" / "validation" / "policy.json").read_text())
        name = "cloudflare-maintenance-traffic-token"

        service = next(item for item in manifest["services"] if item["name"] == "platform-status-service")
        self.assertIn({"id": name, "target": name}, service["secrets"])
        self.assertNotIn("traffic-api-token", [item["id"] for item in service["secrets"]])

        binding = next(item for item in policy["allowedSecrets"] if item["id"] == name)
        self.assertEqual(binding["vaultSecret"], name)
        self.assertEqual(binding["hostPath"], f"/etc/platform-workloads/platform-status-web/{name}")
        self.assertEqual(binding["groupId"], 10001)
        self.assertEqual(binding["mode"], "0640")
        self.assertNotIn("traffic-api-token", [item["id"] for item in policy["allowedSecrets"]])


if __name__ == "__main__":
    unittest.main()
