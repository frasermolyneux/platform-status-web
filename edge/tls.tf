data "cloudflare_rulesets" "status_configuration" {
  for_each = local.status_sites
  zone_id  = each.value.zone_id
}

resource "cloudflare_ruleset" "status_tls" {
  for_each = local.status_sites

  zone_id     = each.value.zone_id
  name        = "platform-status-origin-tls"
  description = "Strict origin TLS only for the public status hostname."
  kind        = "zone"
  phase       = "http_config_settings"
  rules = [{
    ref         = "status_origin_strict_tls"
    description = "Validate the status origin certificate without changing other hostnames."
    expression  = "http.host eq \"${each.value.hostname}\""
    action      = "set_config"
    enabled     = true
    action_parameters = {
      ssl = "strict"
    }
  }]

  lifecycle {
    prevent_destroy = true

    precondition {
      condition = alltrue([
        for ruleset in data.cloudflare_rulesets.status_configuration[each.key].rulesets :
        ruleset.phase != "http_config_settings" || ruleset.name == "platform-status-origin-tls"
      ])
      error_message = "An existing configuration entrypoint is not owned by this stack. Stop for reviewed preservation/adoption; never replace its rules."
    }
  }
}
