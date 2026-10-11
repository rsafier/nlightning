#!/usr/bin/env bash
# Stage the current net10 Integration.Tests output into a new, uniquely tagged harness runner image.
# Build the project first. Kind users must load the resulting image into their cluster before running the wrapper.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../../.." && pwd)"
config="${1:-Release}"
tag="${NLTG_RUNNER_TAG:-lnd-subscriptions-$(date -u +%Y%m%d%H%M%S)}"
[[ "$tag" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$ ]] || { echo 'Invalid NLTG_RUNNER_TAG' >&2; exit 2; }
image="nltg-spike-runner:$tag"
if docker image inspect "$image" >/dev/null 2>&1; then
    echo "Refusing to replace existing image $image; choose a new NLTG_RUNNER_TAG" >&2
    exit 2
fi
output="$repo_root/test/NLightning.Integration.Tests/bin/$config/net10.0"
[[ -f "$output/NLightning.Integration.Tests.dll" ]] || { echo "Build output missing: $output" >&2; exit 2; }
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/runner"
cp -a "$output/." "$stage/runner/"
rm -f "$stage/runner/NLightning.Integration.Tests" "$stage/runner/NLightning.Integration.Tests.exe"
cp "$repo_root/test/NLightning.Integration.Tests/Cluster/LndSubscriptions/image/Dockerfile" "$stage/Dockerfile"
docker build --tag "$image" "$stage"
echo "Runner image: $image"
