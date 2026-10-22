from datetime import datetime, timedelta, timezone
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import bootstrap_origin_tls


class OriginTlsRenewalTests(unittest.TestCase):
    def metadata(self, days):
        return [{
            "name": "origin-tls",
            "attributes": {"enabled": True, "expires": (datetime.now(timezone.utc) + timedelta(days=days)).isoformat()},
            "tags": {
                "managed-by": "platform-status-web",
                "certificate-hostnames": ",".join(bootstrap_origin_tls.HOSTNAMES),
            },
        }]

    def test_unexpired_bundle_is_retained_without_reading_its_private_key_or_calling_cloudflare(self):
        with patch.object(bootstrap_origin_tls, "az_json", return_value=self.metadata(90)), \
                patch.object(bootstrap_origin_tls, "new_certificate") as issue, \
                patch.object(bootstrap_origin_tls, "set_secret") as write:
            bootstrap_origin_tls.renew("test-vault")
        issue.assert_not_called()
        write.assert_not_called()

    def test_actual_issued_certificate_must_match_the_csr_key_and_both_names(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ca_key, ca, request, certificate = [root / name for name in ("ca.key", "ca.pem", "request.pem", "certificate.pem")]
            bootstrap_origin_tls.openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", ca_key)
            bootstrap_origin_tls.openssl("req", "-new", "-x509", "-key", ca_key, "-days", "365",
                                         "-subj", "/CN=StatusTestCA", "-out", ca)

            def issue(_token, csr):
                request.write_text(csr)
                extensions = root / "extensions"
                extensions.write_text("subjectAltName=DNS:mxstatus.io,DNS:status.xtremeidiots.com\n")
                bootstrap_origin_tls.openssl("x509", "-req", "-in", request, "-CA", ca, "-CAkey", ca_key,
                                             "-CAcreateserial", "-days", "365", "-extfile", extensions,
                                             "-out", certificate)
                return {"certificate": certificate.read_text(), "id": "test-certificate"}

            with patch.object(bootstrap_origin_tls, "request_certificate", side_effect=issue):
                bundle, expiry, _ = bootstrap_origin_tls.new_certificate("test-only-token")
            self.assertIn("BEGIN CERTIFICATE", bundle)
            self.assertIn("PRIVATE KEY", bundle)
            self.assertGreater(datetime.fromisoformat(expiry), datetime.now(timezone.utc) + timedelta(days=300))

            with patch.object(bootstrap_origin_tls, "request_certificate", return_value={
                "certificate": ca.read_text(), "id": "wrong-key-and-names"
            }):
                with self.assertRaisesRegex(RuntimeError, "private key"):
                    bootstrap_origin_tls.new_certificate("test-only-token")

            def issue_without_names(token, csr):
                request.write_text(csr)
                bootstrap_origin_tls.openssl("x509", "-req", "-in", request, "-CA", ca, "-CAkey", ca_key,
                                             "-days", "365", "-out", certificate)
                return {"certificate": certificate.read_text(), "id": "missing-names"}

            with patch.object(bootstrap_origin_tls, "request_certificate", side_effect=issue_without_names):
                with self.assertRaisesRegex(RuntimeError, "hostnames"):
                    bootstrap_origin_tls.new_certificate("test-only-token")

    def test_due_certificate_is_replaced_as_one_validated_bundle(self):
        with patch.object(bootstrap_origin_tls, "az_json", return_value=self.metadata(10)), \
                patch.dict(bootstrap_origin_tls.os.environ, {"CLOUDFLARE_API_TOKEN": "test-only-token"}), \
                patch.object(bootstrap_origin_tls, "new_certificate",
                             return_value=("test-only-bundle", "2030-01-01T00:00:00+00:00", "test-certificate")), \
                patch.object(bootstrap_origin_tls, "set_secret") as write:
            bootstrap_origin_tls.renew("test-vault")
        self.assertEqual(write.call_count, 1)
        self.assertEqual(write.call_args.args, ("test-vault", "origin-tls", "test-only-bundle"))
        self.assertEqual(write.call_args.kwargs["expires"], "2030-01-01T00:00:00+00:00")

    def test_certificate_failure_never_replaces_the_current_bundle(self):
        with patch.object(bootstrap_origin_tls, "az_json", return_value=self.metadata(10)), \
                patch.dict(bootstrap_origin_tls.os.environ, {"CLOUDFLARE_API_TOKEN": "test-only-token"}), \
                patch.object(bootstrap_origin_tls, "new_certificate", side_effect=RuntimeError("Invalid certificate")), \
                patch.object(bootstrap_origin_tls, "set_secret") as write:
            with self.assertRaises(RuntimeError):
                bootstrap_origin_tls.renew("test-vault")
        write.assert_not_called()


if __name__ == "__main__":
    unittest.main()
