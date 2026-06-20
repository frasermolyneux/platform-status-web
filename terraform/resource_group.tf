data "azurerm_resource_group" "rg" {
  name = local.resource_group_name
}

data "azurerm_client_config" "current" {}

resource "random_id" "environment_id" {
  byte_length = 6
}
