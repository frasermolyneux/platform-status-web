output "func_apps" {
  value = [{
    name                = azurerm_linux_function_app.app.name
    resource_group_name = azurerm_linux_function_app.app.resource_group_name
  }]
}

output "static_web_app" {
  value = {
    name                = azurerm_static_web_app.swa.name
    resource_group_name = azurerm_static_web_app.swa.resource_group_name
  }
}

output "static_web_app_name" {
  value = azurerm_static_web_app.swa.name
}

output "key_vault" {
  value = {
    name                = azurerm_key_vault.kv.name
    id                  = azurerm_key_vault.kv.id
    resource_group_name = azurerm_key_vault.kv.resource_group_name
  }
}
