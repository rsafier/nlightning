#!/usr/bin/env bash
# Retired (NL-820): the Docker LND backend and LNUnit are gone. The ABCD multi-hop suite (Docker.Abcd) runs on the
# Kubernetes harness only:
#   scripts/run-cluster.sh --matrix abcd
#   scripts/run-cluster.sh -n 3 --suite abcd [--class <test class>]   (three runs in a row: -n 3 -j 1)
# See test/NLightning.Integration.Tests/Cluster/CLAUDE.md. Without NLTG_TEST_BACKEND=cluster the tests of the LND
# regtest network are reported skipped. This pointer only says so and exits 2.
set -euo pipefail
sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//' >&2
exit 2
