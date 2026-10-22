import copy
import unittest

from validate_edge_plan import validate_plan


def staged_plan():
    values = {
        "cloudflare_load_balancer.status": {
            "enabled": False, "name": "mxstatus.io", "zone_id": "mx-zone", "proxied": True,
            "steering_policy": "off", "session_affinity": "none",
        },
        "cloudflare_load_balancer_pool.status": {
            "name": "platform-status-origins", "account_id": "account",
            "check_regions": ["ENAM"], "enabled": True, "minimum_origins": 1,
            "origin_steering": {"policy": "random"},
            "origins": [
                {"name": "ns346663", "address": "5.196.80.194", "enabled": True, "weight": 1},
                {"name": "ns5019527", "address": "15.235.119.97", "enabled": True, "weight": 1},
            ],
        },
        "cloudflare_load_balancer_monitor.status": {
            "account_id": "account", "type": "https", "method": "GET", "path": "/health/ready",
            "expected_codes": "200", "expected_body": "\"status\":\"ready\"",
            "header": {"Host": ["mxstatus.io"]}, "probe_zone": "mxstatus.io",
            "allow_insecure": False, "follow_redirects": False,
            "interval": 60, "timeout": 5, "retries": 1, "consecutive_up": 2, "consecutive_down": 2,
        },
    }
    for zone, hostname in (("mxstatus.io", "mxstatus.io"), ("xtremeidiots.com", "status.xtremeidiots.com")):
        values[f'cloudflare_ruleset.status_tls["{zone}"]'] = {
            "zone_id": "mx-zone" if zone == "mxstatus.io" else "xi-zone",
            "name": "platform-status-origin-tls",
            "kind": "zone", "phase": "http_config_settings",
            "rules": [{
                "action": "set_config", "enabled": True, "expression": f'http.host eq "{hostname}"',
                "action_parameters": {"ssl": "strict"},
            }],
        }
    return {
        "resource_changes": [
            {"mode": "managed", "address": address, "change": {"actions": ["create"], "after": after}}
            for address, after in values.items()
        ],
        "planned_values": {"outputs": {"status_edge_preflight": {"value": {
            "mxstatus.io": {"zone_id": "mx-zone", "account_id": "account"},
            "xtremeidiots.com": {"zone_id": "xi-zone", "account_id": "account"},
        }}}},
    }


def activation_plan():
    plan = staged_plan()
    for resource in plan["resource_changes"]:
        change = resource["change"]
        change["actions"] = ["no-op"]
        change["before"] = copy.deepcopy(change["after"])
    load_balancer = plan["resource_changes"][0]["change"]
    load_balancer["actions"] = ["update"]
    load_balancer["after"]["enabled"] = True
    load_balancer["before"]["modified_on"] = "previous-timestamp"
    load_balancer["after"]["modified_on"] = None
    load_balancer["after_unknown"] = {"modified_on": True}
    return plan


class EdgePlanTests(unittest.TestCase):
    def test_accepts_disabled_stage_and_safe_partial_apply_retry(self):
        plan = staged_plan()
        self.assertEqual(validate_plan(plan), 5)
        plan["resource_changes"][0]["change"]["actions"] = ["no-op"]
        plan["resource_changes"].append({"mode": "data", "change": {"actions": ["read"]}})
        self.assertEqual(validate_plan(plan), 4)

    def test_rejects_resource_drift_deletion_replacement_import_and_extra_resources(self):
        for change in ({"actions": ["update"]}, {"actions": ["delete"]},
                       {"actions": ["delete", "create"]}, {"importing": {"id": "existing"}}):
            with self.subTest(change=change):
                plan = staged_plan()
                plan["resource_changes"][0]["change"].update(change)
                with self.assertRaises(ValueError):
                    validate_plan(plan)
        plan = staged_plan()
        plan["resource_changes"].append(copy.deepcopy(plan["resource_changes"][0]))
        with self.assertRaises(ValueError):
            validate_plan(plan)

    def test_accepts_only_staged_load_balancer_activation_and_unchanged_retry(self):
        plan = activation_plan()
        self.assertEqual(validate_plan(plan, activate=True), 0)
        plan["resource_changes"][0]["change"]["actions"] = ["no-op"]
        plan["resource_changes"][0]["change"]["before"] = copy.deepcopy(
            plan["resource_changes"][0]["change"]["after"])
        self.assertEqual(validate_plan(plan, activate=True), 0)

    def test_refuses_activation_before_staging_and_changes_beyond_enablement(self):
        with self.assertRaises(ValueError):
            validate_plan(staged_plan(), activate=True)
        for index, key, value in (
            (0, "description", "unreviewed-change"),
            (0, "default_pools", ["another-pool"]),
        ):
            with self.subTest(key=key):
                plan = activation_plan()
                plan["resource_changes"][index]["change"]["after"][key] = value
                with self.assertRaises(ValueError):
                    validate_plan(plan, activate=True)
        plan = activation_plan()
        plan["resource_changes"][1]["change"]["actions"] = ["update"]
        with self.assertRaises(ValueError):
            validate_plan(plan, activate=True)

    def test_rejects_activation_and_weakened_or_broadened_tls(self):
        plan = staged_plan()
        plan["resource_changes"][0]["change"]["after"]["enabled"] = True
        with self.assertRaises(ValueError):
            validate_plan(plan)
        for key, value in (("expression", "true"), ("action_parameters", {"ssl": "full"})):
            with self.subTest(key=key):
                plan = staged_plan()
                plan["resource_changes"][-1]["change"]["after"]["rules"][0][key] = value
                with self.assertRaises(ValueError):
                    validate_plan(plan)

    def test_rejects_added_substituted_disabled_or_host_overridden_origins(self):
        plan = staged_plan()
        plan["resource_changes"][1]["change"]["after"]["origins"].append(
            {"name": "third", "address": "192.0.2.1", "enabled": True, "weight": 1})
        with self.assertRaises(ValueError):
            validate_plan(plan)
        for key, value in (("address", "192.0.2.1"), ("enabled", False),
                           ("weight", 2), ("header", {"Host": ["another.example"]})):
            with self.subTest(key=key):
                plan = staged_plan()
                plan["resource_changes"][1]["change"]["after"]["origins"][0][key] = value
                with self.assertRaises(ValueError):
                    validate_plan(plan)

    def test_rejects_liveness_or_weakened_publication_readiness_monitors(self):
        for key, value in (
            ("method", "HEAD"), ("path", "/health/live"), ("expected_codes", "2xx"),
            ("expected_body", ""), ("expected_body", "\"status\":\"live\""),
            ("header", {"Host": ["another.example"]}), ("probe_zone", "another.example"),
            ("follow_redirects", True), ("allow_insecure", True), ("interval", 15), ("port", 8443),
        ):
            with self.subTest(key=key, value=value):
                plan = staged_plan()
                plan["resource_changes"][2]["change"]["after"][key] = value
                with self.assertRaises(ValueError):
                    validate_plan(plan)
