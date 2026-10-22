variable "cloudflare_account_id" {
  type        = string
  description = "Account supplied by the scoped Production credential contract."

  validation {
    condition     = can(regex("^[a-f0-9]{32}$", var.cloudflare_account_id))
    error_message = "A Cloudflare account identifier is required."
  }
}

data "cloudflare_zone" "status" {
  filter = {
    name   = "mxstatus.io"
    status = "active"
    account = {
      id = var.cloudflare_account_id
    }
  }
}

resource "cloudflare_load_balancer_monitor" "status" {
  account_id       = var.cloudflare_account_id
  description      = "Public status origin disk-publication readiness"
  type             = "https"
  method           = "GET"
  path             = "/health/ready"
  expected_codes   = "200"
  expected_body    = "\"status\":\"ready\""
  interval         = 60
  timeout          = 5
  retries          = 1
  consecutive_up   = 2
  consecutive_down = 2
  allow_insecure   = false
  follow_redirects = false
  probe_zone       = "mxstatus.io"
  header = {
    Host = ["mxstatus.io"]
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "cloudflare_load_balancer_pool" "status" {
  account_id      = var.cloudflare_account_id
  name            = "platform-status-origins"
  description     = "Independent status origins; one publisher and no automatic database promotion"
  enabled         = true
  monitor         = cloudflare_load_balancer_monitor.status.id
  minimum_origins = 1
  check_regions   = ["ENAM"]
  origin_steering = {
    policy = "random"
  }
  origins = [
    {
      name    = "ns346663"
      address = "5.196.80.194"
      enabled = true
      weight  = 1
    },
    {
      name    = "ns5019527"
      address = "15.235.119.97"
      enabled = true
      weight  = 1
    }
  ]

  lifecycle {
    prevent_destroy = true
  }
}

resource "cloudflare_load_balancer" "status" {
  zone_id          = data.cloudflare_zone.status.id
  name             = "mxstatus.io"
  description      = "Shared public status routing for Molyneux and XtremeIdiots"
  enabled          = true
  proxied          = true
  default_pools    = [cloudflare_load_balancer_pool.status.id]
  fallback_pool    = cloudflare_load_balancer_pool.status.id
  steering_policy  = "off"
  session_affinity = "none"

  lifecycle {
    prevent_destroy = true
  }
}
