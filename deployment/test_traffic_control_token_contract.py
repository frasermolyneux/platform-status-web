import json
from pathlib import Path
import unittest


class TrafficControlTokenContractTests(unittest.TestCase):
    def test_workload_and_policy_bind_the_traffic_control_token(self):
        root = Path(__file__).resolve().parents[1]
        manifest = json.loads((root / "workload.json").read_text(encoding="utf-8"))
        policy = json.loads((root / ".github" / "validation" / "policy.json").read_text(encoding="utf-8"))
        name = "cloudflare-traffic-control-token"

        service = next((item for item in manifest["services"] if item["name"] == "platform-status-service"), None)
        self.assertIsNotNone(service, "Workload manifest is missing platform-status-service")
        self.assertTrue(any(item["id"] == name and item["target"] == name for item in service["secrets"]),
                        f"Workload service is missing the {name} secret mount")
        binding = next((item for item in policy["allowedSecrets"] if item["id"] == name), None)
        self.assertIsNotNone(binding, f"Validation policy is missing {name}")
        self.assertEqual(binding["vaultSecret"], name)
        self.assertEqual(binding["hostPath"], f"/etc/platform-workloads/platform-status-web/{name}")
        self.assertEqual(binding["groupId"], 10001)
        self.assertEqual(binding["mode"], "0640")

if __name__ == "__main__":
    unittest.main()
