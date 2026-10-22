"""Reject changes outside the reviewed Cloudflare staging or activation boundary."""

import argparse
import json
import sys


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
    for resource in managed:
        change = resource["change"]
        allowed = (["create"], ["no-op"])
        if activate:
            allowed = (["no-op"], ["update"]) if resource["address"] == "cloudflare_load_balancer.status" else (["no-op"],)
        if change["actions"] not in allowed or change.get("importing") is not None:
            raise ValueError(f"Unapproved update, deletion, replacement or import: {resource['address']}")
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
    load_balancer = resources["cloudflare_load_balancer.status"]
    if (load_balancer["enabled"] is not activate or load_balancer["name"] != "mxstatus.io"
            or load_balancer["proxied"] is not True or load_balancer["steering_policy"] != "off"
            or load_balancer["session_affinity"] != "none"):
        raise ValueError("The single MX load balancer must retain the reviewed routing state without paid steering or affinity.")
    if resources["cloudflare_load_balancer_pool.status"]["check_regions"] != ["ENAM"]:
        raise ValueError("Only the approved single monitoring region is allowed.")
    monitor = resources["cloudflare_load_balancer_monitor.status"]
    if monitor["allow_insecure"] is not False or monitor["type"] != "https" or monitor["interval"] != 60:
        raise ValueError("The readiness monitor must retain strict HTTPS and its approved 60-second interval.")
    preflight = plan["planned_values"]["outputs"]["status_edge_preflight"]["value"]
    if load_balancer["zone_id"] != preflight["mxstatus.io"]["zone_id"]:
        raise ValueError("The load balancer must belong to the preflight-verified MX zone.")
    for zone, hostname in (("mxstatus.io", "mxstatus.io"), ("xtremeidiots.com", "status.xtremeidiots.com")):
        ruleset = resources[f'cloudflare_ruleset.status_tls["{zone}"]']
        rules = ruleset["rules"]
        if (ruleset["zone_id"] != preflight[zone]["zone_id"] or ruleset["kind"] != "zone"
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
