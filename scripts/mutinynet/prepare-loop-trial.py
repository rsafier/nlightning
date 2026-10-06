#!/usr/bin/env python3
"""Prepare a private candidate config for one FAFO Loop trial; never edit the live config."""
import argparse
import json
import os
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--config-file", type=Path, required=True)
parser.add_argument("--candidate-dir", type=Path, required=True)
parser.add_argument("--port", type=int, default=10019)
args = parser.parse_args()
source = args.config_file.expanduser().resolve()
candidate = args.candidate_dir.expanduser().resolve()
if candidate == source.parent or not 1024 <= args.port <= 65535:
    parser.error("use a separate candidate directory and a port from 1024 to 65535")
config = json.loads(source.read_text())
# JSON configuration keys are case-insensitive in .NET: remove any existing spelling.
for key in list(config):
    if key.lower() == "lndgrpc":
        del config[key]
config["LndGrpc"] = {
    "Enabled": True,
    "ListenAddress": "127.0.0.1",
    "Port": args.port,
    "DataDirectory": str(source.parent / "lnd-grpc-loop-trial"),
    "EnableSigner": True,
    "AllowMainnet": False,
    "AllowSignerOnMainnet": False,
    "AllowNoMacaroons": False,
    "Signer": {
        "AllowedKeyFamilies": [21, 99, 42060, 42068, 42069],
        "MaxSessions": 1000,
        "SessionLifetime": "01:00:00"
    }
}
candidate.mkdir(mode=0o700, parents=True, exist_ok=True)
output = candidate / "appsettings.json"
# Exclusive creation prevents accidentally replacing an earlier candidate.
with os.fdopen(os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as file:
    json.dump(config, file, indent=2)
    file.write("\n")
print(f"Candidate saved to {output}; live config is unchanged.")
print(f"Loopback TLS/macaroon listener: 127.0.0.1:{args.port}; swap signer enabled in candidate only.")
