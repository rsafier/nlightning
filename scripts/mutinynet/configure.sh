#!/usr/bin/env bash
# Points ~/.nltg/<network>/appsettings.json at the local Mutinynet bitcoind (RPC 127.0.0.1:38332, ZMQ 28332/28333)
# and turns on migrations. The file must exist: start the daemon once first (start-daemon.sh; the first run writes
# the template). Needs jq and openssl. Creates the key password file (mode 600) if it is missing.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"
settings="$NLTG_DIR/appsettings.json"
mkdir -p "$NLTG_DIR"
if [[ ! -f "$NLTG_PASSWORD_FILE" ]]; then
    (umask 077; openssl rand -hex 24 > "$NLTG_PASSWORD_FILE")
    echo "Wrote a new key password to $NLTG_PASSWORD_FILE"
fi
if [[ ! -f "$settings" ]]; then
    echo "$settings does not exist yet; run start-daemon.sh once (it writes the template)" >&2
    exit 1
fi
# shellcheck disable=SC1091
. "$MUTINYNET_DIR/.env"
tmp="$(mktemp)"
jq --arg user "$RPCUSER" --arg pass "$RPCPASSWORD" '
  .Bitcoin.RpcEndpoint = "http://127.0.0.1:38332"
  | .Bitcoin.RpcUser = $user
  | .Bitcoin.RpcPassword = $pass
  | .Bitcoin.ZmqHost = "127.0.0.1"
  | .Bitcoin.ZmqBlockPort = 28332
  | .Bitcoin.ZmqTxPort = 28333
  | .Database.RunMigrations = true' "$settings" > "$tmp"
(umask 077; cat "$tmp" > "$settings")
rm -f "$tmp"
chmod 600 "$settings"
echo "Configured $settings"
