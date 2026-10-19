import argparse
import base64
import fcntl
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


def run(args, data=None):
    result = subprocess.run(args, input=data, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError(f"Command failed: {args[0]} {args[1]}")
    return result.stdout


def atomic_write(path, data):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as temporary:
        temporary.write(data)
        temporary.flush()
        os.fsync(temporary.fileno())
        temporary_path = Path(temporary.name)
    try:
        temporary_path.replace(path)
    finally:
        temporary_path.unlink(missing_ok=True)


def synchronize(config, check):
    store = json.loads(run(["docker", "exec", config["traefik_container"], "cat", "/acme.json"]))
    certificates = [cert for resolver in store.values() for cert in resolver.get("Certificates", [])]
    for target in config["targets"]:
        hostname = target["hostname"]
        matches = [cert for cert in certificates if hostname in
                   [cert["domain"]["main"], *cert["domain"].get("sans", [])]]
        if len(matches) != 1:
            raise RuntimeError(f"Expected one certificate for {hostname}")
        certificate = base64.b64decode(matches[0]["certificate"], validate=True)
        key = base64.b64decode(matches[0]["key"], validate=True)
        match = run(["openssl", "x509", "-noout", "-checkhost", hostname], certificate)
        if b"does match certificate" not in match:
            raise RuntimeError(f"Certificate hostname mismatch for {hostname}")
        run(["openssl", "x509", "-noout", "-checkend", "86400"], certificate)
        certificate_public_key = run(["openssl", "x509", "-noout", "-pubkey"], certificate)
        private_public_key = run(["openssl", "pkey", "-pubout"], key)
        if certificate_public_key != private_public_key:
            raise RuntimeError(f"Certificate key mismatch for {hostname}")
        directory = Path(target["directory"])
        if check:
            print(f"Validated certificate and key for {hostname}")
            continue
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        directory.chmod(0o700)
        certificate_path = directory / "fullchain.pem"
        key_path = directory / "privkey.pem"
        changed = (not certificate_path.exists() or certificate_path.read_bytes() != certificate or
                   not key_path.exists() or key_path.read_bytes() != key)
        if changed:
            atomic_write(key_path, key)
            atomic_write(certificate_path, certificate)
        container = json.loads(run(["docker", "inspect", target["container"]]))[0]
        expected_env = "GLOBAL_DOWNSTREAM_CERT_PATH=/etc/pooler/tls/fullchain.pem"
        if expected_env not in container["Config"]["Env"]:
            print(f"Exported certificate for {hostname}; pooler TLS is not configured yet")
            continue
        fingerprint = run(["openssl", "x509", "-noout", "-fingerprint", "-sha256"], certificate)
        marker = directory / "loaded-fingerprint"
        if not marker.exists() or marker.read_bytes() != fingerprint:
            run(["docker", "restart", "--time", "30", target["container"]])
            for _ in range(45):
                state = json.loads(run(["docker", "inspect", target["container"]]))[0]["State"]
                if state.get("Health", {}).get("Status") == "healthy":
                    break
                time.sleep(2)
            else:
                raise RuntimeError(f"Pooler did not become healthy: {target['container']}")
            atomic_write(marker, fingerprint)
            print(f"Reloaded renewed certificate for {hostname}")
        else:
            print(f"Certificate unchanged for {hostname}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export existing Traefik certificates to Supavisor")
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    with args.config.with_suffix(".lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        synchronize(json.loads(args.config.read_text()), args.check)
