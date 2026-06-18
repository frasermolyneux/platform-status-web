# platform-status-web

Multi-site public status page that combines Azure Application Insights availability data with GitHub Issues-backed incident content to publish a customer-facing status experience for multiple platforms.

## Architecture overview

The project has two deployable application parts plus Terraform-managed infrastructure:

- `MX.Platform.Status.App` is a .NET 9 isolated Azure Functions app that reads availability telemetry from Application Insights, merges that with incident and history content from GitHub, and exposes the API/runtime endpoints used by the status site.
- `MX.Platform.Status.Web` is an Astro static frontend deployed to Azure Static Web Apps and configured to use the Function App as its API backend.
- `terraform` provisions the shared resource group integration, Function App, service plan, storage, key vault, Application Insights access, Static Web App, and supporting role assignments.

## Supported sites

- `xi`
- `mx`
- `dev`

## Local development

### Function App

```bash
cd src
dotnet build
```

### Astro frontend

```bash
cd src/MX.Platform.Status.Web
npm install
npm run build
```

## How to add a new site

1. Add the new site definition and metadata to the content repository once it is available.
2. Update any site mapping/configuration in the Function App so the site is included in rollup, incident, and history processing.
3. Update the Astro frontend navigation and rendering if the new site needs bespoke presentation or labels.
4. Add any required Application Insights resource access in `terraform\tfvars\*.tfvars`.
5. Validate locally, then use the PR verify and deployment workflows to promote the change.

## Planned status content repository

Planned content lives in: https://github.com/frasermolyneux/status-pages

## Terraform resources created

Terraform creates and configures:

- Azure resource group integration via remote state lookups
- Azure Storage account and blob containers
- Azure Key Vault and required secrets
- Azure Application Insights access and monitoring integration
- Azure Functions service plan
- Azure Linux Function App
- Azure Static Web App and Function App registration
- Required role assignments for runtime access

## Manual post-deploy steps

- Create and configure the SonarCloud project for `frasermolyneux_platform-status-web`.
- Create the GitHub PAT used for content access and store it in the target Key Vault.
- Create and store any webhook/shared secrets required by the Function App.
- Configure Azure Static Web App custom domains and DNS validation records.
- Confirm any production hostname mappings and certificates after the first successful deployment.
