# Architecture

## Overview

`platform-status-web` is a multi-site public status page system. It reads App Insights availability telemetry (from `platform-sitewatch-func`) and GitHub Issues (from `status-pages` content repo) to serve a GitHub-style minimal status page via Azure Static Web Apps.

## Components

- **Astro static shell** (`src/MX.Platform.Status.Web/`) — vanilla TS frontend, <10KB JS gzipped
- **.NET Function App** (`src/MX.Platform.Status.App/`) — `status-api` and `status-rollup` functions
- **Terraform** (`terraform/`) — Azure resources (SWA, Function App, Storage, Key Vault, App Insights)

## Static Web App: dedicated Standard SKU, own region

The status service uses one dedicated Azure Static Web App per environment (not a shared/multi-tenant SWA in `platform-hosting`), because SWA is billed per application and this service should remain independently owned. That single SWA serves all status tenants (`status.xtremeidiots.com`/`xi`, `mxstatus.io`/`mx`, `dev.mxstatus.io`/`dev`) from one deployed artifact via host-based routing (see below), not one SWA per tenant.

The SWA runs the **Standard** SKU (`var.static_web_app_sku`) in both dev and prd. The Free tier cannot host a linked bring-your-own Function App: `azurerm_static_web_app_function_app_registration` fails against Free with `SkuCode 'Free' is invalid`, and Microsoft's documentation confirms Standard is required for linked APIs. Expected incremental cost is ~GBP 6.82/month in West Europe plus bandwidth above 100 GB/month.

The SWA runs in **West Europe** (`var.static_web_app_location`) because Azure Static Web Apps are not available in Sweden Central; the Function App, Storage, Key Vault, and Application Insights remain in **Sweden Central** (`var.location`) with the rest of the platform estate.

## Multi-site routing

The app is multi-site by resolved public hostname, NOT by URL path. `status.xtremeidiots.com` and `mxstatus.io` both serve `/` but with different content per host. Because this Function App is linked to Azure Static Web Apps as a "bring your own Function App" (BYOFA) backend, the `Host` header it sees reflects the internal SWA↔Function App hop, not the custom domain the browser requested. `RequestHostResolver` resolves the original hostname from a validated first hop of `X-Forwarded-Host` when present, falling back to `Host`; `SiteResolver` then maps that hostname to a site configuration loaded from the `status-pages` GitHub repo.

## Telemetry contract with platform-sitewatch-func

Every `availabilityResults` row produced by `platform-sitewatch-func` carries three explicit `customDimensions`: `componentId`, `siteId`, and `region` (the producer's canonical Azure region string, e.g. `uksouth`). Status-web depends on this contract rather than inferring tenancy from component-name prefixes:

- `AvailabilityQueryBuilder`/`AvailabilityClient` only ever filter on `customDimensions.*` keys.
- `TelemetryFilters.WithSiteId` forces `customDimensions.siteId` onto every query filter at call time (`GetStatusFunction`, `DailyRollupService`), overriding anything content authors configure, so a misconfigured or malicious component filter can never read another tenant's telemetry.
- Live status is queried per region (`BuildLiveRegionalQuery`/`QueryLiveRegionalAsync`, grouped by `customDimensions.region`) over a recent, configurable window (`LIVE_WINDOW_MINUTES`, default 15 minutes) rather than `startofday(now())`. `ComponentStatusCalculator.ClassifyLiveStatusRegional` then applies: all reporting regions healthy → operational; a subset failing → degraded; all reporting regions failing → outage; no samples, or all regions stale/missing → unknown. An optional `sla.expectedRegions` list in component content makes a completely missing region count against health instead of being silently ignored.
- Daily rollups remain date-based (`BuildDailyRollupQuery`) and keep `sum(itemCount)` semantics; only the live path moved off `startofday`.
- A component can query one or more Application Insights resources (`source.resources`, falling back to the legacy singular `source.resource`); per-resource regional results are merged (summed) by region before classification.

See `platform-sitewatch-func`'s `docs/telemetry-contract.md` (or equivalent) for the producer side of this contract, and keep both repos' contract fixtures/tests in sync — see that repo's contract fixture for the canonical shape and update-in-lockstep guidance.

## Deployment order

`platform-sitewatch-func` (the telemetry producer) must be deployed before `platform-status-web`/`status-pages` content start relying on the new `componentId`/`siteId`/`region` dimensions or the regional live-status query, since the consumer's regional classifier and enforced `siteId` filter assume every row already carries them.

## Data flow

1. `platform-sitewatch-func` runs availability probes → writes to App Insights
2. `status-api` Function queries AI availability results via KQL
3. `status-api` fetches incident data from GitHub Issues in `status-pages` repo using Mxio-idp-bot GitHub App installation tokens, with the PEM stored in Key Vault
4. Astro frontend calls `/api/status`; the Function resolves the site from the `Host` header and returns the matching response
5. `status-rollup` timer function (daily 02:00 UTC) aggregates history to blob storage
