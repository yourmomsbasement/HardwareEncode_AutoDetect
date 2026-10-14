#!/usr/bin/env python3
"""Smoke-test an isolated, newly started Jellyfin unstable server (never production)."""

import argparse
import functools
import http.server
import json
import secrets
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8096")
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    if not args.url.startswith("http://127.0.0.1:"):
        parser.error("only isolated loopback servers are supported")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    headers = {"Authorization": 'MediaBrowser Client="HWAD smoke", Device="CI", DeviceId="hwad-smoke", Version="1"'}

    def request(path, data=None):
        body = None if data is None else json.dumps(data).encode()
        req = urllib.request.Request(args.url + path, data=body,
                                     headers={**headers, "Content-Type": "application/json"})
        with opener.open(req, timeout=180) as response:
            content = response.read()
            return json.loads(content) if content else None

    for attempt in range(90):
        try:
            info = request("/System/Info/Public")
            if "StartupWizardCompleted" in info and "Version" in info:
                break
            if attempt == 89:
                raise RuntimeError("Server never completed startup")
            time.sleep(1)
        except (urllib.error.URLError, TimeoutError):
            if attempt == 89:
                raise
            time.sleep(1)
    if info["StartupWizardCompleted"]:
        raise RuntimeError("Refusing to modify an already configured server")
    password = secrets.token_urlsafe(24)
    request("/Startup/User")
    request("/Startup/User", {"Name": "hwad-smoke", "Password": password})
    request("/Startup/Complete", {})
    auth = request("/Users/AuthenticateByName", {"Username": "hwad-smoke", "Pw": password})
    headers["Authorization"] += f', Token="{auth["AccessToken"]}"'

    manifest = json.loads(args.manifest.read_text())[0]
    plugin_id = manifest["guid"]
    expected_version = manifest["versions"][0]["version"]
    plugins = request("/Plugins")
    installed = next(plugin for plugin in plugins if uuid.UUID(plugin["Id"]) == uuid.UUID(plugin_id))
    assert installed["Version"] == expected_version, installed
    assert installed["Status"] == "Active", installed
    assert "HasBackup" in request("/HardwareAutoDetect/Status")
    report = request("/HardwareAutoDetect/Run?apply=false", {})
    assert report is not None

    class QuietHandler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *_):
            pass

    handler = functools.partial(QuietHandler, directory=str(args.manifest.resolve().parent))
    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler) as catalog:
        thread = threading.Thread(target=catalog.serve_forever, daemon=True)
        thread.start()
        try:
            url = f"http://127.0.0.1:{catalog.server_port}/{args.manifest.name}"
            request("/Repositories", [{"Name": "HWAD test", "Url": url, "Enabled": True}])
            packages = request("/Packages")
            available = next(package for package in packages if uuid.UUID(package["guid"]) == uuid.UUID(plugin_id))
            assert any(v["version"] == expected_version for v in available["versions"]), available
        finally:
            catalog.shutdown()
            thread.join()
    print(f"PASS: Jellyfin {info['Version']} loaded plugin {expected_version}; "
          "admin status and detection preview work; plugin is available in its catalog.")


if __name__ == "__main__":
    main()
