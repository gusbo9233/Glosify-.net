#!/usr/bin/env python3
"""Preview or delete only Glosify's retired Azure audio cache. Requires az login."""
import argparse
import json
import re
import subprocess

CONTAINER = "tts-cache"
PATTERN = re.compile(r"[a-z]{2,3}-[A-Z]{2}-[^/]+Neural/[0-9a-f]{64}\.mp3\Z")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--account", required=True, help="Exact Azure storage account name")
    parser.add_argument("--delete", action="store_true", help="Delete previewed matching blobs; default is read-only")
    args = parser.parse_args()
    if not re.fullmatch(r"[a-z0-9]{3,24}", args.account):
        parser.error("Invalid storage account name")
    common = ["--account-name", args.account, "--container-name", CONTAINER, "--auth-mode", "login", "--only-show-errors"]
    # --num-results '*' requests all pages. No keys, connection strings, or user
    # PDF containers are read. Individual deletes require the listed ETag.
    result = subprocess.run(["az", "storage", "blob", "list", *common, "--num-results", "*", "--output", "json"],
                            check=True, capture_output=True, text=True)
    blobs = [b for b in json.loads(result.stdout) if PATTERN.fullmatch(b["name"]) and b.get("properties", {}).get("etag")]
    print(json.dumps({"account": args.account, "container": CONTAINER, "delete": args.delete,
                      "blobs": [{"name": b["name"], "bytes": b["properties"].get("contentLength", 0)} for b in blobs]}, indent=2))
    if args.delete:
        for blob in blobs:
            subprocess.run(["az", "storage", "blob", "delete", *common, "--name", blob["name"],
                            "--if-match", blob["properties"]["etag"], "--output", "none"], check=True)


if __name__ == "__main__":
    main()
