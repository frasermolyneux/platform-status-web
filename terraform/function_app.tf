resource "azurerm_service_plan" "sp" {
  name                = format("asp-platform-status-web-%s-%s", var.environment, var.location)
  location            = data.azurerm_resource_group.rg.location
  resource_group_name = data.azurerm_resource_group.rg.name

  os_type  = "Linux"
  sku_name = "Y1"

  tags = var.tags
}

resource "azurerm_linux_function_app" "app" {
  name = format("fn-platform-status-web-%s-%s-%s", var.environment, var.location, random_id.environment_id.hex)
  tags = var.tags

  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  service_plan_id = azurerm_service_plan.sp.id

  storage_account_name       = azurerm_storage_account.sa.name
  storage_account_access_key = azurerm_storage_account.sa.primary_access_key

  https_only                    = true
  public_network_access_enabled = true

  functions_extension_version = "~4"

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "9.0"
    }

    application_insights_connection_string = azurerm_application_insights.ai.connection_string

    ftps_state          = "Disabled"
    always_on           = false
    minimum_tls_version = "1.2"
  }

  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING"      = azurerm_application_insights.ai.connection_string
    "ApplicationInsightsAgent_EXTENSION_VERSION" = "~3"

    "STATUS_CONTENT_REPO"            = "frasermolyneux/status-pages"
    "STATUS_CONTENT_BRANCH"          = "main"
    "GitHubApp__AppId"               = var.github_app_id
    "GitHubApp__InstallationId"      = var.github_app_installation_id
    "GitHubApp__PemSecretName"       = azurerm_key_vault_secret.github_app_pem.name
    "WEBHOOK_SECRET_URI"             = azurerm_key_vault_secret.webhook_secret.versionless_id
    "HISTORY_BLOB_CONTAINER"         = azurerm_storage_container.history.name
    "STALE_CACHE_BLOB_CONTAINER"     = azurerm_storage_container.stale_cache.name
    "STORAGE_ACCOUNT_NAME"           = azurerm_storage_account.sa.name
    "LIVE_CACHE_TTL_SECONDS"         = "30"
    "CONTENT_CACHE_TTL_SECONDS"      = "60"
    "ROLLUP_REPLAY_DAYS"             = "3"
    "ROLLUP_BACKFILL_DAYS_FIRST_RUN" = "30"
  }

  lifecycle {
    ignore_changes = [
      app_settings["WEBSITE_ENABLE_SYNC_UPDATE_SITE"],
      app_settings["WEBSITE_RUN_FROM_PACKAGE"],
    ]
  }
}
