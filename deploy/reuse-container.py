"""Replace an existing Jackett container while preserving its Docker configuration.

Run on the Docker host as root: python3 reuse-container.py jackett jackett-optimized:local
The stopped original container and a private config backup are retained for rollback.
"""
import datetime
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request


class DockerConnection(http.client.HTTPConnection):
    def __init__(self):
        super().__init__("localhost", timeout=120)

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect("/var/run/docker.sock")


def docker(method, path, data=None):
    conn = DockerConnection()
    try:
        body = json.dumps(data).encode() if data is not None else None
        conn.request(method, path, body, {"Content-Type": "application/json"})
        response = conn.getresponse()
        payload = response.read()
        if response.status >= 400:
            raise RuntimeError(f"Docker {method} {path}: HTTP {response.status}")
        return json.loads(payload) if payload else None
    finally:
        conn.close()


def main():
    name, image = sys.argv[1:3]
    original = docker("GET", f"/containers/{name}/json")
    docker("GET", f"/images/{image}/json")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup_name = f"{name}-pre-optimized-{stamp}"
    backup_dir = Path("/home/jellyfin/jackett-optimized/backups") / stamp
    backup_dir.mkdir(mode=0o700, parents=True)
    os.chmod(backup_dir, 0o700)
    snapshot = backup_dir / "container.json"
    snapshot.write_text(json.dumps(original, indent=2))
    snapshot.chmod(0o600)
    config_mount = next(m for m in original["Mounts"] if m["Destination"] == "/config")
    subprocess.run(["tar", "-czf", str(backup_dir / "config.tar.gz"), "-C", config_mount["Source"], "."], check=True)
    (backup_dir / "config.tar.gz").chmod(0o600)

    payload = dict(original["Config"])
    payload["Image"] = image
    payload["Env"] = [e for e in payload.get("Env", []) if not e.startswith("AUTO_UPDATE=")] + ["AUTO_UPDATE=false"]
    payload["Labels"] = dict(payload.get("Labels") or {})
    payload["Labels"]["com.centurylinklabs.watchtower.enable"] = "false"
    payload["HostConfig"] = original["HostConfig"]
    if original["HostConfig"]["NetworkMode"] not in ("bridge", "host", "none", "default"):
        payload["NetworkingConfig"] = {"EndpointsConfig": {
            network: {k: settings[k] for k in ("IPAMConfig", "Links", "Aliases") if settings.get(k)}
            for network, settings in original["NetworkSettings"]["Networks"].items()
        }}

    created = False
    renamed = False
    try:
        docker("POST", f"/containers/{name}/stop?t=30")
        docker("POST", f"/containers/{name}/rename?name={backup_name}")
        renamed = True
        new = docker("POST", f"/containers/create?name={name}", payload)
        created = True
        docker("POST", f"/containers/{new['Id']}/start")
        for _ in range(60):
            try:
                with urllib.request.urlopen("http://127.0.0.1:9117/health", timeout=2) as response:
                    if response.status == 200:
                        print(json.dumps({"deployed": name, "image": image, "rollback_container": backup_name,
                                          "backup": str(backup_dir)}))
                        return
            except Exception:
                pass
            time.sleep(1)
        raise RuntimeError("Jackett did not pass its health check")
    except Exception:
        if created:
            docker("DELETE", f"/containers/{name}?force=true")
        if renamed:
            docker("POST", f"/containers/{backup_name}/rename?name={name}")
        docker("POST", f"/containers/{name}/start")
        print("Deployment failed; the original container has been restarted.", file=sys.stderr)
        raise


if __name__ == "__main__":
    main()
