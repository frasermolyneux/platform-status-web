data "cloudflare_zone" "xtremeidiots" {
  filter = {
    name   = "xtremeidiots.com"
    status = "active"
    account = {
      id = var.cloudflare_account_id
    }
  }
}

locals {
  status_sites = {
    "mxstatus.io" = {
      zone_id  = data.cloudflare_zone.status.id
      hostname = "mxstatus.io"
    }
    "xtremeidiots.com" = {
      zone_id  = data.cloudflare_zone.xtremeidiots.id
      hostname = "status.xtremeidiots.com"
    }
  }
}

data "cloudflare_zone_setting" "ssl" {
  for_each   = local.status_sites
  zone_id    = each.value.zone_id
  setting_id = "ssl"
}

data "cloudflare_dns_records" "status" {
  for_each = local.status_sites
  zone_id  = each.value.zone_id
  name = {
    exact = each.value.hostname
  }
}

output "status_edge_preflight" {
  description = "Read-only account, effective zone SSL mode, and existing status routing; inspect before activation."
  value = {
    for zone, site in local.status_sites : zone => {
      account_id = var.cloudflare_account_id
      zone_id    = site.zone_id
      ssl_mode   = data.cloudflare_zone_setting.ssl[zone].value
      configuration_entrypoints = [
        for ruleset in data.cloudflare_rulesets.status_configuration[zone].rulesets : {
          id   = ruleset.id
          name = ruleset.name
          kind = ruleset.kind
        } if ruleset.phase == "http_config_settings"
      ]
      records = [
        for record in data.cloudflare_dns_records.status[zone].result : {
          id      = record.id
          type    = record.type
          name    = record.name
          content = record.content
          proxied = record.proxied
        } if contains(["A", "AAAA", "CNAME", "HTTPS"], record.type)
      ]
    }
  }
}
