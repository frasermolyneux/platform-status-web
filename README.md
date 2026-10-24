# platform-status-web

[![Build and Test](https://github.com/frasermolyneux/platform-status-web/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/frasermolyneux/platform-status-web/actions/workflows/build-and-test.yml)
[![Code Quality](https://github.com/frasermolyneux/platform-status-web/actions/workflows/codequality.yml/badge.svg)](https://github.com/frasermolyneux/platform-status-web/actions/workflows/codequality.yml)

Multi-brand public status pages for `mxstatus.io` and
`status.xtremeidiots.com`, with an Astro frontend and an ASP.NET Core service.
The target topology is two independently serving origins on `ns346663` and
`ns5019527`, behind Cloudflare health-based routing.

## Availability boundary

Public requests read atomic, durable publications from the origin's local disk.
They do not query Azure, GitHub, PostgreSQL, or Entra. A publisher performs direct
HTTPS probes, maintains observation-based history, and publishes approved incident
and maintenance updates. The other origin copies publications from its local
PostgreSQL standby. Missing or stale observations are **unknown**, never healthy.

PostgreSQL replication is asynchronous: public readability survives loss of the
writer, but the latest unreplicated updates may be lost. There is deliberately no
automatic two-node write failover. Fence the former writer before any promotion.
Both origins remain in OVH, so this does not cover an OVH-wide failure.

Azure public RSS and optional certificate-authenticated Sitewatch queries run in
a separate private evidence worker. The public Azure feed covers broad incidents,
not complete Azure or subscription health. Secure Azure common-schema alert
ingestion stores private evidence; it does not create public incidents.

## Runtime

Build `Dockerfile` for the service and `deployment\postgres.Dockerfile` for
PostgreSQL. The latter deliberately does not inherit an image `VOLUME`, allowing
the platform's constrained-runtime policy to validate every persistent mount.
Use immutable image digests, non-root users, a read-only root filesystem, dropped
capabilities, and a writable `/tmp` tmpfs.

`STATUS_RUNTIME_FILE` points to platform-owned JSON with a `Status` section.
Configure `DataDirectory`, `DatabaseHost`, `DatabasePort`, `Publisher`, and,
on the standby application, `WriterHost` and `WriterPort`. The application reads
the database password from `DatabasePasswordFile`, defaulting to
`/run/secrets/app-password`. Never put passwords in the runtime JSON.
The database process runs as `999:10001`; the service runs as `10001:10001`.

The root `workload.json` pins the published application and PostgreSQL digests and
is identical on both origins. The platform supplies the different node roles,
writer addresses, and Entra metadata through read-only configuration. The CI
policy in `.github\validation\policy.json` mirrors the permitted mounts, secrets,
networks, resource limits, and primary-node Tailscale binding; it does not grant
host privileges. The standby host policy supplies its own bind address.
The protected main release validates it with the pinned private platform
compiler, fetched using a repository-scoped, read-only GitHub App token. Public
pull-request jobs do not receive private platform source or credentials.

The private database bridge needs outbound access for replication over Tailscale.
PostgreSQL does not join the shared public-proxy network. Its only published port
is Tailscale TCP 15432, further restricted to the peer origin by host and tailnet
policy. `status-database.mxstatus.io` is the manifest's logical private ingress
identifier, not a requirement to publish a public database DNS record.

The database image requires read-only `/node` configuration files:
`database-role` (`primary` or `standby`), `primary-host`, `primary-port`, and
`peer-address` (the other origin's Tailscale IPv4 address). Mount its writable
data at `/var/lib/postgresql/data`, owned by UID 999. Mount the three Key
Vault-materialized files `postgres-password`, `app-password`, and
`replication-password` under `/run/secrets`, readable by GID 10001. Restrict the
database port to the two origins over Tailscale; it is never a public listener.

Platform-owned ingress must preserve the actual HTTP `Host`. Arbitrary
`X-Forwarded-Host` values are ignored. `/health/live` and `/health/ready` are
host-independent. Readiness checks local publication readability, not monitored
service health or Azure/database reachability. An initial origin can serve an
explicit unknown bootstrap publication before its first observation.

## Operations and identity

`/ops` loads Microsoft Entra authentication separately from the public frontend.
Configure `TenantId` and `ClientId`, a single-tenant SPA registration with both
HTTPS `/ops` redirect URIs, delegated `api://{clientId}/Status.Access`, and
`Status.Reader` / `Status.Operator` app roles. Assign users explicitly; initially
only the owner should receive the operator role.

`/api/integrations/azure` requires the `Status.Ingest` application role and an
allowlisted caller application in `WebhookApplicationIds`. Issuer, audience,
lifetime, role, and delegated scope are enforced by the API, not the browser.
Monitoring payloads and subscription details stay behind authentication.

The operations workspace has dedicated pages: `/ops` for service health,
`/ops/evidence` for monitoring results, `/ops/incidents` for incidents and
maintenance, and `/ops/banners` for announcements. Readers can inspect evidence;
only operators can write. Draft forms stay in the signed-in user's browser tab
across reauthentication. Conflicts preserve the form, and an older draft is never
silently restored over a newer saved revision.

Service diagnostics distinguish the live HTTP check from public incident and
maintenance overrides. Each check records its target, observer, HTTP status,
duration, failure reason and observation time privately. The dashboard flags stale
results and checks that do not yet match the public publication, including during
replication lag. Azure alerts and advisories are context, not automatic public
availability signals.

Use **Promote to incident** on an evidence item to open an editable, tab-scoped
incident draft. Known services, severity and state are pre-selected; unmatched
alerts require the operator to select affected services. Private source context
is shown separately and retained with a private source reference, not copied into the public update. Review the wording
and preview, then explicitly choose whether to publish. Promotion alone makes no
server-side changes.

GeoLocation Website is listed on both brands. Approved incidents affecting it
appear on both sites through explicit shared-component mappings, including when
declared on only one brand. Other services and private evidence remain isolated.
Shared incidents are managed on their originating site, or in their source GitHub
issue. XI also includes Servers Integration API and Server Events. Server Agent
is omitted until it has a suitable availability signal.

### Services, components and checks

The catalogue uses **Service → Component → Check**. Public overviews list services;
opening one shows its components and their histories. Opening a component shows its
individual checks. A service aggregates component status, and a component aggregates
its configured checks. Checks report operational, degraded, outage or unknown;
missing or stale observations remain unknown, not healthy.

XtremeIdiots Portal groups its Website, Repository APIs v1/v2, Servers Integration
API, Server Events and GeoLocation Website. GeoLocation remains a top-level MX
service. Explicit shared identities retain the same GeoLocation status and incidents
across those different presentations. `sharedId` identifies the same logical check
on both brands without changing existing private evidence IDs.

`checks` supports independent `http` and authenticated `telemetry` definitions,
each with its own ID, component, name and expected interval. A permitted
`Status.Ingest` application can POST `{site, checkId, status, observedAt, detail}`
to `/api/integrations/checks/{siteId}/{checkId}` for a configured telemetry check.
Scope must match the definition; older observations cannot replace newer results.
Details stay private. HTTP targets stay in reviewed configuration.
HTTP checks default to `GET`; `method: "HEAD"` is also supported. Other methods
are rejected, and shared definitions must agree on the method.

Legacy `probes` remain in the production catalogue for rolling compatibility with
the previous image. Explicit `checks` take precedence for each component; legacy
probes are used only for components without explicit checks.

### Bounded-cost observability coverage

MX additionally covers BQBA, DM Electrical Services, CraftPledge, Talk With Tiles,
Travel Itinerary, Twenty One, and Trip Side Kick's website and application.
XI adds the CDN under Community Forums, Maps Manager under XI Redirect,
Community Games & Points (Arcade, Casino, Daily Games, Powerball and Wallet),
and the published FreezeTag statistics page.

New checks run at five-minute intervals. GeoLocation Website and Portal Website
combine origin liveness with public-hostname routing; Repository APIs combine
liveness with database readiness. These existing App Service liveness checks,
including GeoLocation API, run every two minutes to offset the additional Azure
HTTP request budget. Shared GeoLocation checks execute once, not once per brand.
The publisher reads durable observations before scheduling, retains results for
checks not yet due, and collects at most five overdue checks per round, oldest
deadline first. Collection and its history sample commit in one transaction.
Each round leaves time for incident and banner publication rather than awaiting
the entire due backlog. Restarting does not
reset a check's schedule. HTTP-only history records components when they are
actually observed, not on skipped collection rounds.
Intervals are minimums; due checks run on the next publisher collection round.
New Azure website baselines use `HEAD` to avoid downloading response bodies.
Trip Side Kick Application uses its dependency-free liveness endpoint, not its
database-backed readiness endpoint or an authenticated application page.

This pass uses existing host limits, workspaces, telemetry and alert resources;
it does not add Sitewatch tests, alert series, log ingestion pipelines, paid
delivery canaries, MaxMind requests, or serverless-SQL readiness probes.
Existing Azure HTTP checks plus the additional App Service checks have a lower
configured request rate than the previous Azure checks alone. This is a request
budget, not a guarantee about metered telemetry bytes or the final Azure invoice;
request telemetry, log sizes and consumption costs must still be observed after
release. Existing Functions checks retain their previous cadence.

These are availability baselines, not transaction monitoring: login redirects
are accepted without following them; CDN liveness does not prove every asset is
available; a published statistics page does not prove updater freshness. Those
signals, scheduled-job freshness, queue depth and private host/backup evidence
remain strategic monitoring concerns owned by `platform-monitoring` and workload
producers. Raw alerts remain private until an operator publishes an incident.
Server Agent, protocol-aware TeamSpeak checks, additional consumption-Function
probes, weekly-job missed-run state and new Azure alert resources are deferred
pending a meaningful signal and an approved cost envelope.

Declared component or service impact overrides healthy checks and aggregates upward.
Observed impact, scope and maintenance windows are retained separately from public
messages, so resolving an incident or editing its scope does not recolour the past
as healthy. Existing active incidents acquire a baseline when first observed by
the upgraded service; unknown older severity is not invented. Percentages remain
**check availability**, not declared service uptime. A degraded telemetry observation
counts as a non-healthy check sample; its live declared status is still degraded.

Public pages and idle incident/banner workspaces refresh every five seconds;
manual incident updates normally reach both origins and the browser within
15 seconds. Probe collection keeps its separate configured interval. GitHub
changes still require the source polling interval below. Open editors are not
replaced by automatic refresh. Incident pages list affected components, source
issues where applicable, and recorded state/impact changes alongside descriptions.
Older updates without recorded impact levels are identified as such.

### Hostname traffic controls

`/ops/traffic` reads the live Cloudflare configuration for the current brand's
approved hostnames. Changes require the delegated scope, `Status.Operator`, and
the separately assigned `Status.TrafficOperator` role. An operator must confirm
the exact hostname and provide a private audit reason. Readers cannot switch
traffic; status hostnames and arbitrary destinations cannot be registered.

The shared Worker is deployed by `platform-connectivity`. The controller owns
only its exact-host maintenance routes; Terraform never manages those routes.
Normal traffic does not invoke the maintenance Worker. Page mode returns a
self-contained, noncacheable `503` with `Retry-After` and a status link. Redirect
mode sends only HTML GET/HEAD requests with top-level browser navigation metadata
to the correct status brand with a temporary `302`; API, form, probe, download
and HTML fetch requests still receive `503`. Older clients without navigation
metadata receive the maintenance page rather than a redirect.
Paths, queries, cookies and request bodies are never forwarded to the status site.
Custom public wording belongs in separately approved banners or incidents.

Activation verifies proxied DNS, refuses overlapping foreign Worker routes,
explicitly sets `request_limit_fail_open=false`, reads it back and checks the
public response. A failed or uncertain operation never automatically removes the
block. Restoration removes only the observed owned route, checks removal, and
does not withdraw public communications or imply the underlying app is healthy.
Existing connections, browser caches and direct-origin access are outside this
new-request interception boundary.

Per-host writer advisory locks, intent revisions and observed route fingerprints
serialize changes. Every edge mutation checks the original database session still
holds its lock and intent. Interrupted intents have a two-minute in-progress
window, then require explicit, read-only reconciliation of the original requested
outcome. Dispatched mutations with an unknown result remain fenced without expiry:
**Reconcile previous request** verifies the original route/public response and
sends no new edge mutation. A delayed restore cannot be followed by a conflicting
maintenance change while its DELETE may still complete. If the earlier outcome
never materializes, inspect and resolve it in Cloudflare before reconciling.
Intent is
audited durably before external mutation, followed by completion or failure.
The UI distinguishes the live route from the last requested operation; manual
Cloudflare changes are not automatically reversed. The two operations are not a
distributed transaction. Fresh route checks narrow dashboard races, but Cloudflare
does not provide an atomic compare-and-write here: coordinate dashboard overrides
with operators. Audit unavailability disables application controls;
an explicitly authorised administrator can still use Cloudflare's dashboard.

`TrafficConfigurationFile` and `TrafficTokenFile` are read-only secret mounts
supplied by `platform-baremetal`; the approved registry and Worker names come from
connectivity's remote-state output. The token may manage routes and read DNS and
account analytics, but cannot change DNS, Worker code, zone plans or billing.
Workers Free has an account-wide allowance of 100,000 requests/day. The workspace
shows estimated UTC-day usage; the publisher refreshes private usage evidence
every 15 minutes and warns at 80,000 requests. Unavailable analytics is reported,
never interpreted as zero usage. This feature does not subscribe to Workers Paid.

For an emergency override, select the correct zone in Cloudflare, inspect
**Workers Routes**, and modify only the exact `<hostname>/*` route pointing to
`platform-maintenance-page` or `platform-maintenance-redirect`. Keep its failure
mode **Fail closed** while blocking. To reopen, remove that route only, confirm the
public response, and record the override in the incident timeline. Never remove
another application's routes, disable the zone proxy, or include either status
hostname. Refresh `/ops/traffic` afterward to see the actual edge state.

Deployment acceptance uses only `maintenance-check.bishopsbees.co.uk`, a dedicated
Worker-owned endpoint with no production origin. A local authorised administrator
can run the service with `--verify-maintenance-edge` to exercise page, browser
redirect, non-browser blocking and restoration through the same controller and
audit writer. It refuses an already intercepted canary and never targets a
monitored application. If acceptance fails, inspect the canary and explicitly
run `--restore-maintenance-canary`; there is no automatic reopening.

### Manual announcements

Use **New announcement** on `/ops/banners` to preview, save, publish or withdraw a plain-text banner
on the current brand. Informational, warning and critical appearances are
available, with optional local-time start and expiry. Drafts remain private.
Writes are revision-checked and audited, including writes forwarded from the
standby application to the primary writer. Public scheduling and expiry are
evaluated against cached publications even if PostgreSQL is unavailable.
The publication checkbox controls public visibility. **Archive notice** opens the
existing text for confirmation and withdraws it when saved; archived notices are
hidden from the management list unless **Show archived notices** is selected.
**Restore notice as draft** retains the text without automatically republishing.

### GitHub incident publishing

The incident editor defaults to GitHub. Operators can create, update and resolve
issues here without knowing labels; service/component selection generates them.
Saving an approved update publishes it locally first and transactionally queues
GitHub delivery. GitHub downtime does not block public status publication.
**Local only** remains available for independent emergency incidents.

Draft messages and private evidence are never exported. Withdrawing an existing
GitHub incident removes only its publication label, not its issue body or private
draft wording. Previously approved history remains distinct from private edits.
Once an incident has entered GitHub delivery, later edits retain that destination.
Unpublished brand-specific drafts are not republished by another brand's edit;
they wait for an approved GitHub import so private scope changes remain private.
Restoring a withdrawn brand merges approved history from its peers rather than
replacing their newer updates with the withdrawn brand's older timeline.

The workspace shows pending, synced and review-required delivery states. Stable
operation IDs recover lost replies without blindly repeating issue/comment writes.
External changes stop automatic delivery; **Review GitHub delivery** shows the
remote version and requires explicit confirmation before retrying it. Review is
revision/fingerprint checked, not a last-write-wins overwrite. Permanent failures
and ambiguous writes require review; transient failures retry without blocking
other incidents. GitHub does not offer atomic issue compare-and-swap: coordinate
simultaneous external metadata edits with operators. Existing issue bodies are never
rewritten by the editor.

Missing or inaccessible source issues are shown explicitly, without offering a
retry that might create a duplicate. Restore the issue or App access, then use
**Recheck GitHub access** and review its current version. If the issue was
permanently deleted, resolve the existing incident locally through its editor
and create a new incident; delivery failure never blocks those local updates.

The independent service polls the public `frasermolyneux/status-pages` repository
every five minutes, separately from public publication and probe collection.
Use that repository's incident templates and maintainer instructions.

An eligible issue needs `status:incident`, `site:mx` and/or `site:xi`, exactly one
`severity:degraded` or `severity:outage`, and `workload:<name>` labels matching
the checked-in catalogue. Explicit `component:<public-component-id>` labels are
also supported. Every selected site must have affected public components.
An authorized repository maintainer adds **`status:published`** to approve
publication; without it, imported incidents are private drafts.

The repository owner is a trusted author by default. Additional authors must be
explicitly configured in `GitHubTrustedAuthors`; GitHub author-association claims
do not grant publishing trust. Untrusted issues and comments cannot supply
public content. Once published, trusted edits and comments synchronize without
adding another label. A comment may begin with `state: identified`,
`state: monitoring` or another supported state. Without a state label, the last
trusted update selects the active state; resolve by closing the issue or adding
`state:resolved`. Plain text is HTML-escaped;
internal comment blocks are removed. Do not put confidential information in a
public GitHub repository, regardless of rendering.

Closing resolves the mapped incident; reopening restores its active state.
Removing the publish label withdraws it without deleting its audit history.
Source-owned incidents have stable IDs and can be edited through either GitHub or
the status editor. Previously published incident links remain valid through
aliases when the selected brands change. Restoring a brand retains the source's
latest approved history without copying private drafts.
Label, closure and reopening transitions remain in the durable
timeline; unrelated edits and untrusted comments do not rewrite those transitions.
Observed description edits, reversions and impact changes are retained rather
than replacing older public updates. Historic impact is not inferred from the
issue's current labels.
API failures and incomplete pagination retain the last
durable incident state and surface a private integration error. Missing issues
are checked individually before withdrawal to avoid pagination races.

`GitHubEnabled` (default `true`), `GitHubRepository`, `GitHubTrustedAuthors` and
`GitHubRefreshSeconds` (60-3600) configure synchronization. The dedicated
**MX.IO Status** App is declared and bootstrapped by `platform-workloads`, installed
only on `status-pages` with Issues read/write. `GitHubAppId`,
`GitHubInstallationId` and the read-only `GitHubPrivateKeyFile` mount configure
authentication; only the configured repository and issue-write permission are
requested for short-lived installation tokens. The App bot must be explicitly
trusted. The key remains in workload Key Vault and host secret mounts, not runtime
JSON, Actions secrets or Terraform state. `GitHubTokenFile` is an alternative
explicitly provisioned writer credential, not the normal deployment.
Anonymous public reads remain possible without writer credentials, but exports
stay visibly queued. Unchanged comments are cached for at most 30 minutes.

During an Entra outage, a host administrator can submit an incident JSON command
on standard input to the running service executable with
`--emergency-publish <site> [incident-id]`. This uses the same validation,
optimistic revision, writer connection, and audit trail as authenticated writes;
the actor is recorded as `emergency:local-administrator`. It does not bypass
database availability or promote a standby.

The checked-in `catalog.json` is an initial public catalogue derived from
Sitewatch. Token-dependent/private targets are excluded; approve catalogue changes
through normal reviewed source changes.

## Development and release

```powershell
Set-Location src
dotnet test MX.Platform.Status.slnx
Set-Location MX.Platform.Status.Web
npm ci
npm test
npm run build
npx tsc --noEmit
```

CI additionally builds both images and exercises real primary/standby replication,
authenticated banner persistence, revision/brand checks, GitHub publication,
resolution/reopening/withdrawal, source ownership and rate-limit retention,
draft privacy, writes through the standby application, local-cache survival after
database loss and application restart, and truthful staleness.
Image-test runtimes explicitly disable external GitHub ingestion; the controlled
GitHub fixture covers incident synchronization without importing live incidents.

Successful `main` CI publishes immutable images to the existing platform ACR.
Manifest-only and documentation-only changes do not rebuild images while a source
publication record is retained. Gating compares all inputs with the last recorded
build source, not just the final commit, so skipped or overlapping CI runs cannot
lose source changes. Missing or expired provenance requires one full publication;
source-specific digest artifacts are retained for 90 days. Manual image
publication can explicitly refresh base images. Published digests still require a
reviewed `workload.json` update before origins pull them.
Publishing an image does not enable a server deployment or change public DNS.
Platform policies, secret access, ingress, approved digest manifests, Entra
registration, and Cloudflare routing must be provisioned before cutover.

Before the first host deployment, run **Bootstrap Status Database Credentials**
from reviewed `main`. Its Production OIDC identity discovers the one tagged
runtime vault in the status resource group and verifies the reviewed policy
binding. It generates only missing database credentials directly into Key Vault,
never prints or stores values in GitHub, and never rotates an existing password.
Disabled or expiring credentials fail explicitly and require an operator-led
database/secret recovery or rotation. The same vault serves both origins.

**Renew Status Origin TLS** issues a 365-day Cloudflare Origin CA certificate
for both public names using the narrowly scoped edge token. The private key
stays local to the protected runner until the validated key/certificate pair is
stored as the single `origin-tls` Key Vault secret. It runs weekly, retaining
valid certificates until the final 30 days and never changing DNS or routing.
Platform-owned proxy reconciliation installs the bundle atomically and restarts
Caddy after validation. Old certificates are not revoked during a rolling update.

The old Functions project and `terraform\` are retained as shared domain code and
the existing Azure state definition. Automatic legacy deployment and development
destruction are disabled. Legacy deploy/destroy workflows require explicit
approval variables; existing Azure resources are not retired by this change.
Azure retirement requires separate approval, as does any Cloudflare configuration
above the agreed US$5/month baseline.
