variable "environment" {
  default = "dev"
}

variable "workload_name" {
  type    = string
  default = "platform-status-web"
}

variable "location" {
  type    = string
  default = "swedencentral"
}

variable "subscription_id" {}

variable "platform_workloads_state" {
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
  default = {}
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
