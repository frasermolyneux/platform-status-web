# Public status edge

This stack owns one Cloudflare load balancer, one two-origin pool, one HTTPS
readiness monitor, and hostname-scoped strict TLS configuration entrypoints for
the two status zones. It does not own the subscription, existing DNS records,
or global zone settings. The scoped Production API token is
provided as `CLOUDFLARE_API_TOKEN`; its environment secret is named
`CLOUDFLARE_API_KEY`.

Both brands share the load balancer. See `main.tf` for the configured origins
and health-check settings. Origins deliberately have no Host override, preserving
brand isolation. The monitor sends `Host: mxstatus.io`, validates origin TLS,
and requires the local-publication readiness response.

`status.xtremeidiots.com` uses a CNAME to `mxstatus.io` in the
existing `platform-connectivity` zone configuration, not a second load balancer.
Both zones must belong to the same account. The existing status configuration rules
set `ssl = "strict"` for `mxstatus.io` and `status.xtremeidiots.com`; both global
zone SSL modes remain unchanged. Configuration Rules are available on the free
plan. The XtremeIdiots entrypoint additionally owns one exact-host strict TLS
rule for `maps.xtremeidiots.com`, preserving its status rule and all
zone-wide settings. Two additional exact-host rules set strict TLS for
`www.xtremeidiots.com` and `cdn.xtremeidiots.com`; their origins must present
unexpired trusted certificates matching their respective hostnames. DNS remains owned by `platform-connectivity`; no competing
configuration entrypoint may be created there. Confirm existing apex/CNAME
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

`Status Edge` uses Production OIDC for eligible same-repository pull requests
and main-only manual operations. Pull requests and the `activate` operation use
a refresh-only preflight:
the five staged resources must already exist, with no managed changes or
unreviewed drift. A retry may accept only a known, verified false-to-true
enablement change and its modification timestamp from an interrupted approved
activation; all other resource drift remains blocked.
The check retains exact-host strict TLS, exactly the two approved enabled
equal-weight origins without Host overrides, one monitoring region, and strict
60-second HTTPS checks. A pull request's refresh-only plan checks live drift,
not the proposed configuration changes; a passing check is not a proposed-change
plan. The preflight plan is never applied to enable routing.

For an approved activation, first verify both origins' Cloudflare health, fresh
non-bootstrap publications for both brands, authentication boundaries,
replication, disk-cache survival, and existing services. Confirm DNS and
apex/CNAME compatibility; disabling the load balancer only restores a retained
matching DNS route if one exists. Complete browser-based Entra operator sign-in
after public HTTPS routing is active.

The activation API path avoids relaxing checks when optional routing fields are
unknown in a Terraform enablement update. After checking Cloudflare readiness for
both origins, `activate` sends only `PATCH {"enabled": true}` to the existing MX
load balancer. It reads the result back and requires every other setting except
its modification timestamp to be unchanged. Already-enabled retries recheck health
without repeating the PATCH. Unexpected post-activation changes fail explicitly
and require operator review; failure does not imply routing stayed disabled.

The same protected operation then runs a normal Terraform plan against the
reviewed enabled configuration, checks its saved plan, and reconciles state.
No optional defaults are ignored or adopted. Imports, creations, replacements,
deletes, unrelated changes, and unknown routing values remain blocked.
Transient saved plans are removed even when a step fails.

The `plan`, `maps-tls-plan` and `web-tls-plan` operations render normal plans in
the workflow summary; they do not publish downloadable plan artifacts. The
corresponding `apply`, `maps-tls` and `web-tls` operations create, validate and
apply a fresh plan from reviewed `main`. The maps and web operations enforce
their respective exact-host TLS migration boundaries in
`deployment/validate_edge_plan.py`; they reject unrelated changes and drift.
Before any TLS change, verify public trust, hostname coverage and expiry for
each configured origin certificate. If recovery is needed, use a separately
reviewed change, preserve unrelated strict rules, and do not weaken zone-wide TLS.

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

The edge backend uses the approved storage location and the isolated
`status-edge-prd.tfstate` key. Initialize it with the edge backend configuration:

```powershell
terraform -chdir=edge init -lockfile=readonly -backend-config=backends\prd.backend.hcl
```

CI checks the effective storage account, container and state key before planning.
Do not run local state-backed plans or applies. For local schema validation:

```powershell
terraform -chdir=edge init -lockfile=readonly -backend=false
terraform -chdir=edge validate
terraform -chdir=edge fmt -check -recursive
```

Select the Terraform CLI version from `edge/.terraform-version` before running
these commands. The committed provider lock file remains authoritative; do not
commit local `.terraform/` directories or saved plans. Resource deletion is
deliberately protected and requires an explicit reviewed change.
