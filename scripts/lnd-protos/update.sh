#!/usr/bin/env bash
# Fetches LND's gRPC .proto files at one exact tag into test/NLightning.Testing.Lnd/Protos/ (the source of the
# in-tree LND gRPC client's Grpc.Tools codegen; see test/NLightning.Testing.Lnd/README.md).
#
#   scripts/lnd-protos/update.sh [lnd tag (default v0.21.4-beta)]
#
# The file set is LNUnit.LND's LND set (nbd-wtf/LNUnit, update-protofiles.sh) plus chainrpc/chainkit.proto: every
# service proto under lnrpc/ at the tag except lnclipb/lncli.proto (lncli's output types, no service). Loop's protos
# (LNUnit's LoopConnection) are not fetched: they come from another repository and nothing here uses Loop.
#
# Each file is written byte for byte as published at the tag, with one change: the line
#   option csharp_namespace = "NLightning.Testing.Lnd.<Package>";
# right after its `package <name>;` line, so the generated types live in NLightning.Testing.Lnd.Lnrpc,
# NLightning.Testing.Lnd.Routerrpc, ... and never collide with lnunit.lnd's global Lnrpc/Routerrpc/... namespaces in
# the same assembly. Protos/manifest.txt records the tag, the commit it points to and, per file, the sha256 of the
# upstream file and of the committed one; NLightning.Testing.Lnd.Tests checks both against the committed files.
# Every file is downloaded by the commit the tag peels to (never by the tag name, which could move or be shadowed by a
# branch of the same name), so the recorded commit is exactly what was downloaded.
#
# LND's protos are MIT ("Copyright (C) 2015-2022 Lightning Labs and The Lightning Network Developers") and carry no
# license header of their own, so the script also writes LND's root LICENSE at the same commit to
# Protos/LICENSE-LND.txt (manifest line `license`): the notice ships with the protos and the code generated from them.
#
# Needs curl, git, awk and sha256sum or shasum. Run it from anywhere; it writes only under Protos/.
set -euo pipefail

TAG="${1:-v0.21.4-beta}"
NAMESPACE_PREFIX="NLightning.Testing.Lnd"
REPO="lightningnetwork/lnd"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/test/NLightning.Testing.Lnd/Protos"

FILES=(
  lightning.proto
  stateservice.proto
  walletunlocker.proto
  autopilotrpc/autopilot.proto
  chainrpc/chainkit.proto
  chainrpc/chainnotifier.proto
  devrpc/dev.proto
  invoicesrpc/invoices.proto
  neutrinorpc/neutrino.proto
  peersrpc/peers.proto
  routerrpc/router.proto
  signrpc/signer.proto
  verrpc/verrpc.proto
  walletrpc/walletkit.proto
  watchtowerrpc/watchtower.proto
  wtclientrpc/wtclient.proto
)

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# The commit the tag points to: the peeled ^{} entry of an annotated tag, else the tag's own entry.
COMMIT="$(git ls-remote "https://github.com/$REPO" "refs/tags/$TAG^{}" "refs/tags/$TAG" 2>/dev/null \
  | awk '$2 ~ /\^\{\}$/ { peeled = $1 } $2 !~ /\^\{\}$/ { plain = $1 } END { print (peeled != "" ? peeled : plain) }')"
if [[ -z "$COMMIT" ]]; then
  echo "error: tag $TAG not found in github.com/$REPO" >&2
  exit 1
fi

MANIFEST="$STAGE/manifest.txt"
{
  echo "# Written by scripts/lnd-protos/update.sh; do not edit by hand."
  echo "# file <path> <sha256 of the upstream file> <sha256 of the committed file (upstream + the csharp_namespace line)>"
  echo "# license <committed file> <upstream path at lnd_commit> <sha256 (committed byte for byte)>"
  echo "lnd_repo github.com/$REPO"
  echo "lnd_tag $TAG"
  echo "lnd_commit $COMMIT"
  echo "csharp_namespace_prefix $NAMESPACE_PREFIX"
} >"$MANIFEST"

for file in "${FILES[@]}"; do
  upstream="$STAGE/upstream/$file"
  target="$STAGE/protos/$file"
  mkdir -p "$(dirname "$upstream")" "$(dirname "$target")"
  url="https://raw.githubusercontent.com/$REPO/$COMMIT/lnrpc/$file"
  if ! curl -fsSL --retry 3 -o "$upstream" "$url"; then
    echo "error: could not fetch $url" >&2
    exit 1
  fi

  if grep -q 'csharp_namespace' "$upstream"; then
    echo "error: $file already sets csharp_namespace; update this script before regenerating" >&2
    exit 1
  fi
  packages="$(grep -cE '^package [A-Za-z0-9_]+;$' "$upstream" || true)"
  if [[ "$packages" != "1" ]]; then
    echo "error: $file has $packages 'package' lines, expected exactly 1" >&2
    exit 1
  fi
  package="$(grep -E '^package [A-Za-z0-9_]+;$' "$upstream" | sed -E 's/^package ([A-Za-z0-9_]+);$/\1/')"
  namespace="$NAMESPACE_PREFIX.$(printf '%s' "${package:0:1}" | tr '[:lower:]' '[:upper:]')${package:1}"

  # head/tail keep every other byte as published (awk would add a final newline a file lacks).
  line_no="$(grep -nE '^package [A-Za-z0-9_]+;$' "$upstream" | cut -d: -f1)"
  {
    head -n "$line_no" "$upstream"
    printf 'option csharp_namespace = "%s";\n' "$namespace"
    tail -n "+$((line_no + 1))" "$upstream"
  } >"$target"

  echo "file $file $(sha256 "$upstream") $(sha256 "$target")" >>"$MANIFEST"
  echo "fetched $file ($package -> $namespace)"
done

# LND's MIT notice (lnrpc/ has no LICENSE of its own; the repository root's covers it).
license_url="https://raw.githubusercontent.com/$REPO/$COMMIT/LICENSE"
if ! curl -fsSL --retry 3 -o "$STAGE/LICENSE-LND.txt" "$license_url"; then
  echo "error: could not fetch $license_url" >&2
  exit 1
fi
if ! grep -q 'Lightning Labs' "$STAGE/LICENSE-LND.txt"; then
  echo "error: $license_url does not name Lightning Labs; check LND's license before regenerating" >&2
  exit 1
fi
echo "license LICENSE-LND.txt LICENSE $(sha256 "$STAGE/LICENSE-LND.txt")" >>"$MANIFEST"

# Every import must be another file of the set or a well-known Google type, or the codegen would fail later.
for file in "${FILES[@]}"; do
  while IFS= read -r import; do
    [[ -z "$import" || "$import" == google/protobuf/* ]] && continue
    if [[ ! -f "$STAGE/protos/$import" ]]; then
      echo "error: $file imports $import, which is not in the fetched set; add it to FILES" >&2
      exit 1
    fi
  done < <(sed -nE 's/^import (public )?"([^"]+)";.*$/\2/p' "$STAGE/protos/$file")
done

find "$OUT" -name '*.proto' -delete 2>/dev/null || true
mkdir -p "$OUT"
cp -R "$STAGE/protos/." "$OUT/"
cp "$MANIFEST" "$OUT/manifest.txt"
cp "$STAGE/LICENSE-LND.txt" "$OUT/LICENSE-LND.txt"
find "$OUT" -type d -empty -delete
echo "LND $TAG ($COMMIT): ${#FILES[@]} protos written to ${OUT#"$ROOT"/}"
