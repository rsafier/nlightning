#!/usr/bin/env bash
# Runs one peer implementation's interop Docker tests (NL-180) from the host process: the fixtures publish every port
# they use on 127.0.0.1 and the peers dial us at host.docker.internal, so no SDK container is needed (NL-276 does not
# apply).
#
# Usage: scripts/run-interop.sh <eclair|ldk|cln> [configuration (default Release)] [--build] [extra xunit v3 args...]
# e.g.   scripts/run-interop.sh eclair
#        scripts/run-interop.sh eclair Release -explicit on
#        scripts/run-interop.sh ldk Release --build
#
# 1. Refuses to start while another run's containers are up (miner/alice/bob/carol/david, nltg-cln*, nltg-eclair*,
#    nltg-ldk*): one Docker test process at a time on this machine. It never touches other containers (k8s,
#    mutinynet-bitcoind).
# 2. With --build, builds the peer image (test/Docker/eclair, test/Docker/ldk_server) when its tag is missing, and
#    nothing else; the fixtures build a missing image themselves too.
# 3. Builds the solution and runs the test assembly with -trait Category=Interop.<Peer> plus the extra arguments.
# INTEROP_FRAMEWORK picks the framework (default net10.0).
set -euo pipefail

if [[ $# -lt 1 ]]; then
    sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
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
    eclair) category="Interop.Eclair"; image_tag="nltg-eclair:0.14.3"; docker_dir="eclair"; build_args=() ;;
    ldk) category="Interop.Ldk"; image_tag="nltg-ldk-server:dc02b76c"; docker_dir="ldk_server"; build_args=() ;;
    cln) category="Interop.Cln"; image_tag=""; docker_dir=""; build_args=() ;;
    *) echo "Unknown peer '$peer' (eclair, ldk or cln)" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj"
framework="${INTEROP_FRAMEWORK:-net10.0}"

busy="$(docker ps --format '{{.Names}}' | grep -E '^(miner|alice|bob|carol|david)$|^nltg-(cln|eclair|ldk)' || true)"
if [[ -n "$busy" ]]; then
    echo "Another Docker test run is using these containers; wait until it ends:" >&2
    echo "$busy" >&2
    exit 1
fi

if [[ "$build_image" == true ]]; then
    if [[ -z "$image_tag" ]]; then
        echo "--build: $peer uses a pulled image, nothing to build"
    elif docker image inspect "$image_tag" > /dev/null 2>&1; then
        echo "--build: $image_tag is already built"
    else
        docker build -t "$image_tag" ${build_args[@]+"${build_args[@]}"} "$repo_root/test/Docker/$docker_dir"
    fi
fi

dotnet build "$repo_root/NLightning.sln" -c "$configuration" -p:MSBuildWarningsAsMessages=MSB4121
dotnet run --project "$project" -c "$configuration" -f "$framework" --no-build -- \
    -trait "Category=$category" ${extra[@]+"${extra[@]}"}