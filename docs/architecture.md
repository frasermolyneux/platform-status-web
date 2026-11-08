# Architecture

## Independent service

The runtime is `src\MX.Platform.Status.Service`: ASP.NET Core serves the Astro
assets and host-isolated local JSON publications. PostgreSQL holds incidents,
audit events, daily availability counters, private monitoring evidence, and publications.
A single publisher projects cached monitoring signals; a standby copies replicated
publications onto its own durable local disk. Monitoring collection runs independently
and never gates publication. See the repository README for configuration,
identity, availability limits, and deployment prerequisites.

The service references the App assembly to reuse its
contracts, domain types, regional classifiers, and merger. Shared KQL tenant
filtering and `sum(itemCount)` conventions below still apply to aggregate
telemetry queries.

## Monitoring ownership and independent consumption

`platform-monitoring` remains strategic; Sitewatch owns targets, active collection,
cadence/retries, native availability measurements and operational alert rules.
Status owns public signal aggregation/history, service presentation and operator
publishing. No workload probe engine, scheduler or monitoring sidecar belongs here.
An approved cost constraint never permits bypassing this boundary.

Sitewatch emits `siteId`, `componentId`, `checkId` and `region`; the
[telemetry contract](../contract/availability-telemetry-contract.json) defines the schema.
The mirrored `contract/status-monitoring-bindings.json` declares consumed canonical
identities, telemetry aliases, regions and intervals without workload URLs.
Producer Terraform input validation fails if a consumed production binding loses
coverage; consumer tests fail if the catalogue drifts. Shared GeoLocation checks
use canonical MX measurements and project to both brands without recollection.

`MonitoringWorker` polls Application Insights independently, batching by resource
and selecting `arg_max(timestamp, success, message)` per identity and region.
It authenticates with a dedicated certificate reader restricted to the required
App Insights resources, not deployment credentials or workspace-wide access.
Each query explicitly filters owning site/component/check identities;
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

Cached brand aliases and a durable history snapshot commit together. Independent
refreshes may advance the live cache while unconsumed snapshots remain queued.
History consumes snapshots in order, in bounded batches; snapshot acknowledgement,
cursor and daily counter changes share a transaction. A failed history write
retains its snapshot even if a recovery replaces the live cache before retry.
The history transaction uses a repeatable-read snapshot and never jumps to the
latest cache ahead of a queued backlog. History advances once per changed source
observation vector, not every publication/poll, and restart does not recount
cached inputs. Historical outcomes are classified at measurement
time, so an unconsumed sample still retries after live freshness expires. Monitoring
checks reject cached results without matching canonical provenance and regional
observations. Known slow failures survive faster inputs.
Maintenance advances the cursor without adding an availability sample.
Historical incident overrides remain independently applied after measurement
aggregation. Archived legacy probe evidence is readable privately, never used as
a live input.

The same history transaction updates 15-minute local counters and worst measured
state, retained for four days. Publications include 96 quarter-hour and 72 hourly
buckets per public component/service, with missing periods explicit and incident
impact intervals applied separately. Public detail views select these durable
windows without reading PostgreSQL or Azure. Daily data is never expanded into
invented sub-day measurements.

Monitoring releases a non-maintenance incident's live override while preserving its
open lifecycle and earlier impact intervals. GitHub severity removal is accepted
only for monitoring/resolved states with a recorded prior severity; invalid active
metadata remains an explicit synchronization error, visible to operators. A delayed
GitHub recovery import uses its recorded transition time, bounded by existing impact
history, not the new poll time. Maintenance retains its explicit scheduled window.

Deploy/verify the producer before the consumer. The new image's
`--refresh-monitoring` preflight seeds the writable cache and refuses incomplete
fresh regional coverage from the current successful queries, not recovered cache,
before cutover. Startup rejects legacy workload probes,
HTTP targets and unsupported collection fields; generic ingestion cannot overwrite
monitoring-owned inputs. The US$15/month incremental normal-operation allowance is
planning guidance, not a hard cap or permission to silently remove coverage.

## Process observations and supporting impact

Server Agent, Ban Synchronisation and Portal Synchronisation consume typed
`ProcessHealthSnapshot` events from approved identities and exact cloud roles in
the existing Portal Application Insights resource. The mirrored
`src/contract/process-health-policy.json` belongs to platform-monitoring. Workloads
own complete-inventory/outcome evaluation; status does not calculate business job
deadlines or create workload probes. Process queries are independent of native
availability queries, so one feed cannot prevent the other refreshing.

Original assessment timestamps and deduplicated observation identities survive
repolls and missing queries; stale evidence becomes Unknown. Generic ingestion
cannot overwrite either monitoring source type. Producer-first
`--refresh-monitoring` requires fresh current-query coverage for both types.

Supporting process leaves declare `maximumParentImpact: degraded`: their own
confirmed capability failure or declared incident may be Outage, but contribution
to Portal is at most Degraded. Essential website/API failures and explicitly
declared Portal outages remain Outage. Caps apply to live, daily, granular and
incident aggregation and never alter measured success/failure counts or turn
Unknown/maintenance into another state. Process daily history stores worst actual
measured impact transactionally alongside existing counters, rather than turning
a day of Degraded observations red through availability percentages. Initial
unobserved periods stay Unknown; no public history import is performed.

## Shared telemetry query conventions

Aggregate Application Insights queries use `sum(itemCount)`, not `count()`,
to account for sampling. Every query must enforce the owning source site;
shared aggregate query paths use `TelemetryFilters.WithSiteId`, and the
independent batch builder explicitly filters site, component and check.
Cross-brand references require an explicitly shared component.

Live classification is regional: a subset of reporting regions failing is
degraded, all reporting regions failing is outage, and missing or stale input
is never healthy. Historical days are unknown when their total sample count is
zero; live staleness thresholds do not rewrite historic days.
