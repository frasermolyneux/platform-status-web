"""Reject changes outside the reviewed Cloudflare staging or activation boundary."""

import argparse
import copy
import json
import sys


COMPUTED_LB_METADATA = {"created_on", "modified_on", "zone_name"}


def contains_unknown(value):
    if isinstance(value, dict):
        return any(contains_unknown(item) for item in value.values())
    if isinstance(value, list):
        return any(contains_unknown(item) for item in value)
    return value is True


def validate_drift(plan, *, allow_enablement=False):
    for resource in plan.get("resource_drift", []):
        if resource["mode"] != "managed" or resource["change"]["actions"] == ["no-op"]:
            continue
        change = resource["change"]
        before, after = change.get("before"), change.get("after")
        if (not allow_enablement or resource["address"] != "cloudflare_load_balancer.status"
                or change["actions"] != ["update"] or before is None or after is None
                or before.get("enabled") is not False or after.get("enabled") is not True
                or contains_unknown(change.get("after_unknown", {}))
                or any(before.get(key) != after.get(key)
                       for key in before.keys() | after.keys() if key not in {"enabled", "modified_on"})):
            raise ValueError("The edge plan must contain no unreviewed managed drift.")


def validate_plan(plan, *, activate=False):
    validate_drift(plan, allow_enablement=activate)
    expected = {
        "cloudflare_load_balancer.status",
        "cloudflare_load_balancer_pool.status",
        "cloudflare_load_balancer_monitor.status",
        'cloudflare_ruleset.status_tls["mxstatus.io"]',
        'cloudflare_ruleset.status_tls["xtremeidiots.com"]',
    }
    managed = [resource for resource in plan["resource_changes"] if resource["mode"] == "managed"]
    if len(managed) != len(expected) or {resource["address"] for resource in managed} != expected:
        raise ValueError("The edge stage must contain only the five approved managed resources.")
    resources = {}
    changes = {resource["address"]: resource["change"] for resource in managed}
    for resource in managed:
        change = resource["change"]
        allowed = (["create"], ["no-op"])
        if activate:
            allowed = (["no-op"], ["update"]) if resource["address"] == "cloudflare_load_balancer.status" else (["no-op"],)
        if change["actions"] not in allowed or change.get("importing") is not None:
            raise ValueError(f"Unapproved update, deletion, replacement or import: {resource['address']}")
        if activate:
            unknowns = dict(change.get("after_unknown", {}))
            if resource["address"] == "cloudflare_load_balancer.status" and change["actions"] == ["update"]:
                for key in COMPUTED_LB_METADATA:
                    if unknowns.get(key) is True:
                        unknowns.pop(key)
            if contains_unknown(unknowns):
                raise ValueError(f"Activation requires known settings except computed non-routing metadata: {resource['address']}")
        if activate and change["actions"] == ["update"]:
            before, after = change["before"], change["after"]
            if before["enabled"] is not False or after["enabled"] is not True:
                raise ValueError("The only approved update enables the previously staged MX load balancer.")
            for key in before.keys() | after.keys():
                if key == "enabled" or (
                    key in COMPUTED_LB_METADATA and change.get("after_unknown", {}).get(key) is True
                ):
                    continue
                if before.get(key) != after.get(key):
                    raise ValueError(f"Activation must not change another load-balancer setting: {key}")
        resources[resource["address"]] = change["after"]
    configuration = {
        resource["address"]: resource
        for resource in plan.get("configuration", {}).get("root_module", {}).get("resources", [])
    }
    for owner, field, target, many in (
        ("cloudflare_load_balancer_pool.status", "monitor", "cloudflare_load_balancer_monitor.status", False),
        ("cloudflare_load_balancer.status", "default_pools", "cloudflare_load_balancer_pool.status", True),
        ("cloudflare_load_balancer.status", "fallback_pool", "cloudflare_load_balancer_pool.status", False),
    ):
        target_id = resources[target].get("id")
        if target_id:
            expected_value = [target_id] if many else target_id
            if resources[owner].get(field) != expected_value:
                raise ValueError(f"The approved association is required: {owner}.{field} -> {target}")
        else:
            unknown = changes[owner].get("after_unknown", {}).get(field)
            references = configuration.get(owner, {}).get("expressions", {}).get(field, {}).get("references", [])
            if (changes[target].get("after_unknown", {}).get("id") is not True
                    or not (unknown is True or (many and unknown == [True]))
                    or f"{target}.id" not in references
                    or not set(references) <= {target, f"{target}.id"}):
                raise ValueError(f"Unknown IDs require the exact approved configuration reference: {owner}.{field}")
    load_balancer = resources["cloudflare_load_balancer.status"]
    if (load_balancer["enabled"] is not activate or load_balancer["name"] != "mxstatus.io"
            or load_balancer["proxied"] is not True or load_balancer["steering_policy"] != "off"
            or load_balancer["session_affinity"] != "none"):
        raise ValueError("The single MX load balancer must retain the reviewed routing state without paid steering or affinity.")
    pool = resources["cloudflare_load_balancer_pool.status"]
    if (pool["name"] != "platform-status-origins"
            or pool["check_regions"] != ["ENAM"] or pool["enabled"] is not True
            or pool["minimum_origins"] != 1 or pool["origin_steering"]["policy"] != "random"):
        raise ValueError("The enabled pool must retain the approved single region and simple origin steering.")
    origins = pool["origins"]
    if (len(origins) != 2
            or {(origin["name"], origin["address"]) for origin in origins} != {
                ("ns346663", "5.196.80.194"), ("ns5019527", "15.235.119.97")}
            or any(origin["enabled"] is not True or origin["weight"] != 1 or origin.get("header")
                   for origin in origins)):
        raise ValueError("Exactly the two approved enabled, equal-weight origins without Host overrides are required.")
    monitor = resources["cloudflare_load_balancer_monitor.status"]
    expected_monitor = {
        "type": "https", "method": "GET", "path": "/health/ready",
        "expected_codes": "200", "expected_body": "\"status\":\"ready\"",
        "header": {"Host": ["mxstatus.io"]}, "probe_zone": "mxstatus.io",
        "allow_insecure": False, "follow_redirects": False,
        "interval": 60, "timeout": 5, "retries": 1,
        "consecutive_up": 2, "consecutive_down": 2,
    }
    if (any(monitor[key] != value for key, value in expected_monitor.items())
            or monitor.get("port") not in (None, 443)):
        raise ValueError("The strict HTTPS monitor must retain the exact publication-readiness and 60-second checking contract.")
    preflight = plan["planned_values"]["outputs"]["status_edge_preflight"]["value"]
    if (pool["account_id"] != preflight["mxstatus.io"]["account_id"]
            or monitor["account_id"] != preflight["mxstatus.io"]["account_id"]):
        raise ValueError("The pool and monitor must remain in the preflight-verified account.")
    if load_balancer["zone_id"] != preflight["mxstatus.io"]["zone_id"]:
        raise ValueError("The load balancer must belong to the preflight-verified MX zone.")
    for zone, hostname in (("mxstatus.io", "mxstatus.io"), ("xtremeidiots.com", "status.xtremeidiots.com")):
        ruleset = resources[f'cloudflare_ruleset.status_tls["{zone}"]']
        rules = ruleset["rules"]
        if (ruleset["zone_id"] != preflight[zone]["zone_id"]
                or ruleset["name"] != "platform-status-origin-tls" or ruleset["kind"] != "zone"
                or ruleset["phase"] != "http_config_settings"
                or len(rules) not in ((1, 2) if zone == "xtremeidiots.com" else (1,))):
            raise ValueError(f"Unexpected TLS configuration scope for {zone}.")
        rule = rules[0]
        if (rule["action"] != "set_config" or rule["enabled"] is not True
                or rule["expression"] != f'http.host eq "{hostname}"'
                or rule["action_parameters"]["ssl"] != "strict"):
            raise ValueError(f"TLS must remain strict and limited to {hostname}.")
        if len(rules) == 2:
            maps = rules[1]
            if (maps.get("ref") != "maps_origin_strict_tls" or maps["action"] != "set_config"
                    or maps["enabled"] is not True or maps["expression"] != 'http.host eq "maps.xtremeidiots.com"'
                    or maps["action_parameters"]["ssl"] != "strict"
                    or any(value is not None for key, value in maps["action_parameters"].items() if key != "ssl")
                    or maps.get("logging") not in (None, {"enabled": False})
                    or any(value is not None for key, value in maps.items() if key not in {
                        "ref", "description", "action", "enabled", "expression", "action_parameters",
                        "id", "version", "last_updated", "logging"})):
                raise ValueError("The additional TLS rule must be strict and limited to the maps manager.")
    return sum(resource["change"]["actions"] == ["create"] for resource in managed)


