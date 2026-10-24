# Architecture

## Independent service

The new runtime is `src\MX.Platform.Status.Service`: ASP.NET Core serves the Astro
assets and host-isolated local JSON publications. PostgreSQL holds incidents,
audit events, daily availability counters, private monitoring evidence, and publications.
A single publisher projects cached monitoring signals; a standby copies replicated
publications onto its own durable local disk. Monitoring collection runs independently
and never gates publication. See the repository README for configuration,
identity, availability limits, and deployment prerequisites.

The service temporarily references the existing App assembly to reuse its
contracts, domain types, regional classifiers, and merger. It does not run the
Functions host or its GitHub/blob query path. Shared KQL tenant filtering and
`sum(itemCount)` conventions below still apply to aggregate telemetry queries.

## Monitoring ownership and independent consumption

`platform-monitoring` remains strategic; Sitewatch owns targets, active collection,
cadence/retries, native availability measurements and operational alert rules.
Status owns public signal aggregation/history, service presentation and operator
publishing. No workload probe engine, scheduler or monitoring sidecar belongs here.
An approved cost constraint never permits bypassing this boundary.

Sitewatch contract version 2 emits `siteId`, `componentId`, `checkId` and `region`.
The mirrored `contract/status-monitoring-bindings.json` declares consumed canonical
identities, telemetry aliases, regions and intervals without workload URLs.
Producer Terraform input validation fails if a consumed production binding loses
coverage; consumer tests fail if the catalogue drifts. Shared GeoLocation checks
use canonical MX measurements and project to both brands without recollection.

`MonitoringWorker` polls Application Insights independently, batching by resource
and selecting `arg_max(timestamp, success, message)` per identity and region.
It authenticates with a dedicated certificate reader restricted to the five
required App Insights resources, not deployment credentials or workspace-wide
access. Each query explicitly filters owning site/component/check identities;
cross-brand catalogue references require an explicitly shared component.

Fresh regional failures are projected immediately: all reporting regions failing
is outage; partial failures or missing expected regions are degraded; no fresh
regions is unknown. Freshness is at least 180 seconds for ingestion/poll latency,
or three producer intervals for slow checks. Cached observation times are never
restamped. Every refresh merges prior regional observations, including rows that
have left Azure's query window, so eviction cannot fabricate a new history sample.
Retained regions still age stale; only a new measurement replaces them.
An unavailable resource retains its prior cache without preventing
unrelated resources from refreshing. Public requests and publication never await
Azure queries, GitHub or target HTTP requests.

Cached brand aliases commit together. History advances once per changed source
observation vector, not every publication/poll; cursor and daily counter changes
share a transaction. A failed history write rolls back the cursor, and restart
does not recount cached inputs. Historical outcomes are classified at measurement
time, so an unconsumed sample still retries after live freshness expires. Monitoring
checks reject cached results without matching canonical provenance and regional
observations. Known slow failures survive faster inputs.
Maintenance advances the cursor without adding an availability sample.
Historical incident overrides remain independently applied after measurement
aggregation. Archived legacy probe evidence is readable privately, never used as
a live input.

Deploy/verify the producer before the consumer. The new image's
`--refresh-monitoring` preflight seeds the writable cache and refuses incomplete
fresh regional coverage from the current successful queries, not recovered cache,
before cutover. Startup rejects legacy workload probes,
HTTP targets and unsupported collection fields; generic ingestion cannot overwrite
monitoring-owned inputs. The US$15/month incremental normal-operation allowance is
planning guidance, not a hard cap or permission to silently remove coverage.

## Legacy Azure architecture

The remaining sections describe the preserved, inactive Azure implementation,
not the new deployment topology. Do not apply or destroy its Terraform state
as part of the independent-service rollout.

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

## Function App telemetry

The Azure Functions host and the .NET isolated worker have separate logging configuration. Host-generated invocation telemetry is reduced deterministically in `host.json`: `Function = Warning` suppresses successful start/completion traces emitted at `Information`, while retaining warnings, errors, and exceptions; `Host.Results = Error` suppresses successful invocation request records while retaining failed execution requests. Host-side Application Insights sampling is disabled so retention is severity-based rather than probabilistic.

The isolated worker's logging and application behavior are unchanged. This repository does not define request-based Terraform alerts, so no alert depends on successful host request records.

## Deployment order

`platform-sitewatch-func` (the telemetry producer) must be deployed before `platform-status-web`/`status-pages` content start relying on the new `componentId`/`siteId`/`region` dimensions or the regional live-status query, since the consumer's regional classifier and enforced `siteId` filter assume every row already carries them.

## Data flow

1. `platform-sitewatch-func` runs availability probes → writes to App Insights
2. `status-api` Function queries AI availability results via KQL
3. `status-api` fetches incident data from GitHub Issues in `status-pages` repo using Mxio-idp-bot GitHub App installation tokens, with the PEM stored in Key Vault
4. Astro frontend calls `/api/status`; the Function resolves the site from the `Host` header and returns the matching response
5. `status-rollup` timer function (daily 02:00 UTC) aggregates history to blob storage
