# Public status edge

This stack owns one Cloudflare load balancer, one two-origin pool, one HTTPS
readiness monitor, and hostname-scoped strict TLS configuration entrypoints for
the two status zones. It does not own the subscription, existing DNS records,
global zone settings, or retained Azure resources. The scoped Production API token is
provided as `CLOUDFLARE_API_TOKEN`; its environment secret is named
`CLOUDFLARE_API_KEY`.

The reviewed baseline is two origins, 60-second checks, one region (`ENAM`), no
session affinity, and no paid traffic steering. Both brands share this one load
balancer. Origins deliberately have no Host override, preserving brand isolation.
The monitor alone sends `Host: mxstatus.io`, validates origin TLS, and requires the
actual local-publication readiness response.

The load balancer was staged **disabled**, with its enabled pool monitored
without replacing public routing. The separate activation change enables only
that existing MX load balancer. Before applying activation,
verify both origins' Cloudflare health, fresh non-bootstrap publications for both
brands, protected-operation authentication boundaries, replication, disk-cache
survival, and existing services. Complete browser-based Entra operator sign-in
after public HTTPS routing is active.
Disabling the load balancer restores precedence to any retained matching DNS
record. The initial preflight found no matching routing records for either status
hostname, so no previous DNS route existed to restore.

`status.xtremeidiots.com` must subsequently be a CNAME to `mxstatus.io` in the
existing `platform-connectivity` zone configuration, not a second load balancer.
Both zones must belong to the same account. The two configuration rules set
`ssl = "strict"` only for `mxstatus.io` and `status.xtremeidiots.com`; both global
zone SSL modes remain unchanged. Configuration Rules are available on the free
plan, and each status zone uses one rule. Confirm existing apex/CNAME
compatibility before activation.

The plan's `status_edge_preflight` output reads both active zones through the
expected account, their actual global SSL modes, status-name routing records,
and existing configuration entrypoints. These data sources never adopt or
modify DNS or zone settings. Read-only DNS and zone-settings permissions plus
Config Settings Write on these two zones are required.

The entrypoint precondition refuses an existing configuration ruleset with a
different owner name; Cloudflare also rejects duplicate zone-phase creation.
Never import an existing entrypoint or discard its rules just to make this plan
apply. Stop for a separately reviewed preservation/adoption change instead.
Subsequent plans must preserve the stack-owned entrypoints and the exact-host
expressions. Never change the entire XtremeIdiots zone blindly.

## State and delivery

`Status Edge` plans same-repository pull requests with Production OIDC. It applies
only through an explicit `workflow_dispatch` selecting `apply` on reviewed `main`.
It never invokes the retained legacy Azure stack.

Every saved plan is checked before it can be applied: the five staged managed
resources must already exist, and the only permitted change is enabling the
existing MX load balancer. All other load-balancer settings and the existing
pool, monitor and TLS entrypoints must remain unchanged. The check retains
exact-host strict TLS, exactly the two approved enabled equal-weight origins
without Host overrides, one monitoring region, and strict 60-second HTTPS checks.
An unchanged retry is safe; imports, creations, replacements, deletes, unrelated
updates or extra resources stop for separate review.
Cloudflare provider 5.22 marks omitted optional/computed routing defaults unknown
on every update. Activation therefore preserves those staged defaults using
explicit `ignore_changes`; it does not silently accept unknown routing. Altering
these settings later requires a reviewed lifecycle/configuration change. Only
the provider's computed creation/modification timestamps and zone-name metadata
may remain unknown. Every behavior-affecting setting must be known and unchanged.
Transient saved plans are removed even when a step fails.

The `verify` operation only initializes the isolated state and reads the live
Cloudflare pool-health report; it does not plan or apply resources. Both `apply`
and `verify` publish that report without exposing the token. New pools may need
two successful 60-second checks before both origins report healthy. Inspect the
actual report before activation; successful API retrieval alone is not proof
that the origins are healthy.
Verification first reads the actual pool and monitor, then requests pool health.
Provider error codes are surfaced without credentials. If health is unavailable,
the workflow fails explicitly; a successful Terraform apply is not health
acceptance and must not be repeated merely to retry verification.

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
