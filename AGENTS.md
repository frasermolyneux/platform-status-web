# AGENTS.md — platform-status-web

Execution brief for coding agents. For architecture detail beyond this file, read
`docs/architecture.md` and `README.md` first — don't re-derive facts already there.

## What this repo is

Multi-site public status page: Astro static frontend on Azure Static Web Apps, backed
by a .NET Azure Function App (BYOFA), with Terraform-managed Azure infrastructure.

## Archetype: mixed .NET + frontend/static + Terraform

| Area | Path | Toolchain | Notes |
|---|---|---|---|
| Backend | `src/MX.Platform.Status.App/` | .NET 10 (`src/global.json` pins SDK `10.0.100`) | Isolated worker; `status-api` (HTTP) + `status-rollup` (Timer) |
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
- Introduce secrets, connection strings, or hard-coded subscription IDs — auth is OIDC
  + managed identity + Key Vault only.

## Validation

Run the build/test commands above only when the change touches that area's source.
Config/docs-only changes don't need a build.
