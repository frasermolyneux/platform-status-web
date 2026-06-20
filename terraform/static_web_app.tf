resource "azurerm_static_web_app" "swa" {
  name                = format("swa-platform-status-web-%s", var.environment)
  location            = data.azurerm_resource_group.rg.location
  resource_group_name = data.azurerm_resource_group.rg.name
  sku_tier            = "Free"
  sku_size            = "Free"

  tags = var.tags
}

# Link the Function App as the SWA's API backend
resource "azurerm_static_web_app_function_app_registration" "api" {
  static_web_app_id = azurerm_static_web_app.swa.id
  function_app_id   = azurerm_linux_function_app.app.id
}

# Phase 2: Custom domain bindings
# To add custom domains, create azurerm_static_web_app_custom_domain resources:
#
# resource "azurerm_static_web_app_custom_domain" "xi" {
#   static_web_app_id = azurerm_static_web_app.swa.id
#   domain_name       = "status.xtremeidiots.com"
#   validation_type   = "cname-delegation"
# }
#
# resource "azurerm_static_web_app_custom_domain" "mx" {
#   static_web_app_id = azurerm_static_web_app.swa.id
#   domain_name       = "status.molyneux.me"
#   validation_type   = "cname-delegation"
# }
