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
  description = "Azure region for all resources"
  type        = string
  default     = "swedencentral"
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
