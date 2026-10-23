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

The operations workspace presents evidence cards with source, severity and state
filters, plus incident and announcement controls. Readers can inspect evidence;
only operators can write. Draft forms stay in the signed-in user's browser tab
across reauthentication. Conflicts preserve the form, and an older draft is never
silently restored over a newer saved revision.

### Manual announcements

Use **New announcement** to preview, save, publish or withdraw a plain-text banner
on the current brand. Informational, warning and critical appearances are
available, with optional local-time start and expiry. Drafts remain private.
Writes are revision-checked and audited, including writes forwarded from the
standby application to the primary writer. Public scheduling and expiry are
evaluated against cached publications even if PostgreSQL is unavailable.

### GitHub incident publishing

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
Source-owned incidents have stable IDs and are edited in GitHub, not in the
local incident editor. Label, closure and reopening transitions remain in the durable
timeline; unrelated edits and untrusted comments do not rewrite those transitions.
API failures and incomplete pagination retain the last
durable incident state and surface a private integration error. Missing issues
are checked individually before withdrawal to avoid pagination races.

`GitHubEnabled` (default `true`), `GitHubRepository`, `GitHubTrustedAuthors`, `GitHubRefreshSeconds` (60-3600)
and an optional read-only `GitHubTokenFile` are runtime options. No token is
needed for the current small public repository; anonymous GitHub API limits
apply. Unchanged comments are cached for at most 30 minutes to constrain requests.
If volume grows, provision a read-only token through platform-owned secret
mounts rather than placing credentials in runtime JSON or source control.

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
