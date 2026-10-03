#!/usr/bin/env bash
# Retired (NL-820): the Docker LND backend and LNUnit are gone. The gossip-regtest collection runs on the Kubernetes
# harness only, in two suites: gossip (the BOLT 7 proofs, Docker.Gossip) and day0 (the day-0 flows, the LND splice
# observer and the public channel policy):
#   scripts/run-cluster.sh --matrix gossip,day0
#   scripts/run-cluster.sh -n 1 --suite gossip [--class <test class>]   (or --suite day0)
# See test/NLightning.Integration.Tests/Cluster/CLAUDE.md. Without NLTG_TEST_BACKEND=cluster the tests of the LND
# regtest network are reported skipped. This pointer only says so and exits 2.
set -euo pipefail
sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
exit 2