def without_computed_metadata(value):
    if isinstance(value, dict):
        return {key: without_computed_metadata(item) for key, item in value.items()
                if key not in {"id", "version", "last_updated"}}
    if isinstance(value, list):
        return [without_computed_metadata(item) for item in value]
    return value


def validate_maps_tls(plan):
    validate_drift(plan)
    target = 'cloudflare_ruleset.status_tls["xtremeidiots.com"]'
    normalized = copy.deepcopy(plan)
    changes = [resource for resource in normalized["resource_changes"] if resource["mode"] == "managed"]
    matches = [resource for resource in changes if resource["address"] == target]
    if len(matches) != 1:
        raise ValueError("The existing XtremeIdiots TLS entrypoint is required.")
    for resource in changes:
        change = resource["change"]
        allowed = (["no-op"], ["update"]) if resource["address"] == target else (["no-op"],)
        if change["actions"] not in allowed or change.get("importing") is not None:
            raise ValueError("Maps TLS must not change routing, monitors, other zones or resource ownership.")
    change = matches[0]["change"]
    before, after = change.get("before"), change["after"]
    if before is None or len(after["rules"]) != 2 or len(before["rules"]) not in (1, 2):
        raise ValueError("Maps TLS may only append its exact rule to the existing status entrypoint.")
    if before.get("id") != after.get("id"):
        raise ValueError("Maps TLS must preserve the existing ruleset identity.")
    unknowns = without_computed_metadata(change.get("after_unknown", {}))
    rules = unknowns.get("rules")
    if len(before["rules"]) == 1 and isinstance(rules, list) and len(rules) == 2 and isinstance(rules[1], dict):
        if rules[1].get("logging") is True:
            rules[1].pop("logging")
    if contains_unknown(unknowns):
        raise ValueError("Maps TLS requires known scope and settings except generated metadata and new-rule logging defaults.")
    old, new = without_computed_metadata(before), without_computed_metadata(after)
    expected_description = "Strict origin TLS for status sites and the maps manager."
    if new.get("description") != expected_description or old.get("description") not in (
            expected_description, "Strict origin TLS only for the public status hostname."):
        raise ValueError("Unexpected maps TLS entrypoint description.")
    old.pop("description")
    new.pop("description")
    old_rules, new_rules = old.pop("rules"), new.pop("rules")
    if old != new or old_rules != new_rules[:len(old_rules)]:
        raise ValueError("Maps TLS must preserve the complete existing entrypoint and status rule.")
    updates = int(change["actions"] == ["update"])
    change["actions"] = ["no-op"]
    change["after_unknown"] = {}
    validate_plan(normalized, activate=True)
    return updates


