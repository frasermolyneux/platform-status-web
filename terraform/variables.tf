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
  description = "Static Web Apps SKU tier/size. Must be Standard: the linked bring-your-own Azure Functions registration (azurerm_static_web_app_function_app_registration) is rejected by Azure with a `SkuCode 'Free' is invalid` error on the Free tier, so both dev and prd must run Standard. Kept as a variable (set explicitly per environment tfvars) rather than hard-coded so the requirement is visible and any future environment must set it deliberately."
  type        = string
  default     = "Standard"

  validation {
    condition     = contains(["Free", "Standard"], var.static_web_app_sku)
    error_message = "static_web_app_sku must be either \"Free\" or \"Standard\"."
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
