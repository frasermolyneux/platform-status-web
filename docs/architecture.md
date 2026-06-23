# Architecture

## Overview

`platform-status-web` is a multi-site public status page system. It reads App Insights availability telemetry (from `platform-sitewatch-func`) and GitHub Issues (from `status-pages` content repo) to serve a GitHub-style minimal status page via Azure Static Web Apps.

## Components

- **Astro static shell** (`src/MX.Platform.Status.Web/`) — vanilla TS frontend, <10KB JS gzipped
- **.NET Function App** (`src/MX.Platform.Status.App/`) — `status-api` and `status-rollup` functions
- **Terraform** (`terraform/`) — Azure resources (SWA, Function App, Storage, Key Vault, App Insights)

## Multi-site routing

The app is multi-site by `Host` header, NOT by URL path. `status.xtremeidiots.com` and `status.molyneux.me` both serve `/` but with different content per host. The `SiteResolver` maps Host headers to site configurations loaded from the `status-pages` GitHub repo.

## Data flow

1. `platform-sitewatch-func` runs availability probes → writes to App Insights
2. `status-api` Function queries AI availability results via KQL
3. `status-api` fetches incident data from GitHub Issues in `status-pages` repo using Mxio-idp-bot GitHub App installation tokens, with the PEM stored in Key Vault
4. Astro frontend calls `/api/status`; the Function resolves the site from the `Host` header and returns the matching response
5. `status-rollup` timer function (daily 02:00 UTC) aggregates history to blob storage
