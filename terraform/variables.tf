variable "environment" {
  description = "Deployment environment (dev or prd)"
  type        = string
  default     = "dev"
}

variable "workload_name" {
  description = "Workload name used for resource naming and remote-state lookups"
  type        = string
  default     = "platform-status-web"
}

variable "location" {
  description = "Azure region for all resources except the Static Web App (see `static_web_app_location`)"
  type        = string
  default     = "swedencentral"
}

variable "static_web_app_location" {
  description = "Azure region for the Static Web App. Azure Static Web Apps are only available in a fixed subset of regions (centralus, eastus2, westus2, westeurope, eastasia), which does not include swedencentral, so this is tracked independently from `location`. Aligned with the westeurope convention used by other Static Web Apps in this org (molyneux-me, twenty-one)."
  type        = string
  default     = "westeurope"
}

variable "static_web_app_sku" {
  description = "Static Web Apps SKU tier/size. Must be Standard: this module always creates the linked bring-your-own Azure Functions registration (azurerm_static_web_app_function_app_registration), which Azure rejects with a `SkuCode 'Free' is invalid` error on the Free tier, so both dev and prd must run Standard. Kept as a variable (set explicitly per environment tfvars) rather than hard-coded so the requirement is visible and any future environment must set it deliberately. Free is intentionally not a valid value here since it would fail at apply time."
  type        = string
  default     = "Standard"

  validation {
    condition     = var.static_web_app_sku == "Standard"
    error_message = "static_web_app_sku must be \"Standard\": this module always creates azurerm_static_web_app_function_app_registration, which requires the Standard SKU."
  }
}

variable "subscription_id" {
  description = "Azure subscription ID for the target environment"
  type        = string
}

variable "platform_workloads_state" {
  description = "Backend coordinates for the platform-workloads remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "platform_monitoring_state" {
  description = "Backend coordinates for the platform-monitoring remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "tags" {
  description = "Resource tags applied to all taggable resources"
  type        = map(string)
  default     = {}
}

variable "app_insights_resources" {
  description = "AI resources the Function App needs Monitoring Reader access to query availability data"
  type = list(object({
    subscription_id     = string
    resource_group_name = string
    name                = string
  }))
  default = []
}

variable "github_app_pem" {
  description = "PEM private key for the Mxio-idp-bot GitHub App. Injected at apply time from GH_APP_PEM Actions secret."
  type        = string
  sensitive   = true
}

variable "github_app_id" {
  description = "GitHub App ID for Mxio-idp-bot (from GH_APP_ID repo variable)."
  type        = string
}

variable "github_app_installation_id" {
  description = "Installation ID for Mxio-idp-bot on this repo (from GH_APP_INSTALLATION_ID repo variable)."
  type        = string
}
