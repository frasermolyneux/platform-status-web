# Public status edge

This stack owns only one Cloudflare load balancer, one two-origin pool, and one
HTTPS readiness monitor. It does not own the subscription, existing DNS records,
zone settings, or retained Azure resources. The scoped Production API token is
provided as `CLOUDFLARE_API_TOKEN`; its environment secret is named
`CLOUDFLARE_API_KEY`.

The reviewed baseline is two origins, 60-second checks, one region (`ENAM`), no
session affinity, and no paid traffic steering. Both brands share this one load
balancer. Origins deliberately have no Host override, preserving brand isolation.
The monitor alone sends `Host: mxstatus.io`, validates origin TLS, and requires the
actual local-publication readiness response.

The load balancer is initially **disabled**. Its enabled pool can be monitored
without replacing existing public routing. Before a separate reviewed activation,
verify both origins' Cloudflare health, fresh non-bootstrap publications for both
brands, Entra operations, replication, disk-cache survival, and existing services.
Disabling the load balancer restores precedence to any retained matching DNS
record; do not delete that rollback record during this rollout.

`status.xtremeidiots.com` must subsequently be a CNAME to `mxstatus.io` in the
existing `platform-connectivity` zone configuration, not a second load balancer.
Both zones must belong to the same account. Confirm suitable Full (strict) TLS
and existing apex/CNAME compatibility before activation.

The plan's `status_edge_preflight` output reads both active zones through the
expected account, their actual SSL modes, and existing status-name routing
records. These data sources never adopt or modify DNS or zone settings.
Read-only DNS and zone-settings permissions are required. A non-strict mode or
SaaS/custom-hostname CNAME needs an explicit compatibility review before
activation; never change the entire XtremeIdiots zone blindly.

## State and delivery

`Status Edge` plans same-repository pull requests with Production OIDC. It applies
only through an explicit `workflow_dispatch` selecting `apply` on reviewed `main`.
It never invokes the retained legacy Azure stack.

The edge backend reuses the approved storage location, **not its state key**.
Both backend files must be passed in this order:

```powershell
terraform -chdir=edge init -backend-config=..\terraform\backends\prd.backend.hcl -backend-config=backends\prd.backend.hcl
```

The second file selects `status-edge-prd.tfstate`; CI checks that effective key
before planning. Never initialize this stack using only the legacy backend file.
Do not run local state-backed plans or applies. For local schema validation:

```powershell
terraform -chdir=edge init -backend=false
terraform -chdir=edge validate
terraform -chdir=edge fmt -check -recursive
```

Generated lock files remain ignored. Resource deletion is deliberately protected;
retirement requires an explicit reviewed change.
