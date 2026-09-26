#!/usr/bin/env bash
# Runs the ABCD Docker suite (LND Alice -> NLightning Bob -> NLightning Carol -> LND David,
# test/NLightning.Integration.Tests/Docker/Abcd) several times in a row, each run in a fresh
# `dotnet test` process and therefore on a fresh regtest fixture. Stops at the first red run.
# ABCD roadmap §3: the suite is green when it passes 3 runs in a row.
#
# Usage: scripts/run-abcd.sh [runs (default 3)] [configuration (default Release)] [extra dotnet test args...]
# Needs a running Docker daemon (the fixture starts bitcoind and four LND containers).
# ABCD_FRAMEWORK picks the target framework (default net10.0). The suite always runs on one framework only:
# with SDK 11 the test projects also target net11.0, and two frameworks run at once would start two fixtures
# that force-remove each other's fixed-name containers (miner/alice/bob/carol/david).
set -euo pipefail

runs="${1:-3}"
configuration="${2:-Release}"
shift $(( $# > 2 ? 2 : $# ))

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj"
filter="FullyQualifiedName~NLightning.Integration.Tests.Docker.Abcd"
results_dir="$repo_root/TestResults/abcd"
framework="${ABCD_FRAMEWORK:-net10.0}"

if ! [[ "$runs" =~ ^[1-9][0-9]*$ ]]; then
    echo "runs must be a positive integer, got '$runs'" >&2
    exit 2
fi

echo "Building $project ($configuration, $framework)"
dotnet build "$project" -c "$configuration" -f "$framework" -p:MSBuildWarningsAsMessages=MSB4121

for run in $(seq 1 "$runs"); do
    echo "===== ABCD run $run/$runs on $framework ($(date -u +%H:%M:%S)) ====="
    if ! dotnet test "$project" --no-build -c "$configuration" -f "$framework" --filter "$filter" \
        --logger "console;verbosity=normal" --logger "trx;LogFileName=abcd-run-$run-$framework.trx" \
        --results-directory "$results_dir" "$@"; then
        echo "===== ABCD run $run/$runs FAILED; stopping (results in $results_dir) =====" >&2
        exit 1
    fi
done

echo "===== ABCD green $runs/$runs runs in a row ====="