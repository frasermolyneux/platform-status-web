"""Enable only the existing shared status load balancer after live health checks."""

import json
import os
import subprocess
from urllib.request import Request, urlopen


ORIGINS = {"5.196.80.194", "15.235.119.97"}


def enable(edge, request):
    account = f"/accounts/{edge['account_id']}/load_balancers"
    path = f"/zones/{edge['zone_id']}/load_balancers/{edge['load_balancer_id']}"
    before = request(path)
    if (before["id"] != edge["load_balancer_id"] or before["name"] != "mxstatus.io"
            or before["proxied"] is not True or before["default_pools"] != [edge["pool_id"]]
            or before["fallback_pool"] != edge["pool_id"] or before["steering_policy"] != "off"
            or before["session_affinity"] != "none" or not isinstance(before["enabled"], bool)):
        raise ValueError("Refusing activation outside the reviewed shared load-balancer contract.")
    pool = request(account + f"/pools/{edge['pool_id']}")
    if pool["enabled"] is not True or pool["monitor"] != edge["monitor_id"] or pool["check_regions"] != ["ENAM"]:
        raise ValueError("The staged pool must remain enabled with its approved monitor and region.")
    health = request(account + f"/pools/{edge['pool_id']}/health")
    region = health["pop_health"]["ENAM"]
    origins = {address: status for entry in region["origins"] for address, status in entry.items()}
    if (health["pool_id"] != edge["pool_id"] or region["healthy"] is not True
            or set(origins) != ORIGINS
            or any(origin["healthy"] is not True or origin["response_code"] != 200 for origin in origins.values())):
        raise ValueError("Both approved origins must currently pass Cloudflare readiness checks.")
    if before["enabled"] is False:
        request(path, {"enabled": True})
    after = request(path)
    if after["enabled"] is not True:
        raise RuntimeError("The shared status load balancer did not become enabled.")
    changed = [key for key in before.keys() | after.keys()
               if key not in {"enabled", "modified_on"} and before.get(key) != after.get(key)]
    if changed:
        raise RuntimeError("Routing is enabled but unexpected settings changed; operator review required: " + ", ".join(sorted(changed)))
    print("Verified enablement-only activation; every other load-balancer setting is unchanged.")


def main():
    edge = json.loads(subprocess.check_output(
        ["terraform", "-chdir=edge", "output", "-json", "status_edge"], text=True))

    def request(path, body=None):
        headers = {"Authorization": "Bearer " + os.environ["CLOUDFLARE_API_TOKEN"]}
        if body is not None:
            headers["Content-Type"] = "application/json"
        with urlopen(Request(
                "https://api.cloudflare.com/client/v4" + path,
                data=None if body is None else json.dumps(body).encode(),
                method="GET" if body is None else "PATCH", headers=headers), timeout=30) as response:
            result = json.load(response)
        if result["success"] is not True:
            raise RuntimeError("Cloudflare activation request failed: " + json.dumps(result["errors"]))
        return result["result"]

    enable(edge, request)


if __name__ == "__main__":
    main()
