#!/usr/bin/env bash
# Runs the ABCD Docker suite (LND Alice -> NLightning Bob -> NLightning Carol -> LND David,
# test/NLightning.Integration.Tests/Docker/Abcd) several times in a row, each run in a fresh test process and
# therefore on a fresh regtest fixture. Stops at the first red run.
# ABCD roadmap §3: the suite is green when it passes 3 runs in a row.
#
# Usage: scripts/run-abcd.sh [runs (default 3)] [configuration (default Release)] [extra xunit v3 runner args...]
# e.g.   scripts/run-abcd.sh 1 Release -class NLightning.Integration.Tests.Docker.Abcd.AbcdPaymentTests
#
# The host process cannot reach the containers' bridge addresses (NL-276), so the test assembly is built on the host
# and run in an SDK container with --network host (test/CLAUDE.md, as scripts/run-gossip.sh does): on OrbStack that
# reaches the containers and lets LND's host.docker.internal connect back to our node. The container mounts the Docker
# socket (the fixture starts bitcoind and four LND containers) and the repository at the same path, and runs from the
# output directory so the fixture finds test/Docker/custom_lnd.
# Needs a running Docker daemon. Do not run it while another process uses a regtest fixture: all of them use the
# container names miner/alice/bob/carol/david.
# ABCD_FRAMEWORK picks the target framework (default net10.0; mcr.microsoft.com/dotnet/sdk:11.0 for net11.0, whose
# image has no net10.0 runtime). The suite always runs on one framework only: two fixtures would force-remove each
# other's fixed-name containers. ABCD_SDK_IMAGE overrides the runner image. NLTG_TEST_PORT_BASE, when set, is passed
# on (PortPoolUtil's port range).
set -euo pipefail

runs="${1:-3}"
configuration="${2:-Release}"
shift $(( $# > 2 ? 2 : $# ))

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj"
framework="${ABCD_FRAMEWORK:-net10.0}"
output_dir="$repo_root/test/NLightning.Integration.Tests/bin/$configuration/$framework"
results_dir="$repo_root/TestResults/abcd"
namespace="NLightning.Integration.Tests.Docker.Abcd"

case "$framework" in
    net11.0) default_image="mcr.microsoft.com/dotnet/sdk:11.0" ;;
    *) default_image="mcr.microsoft.com/dotnet/sdk:10.0" ;;
esac
image="${ABCD_SDK_IMAGE:-$default_image}"

if ! [[ "$runs" =~ ^[1-9][0-9]*$ ]]; then
    echo "runs must be a positive integer, got '$runs'" >&2
    exit 2
fi

# Without extra arguments the whole suite's namespace runs; with them (e.g. -class/-method) they pick the tests
if [ "$#" -eq 0 ]; then
    set -- -namespace "$namespace"
fi

port_env=()
if [ -n "${NLTG_TEST_PORT_BASE:-}" ]; then
    port_env=(-e "NLTG_TEST_PORT_BASE=$NLTG_TEST_PORT_BASE")
fi

echo "Building $project ($configuration, $framework)"
dotnet build "$project" -c "$configuration" -f "$framework" -p:MSBuildWarningsAsMessages=MSB4121

mkdir -p "$results_dir"
for run in $(seq 1 "$runs"); do
    log="$results_dir/abcd-run-$run-$framework.log"
    echo "===== ABCD run $run/$runs on $framework in $image ($(date -u +%H:%M:%S)), log $log ====="
    if ! docker run --rm --network host ${port_env[@]+"${port_env[@]}"} \
        -v /var/run/docker.sock:/var/run/docker.sock \
        -v "$repo_root:$repo_root" \
        -w "$output_dir" \
        "$image" \
        dotnet "$output_dir/NLightning.Integration.Tests.dll" "$@" -showLiveOutput 2>&1 | tee "$log"; then
        echo "===== ABCD run $run/$runs FAILED; stopping (log $log) =====" >&2
        exit 1
    fi
done

echo "===== ABCD green $runs/$runs runs in a row ====="