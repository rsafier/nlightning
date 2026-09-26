#!/usr/bin/env bash
# Runs the BOLT 5 on-chain Docker proofs (docs/agents/BOLT5_ONCHAIN_PLAN.md, Proof conventions:
# test/NLightning.Integration.Tests/Docker/Onchain) several times in a row, each run in a fresh
# `dotnet test` process and therefore on a fresh regtest fixture of its own (these tests close channels and
# reorg the chain, so they never share a fixture with the regtest collection). Stops at the first red run.
#
# Usage: scripts/run-onchain.sh [runs (default 3)] [configuration (default Release)] [extra dotnet test args...]
# Needs a running Docker daemon (the fixture starts bitcoind and four LND containers). Do not run it while
# another process uses the regtest fixture: both use the container names miner/alice/bob/carol/david.
# ONCHAIN_FRAMEWORK picks the target framework (default net10.0); the proofs always run on one framework only
# (with SDK 11 the test projects also target net11.0, and two fixtures would remove each other's containers).
set -euo pipefail

runs="${1:-3}"
configuration="${2:-Release}"
shift $(( $# > 2 ? 2 : $# ))

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj"
filter="FullyQualifiedName~NLightning.Integration.Tests.Docker.Onchain"
results_dir="$repo_root/TestResults/onchain"
framework="${ONCHAIN_FRAMEWORK:-net10.0}"

if ! [[ "$runs" =~ ^[1-9][0-9]*$ ]]; then
    echo "runs must be a positive integer, got '$runs'" >&2
    exit 2
fi

echo "Building $project ($configuration, $framework)"
dotnet build "$project" -c "$configuration" -f "$framework" -p:MSBuildWarningsAsMessages=MSB4121

for run in $(seq 1 "$runs"); do
    echo "===== On-chain run $run/$runs on $framework ($(date -u +%H:%M:%S)) ====="
    if ! dotnet test "$project" --no-build -c "$configuration" -f "$framework" --filter "$filter" \
        --logger "console;verbosity=normal" --logger "trx;LogFileName=onchain-run-$run-$framework.trx" \
        --results-directory "$results_dir" "$@"; then
        echo "===== On-chain run $run/$runs FAILED; stopping (results in $results_dir) =====" >&2
        exit 1
    fi
done

echo "===== On-chain green $runs/$runs runs in a row ====="