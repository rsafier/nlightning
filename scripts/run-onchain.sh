#!/usr/bin/env bash
# Retired (NL-820): the Docker LND backend and LNUnit are gone. The BOLT 5 on-chain proofs (Docker.Onchain and
# BackupRestoreFlowTests; the anchors proofs, once ONCHAIN_SUITE=anchors) run on the Kubernetes harness only:
#   scripts/run-cluster.sh --matrix onchain,anchors
#   scripts/run-cluster.sh -n 1 --suite onchain [--class <test class>]   (or --suite anchors)
# See test/NLightning.Integration.Tests/Cluster/CLAUDE.md. Without NLTG_TEST_BACKEND=cluster the tests of the LND
# regtest network are reported skipped. This pointer only says so and exits 2.
set -euo pipefail
sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//' >&2
exit 2
