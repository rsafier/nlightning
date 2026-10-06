#!/usr/bin/env bash
# Build verified Go modules on the host; the container stages binaries and performs no networked restore.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
loop_source="${LOOP_SOURCE:?Set LOOP_SOURCE to the Loop PR 1222 checkout}"
loop_commit=3d10930491713c1ee2f9957316747da9a3813072
[[ "$(git -C "$loop_source" rev-parse HEAD)" == "$loop_commit" ]] || { echo "Loop must be pinned to $loop_commit" >&2; exit 1; }
[[ -z "$(git -C "$loop_source" status --porcelain)" ]] || { echo "Loop checkout must be clean" >&2; exit 1; }
tag="${NLTG_LOOP_RUNNER_TAG:-latest}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/loop-bin" "$stage/runner"
(
  cd "$loop_source"
  go mod download
  go mod verify
  CGO_ENABLED=0 go build -mod=readonly -trimpath -p 3 -o "$stage/loop-bin/" ./cmd/loop ./cmd/loopd ./cmd/loopserver-regtest
  go mod download github.com/lightninglabs/aperture@v0.4.0
)
aperture_source="$(go env GOMODCACHE)/github.com/lightninglabs/aperture@v0.4.0"
(
  cd "$aperture_source"
  go mod download
  go mod verify
  CGO_ENABLED=0 go build -mod=readonly -buildvcs=false -trimpath -p 2 -o "$stage/loop-bin/aperture" ./cmd/aperture
)
dotnet build "$repo/test/NLightning.Integration.Tests" -c Release -f net10.0 -m:1 -p:UseSharedCompilation=false -p:MSBuildWarningsAsMessages=MSB4121 -v:minimal
cp -R "$repo/test/NLightning.Integration.Tests/bin/Release/net10.0/." "$stage/runner/"
rm -f "$stage/runner/NLightning.Integration.Tests" "$stage/runner/NLightning.Integration.Tests.exe"
cp "$here/Dockerfile" "$stage/Dockerfile"
docker build --label nltg.spike=true --label app.kubernetes.io/managed-by=nltg-test-harness \
  --label "nltg.loop-commit=$loop_commit" --label nltg.aperture-version=0.4.0 -t "nltg-loop-runner:$tag" "$stage"
echo "built nltg-loop-runner:$tag (Loop $loop_commit, Aperture 0.4.0)"
