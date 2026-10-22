output "status_edge" {
  description = "Identifiers for read-only origin-health verification."
  value = {
    account_id       = var.cloudflare_account_id
    zone_id          = data.cloudflare_zone.status.id
    monitor_id       = cloudflare_load_balancer_monitor.status.id
    pool_id          = cloudflare_load_balancer_pool.status.id
    load_balancer_id = cloudflare_load_balancer.status.id
    enabled          = cloudflare_load_balancer.status.enabled
    strict_tls_ruleset_ids = {
      for zone, ruleset in cloudflare_ruleset.status_tls : zone => ruleset.id
    }
  }
}
