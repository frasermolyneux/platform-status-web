import contextlib
import io
import subprocess
import unittest
from unittest.mock import patch

import bootstrap_secrets


class DatabaseBootstrapTests(unittest.TestCase):
    def test_rerunning_after_partial_initialization_never_rotates_an_existing_credential(self):
        existing = [{"name": "app-password", "attributes": {"enabled": True}}]
        with patch.object(bootstrap_secrets, "az_json", return_value=existing), \
                patch.object(bootstrap_secrets, "set_secret") as write, \
                patch.object(bootstrap_secrets.secrets, "token_urlsafe", return_value="test-only-generated-value"), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            bootstrap_secrets.seed_database("test-vault")
        self.assertEqual([call.args[1] for call in write.call_args_list],
                         ["postgres-password", "replication-password"])
        self.assertNotIn("test-only-generated-value", output.getvalue())

    def test_metadata_failure_aborts_without_generating_or_overwriting_credentials(self):
        failure = subprocess.CalledProcessError(1, ["az", "keyvault", "secret", "list"])
        with patch.object(bootstrap_secrets, "az_json", side_effect=failure), \
                patch.object(bootstrap_secrets, "set_secret") as write:
            with self.assertRaises(subprocess.CalledProcessError):
                bootstrap_secrets.seed_database("test-vault")
        write.assert_not_called()

    def test_disabled_or_expiring_credentials_require_explicit_recovery(self):
        for attributes in ({"enabled": False}, {"enabled": True, "expires": "2030-01-01T00:00:00Z"}):
            with self.subTest(attributes=attributes), \
                    patch.object(bootstrap_secrets, "az_json", return_value=[
                        {"name": "postgres-password", "attributes": attributes}
                    ]), patch.object(bootstrap_secrets, "set_secret") as write:
                with self.assertRaises(RuntimeError):
                    bootstrap_secrets.seed_database("test-vault")
                write.assert_not_called()


if __name__ == "__main__":
    unittest.main()
