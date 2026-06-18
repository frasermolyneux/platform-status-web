locals {
  resource_group_name = data.terraform_remote_state.platform_workloads.outputs.workload_resource_groups[var.workload_name][var.environment].resource_groups[lower(var.location)].name

  platform_monitoring_workspace_id = data.terraform_remote_state.platform_monitoring.outputs.log_analytics.id
}
