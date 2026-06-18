resource "azurerm_storage_account" "sa" {
  name                = "sa${random_id.environment_id.hex}"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  https_traffic_only_enabled      = true
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = true

  identity {
    type = "SystemAssigned"
  }

  tags = var.tags
}

resource "azurerm_storage_container" "history" {
  name                  = "history"
  storage_account_id    = azurerm_storage_account.sa.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "stale_cache" {
  name                  = "stale-cache"
  storage_account_id    = azurerm_storage_account.sa.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "func_content" {
  name                  = "func-content"
  storage_account_id    = azurerm_storage_account.sa.id
  container_access_type = "private"
}
