resource "azurerm_role_assignment" "app_to_keyvault" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_function_app.app.identity[0].principal_id
}

resource "azurerm_role_assignment" "app_to_storage_blob" {
  scope                = azurerm_storage_account.sa.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_function_app.app.identity[0].principal_id
}

# Monitoring Reader on external AI resources (for querying availability data)
resource "azurerm_role_assignment" "app_to_app_insights" {
  for_each = { for idx, ai in var.app_insights_resources : ai.name => ai }

  scope                = format("/subscriptions/%s/resourceGroups/%s/providers/Microsoft.Insights/components/%s", each.value.subscription_id, each.value.resource_group_name, each.value.name)
  role_definition_name = "Monitoring Reader"
  principal_id         = azurerm_linux_function_app.app.identity[0].principal_id
}
