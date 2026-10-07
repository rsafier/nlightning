#!/usr/bin/env bash
# Stage locally verified binaries; the container performs no networked restore.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
tag="${NLTG_LNC_RUNNER_TAG:?Set a unique proof runner tag}"
if docker image inspect "nltg-lnc-runner:$tag" >/dev/null 2>&1; then
  echo "Refusing to overwrite existing proof image tag: $tag" >&2
  exit 1
fi
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/lnc-bin" "$stage/runner"
(
 cd "$repo/tools/lnc"
 go mod download
 go mod verify
 CGO_ENABLED=0 go build -mod=readonly -trimpath -p 2 -o "$stage/lnc-bin/nltg-lnc" .
 CGO_ENABLED=0 go build -mod=readonly -trimpath -p 2 -o "$stage/lnc-bin/lnc-proof" ./proof
)
(
 go mod download github.com/lightninglabs/aperture@v0.4.0
 cd "$(go env GOMODCACHE)/github.com/lightninglabs/aperture@v0.4.0"
 go mod download
 go mod verify
 CGO_ENABLED=0 go build -mod=readonly -buildvcs=false -trimpath -p 2 -o "$stage/lnc-bin/aperture" ./cmd/aperture
)
# Build Integration.Tests first, sequentially with other shared .NET builds.
echo "Staging verified LNC binaries and net10 runner"
cp -R "$repo/test/NLightning.Integration.Tests/bin/Release/net10.0/." "$stage/runner/"
rm -f "$stage/runner/NLightning.Integration.Tests" "$stage/runner/NLightning.Integration.Tests.exe"
cp "$here/Dockerfile" "$stage/Dockerfile"
docker build --label nltg.spike=true --label app.kubernetes.io/managed-by=nltg-test-harness \
  --label nltg.aperture-version=0.4.0 -t "nltg-lnc-runner:$tag" "$stage"
echo "built nltg-lnc-runner:$tag"
