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
npm run build
npx tsc --noEmit
```

CI additionally builds both images and exercises real primary/standby replication,
draft privacy, writes through the standby application, local-cache survival after
database loss and application restart, and truthful staleness.

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
