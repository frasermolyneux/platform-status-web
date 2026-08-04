environment             = "prd"
workload_name           = "platform-status-web"
location                = "swedencentral"
static_web_app_location = "westeurope"
static_web_app_sku      = "Standard"

subscription_id = "7760848c-794d-4a19-8cb2-52f71a21ac2b"

app_insights_resources = [
  {
    subscription_id     = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
    resource_group_name = "rg-platform-sitewatch-func-prd-uksouth"
    name                = "ai-platform-sitewatch-func-prd-uksouth"
  },
  {
    subscription_id     = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
    resource_group_name = "rg-platform-sitewatch-func-prd-eastus"
    name                = "ai-platform-sitewatch-func-prd-eastus"
  },
  {
    subscription_id     = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
    resource_group_name = "rg-platform-sitewatch-func-prd-westeurope"
    name                = "ai-platform-sitewatch-func-prd-westeurope"
  },
  # Portal and Geolocation Application Insights: identified via live Azure Resource
  # Graph inspection (Microsoft.Insights/components tagged Workload=portal-core /
  # Workload=geo-location) to close the known Monitoring Reader gap so status-web
  # can query these sites' own availability telemetry. These resources live in
  # sibling products' own subscriptions (not platform-status-web's), so applying
  # this role assignment requires the deploy-prd OIDC service principal to have
  # Microsoft.Authorization/roleAssignments/write on the scopes below - confirm
  # this before the first prd apply after this change.
  {
    subscription_id     = "32444f38-32f4-409f-889c-8e8aa2b5b4d1"
    resource_group_name = "rg-portal-core-prd-uksouth"
    name                = "ai-portal-core-prd-uksouth"
  },
  {
    subscription_id     = "903b6685-c12a-4703-ac54-7ec1ff15ca43"
    resource_group_name = "rg-geo-location-prd-swedencentral"
    name                = "ai-geo-location-prd-swedencentral"
  }
]

platform_workloads_state = {
  resource_group_name  = "rg-tf-platform-workloads-prd-uksouth-01"
  storage_account_name = "sadz9ita659lj9xb3"
  container_name       = "tfstate"
  key                  = "terraform.tfstate"
  subscription_id      = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
  tenant_id            = "e56a6947-bb9a-4a6e-846a-1f118d1c3a14"
}

platform_monitoring_state = {
  resource_group_name  = "rg-tf-platform-monitoring-prd-uksouth-01"
  storage_account_name = "sa74f04c5f984e"
  container_name       = "tfstate"
  key                  = "terraform.tfstate"
  subscription_id      = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
  tenant_id            = "e56a6947-bb9a-4a6e-846a-1f118d1c3a14"
}

tags = {
  Environment = "prd"
  Workload    = "platform-status-web"
  Owner       = "frasermolyneux"
  Source      = "https://github.com/frasermolyneux/platform-status-web"
}
