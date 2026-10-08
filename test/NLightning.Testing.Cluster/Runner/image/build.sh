#!/usr/bin/env bash
# Builds the in-cluster test runner image nltg-spike-runner:<tag> (default latest) from the built test assemblies.
# OrbStack's cluster shares the Docker image store, so the Job uses it with imagePullPolicy Never.
#   test/NLightning.Testing.Cluster/Runner/image/build.sh [Release|Debug]
#   NLTG_RUNNER_TAG=dev test/NLightning.Testing.Cluster/Runner/image/build.sh
#   NLTG_RUNNER_PROJECT=integration NLTG_RUNNER_TAG=remote-signer test/NLightning.Testing.Cluster/Runner/image/build.sh
#   NLTG_RUNNER_NO_BUILD=1 stages already verified binaries; caller must rebuild after source changes.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
config="${1:-Release}"
tag="${NLTG_RUNNER_TAG:-latest}"
image="nltg-spike-runner:${tag}"
case "${NLTG_RUNNER_PROJECT:-cluster}" in
  cluster) assembly=NLightning.Testing.Cluster.Tests ;;
  integration) assembly=NLightning.Integration.Tests ;;
  *) echo 'NLTG_RUNNER_PROJECT must be cluster or integration' >&2; exit 2 ;;
esac
project="$repo/test/$assembly"
out="$project/bin/$config/net10.0"

if [[ "${NLTG_RUNNER_NO_BUILD:-0}" != 1 ]]; then
  dotnet build "$project" -c "$config" -f net10.0 -p:MSBuildWarningsAsMessages=MSB4121 -p:NltgTargetNet11=false -nologo -v q
fi
if [[ ! -f "$out/$assembly.dll" ]]; then
  echo "Runner assembly missing: $out/$assembly.dll" >&2
  exit 2
fi

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp -R "$out"/. "$stage"/
# The host's apphost (a macOS or Windows executable) is useless in the Linux image; the Job runs `dotnet <dll>`.
rm -f "$stage/$assembly" "$stage/$assembly.exe"
runner_dockerfile="${NLTG_RUNNER_DOCKERFILE:-$here/Dockerfile}"
if [[ ! -f "$runner_dockerfile" ]]; then
  echo 'NLTG_RUNNER_DOCKERFILE must name an existing Dockerfile' >&2
  exit 2
fi
cp "$runner_dockerfile" "$stage/Dockerfile"
# Optional, already built pinned VLS gateway for actual process interoperability proofs.
# No Rust build or downloads occur inside the runner image.
if [[ -n "${NLTG_VLS_GATEWAY_BINARY:-}" ]]; then
  if [[ ! -f "$NLTG_VLS_GATEWAY_BINARY" || ! -x "$NLTG_VLS_GATEWAY_BINARY" ]]; then
    echo 'NLTG_VLS_GATEWAY_BINARY must name an executable built by tools/vls-gateway/run.sh build' >&2
    exit 2
  fi
  cp -- "$NLTG_VLS_GATEWAY_BINARY" "$stage/nlightning-vls-gateway"
fi

docker build --label "nltg.spike=true" --label "app.kubernetes.io/managed-by=nltg-test-harness" \
  -t "$image" "$stage"
echo "built $image"
