# Copilot Instructions for platform-status-web

## Repository overview

`platform-status-web` is an Azure Static Web App + .NET Function App that serves multi-site public status pages. It reads App Insights availability telemetry from `platform-sitewatch-func` and GitHub Issues from a `status-pages` content repo.

## Architecture

- **Frontend**: Astro static shell with vanilla TypeScript (~200 lines). No React/Preact/Vue. Target <10KB JS gzipped.
- **Backend**: .NET 10 Azure Functions (isolated worker) — `status-api` (HTTP) and `status-rollup` (Timer).
- **Infrastructure**: Terraform in `terraform/` — Azure resources in Sweden Central only.
- **Multi-site**: Routed by resolved public hostname (`X-Forwarded-Host` when present and valid, else `Host`), not URL path — see `RequestHostResolver`.

## Key conventions

- KQL queries always use `sum(itemCount)`, never `count()`
- Historic day `unknown` = `total == 0` only; `3×expectedInterval` staleness applies only to live status
- Live status queries use a recent configurable window (`LIVE_WINDOW_MINUTES` app setting, default 15), not `startofday(now())`, so a fresh outage is never masked behind hours of earlier healthy samples
- Live status is classified per probe region (`ComponentStatusCalculator.ClassifyLiveStatusRegional`): a subset of reporting regions failing is `degraded`; only all reporting regions failing is `outage`; missing/stale telemetry is never presented as healthy
- Every Application Insights query filter is forced through `TelemetryFilters.WithSiteId`, which always sets `customDimensions.siteId` to the resolved site — tenant isolation does not depend on content authors remembering to filter by site
- SiteConfigLoader has a blob fallback for GitHub outages
- GitHub PAT stored in Key Vault; accessed via managed identity
- BYOFA pattern (bring-your-own Function App for SWA), not SWA managed functions

See `docs/architecture.md` for the full telemetry contract and query classification
rules, and `AGENTS.md` for build commands and repo boundaries.

## Related repositories

- `platform-sitewatch-func` — availability probe Function App (closest sibling for conventions)
- `platform-monitoring` — central Log Analytics workspace (consumed via Terraform remote state)
- `platform-workloads` — workload provisioning (RBAC grants)
- `status-pages` — content repo with site.yaml + components.yaml (future)
