#!/usr/bin/env bash
# Builds the in-cluster test runner image nltg-spike-runner:<tag> (default latest) from the built test assemblies.
# OrbStack's cluster shares the Docker image store, so the Job uses it with imagePullPolicy Never.
#   test/NLightning.Testing.Cluster/Runner/image/build.sh [Release|Debug]
#   NLTG_RUNNER_TAG=dev test/NLightning.Testing.Cluster/Runner/image/build.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
config="${1:-Release}"
tag="${NLTG_RUNNER_TAG:-latest}"
image="nltg-spike-runner:${tag}"
project="$repo/test/NLightning.Testing.Cluster.Tests"
out="$project/bin/$config/net10.0"

dotnet build "$project" -c "$config" -f net10.0 -p:MSBuildWarningsAsMessages=MSB4121 -p:NltgTargetNet11=false -nologo -v q

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp -R "$out"/. "$stage"/
# The host's apphost (a macOS or Windows executable) is useless in the Linux image; the Job runs `dotnet <dll>`.
rm -f "$stage/NLightning.Testing.Cluster.Tests" "$stage/NLightning.Testing.Cluster.Tests.exe"
cp "$here/Dockerfile" "$stage/Dockerfile"

docker build --label "nltg.spike=true" --label "app.kubernetes.io/managed-by=nltg-test-harness" \
  -t "$image" "$stage"
echo "built $image"
