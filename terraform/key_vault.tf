resource "azurerm_key_vault" "kv" {
  name                = substr(format("kv-%s-%s", random_id.environment_id.hex, var.location), 0, 24)
  location            = data.azurerm_resource_group.rg.location
  resource_group_name = data.azurerm_resource_group.rg.name
  tenant_id           = data.azurerm_client_config.current.tenant_id

  tags = var.tags

  soft_delete_retention_days = 90
  purge_protection_enabled   = true
  rbac_authorization_enabled = true

  sku_name = "standard"

  network_acls {
    bypass         = "AzureServices"
    default_action = "Allow"
  }
}

resource "azurerm_key_vault_secret" "github_pat" {
  name         = "github-pat"
  value        = "placeholder"
  key_vault_id = azurerm_key_vault.kv.id
  content_type = "text/plain"

  lifecycle {
    ignore_changes = [value]
  }
}

resource "azurerm_key_vault_secret" "webhook_secret" {
  name         = "webhook-secret"
  value        = "placeholder"
  key_vault_id = azurerm_key_vault.kv.id
  content_type = "text/plain"

  lifecycle {
    ignore_changes = [value]
  }
}
