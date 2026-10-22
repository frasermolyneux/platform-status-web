"""Renew one validated Origin CA key/certificate bundle without changing routing."""

from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from urllib.request import Request, urlopen

from bootstrap_secrets import az_json, reviewed_vault, set_secret


HOSTNAMES = ("mxstatus.io", "status.xtremeidiots.com")
SECRET_NAME = "origin-tls"
OWNER_TAG = "platform-status-web"


def request_certificate(token, csr):
    request = Request(
        "https://api.cloudflare.com/client/v4/certificates",
        data=json.dumps({
            "csr": csr,
            "hostnames": HOSTNAMES,
            "request_type": "origin-ecc",
            "requested_validity": 365,
        }).encode(),
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        method="POST",
    )
    with urlopen(request, timeout=60) as response:
        document = json.load(response)
    if document.get("success") is not True:
        raise RuntimeError("Cloudflare did not create the origin certificate.")
    return document["result"]


def openssl(*arguments):
    return subprocess.check_output(["openssl", *map(str, arguments)], text=True)


def new_certificate(token):
    with tempfile.TemporaryDirectory(prefix="status-origin-tls-") as directory:
        root = Path(directory)
        key, csr, certificate = root / "key.pem", root / "request.pem", root / "certificate.pem"
        openssl("ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", key)
        key.chmod(0o600)
        openssl("req", "-new", "-sha256", "-key", key, "-out", csr, "-subj", "/CN=mxstatus.io",
                "-addext", "subjectAltName=" + ",".join(f"DNS:{name}" for name in HOSTNAMES))
        issued = request_certificate(token, csr.read_text())
        certificate.write_text(issued["certificate"], encoding="ascii")
        if openssl("x509", "-in", certificate, "-pubkey", "-noout") != openssl("pkey", "-in", key, "-pubout"):
            raise RuntimeError("Origin certificate does not match its locally generated private key.")
        names = re.findall(r"\bDNS:([^,\s]+)", openssl("x509", "-in", certificate, "-noout", "-ext", "subjectAltName"))
        if {name.lower() for name in names} != set(HOSTNAMES):
            raise RuntimeError("Origin certificate must cover exactly the two approved public hostnames.")
        openssl("x509", "-in", certificate, "-noout", "-checkend", 60 * 24 * 60 * 60)
        expiry = datetime.strptime(
            openssl("x509", "-in", certificate, "-enddate", "-noout").strip().removeprefix("notAfter="),
            "%b %d %H:%M:%S %Y %Z",
        ).replace(tzinfo=timezone.utc).isoformat()
        return certificate.read_text() + "\n" + key.read_text(), expiry, issued["id"]


def renew(vault):
    existing = [
        entry for entry in az_json("keyvault", "secret", "list", "--vault-name", vault)
        if entry["name"] == SECRET_NAME
    ]
    if existing:
        attributes = existing[0]["attributes"]
        tags = existing[0].get("tags") or {}
        if (attributes.get("enabled") is not True or attributes.get("expires") is None
                or tags.get("managed-by") != OWNER_TAG
                or tags.get("certificate-hostnames") != ",".join(HOSTNAMES)):
            raise RuntimeError("Existing origin TLS secret does not have the reviewed renewal metadata.")
        expires = datetime.fromisoformat(attributes["expires"].replace("Z", "+00:00"))
        if expires.tzinfo is None:
            raise RuntimeError("Origin certificate expiry must include a timezone.")
        if expires > datetime.now(timezone.utc) + timedelta(days=30):
            print("Retained the existing origin TLS certificate; renewal is not due.")
            return
    bundle, expiry, certificate_id = new_certificate(os.environ["CLOUDFLARE_API_TOKEN"])
    set_secret(vault, SECRET_NAME, bundle, expires=expiry, tags=(
        f"managed-by={OWNER_TAG}",
        "certificate-hostnames=" + ",".join(HOSTNAMES),
        f"origin-certificate-id={certificate_id}",
    ))
    print("Stored one validated origin TLS key/certificate bundle. No DNS or routing was changed.")


if __name__ == "__main__":
    renew(reviewed_vault())
