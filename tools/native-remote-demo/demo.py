#!/usr/bin/env python3
"""Regtest-only, node-trusted native remote signing demo supervisor."""

import argparse
import fcntl
import json
import math
import os
from pathlib import Path
import re
import secrets
import signal
import stat
import subprocess
import sys
import threading
import time
from urllib.parse import urlsplit


HISTORY_SUFFIXES = ("", ".key-index", ".enrollment", ".nonces", ".swap-sessions")
PUBLIC_KEY = re.compile(r"0[23][0-9a-fA-F]{64}\Z")


def absolute(value):
    path = Path(value)
    if not path.is_absolute():
        raise ValueError("Paths must be absolute.")
    for item in (path, *path.parents):
        if item.is_symlink():
            raise ValueError("Symbolic links are not supported for demo paths.")
    return path


def private_file(value):
    path = absolute(value)
    mode = path.stat()
    if not stat.S_ISREG(mode.st_mode) or mode.st_uid != os.getuid() or mode.st_mode & 0o077:
        raise ValueError("Secret and state files must be regular, owner-owned files with mode 600.")
    return path


def write_json(path, value):
    temporary = path.with_name(path.name + ".tmp-" + secrets.token_hex(6))
    try:
        with open(temporary, "x", encoding="utf-8") as stream:
            os.chmod(temporary, 0o600)
            json.dump(value, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        temporary.unlink(missing_ok=True)


def load(root):
    root = absolute(root)
    mode = root.stat()
    if mode.st_uid != os.getuid() or mode.st_mode & 0o077:
        raise ValueError("Demo root must be owned by the current user with mode 700.")
    manifest = json.loads(private_file(root / "demo.json").read_text())
    if manifest.get("version") != 1 or manifest.get("root") != str(root) or manifest.get("network") != "regtest":
        raise ValueError("Unsupported or relocated demo manifest.")
    for node in manifest["nodes"]:
        for key in ("signerState", "signerSocket", "signerToken", "nodeConfig", "nodeIpc", "nodeCookie", "nodeDatabase"):
            path = absolute(node[key])
            if not path.is_relative_to(root / node["name"]):
                raise ValueError("Manifest paths must stay within their node directory.")
    return manifest


def selected(manifest, name):
    nodes = [node for node in manifest["nodes"] if name is None or node["name"] == name]
    if not nodes:
        raise ValueError("Unknown node.")
    return nodes


def histories(node, require=False):
    paths = [Path(node["signerState"] + suffix) for suffix in HISTORY_SUFFIXES]
    found = [path.exists() for path in paths]
    config = json.loads(private_file(node["nodeConfig"]).read_text())
    if require or any(found) or config["Signing"].get("ExpectedNodePublicKey"):
        if not all(found):
            raise ValueError("Incomplete signer history: restore the complete original bundle; never reset it.")
        for path in paths:
            private_file(path)
    if Path(node["signerState"] + ".authority-profile").exists():
        raise ValueError("This prototype launcher cannot operate an authority-profile signer.")


def pin(manifest, node, public_key):
    if not PUBLIC_KEY.fullmatch(public_key):
        raise ValueError("Expected a compressed public node key.")
    root = Path(manifest["root"])
    lock_path = root / "identity.lock"
    if lock_path.exists():
        private_file(lock_path)
    with open(lock_path, "a") as lock:
        os.chmod(lock_path, 0o600)
        deadline = time.monotonic() + 5
        while True:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    raise ValueError("Identity pinning is busy; retry without modifying state.")
                time.sleep(0.05)
        # Public identity comparison and pinning share one root-wide lock, including independent supervisors.
        histories(node, require=True)
        path = Path(node["nodeConfig"])
        config = json.loads(private_file(path).read_text())
        existing = config["Signing"].get("ExpectedNodePublicKey")
        if existing and existing.lower() != public_key.lower():
            raise ValueError("Signer identity differs from the pinned node identity.")
        for other in manifest["nodes"]:
            if other["name"] == node["name"]:
                continue
            other_config = json.loads(private_file(other["nodeConfig"]).read_text())
            other_key = other_config["Signing"].get("ExpectedNodePublicKey")
            if other_key and other_key.lower() == public_key.lower():
                raise ValueError("Separate hosted nodes must have distinct public signing identities.")
        if not existing:
            config["Signing"]["ExpectedNodePublicKey"] = public_key.lower()
            write_json(path, config)


def initialize(args):
    root = absolute(args.root)
    core = json.loads(private_file(args.core_config).read_text())
    if set(core) != {"RpcEndpoint", "RpcUser", "RpcPassword"} or not all(isinstance(v, str) and v for v in core.values()):
        raise ValueError("Core configuration requires exactly RpcEndpoint, RpcUser and RpcPassword strings.")
    endpoint = urlsplit(core["RpcEndpoint"])
    if endpoint.scheme not in ("http", "https") or not endpoint.hostname or endpoint.username or endpoint.password:
        raise ValueError("Core endpoint requires HTTP(S) without URL credentials.")
    binaries = {key: str(absolute(getattr(args, key))) for key in ("dotnet", "signer_dll", "node_dll", "client_dll")}
    for value in binaries.values():
        if not Path(value).is_file():
            raise ValueError("A requested executable or assembly is missing.")
    if args.base_peer_port < 1024 or args.base_peer_port > 65534:
        raise ValueError("Base peer port must be between 1024 and 65534.")
    # Refuse overwriting even an empty existing root: initialization creates a new identity namespace.
    root.mkdir(mode=0o700)
    manifest = {"version": 1, "root": str(root), "network": "regtest", "dotnet": binaries["dotnet"],
                "signerDll": binaries["signer_dll"], "nodeDll": binaries["node_dll"], "clientDll": binaries["client_dll"], "nodes": []}
    for index, name in enumerate(("a", "b")):
        directory = root / name
        directory.mkdir(mode=0o700)
        signer = directory / "signer"
        signer.mkdir(mode=0o700)
        node_dir = directory / "node"
        node_dir.mkdir(mode=0o700)
        socket = signer / "signer.sock"
        if len(os.fsencode(socket)) > 100:
            raise ValueError("Choose a shorter root; Unix socket paths must fit the platform limit.")
        node = {"name": name, "nodeId": "demo-" + name, "ownerId": "demo-owner-" + name,
                "signerId": "demo-signer-" + name, "peerPort": args.base_peer_port + index,
                "signerState": str(signer / "history"), "signerSocket": str(socket), "signerToken": str(signer / "token"),
                "nodeConfig": str(node_dir / "appsettings.json"), "nodeIpc": str(node_dir / "nltg.ipc"),
                "nodeCookie": str(node_dir / "nltg.cookie"), "nodeDatabase": str(node_dir / "node.db")}
        with open(node["signerToken"], "x", encoding="ascii") as stream:
            os.chmod(node["signerToken"], 0o600)
            stream.write(secrets.token_hex(32) + "\n")
        config = {"Node": {"Network": "regtest", "Daemon": False, "ListenAddresses": [f"127.0.0.1:{node['peerPort']}"],
                           "FeeUpdates": {"Enabled": False}, "Bootstrap": {"Enabled": False}},
                  "Signing": {"Mode": "RemoteNative", "NodeId": node["nodeId"], "OwnerId": node["ownerId"],
                              "SignerId": node["signerId"], "SocketPath": node["signerSocket"], "AuthTokenFile": node["signerToken"]},
                  "Database": {"Provider": "Sqlite", "ConnectionString": 'Data Source="' + node["nodeDatabase"].replace('"', '""') + '"', "RunMigrations": True},
                  "Bitcoin": {**core, "Notifications": "Poll", "PollInterval": "00:00:01", "TipPollInterval": "00:00:01"},
                  "FeeEstimation": {"Source": "Fixed", "FixedFeeRatePerKw": 2500, "CacheFile": str(node_dir / "fees.json")},
                  "Accounting": {"Prices": {"Source": "None"}}, "Gossip": {"Enabled": False, "SyncEnabled": False, "RelayEnabled": False}}
        if args.legacy_channels:
            config["Node"]["Features"] = {"OptionAnchors": "No"}
        write_json(Path(node["nodeConfig"]), config)
        manifest["nodes"].append(node)
    write_json(root / "demo.json", manifest)
    print("DEMO_INITIALIZED root=" + str(root), flush=True)


def environment():
    # Node configuration must not inherit ambient NLTG overrides or ASP.NET listener settings.
    return {key: value for key, value in os.environ.items()
            if not key.startswith(("NLTG_", "ASPNETCORE_"))}


def process_start(pid):
    try:
        return Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
    except (FileNotFoundError, ProcessLookupError):
        return None


def client_command(manifest, node, arguments):
    return [manifest["dotnet"], manifest["clientDll"], "--cookie", str(Path(node["nodeCookie"]).parent), *arguments]


def run(args):
    manifest = load(args.root)
    nodes = selected(manifest, args.node)
    children, locks, streams, drain_threads = [], [], [], []
    controls, acknowledgement = {}, None
    shutdown = threading.Event()
    previous = {sig: signal.signal(sig, lambda _sig, _frame: shutdown.set()) for sig in (signal.SIGINT, signal.SIGTERM)}
    try:
        for node in nodes:
            directory = Path(manifest["root"]) / node["name"]
            lock_path = directory / "supervisor.lock"
            if lock_path.exists():
                private_file(lock_path)
            lock = open(lock_path, "a")
            os.chmod(lock_path, 0o600)
            locks.append(lock)
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            histories(node)
            # Never unlink another process's endpoint, including an endpoint left by a crash.
            for endpoint in (node["signerSocket"], node["nodeIpc"]):
                if os.path.lexists(endpoint):
                    raise ValueError("Endpoint already exists. Verify its owner is stopped before operator-managed recovery.")
            seed_path = private_file(getattr(args, "seed_" + node["name"] + "_file") or "")
            if seed_path.is_relative_to(Path(manifest["root"])):
                raise ValueError("Seed files must be supplied externally, outside demo/node state directories.")
            # The launcher never persists seed bytes. Only the signer child receives this pipe.
            if seed_path.stat().st_size > 66:
                raise ValueError("Seed file exceeds the bounded one-line format.")
            seed = bytearray(seed_path.read_bytes())
            try:
                if not re.fullmatch(rb"[0-9a-fA-F]{64}\r?\n?", seed):
                    raise ValueError("Seed must be one 64-character hexadecimal line.")
                command = [manifest["dotnet"], manifest["signerDll"], "--seed-stdin", "--state-file", node["signerState"],
                           "--socket", node["signerSocket"], "--auth-token-file", node["signerToken"], "--network", "regtest",
                           "--node-id", node["nodeId"], "--owner-id", node["ownerId"], "--signer-id", node["signerId"]]
                ready, identity = threading.Event(), []
                log_path = directory / "signer.log"
                if log_path.exists():
                    private_file(log_path)
                log = open(log_path, "a", encoding="utf-8")
                streams.append(log)
                signer = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                          text=False, env=environment(), cwd=directory)
                children.append(signer)

                def drain(process=signer, output=log, event=ready, result=identity):
                    for raw in iter(process.stdout.readline, b""):
                        line = raw.decode("utf-8", errors="replace")
                        output.write(line)
                        output.flush()
                        if line.startswith("SIGNER_READY "):
                            match = re.search(r"\bnode=(0[23][0-9a-fA-F]{64})\b", line)
                            if match:
                                result.append(match.group(1))
                                event.set()
                    process.stdout.close()

                drainer = threading.Thread(target=drain, daemon=True)
                drainer.start()
                drain_threads.append(drainer)
                signer.stdin.write(seed.rstrip(b"\r\n"))
                signer.stdin.write(b"\n")
                signer.stdin.close()
            finally:
                seed[:] = b"\0" * len(seed)
            deadline = time.monotonic() + args.timeout
            while not ready.wait(0.1):
                if shutdown.is_set() or signer.poll() is not None or time.monotonic() >= deadline:
                    raise ValueError("Signer failed readiness. Inspect its private log; state is retained.")
            pin(manifest, node, identity[0])
            log_path = directory / "node.log"
            if log_path.exists():
                private_file(log_path)
            log = open(log_path, "a", encoding="utf-8")
            streams.append(log)
            daemon = subprocess.Popen([manifest["dotnet"], manifest["nodeDll"], "--config", node["nodeConfig"], "--daemon=false"],
                                      stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, env=environment(), cwd=directory)
            children.append(daemon)
            while True:
                if shutdown.is_set() or daemon.poll() is not None or signer.poll() is not None or time.monotonic() >= deadline:
                    raise ValueError("Node failed readiness. Inspect its private log; state is retained.")
                if Path(node["nodeCookie"]).exists() and Path(node["nodeIpc"]).exists():
                    try:
                        check = subprocess.run(client_command(manifest, node, ["info"]), stdin=subprocess.DEVNULL,
                                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5, env=environment())
                        if check.returncode == 0:
                            break
                    except subprocess.TimeoutExpired:
                        pass
                shutdown.wait(0.2)
            controls[node["name"]] = (directory, signer)
            write_json(directory / "runtime.json", {"supervisorPid": os.getpid(), "supervisorStart": process_start(os.getpid()),
                       "signerPid": signer.pid, "signerStart": process_start(signer.pid), "nodePid": daemon.pid,
                       "nodeStart": process_start(daemon.pid), "nodePublicKey": identity[0]})
            print(f"DEMO_NODE_READY name={node['name']} peer=127.0.0.1:{node['peerPort']} node={identity[0]} ipc={node['nodeIpc']}", flush=True)
        while not shutdown.wait(0.2):
            for directory, signer in controls.values():
                request_path = directory / "control.json"
                if request_path.exists():
                    request = json.loads(private_file(request_path).read_text())
                    if request.get("supervisorPid") == os.getpid() and request.get("supervisorStart") == process_start(os.getpid()) and request.get("command") == "stop-signer":
                        # A private control request is executed by the actual owning supervisor.
                        signer.terminate()
                        acknowledgement = (directory, request["requestId"])
                        shutdown.set()
                        break
            if shutdown.is_set():
                break
            if any(child.poll() is not None for child in children):
                raise ValueError("A supervised child exited. Stopping this supervisor's remaining children; state is retained.")
    finally:
        # Node first, signer second. Only actual owned child handles are signalled, never manifest PIDs.
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=10)
        for drainer in drain_threads:
            drainer.join(timeout=5)
        for stream in streams:
            stream.close()
        if acknowledgement:
            directory, request_id = acknowledgement
            write_json(directory / "control-response.json", {"requestId": request_id, "stopped": True})
        for lock in locks:
            lock.close()
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    initialize_parser = commands.add_parser("init", help="Create fresh isolated regtest configuration; no keys generated or stored.")
    for flag in ("root", "core-config", "dotnet", "signer-dll", "node-dll", "client-dll"):
        initialize_parser.add_argument("--" + flag, required=True)
    initialize_parser.add_argument("--base-peer-port", type=int, default=19735)
    initialize_parser.add_argument("--legacy-channels", action="store_true", help="Disable anchor negotiation for bounded legacy on-chain acceptance.")
    run_parser = commands.add_parser("run", help="Foreground supervision; SIGINT/SIGTERM stops owned children.")
    run_parser.add_argument("--root", required=True)
    run_parser.add_argument("--node", choices=("a", "b"))
    run_parser.add_argument("--seed-a-file")
    run_parser.add_argument("--seed-b-file")
    run_parser.add_argument("--timeout", type=float, default=180)
    for command in ("status", "pin", "client", "stop-signer"):
        command_parser = commands.add_parser(command)
        command_parser.add_argument("--root", required=True)
        command_parser.add_argument("--node", choices=("a", "b"), required=command != "status")
        if command == "pin":
            command_parser.add_argument("--public-key", required=True)
        if command == "client":
            command_parser.add_argument("arguments", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    try:
        if args.command == "init":
            initialize(args)
        elif args.command == "run":
            if not math.isfinite(args.timeout) or not 1 <= args.timeout <= 600:
                raise ValueError("Readiness timeout must be between 1 and 600 seconds.")
            run(args)
        else:
            manifest = load(args.root)
            nodes = selected(manifest, args.node)
            if args.command == "stop-signer":
                directory = Path(manifest["root"]) / nodes[0]["name"]
                runtime = json.loads(private_file(directory / "runtime.json").read_text())
                if not runtime.get("supervisorStart") or process_start(runtime["supervisorPid"]) != runtime["supervisorStart"]:
                    raise ValueError("No matching live supervisor owns this node.")
                request_id = secrets.token_hex(16)
                write_json(directory / "control.json", {"command": "stop-signer", "requestId": request_id,
                           "supervisorStart": runtime["supervisorStart"], "supervisorPid": runtime["supervisorPid"]})
                deadline = time.monotonic() + 50
                while time.monotonic() < deadline:
                    response_path = directory / "control-response.json"
                    if response_path.exists():
                        response = json.loads(private_file(response_path).read_text())
                        if response.get("requestId") == request_id and response.get("stopped"):
                            print("DEMO_STOPPED name=" + nodes[0]["name"], flush=True)
                            return 0
                    time.sleep(0.2)
                raise ValueError("Supervisor did not acknowledge stop; inspect status without deleting state or endpoints.")
            elif args.command == "pin":
                pin(manifest, nodes[0], args.public_key)
            elif args.command == "client":
                arguments = args.arguments[1:] if args.arguments[:1] == ["--"] else args.arguments
                if not arguments:
                    raise ValueError("Supply a product client command after --.")
                return subprocess.call(client_command(manifest, nodes[0], arguments), env=environment())
            else:
                for node in nodes:
                    path = Path(manifest["root"]) / node["name"] / "runtime.json"
                    runtime = json.loads(private_file(path).read_text()) if path.exists() else {}
                    live = {kind: bool(runtime.get(kind + "Start")) and process_start(runtime[kind + "Pid"]) == runtime[kind + "Start"]
                            for kind in ("supervisor", "signer", "node")}
                    print(json.dumps({"name": node["name"], "processesAlive": live, "runtime": runtime}))
        return 0
    except (ValueError, OSError, json.JSONDecodeError) as error:
        # No subprocess output or secret/config contents are copied into public errors.
        print(f"DEMO_ERROR {type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
