"""Reject changes outside the approved disabled Cloudflare staging contract."""

import json
import sys


def validate_plan(plan):
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
    for resource in managed:
        change = resource["change"]
        if change["actions"] not in (["create"], ["no-op"]) or change.get("importing") is not None:
            raise ValueError(f"Unapproved update, deletion, replacement or import: {resource['address']}")
        resources[resource["address"]] = change["after"]
    load_balancer = resources["cloudflare_load_balancer.status"]
    if (load_balancer["enabled"] is not False or load_balancer["name"] != "mxstatus.io"
            or load_balancer["proxied"] is not True or load_balancer["steering_policy"] != "off"
            or load_balancer["session_affinity"] != "none"):
        raise ValueError("Staging must keep the single MX load balancer disabled without paid steering or affinity.")
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
    creations = validate_plan(json.load(sys.stdin))
    print(f"Approved disabled edge stage: {creations} creations, {5 - creations} unchanged resources.")
