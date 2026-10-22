# Copilot Instructions for platform-status-web

## Repository overview

`platform-status-web` now has an independent ASP.NET Core service serving Astro
assets and durable local publications. Public reads must never depend on Azure,
GitHub, Entra, or PostgreSQL. The old Function App is retained only for domain
reuse and legacy state; its architecture below does not describe the new host.

## Architecture

- **Frontend**: Astro static shell with vanilla TypeScript (~200 lines). No React/Preact/Vue. Target <10KB JS gzipped.
- **Backend**: .NET 10 ASP.NET Core in `MX.Platform.Status.Service`; reuse domain logic from the legacy App assembly.
- **Infrastructure**: Existing `terraform/` retains Azure state; host/runtime policies belong to `platform-baremetal`, DNS ownership to `platform-connectivity`.
- **Multi-site**: The independent service uses actual `Host`, never arbitrary forwarded-host headers. Preserve Host at the reverse proxy.
- **Persistence**: Single publisher, asynchronous PostgreSQL standby, atomic local publications. No automatic promotion without fencing.
- **Visibility**: Azure evidence stays private; operators explicitly approve public incidents.

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
