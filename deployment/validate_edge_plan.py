"""Reject changes outside the reviewed Cloudflare staging or activation boundary."""

import argparse
import json
import sys


def contains_unknown(value):
    if isinstance(value, dict):
        return any(contains_unknown(item) for item in value.values())
    if isinstance(value, list):
        return any(contains_unknown(item) for item in value)
    return value is True


def validate_plan(plan, *, activate=False):
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
                if unknowns.get("modified_on") is True:
                    unknowns.pop("modified_on")
            if contains_unknown(unknowns):
                raise ValueError(f"Activation requires known settings except the computed modification timestamp: {resource['address']}")
        if activate and change["actions"] == ["update"]:
            before, after = change["before"], change["after"]
            if before["enabled"] is not False or after["enabled"] is not True:
                raise ValueError("The only approved update enables the previously staged MX load balancer.")
            for key in before.keys() | after.keys():
                if key == "enabled" or (
                    key == "modified_on" and change.get("after_unknown", {}).get(key) is True
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
                or ruleset["phase"] != "http_config_settings" or len(rules) != 1):
            raise ValueError(f"Unexpected TLS configuration scope for {zone}.")
        rule = rules[0]
        if (rule["action"] != "set_config" or rule["enabled"] is not True
                or rule["expression"] != f'http.host eq "{hostname}"'
                or rule["action_parameters"]["ssl"] != "strict"):
            raise ValueError(f"TLS must remain strict and limited to {hostname}.")
    return sum(resource["change"]["actions"] == ["create"] for resource in managed)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--activate", action="store_true")
    args = parser.parse_args()
    plan = json.load(sys.stdin)
    creations = validate_plan(plan, activate=args.activate)
    if args.activate:
        updates = sum(resource["mode"] == "managed" and resource["change"]["actions"] == ["update"]
                      for resource in plan["resource_changes"])
        print(f"Approved MX activation: {updates} enablement updates, {5 - updates} unchanged resources.")
    else:
        print(f"Approved disabled edge stage: {creations} creations, {5 - creations} unchanged resources.")
