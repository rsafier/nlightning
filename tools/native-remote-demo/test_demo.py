"""Lightweight launcher boundary checks; no .NET processes or Bitcoin topology."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("demo.py")
SPEC = importlib.util.spec_from_file_location("native_remote_demo", SCRIPT)
DEMO = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DEMO)


class LauncherBoundaries(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="native-p1-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.root = self.directory / "demo"
        self.binary = self.directory / "unused-binary"
        self.binary.write_text("not executed")
        self.core = self.directory / "core.json"
        self.core.write_text(json.dumps({"RpcEndpoint": "http://127.0.0.1:18443", "RpcUser": "user", "RpcPassword": "private-test-password"}))
        self.core.chmod(0o600)

    def invoke(self, *arguments):
        return subprocess.run([sys.executable, str(SCRIPT), *map(str, arguments)], capture_output=True, text=True, timeout=5)

    def initialize(self, *extra):
        result = self.invoke("init", "--root", self.root, "--core-config", self.core,
                             "--dotnet", self.binary, "--signer-dll", self.binary,
                             "--node-dll", self.binary, "--client-dll", self.binary, *extra)
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads((self.root / "demo.json").read_text())

    def history(self, node):
        for suffix in DEMO.HISTORY_SUFFIXES:
            path = Path(node["signerState"] + suffix)
            path.write_text("retained safety history")
            path.chmod(0o600)

    def test_initialization_isolated_private_and_secret_free_manifest(self):
        manifest = self.initialize()
        a, b = manifest["nodes"]
        for key in ("nodeId", "ownerId", "signerId", "peerPort", "signerState", "signerToken", "nodeDatabase", "nodeIpc", "nodeCookie"):
            self.assertNotEqual(a[key], b[key])
        self.assertNotEqual(Path(a["signerToken"]).read_text(), Path(b["signerToken"]).read_text())
        self.assertNotIn("private-test-password", (self.root / "demo.json").read_text())
        self.assertNotIn("seed", (self.root / "demo.json").read_text().lower())
        for path in self.root.rglob("*"):
            self.assertEqual(0, path.stat().st_mode & 0o077)

    def test_legacy_acceptance_pins_effective_node_feature_section(self):
        manifest = self.initialize("--legacy-channels")
        for node in manifest["nodes"]:
            config = json.loads(Path(node["nodeConfig"]).read_text())
            self.assertEqual("No", config["Node"]["Features"]["OptionAnchors"])

    def test_existing_root_rejected_without_overwrite(self):
        self.initialize()
        before = (self.root / "demo.json").read_bytes()
        with self.assertRaises(FileExistsError):
            DEMO.initialize(type("Args", (), {"root": str(self.root), "core_config": str(self.core),
                            "dotnet": str(self.binary), "signer_dll": str(self.binary), "node_dll": str(self.binary),
                            "client_dll": str(self.binary), "base_peer_port": 19735, "legacy_channels": False})())
        self.assertEqual(before, (self.root / "demo.json").read_bytes())

    def test_public_core_credentials_rejected_without_secret_output(self):
        self.core.chmod(0o644)
        result = self.invoke("init", "--root", self.root, "--core-config", self.core,
                             "--dotnet", self.binary, "--signer-dll", self.binary,
                             "--node-dll", self.binary, "--client-dll", self.binary)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.root.exists())
        self.assertNotIn("private-test-password", result.stdout + result.stderr)

    def test_missing_history_blocks_before_any_product_process(self):
        node = self.initialize()["nodes"][0]
        Path(node["signerState"] + ".key-index").write_text("allocated")
        result = self.invoke("run", "--root", self.root, "--node", "a")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Incomplete signer history", result.stderr)
        self.assertFalse((self.root / "a" / "signer.log").exists())

    def test_pin_cannot_replace_identity_or_admit_seed_only_restore(self):
        node = self.initialize()["nodes"][0]
        self.history(node)
        public_key = "02" + "11" * 32
        self.assertEqual(0, self.invoke("pin", "--root", self.root, "--node", "a", "--public-key", public_key).returncode)
        before = Path(node["nodeConfig"]).read_bytes()
        changed = self.invoke("pin", "--root", self.root, "--node", "a", "--public-key", "03" + "22" * 32)
        self.assertNotEqual(0, changed.returncode)
        self.assertEqual(before, Path(node["nodeConfig"]).read_bytes())
        for suffix in DEMO.HISTORY_SUFFIXES:
            Path(node["signerState"] + suffix).unlink()
        failed = self.invoke("run", "--root", self.root, "--node", "a")
        self.assertIn("Incomplete signer history", failed.stderr)

    def test_same_public_identity_rejected_without_changing_either_pin(self):
        manifest = self.initialize()
        for node in manifest["nodes"]:
            self.history(node)
        public_key = "02" + "11" * 32
        self.assertEqual(0, self.invoke("pin", "--root", self.root, "--node", "a", "--public-key", public_key).returncode)
        before = [Path(node["nodeConfig"]).read_bytes() for node in manifest["nodes"]]
        rejected = self.invoke("pin", "--root", self.root, "--node", "b", "--public-key", public_key)
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("distinct public signing identities", rejected.stderr)
        self.assertEqual(before, [Path(node["nodeConfig"]).read_bytes() for node in manifest["nodes"]])

    def test_concurrent_same_identity_pins_admit_only_one_node(self):
        manifest = self.initialize()
        for node in manifest["nodes"]:
            self.history(node)
        public_key = "03" + "22" * 32
        processes = [subprocess.Popen([sys.executable, str(SCRIPT), "pin", "--root", str(self.root),
                     "--node", name, "--public-key", public_key], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                     for name in ("a", "b")]
        for process in processes:
            process.communicate(timeout=5)
        self.assertEqual([0, 1], sorted(process.returncode for process in processes))
        pinned = [json.loads(Path(node["nodeConfig"]).read_text())["Signing"].get("ExpectedNodePublicKey")
                  for node in manifest["nodes"]]
        self.assertEqual(1, pinned.count(public_key))

    def test_nonfinite_or_nonpositive_timeout_rejected_before_state_access(self):
        for value in ("nan", "inf", "-inf", "0", "-1"):
            with self.subTest(value=value):
                result = self.invoke("run", "--root", self.root, "--node", "a", "--timeout=" + value)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Readiness timeout", result.stderr)
                self.assertFalse(self.root.exists())

    def test_existing_endpoint_is_not_unlinked(self):
        node = self.initialize()["nodes"][0]
        endpoint = Path(node["signerSocket"])
        endpoint.write_text("must not delete")
        result = self.invoke("run", "--root", self.root, "--node", "a")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("must not delete", endpoint.read_text())

    def test_stale_supervisor_pid_does_not_signal_or_request_control(self):
        self.initialize()
        runtime = self.root / "a" / "runtime.json"
        runtime.write_text(json.dumps({"supervisorPid": os.getpid(), "supervisorStart": "stale-start"}))
        runtime.chmod(0o600)
        result = self.invoke("stop-signer", "--root", self.root, "--node", "a")
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.root / "a" / "control.json").exists())

    def test_authority_profile_cannot_be_downgraded(self):
        node = self.initialize()["nodes"][0]
        Path(node["signerState"] + ".authority-profile").write_text("installed")
        result = self.invoke("run", "--root", self.root, "--node", "a")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("authority-profile", result.stderr)
        self.assertFalse((self.root / "a" / "signer.log").exists())


if __name__ == "__main__":
    unittest.main()
