import copy
import unittest
from unittest.mock import Mock

from activate_edge import enable


EDGE = {"account_id": "account", "zone_id": "zone", "load_balancer_id": "lb",
        "pool_id": "pool", "monitor_id": "monitor"}


def responses():
    before = {"id": "lb", "name": "mxstatus.io", "proxied": True, "default_pools": ["pool"],
              "fallback_pool": "pool", "steering_policy": "off", "session_affinity": "none",
              "enabled": False, "country_pools": None}
    pool = {"enabled": True, "monitor": "monitor", "check_regions": ["ENAM"]}
    health = {"pool_id": "pool", "pop_health": {"ENAM": {"healthy": True, "origins": [
        {"5.196.80.194": {"healthy": True, "response_code": 200}},
        {"15.235.119.97": {"healthy": True, "response_code": 200}},
    ]}}}
    after = dict(before, enabled=True, modified_on="new-time")
    return before, pool, health, after


class ActivationTests(unittest.TestCase):
    def test_sends_only_enablement_and_verifies_unchanged_settings(self):
        before, pool, health, after = responses()
        request = Mock(side_effect=[before, pool, health, after, after])
        enable(EDGE, request)
        self.assertEqual(request.call_args_list[3].args, (
            "/zones/zone/load_balancers/lb", {"enabled": True}))

    def test_retry_checks_health_without_repeating_patch(self):
        before, pool, health, after = responses()
        before["enabled"] = True
        request = Mock(side_effect=[before, pool, health, after])
        enable(EDGE, request)
        self.assertTrue(all(len(call.args) == 1 for call in request.call_args_list))

    def test_unhealthy_missing_or_substituted_origin_never_activates(self):
        for mutation in ("unhealthy", "missing", "substituted"):
            with self.subTest(mutation=mutation):
                before, pool, health, after = responses()
                origins = health["pop_health"]["ENAM"]["origins"]
                if mutation == "unhealthy":
                    origins[0]["5.196.80.194"]["healthy"] = False
                elif mutation == "missing":
                    origins.pop()
                else:
                    origins[0] = {"192.0.2.1": {"healthy": True, "response_code": 200}}
                request = Mock(side_effect=[before, pool, health])
                with self.assertRaises(ValueError):
                    enable(EDGE, request)
                self.assertEqual(request.call_count, 3)

    def test_unexpected_post_activation_change_is_not_reported_as_success(self):
        before, pool, health, after = responses()
        changed = copy.deepcopy(after)
        changed["country_pools"] = {"US": ["unapproved-pool"]}
        request = Mock(side_effect=[before, pool, health, after, changed])
        with self.assertRaisesRegex(RuntimeError, "Routing is enabled.*country_pools"):
            enable(EDGE, request)