def validate_preflight(plan):
    validate_drift(plan, allow_enablement=True)
    if "resource_changes" not in plan:
        values = plan["planned_values"]
        if "resources" not in values.get("root_module", {}):
            values = plan["prior_state"]["values"]
            print("No-change refresh-only plan: validating its saved prior state after the no-drift check.")
            plan = dict(plan, planned_values=values)
        plan = dict(plan, resource_changes=[
            {"address": resource["address"], "mode": resource["mode"],
             "change": {"actions": ["no-op"], "after": resource["values"]}}
            for resource in values["root_module"]["resources"]
        ])
    managed = [resource for resource in plan["resource_changes"] if resource["mode"] == "managed"]
    if any(resource["change"]["actions"] != ["no-op"] for resource in managed):
        raise ValueError("Activation preflight must contain no managed changes.")
    load_balancer = next(resource for resource in managed
                         if resource["address"] == "cloudflare_load_balancer.status")
    enabled = load_balancer["change"]["after"]["enabled"]
    if not isinstance(enabled, bool):
        raise ValueError("Activation preflight requires a known load-balancer enabled state.")
    return validate_plan(plan, activate=enabled)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--activate", action="store_true")
    parser.add_argument("--prepare-activation", action="store_true")
    parser.add_argument("--maps-tls", action="store_true")
    args = parser.parse_args()
    plan = json.load(sys.stdin)
    if args.maps_tls:
        updates = validate_maps_tls(plan)
        print(f"Approved maps TLS: {updates} existing-entrypoint updates; status routing remains unchanged.")
    elif args.prepare_activation:
        validate_preflight(plan)
        print("Verified unchanged staged resources; this refresh-only plan cannot enable routing.")
    elif args.activate:
        validate_plan(plan, activate=True)
        updates = sum(resource["mode"] == "managed" and resource["change"]["actions"] == ["update"]
                      for resource in plan["resource_changes"])
        print(f"Approved MX activation: {updates} enablement updates, {5 - updates} unchanged resources.")
    else:
        creations = validate_plan(plan)
        print(f"Approved disabled edge stage: {creations} creations, {5 - creations} unchanged resources.")
