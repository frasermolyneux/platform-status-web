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
