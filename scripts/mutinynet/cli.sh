#!/usr/bin/env bash
# nltg CLI against the Mutinynet daemon. Usage: scripts/mutinynet/cli.sh info | listchannels | ...
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"
exec "$NLTG_CLIENT_BIN" --network "$NLTG_NETWORK" "$@"
