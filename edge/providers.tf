terraform {
  required_version = ">= 1.15.6"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = ">= 5.22.0, < 5.28.2"
    }
  }

  backend "azurerm" {}
}

provider "cloudflare" {}
