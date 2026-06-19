# Copilot Instructions for platform-status-web

## Repository overview

`platform-status-web` is an Azure Static Web App + .NET Function App that serves multi-site public status pages. It reads App Insights availability telemetry from `platform-sitewatch-func` and GitHub Issues from a `status-pages` content repo.

## Architecture

- **Frontend**: Astro static shell with vanilla TypeScript (~200 lines). No React/Preact/Vue. Target <10KB JS gzipped.
- **Backend**: .NET 9 Azure Functions (isolated worker) — `status-api` (HTTP) and `status-rollup` (Timer).
- **Infrastructure**: Terraform in `terraform/` — Azure resources in Sweden Central only.
- **Multi-site**: Routed by `Host` header, not URL path.

## Key conventions

- KQL queries always use `sum(itemCount)`, never `count()`
- Historic day `unknown` = `total == 0` only; `3×expectedInterval` staleness applies only to live-today
- SiteConfigLoader has a blob fallback for GitHub outages
- GitHub PAT stored in Key Vault; accessed via managed identity
- BYOFA pattern (bring-your-own Function App for SWA), not SWA managed functions

## MCP catalog

This repository is part of the `frasermolyneux` GitHub org. The MCP server `frasermolyneux-copilot` provides org-wide instructions, agents, and prompts for coding standards, workflow templates, and project alignment.

Available tools:
- `frasermolyneux-copilot-list_instructions` — list all org instructions
- `frasermolyneux-copilot-get_instruction` — read a specific instruction
- `frasermolyneux-copilot-search_instructions` — search instructions by keyword
- `frasermolyneux-copilot-list_agents` — list available agents
- `frasermolyneux-copilot-get_agent` — read agent instructions
- `frasermolyneux-copilot-list_prompts` — list available prompts
- `frasermolyneux-copilot-get_prompt` — read a specific prompt

## Related repositories

- `platform-sitewatch-func` — availability probe Function App (closest sibling for conventions)
- `platform-monitoring` — central Log Analytics workspace (consumed via Terraform remote state)
- `platform-workloads` — workload provisioning (RBAC grants)
- `status-pages` — content repo with site.yaml + components.yaml (future)
