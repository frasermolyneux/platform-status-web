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
- **Monitoring ownership**: platform-monitoring/Sitewatch own targets, active checks,
  cadence, retries, operational thresholds and alerts. Status owns consumption,
  aggregation/presentation, public history and operator publishing. Never introduce
  workload probing or a monitoring sidecar here to avoid monitoring cost.
- **Input boundary**: MonitoringWorker uses a dedicated, resource-scoped Entra
  certificate reader to query native App Insights observations independently of
  publication. Public requests/publishing consume cached or durable data only.
  Stale/absent inputs are not healthy, and repolls must not advance sampling history.
- **Enforcement**: retain startup rejection of legacy probes/HTTP targets, protected
  monitored-check ingestion, producer/consumer binding tests and transactional
  sampling cursors and ordered durable history snapshots. A recovery refresh must
  never overwrite an unconsumed outage; acknowledge snapshots in the history
  transaction and never advance past a queued backlog.
  Verify fresh regional input with `--refresh-monitoring` before
  consumer cutover. Cost/coverage conflicts require approval, not an architectural bypass.

## Key conventions

- KQL queries always use `sum(itemCount)`, never `count()`
- Historic day `unknown` = `total == 0` only; `3×expectedInterval` staleness applies only to live status
- The independent consumer selects the latest measurement per site/component/check/region
  with `arg_max`, never window success averages. It explicitly filters all source identities,
  including canonical shared inputs; missing regions prevent healthy classification.
  The legacy Function's `LIVE_WINDOW_MINUTES` path below is not the independent runtime.
- Live status is classified per probe region (`ComponentStatusCalculator.ClassifyLiveStatusRegional`): a subset of reporting regions failing is `degraded`; only all reporting regions failing is `outage`; missing/stale telemetry is never presented as healthy
- Every Application Insights query must enforce the owning source site. The independent
  batch builder requires explicit site/component/check predicates; cross-brand references
  require an explicitly shared component. Legacy Function queries retain
  `TelemetryFilters.WithSiteId`. Never infer tenancy only from an identifier prefix.
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
