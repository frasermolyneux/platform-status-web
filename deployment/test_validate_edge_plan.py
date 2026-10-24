import copy
import unittest

from validate_edge_plan import validate_maps_tls, validate_plan, validate_preflight


def staged_plan():
    values = {
        "cloudflare_load_balancer.status": {
            "enabled": False, "name": "mxstatus.io", "zone_id": "mx-zone", "proxied": True,
            "steering_policy": "off", "session_affinity": "none",
            "default_pools": ["pool-id"], "fallback_pool": "pool-id",
        },
        "cloudflare_load_balancer_pool.status": {
            "name": "platform-status-origins", "account_id": "account",
            "id": "pool-id", "monitor": "monitor-id",
            "check_regions": ["ENAM"], "enabled": True, "minimum_origins": 1,
            "origin_steering": {"policy": "random"},
            "origins": [
                {"name": "ns346663", "address": "5.196.80.194", "enabled": True, "weight": 1},
                {"name": "ns5019527", "address": "15.235.119.97", "enabled": True, "weight": 1},
            ],
        },
        "cloudflare_load_balancer_monitor.status": {
            "account_id": "account", "type": "https", "method": "GET", "path": "/health/ready",
            "id": "monitor-id",
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


def maps_tls_plan():
    plan = staged_plan()
    plan["resource_changes"][0]["change"]["after"]["enabled"] = True
    for resource in plan["resource_changes"]:
        change = resource["change"]
        change["actions"] = ["no-op"]
        change["before"] = copy.deepcopy(change["after"])
    change = plan["resource_changes"][-1]["change"]
    change["actions"] = ["update"]
    change["before"]["id"] = change["after"]["id"] = "existing-xi-tls"
    change["before"]["description"] = "Strict origin TLS only for the public status hostname."
    change["after"]["description"] = "Strict origin TLS for status sites and the maps manager."
    change["after"]["rules"].append({
        "ref": "maps_origin_strict_tls", "description": "Validate the maps manager origin certificate.",
        "action": "set_config", "enabled": True, "expression": 'http.host eq "maps.xtremeidiots.com"',
        "action_parameters": {"ssl": "strict"},
    })
    return plan


class EdgePlanTests(unittest.TestCase):
    def test_maps_tls_appends_only_the_exact_rule_and_allows_idempotent_reconciliation(self):
        plan = maps_tls_plan()
        self.assertEqual(validate_maps_tls(plan), 1)
        change = plan["resource_changes"][-1]["change"]
        change["actions"] = ["no-op"]
        change["before"] = copy.deepcopy(change["after"])
        self.assertEqual(validate_maps_tls(plan), 0)
        self.assertEqual(validate_preflight(plan), 0)

    def test_maps_tls_rejects_status_routing_other_resources_and_existing_rule_changes(self):
        for index, field, value in ((0, "enabled", False), (1, "description", "unreviewed")):
            with self.subTest(index=index):
                plan = maps_tls_plan()
                plan["resource_changes"][index]["change"]["actions"] = ["update"]
                plan["resource_changes"][index]["change"]["after"][field] = value
                with self.assertRaises(ValueError):
                    validate_maps_tls(plan)
        plan = maps_tls_plan()
        plan["resource_changes"][-1]["change"]["after"]["rules"][0]["expression"] = "true"
        with self.assertRaises(ValueError):
            validate_maps_tls(plan)

    def test_maps_tls_rejects_broad_rules_imports_replacements_and_extra_settings(self):
        for mutation in ("wildcard", "parameter", "import", "replace", "identity"):
            with self.subTest(mutation=mutation):
                plan = maps_tls_plan()
                change = plan["resource_changes"][-1]["change"]
                if mutation == "wildcard":
                    change["after"]["rules"][1]["expression"] = 'http.host contains "xtremeidiots.com"'
                elif mutation == "parameter":
                    change["after"]["rules"][1]["action_parameters"]["disable_apps"] = True
                elif mutation == "import":
                    change["importing"] = {"id": "another-ruleset"}
                elif mutation == "replace":
                    change["actions"] = ["delete", "create"]
                else:
                    change["after"]["id"] = "another-ruleset"
                with self.assertRaises(ValueError):
                    validate_maps_tls(plan)

    def test_maps_tls_allows_generated_metadata_but_rejects_unknown_rule_scope(self):
        plan = maps_tls_plan()
        change = plan["resource_changes"][-1]["change"]
        change["after_unknown"] = {"version": True, "last_updated": True, "rules": [{}, {"id": True, "version": True}]}
        self.assertEqual(validate_maps_tls(plan), 1)
        change["after_unknown"]["rules"][1]["expression"] = True
        with self.assertRaises(ValueError):
            validate_maps_tls(plan)

    def test_maps_tls_accepts_only_the_new_rules_computed_logging_default(self):
        plan = maps_tls_plan()
        change = plan["resource_changes"][-1]["change"]
        change["after_unknown"] = {"rules": [{}, {"logging": True}]}
        change["after"]["rules"][1]["logging"] = None
        self.assertEqual(validate_maps_tls(plan), 1)
        existing = copy.deepcopy(plan)
        existing["resource_changes"][-1]["change"]["before"] = copy.deepcopy(change["after"])
        with self.assertRaises(ValueError):
            validate_maps_tls(existing)
        change["after_unknown"]["rules"][0]["logging"] = True
        with self.assertRaises(ValueError):
            validate_maps_tls(plan)
        change["after_unknown"]["rules"][0].clear()
        change["after"]["rules"][1]["logging"] = {"enabled": True}
        with self.assertRaises(ValueError):
            validate_maps_tls(plan)
        change["after"]["rules"][1]["logging"] = {"enabled": False}
        change["after_unknown"] = {}
        self.assertEqual(validate_maps_tls(plan), 1)

    def test_refresh_only_preflight_validates_actual_staged_values_without_resource_changes(self):
        plan = staged_plan()
        plan["planned_values"]["root_module"] = {"resources": [
            {"address": resource["address"], "mode": resource["mode"], "values": resource["change"]["after"]}
            for resource in plan.pop("resource_changes")
        ]}
        self.assertEqual(validate_preflight(plan), 0)
        plan["prior_state"] = {"values": plan.pop("planned_values")}
        plan["planned_values"] = {"root_module": {}}
        self.assertEqual(validate_preflight(plan), 0)
        plan["resource_drift"] = [{"address": "cloudflare_load_balancer_pool.status",
                                  "mode": "managed", "change": {"actions": ["update"]}}]
        with self.assertRaises(ValueError):
            validate_preflight(plan)

    def test_preflight_resumes_only_known_enablement_drift_after_interruption(self):
        plan = activation_plan()
        change = plan["resource_changes"][0]["change"]
        change["after_unknown"] = {}
        change["after"]["modified_on"] = "new-time"
        plan["resource_drift"] = [copy.deepcopy(plan["resource_changes"][0])]
        for resource in plan["resource_changes"]:
            resource["change"]["actions"] = ["no-op"]
        self.assertEqual(validate_preflight(plan), 0)
        self.assertEqual(validate_plan(plan, activate=True), 0)
        drift = plan["resource_drift"][0]["change"]
        for key, value in (("country_pools", {"US": ["another-pool"]}), ("enabled", False)):
            with self.subTest(key=key):
                changed = copy.deepcopy(plan)
                changed["resource_drift"][0]["change"]["after"][key] = value
                with self.assertRaises(ValueError):
                    validate_preflight(changed)
        drift["after_unknown"] = {"country_pools": True}
        with self.assertRaises(ValueError):
            validate_preflight(plan)
        with self.assertRaises(ValueError):
            validate_plan(plan, activate=True)

    def test_reconciliation_rejects_unreviewed_drift_even_when_resource_actions_are_noop(self):
        for index, field, value in (
            (0, "country_pools", {"US": ["another-pool"]}),
            (1, "description", "unreviewed-change"),
        ):
            with self.subTest(index=index, field=field):
                plan = activation_plan()
                for resource in plan["resource_changes"]:
                    change = resource["change"]
                    change["actions"] = ["no-op"]
                    change["after_unknown"] = {}
                    change["before"] = copy.deepcopy(change["after"])
                resource = plan["resource_changes"][index]
                resource["change"]["before"][field] = value
                resource["change"]["after"][field] = value
                drift = copy.deepcopy(resource)
                drift["change"]["actions"] = ["update"]
                drift["change"]["before"][field] = None
                plan["resource_drift"] = [drift]
                with self.assertRaises(ValueError):
                    validate_plan(plan, activate=True)

    def test_rejects_detached_or_substituted_links_in_initial_and_partial_plans(self):
        for actions in (["create"], ["no-op"]):
            for index, field, value in (
                (1, "monitor", None), (1, "monitor", "another-monitor"),
                (0, "default_pools", []), (0, "default_pools", ["another-pool"]),
                (0, "fallback_pool", "another-pool"),
            ):
                with self.subTest(actions=actions, field=field, value=value):
                    plan = staged_plan()
                    plan["resource_changes"][2]["change"]["actions"] = actions
                    plan["resource_changes"][index]["change"]["after"][field] = value
                    with self.assertRaises(ValueError):
                        validate_plan(plan)

    def test_unknown_monitor_id_requires_exact_reference_and_unknown_association(self):
        plan = staged_plan()
        monitor = plan["resource_changes"][2]["change"]
        monitor["after"].pop("id")
        monitor["after_unknown"] = {"id": True}
        pool = plan["resource_changes"][1]["change"]
        pool["after"]["monitor"] = None
        pool["after_unknown"] = {"monitor": True}
        expression = {"references": [
            "cloudflare_load_balancer_monitor.status.id", "cloudflare_load_balancer_monitor.status"]}
        plan["configuration"] = {"root_module": {"resources": [{
            "address": "cloudflare_load_balancer_pool.status", "expressions": {"monitor": expression},
        }]}}
        self.assertEqual(validate_plan(plan), 5)
        pool["after_unknown"]["monitor"] = False
        with self.assertRaises(ValueError):
            validate_plan(plan)
        pool["after_unknown"]["monitor"] = True
        expression["references"] = ["cloudflare_load_balancer_monitor.other.id"]
        with self.assertRaises(ValueError):
            validate_plan(plan)

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
        plan["resource_changes"][0]["change"]["after_unknown"] = {}
        self.assertEqual(validate_plan(plan, activate=True), 0)

    def test_activation_accepts_provider_computed_nonrouting_metadata_only(self):
        plan = activation_plan()
        change = plan["resource_changes"][0]["change"]
        for key, before in (("created_on", "creation-timestamp"), ("zone_name", "mxstatus.io")):
            change["before"][key] = before
            change["after"][key] = None
            change["after_unknown"][key] = True
        self.assertEqual(validate_plan(plan, activate=True), 0)

    def test_activation_rejects_unknown_optional_nested_and_other_resource_settings(self):
        for index, field, unknown in (
            (0, "adaptive_routing", True), (0, "country_pools", {"US": [True]}),
            (0, "new_optional_field", True), (1, "monitor", True),
        ):
            with self.subTest(index=index, field=field):
                plan = activation_plan()
                change = plan["resource_changes"][index]["change"]
                change["before"][field] = None
                change["after"].pop(field, None)
                change.setdefault("after_unknown", {})[field] = unknown
                with self.assertRaises(ValueError):
                    validate_plan(plan, activate=True)

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
