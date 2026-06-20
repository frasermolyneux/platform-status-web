# platform-status-web

[![Build and Test](https://github.com/frasermolyneux/platform-status-web/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/frasermolyneux/platform-status-web/actions/workflows/build-and-test.yml)
[![Code Quality](https://github.com/frasermolyneux/platform-status-web/actions/workflows/codequality.yml/badge.svg)](https://github.com/frasermolyneux/platform-status-web/actions/workflows/codequality.yml)
[![Deploy Development](https://github.com/frasermolyneux/platform-status-web/actions/workflows/deploy-dev.yml/badge.svg)](https://github.com/frasermolyneux/platform-status-web/actions/workflows/deploy-dev.yml)
[![Deploy Production](https://github.com/frasermolyneux/platform-status-web/actions/workflows/deploy-prd.yml/badge.svg)](https://github.com/frasermolyneux/platform-status-web/actions/workflows/deploy-prd.yml)

## Overview

Multi-site public status page that combines Azure Application Insights availability data with GitHub Issues-backed incident content to publish a customer-facing status experience for multiple platforms. Built as an Astro static frontend on Azure Static Web Apps with a .NET Function App backend.

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

## Documentation

Additional documentation is available in the [docs](docs/) folder.

## Contributing

Please refer to the [CONTRIBUTING](CONTRIBUTING.md) file for information on how to contribute to this project.

## Security

Please refer to the [SECURITY](SECURITY.md) file for information on how to report security vulnerabilities.
