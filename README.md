# platform-status-web

Public status pages for frasermolyneux platforms (XtremeIdiots, Molyneux personal). Generic multi-site Astro app that will surface:

- **Component availability** from Azure Application Insights availability tests
- **Incidents** from GitHub Issues (label-driven)

Deployed via Azure Static Web Apps.

## Phase 1 scaffold

This commit is the Phase 1 scaffold:

- Astro + TypeScript project (`output: 'static'`)
- Multi-site routing — one build, sites under `/[slug]/` (e.g. `/xtremeidiots/`, `/molyneux/`)
- Static site registry in [`src/config/sites.ts`](src/config/sites.ts)
- Stubbed data adapters (returning deterministic mock data):
  - [`src/lib/adapters/appInsights.ts`](src/lib/adapters/appInsights.ts)
  - [`src/lib/adapters/githubIssues.ts`](src/lib/adapters/githubIssues.ts)
- Azure Static Web Apps config: [`staticwebapp.config.json`](staticwebapp.config.json)
- GitHub Actions deploy workflow: [`.github/workflows/azure-static-web-apps.yml`](.github/workflows/azure-static-web-apps.yml)

Later phases will replace the adapter stubs with real Application Insights REST queries and live GitHub Issues data.

## Local development

```sh
npm install
npm run dev        # http://localhost:4321
npm run check      # astro/typescript checks
npm run build      # production build → dist/
```

## Project layout

```
src/
  config/sites.ts            Static site + component registry
  lib/
    types.ts                 Shared domain types
    status.ts                Status aggregation helpers
    adapters/
      appInsights.ts         Availability data (Phase 1: mock)
      githubIssues.ts        Incident data (Phase 1: mock)
  layouts/BaseLayout.astro
  components/
    ComponentRow.astro
    IncidentCard.astro
  pages/
    index.astro              Site index / picker
    [site]/index.astro       Per-site status page
    404.astro
staticwebapp.config.json     Azure SWA routing + headers
.github/workflows/           SWA deploy pipeline
```

## Adding a site

Append a `SiteConfig` entry to `src/config/sites.ts`. A new `/{slug}/` route is generated automatically on build.

## Deployment

The workflow expects the `AZURE_STATIC_WEB_APPS_API_TOKEN` repo secret. It runs on pushes to `main` and on PRs (with preview environments).
