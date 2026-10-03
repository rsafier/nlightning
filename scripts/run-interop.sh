#!/usr/bin/env bash
# Runs the Tor interop Docker tests (NL-572: CLN over its onion service; needs Internet, the public Tor network) from the
# host process: the fixture publishes every port it uses on 127.0.0.1 and Tor reaches us at host.docker.internal, so
# no SDK container is needed (NL-276 does not apply). Tor is the only suite left on Docker (owner decision 2026-10-03,
# NL-866): the CLN, Eclair and LDK suites run on the Kubernetes harness only, and for them this script prints the
# run-cluster.sh command and exits 2 (scripts/run-cluster.sh --matrix cln,eclair,eclair2,ldk).
#
# Usage: scripts/run-interop.sh tor [configuration (default Release)] [--build] [extra xunit v3 args...]
# e.g.   scripts/run-interop.sh tor
#        scripts/run-interop.sh tor Release --build
#
# 1. Refuses to start while another run's Tor containers are up (nltg-tor*): one Docker test process at a time on this
#    machine. It never touches other containers (k8s, mutinynet-bitcoind).
# 2. With --build, builds the Tor image (test/Docker/tor) when its tag is missing, and nothing else; the fixture builds
#    a missing image itself too.
# 3. Builds the solution and runs the test assembly with -trait Category=Interop.Tor plus the extra arguments, without
#    NLTG_TEST_BACKEND (the Tor tests skip under NLTG_TEST_BACKEND=cluster).
# INTEROP_FRAMEWORK picks the framework (default net10.0). The run is logged to TestResults/interop/ and a red run's
# failing tests are named in the summary (NL-378).
set -euo pipefail

if [[ $# -lt 1 ]]; then
    sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
fi

peer="$1"
shift
configuration="Release"
if [[ $# -gt 0 && "$1" != -* ]]; then
    configuration="$1"
    shift
fi

build_image=false
extra=()
for arg in "$@"; do
    if [[ "$arg" == "--build" ]]; then
        build_image=true
    else
        extra+=("$arg")
    fi
done

case "$peer" in
    tor) category="Interop.Tor"; image_tag="nltg-tor:alpine3.22"; docker_dir="tor" ;;
    cln|eclair|ldk)
        suites="$peer"
        if [[ "$peer" == eclair ]]; then suites="eclair,eclair2"; fi
        echo "The $peer interop suite runs on the Kubernetes harness only since NL-866 (its Docker backend is retired):" >&2
        echo "  scripts/run-cluster.sh --matrix $suites" >&2
        echo "  scripts/run-cluster.sh -n 1 --suite $peer [--class <test class>]" >&2
        exit 2 ;;
    *) echo "Unknown suite '$peer' (tor; cln, eclair and ldk run on scripts/run-cluster.sh)" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj"
framework="${INTEROP_FRAMEWORK:-net10.0}"

busy="$(docker ps --format '{{.Names}}' | grep -E '^nltg-tor' || true)"
if [[ -n "$busy" ]]; then
    echo "Another Docker test run is using these containers; wait until it ends:" >&2
    echo "$busy" >&2
    exit 1
fi

if [[ "$build_image" == true ]]; then
    if docker image inspect "$image_tag" > /dev/null 2>&1; then
        echo "--build: $image_tag is already built"
    else
        docker build -t "$image_tag" "$repo_root/test/Docker/$docker_dir"
    fi
fi

dotnet build "$repo_root/NLightning.sln" -c "$configuration" -p:MSBuildWarningsAsMessages=MSB4121

results_dir="$repo_root/TestResults/interop"
mkdir -p "$results_dir"
log="$results_dir/interop-$peer-$framework.log"
echo "===== Interop $peer on $framework ($(date -u +%H:%M:%S)), log $log ====="
if ! env -u NLTG_TEST_BACKEND dotnet run --project "$project" -c "$configuration" -f "$framework" --no-build -- \
    -trait "Category=$category" ${extra[@]+"${extra[@]}"} 2>&1 | tee "$log"; then
    echo "===== Interop $peer FAILED (log $log) =====" >&2
    failing_tests="$(grep -a '\[FAIL\]' "$log" | sed -e $'s/\x1b\\[[0-9;]*m//g' -e 's/^[[:space:]]*//' \
                         -e 's/ \[FAIL\].*$//' | sort -u || true)"
    if [ -n "$failing_tests" ]; then
        echo "Failing tests:" >&2
        printf '%s\n' "$failing_tests" >&2
    fi
    exit 1
fi
echo "===== Interop $peer green ====="