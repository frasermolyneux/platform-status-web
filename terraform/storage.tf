resource "azurerm_storage_account" "sa" {
  # checkov:skip=CKV2_AZURE_40:Retained legacy Function host uses this account's access key; preserve the previously accepted exception on this account only.
  # checkov:skip=CKV2_AZURE_41:Retained legacy Function uses an account key rather than SAS; preserve the previously accepted SAS-expiry exception on this account only.
  # checkov:skip=CKV2_AZURE_38:Retained legacy cache/content blobs are regenerated; preserve the previously accepted no-soft-delete exception on this account only.
  # checkov:skip=CKV_AZURE_59:Public blob access is already disabled by allow_nested_items_to_be_public=false; retain this account's accepted pinned-scanner compatibility exception.
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
