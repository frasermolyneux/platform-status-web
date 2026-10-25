# AGENTS.md — platform-status-web

Execution brief for coding agents. For architecture detail beyond this file, read
`docs/architecture.md` and `README.md` first — don't re-derive facts already there.

## What this repo is

Multi-site public status page: Astro frontend served by an independent ASP.NET Core
service, with durable local publications and PostgreSQL primary/standby persistence.
Legacy Azure Functions and Terraform remain for shared domain reuse and retained
Azure state; they are not the new runtime or an approved retirement operation.

## Archetype: mixed .NET + frontend/static + Terraform

| Area | Path | Toolchain | Notes |
|---|---|---|---|
| Backend | `src/MX.Platform.Status.App/` | .NET 10 (`src/global.json` pins SDK `10.0.100`) | Isolated worker; `status-api` (HTTP) + `status-rollup` (Timer) |
| Independent service | `src/MX.Platform.Status.Service/` | .NET 10 | Host-isolated public files, publisher, private evidence, Entra operations |
| Container runtime | `Dockerfile`, `deployment/` | Docker + Python integration test | Non-root service and PostgreSQL images; local publications must survive database loss |
| Tests | `src/MX.Platform.Status.Tests/` | .NET 10 via `dotnet test` | Backend only |
| Frontend | `src/MX.Platform.Status.Web/` | Node 22.x, Astro 7 + vanilla TS | Maintained source: `src/pages/`, `src/client/`, `src/components/`. `dist/` is generated — gitignored, never hand-edit |
| Infrastructure | `terraform/` | Terraform >= 1.15, AzureRM | SWA, Linux Function App, Storage, Key Vault, App Insights; remote-state from `platform-monitoring`/`platform-hosting` |

## Commands (exact)

```powershell
cd src; dotnet build; dotnet test                       # backend + tests
cd src/MX.Platform.Status.Web; npm ci; npm run build     # frontend
terraform -chdir=terraform fmt -check -recursive         # terraform (format/validate only)
terraform -chdir=terraform validate
```

## Boundaries and ownership

- `platform-monitoring` is the strategic monitoring owner; `platform-sitewatch-func`
  owns endpoint targets, active measurement, cadence/retries and availability alerts.
  This service consumes monitoring observations and owns presentation, public history,
  incidents, notices and maintenance controls. Never add workload probes, a probe
  scheduler or a monitoring sidecar here, including to meet a cost constraint.
- The publication worker and public requests read cached/durable data only.
  `MonitoringWorker` independently queries latest native App Insights observations;
  it must not query workload endpoints or refresh old observation timestamps.
- Keep both repositories' telemetry and monitoring-binding fixtures synchronized.
  Producer production input validation and consumer catalogue tests must preserve
  identities, resource aliases, regions and cadence. Deploy/verify the producer first;
  require `--refresh-monitoring` fresh regional coverage before consumer cutover.
- Reject legacy probe/target definitions at startup, including HTTP definitions
  mislabelled as telemetry. Monitoring-owned checks cannot be overwritten through
  generic check ingestion. Preserve unknown/stale semantics and atomic, deduplicated
  sampling cursors/history across repolls, failures and restarts. Cache refresh
  must durably queue full history snapshots; acknowledge them only with the
  history/cursor transaction, in order, without skipping a pending backlog.
- Backend KQL/telemetry conventions are documented in `docs/architecture.md` — read it
  before touching `src/MX.Platform.Status.App/`.
- `staticwebapp.config.json` and `src/MX.Platform.Status.Web/dist/` are frontend build
  artifacts, not hand-maintained files.
- `terraform/tfvars/*.tfvars` and `terraform/backends/*.backend.hcl` are environment
  config affecting real Azure deployment behavior.
- `.terraform.lock.hcl` and `.terraform/` are gitignored — keep untracked.

## Do NOT

- Run `terraform apply`/`plan` against real backends, or change provider/resource
  behavior, naming, or tagging outside the explicit task.
- Change application/dependency/framework versions, public contracts (API responses,
  KQL query shape, `status-pages` content schema), or generated output unless that is
  the explicit task.
- Introduce secrets, connection strings, or hard-coded subscription IDs. Azure/CI auth
  remains OIDC/managed identity; the on-premises monitoring consumer uses a dedicated
  resource-scoped Entra certificate reader with its private key materialized from
  Key Vault into the service only. Never use deployment credentials or client secrets.

## Validation

Run the build/test commands above only when the change touches that area's source.
Config/docs-only changes don't need a build.
