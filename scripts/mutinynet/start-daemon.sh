#!/usr/bin/env bash
# Starts nltg on Mutinynet in the foreground (Ctrl-C stops it) and appends its output to ~/.nltg/<network>/daemon.out.
# Usage: scripts/mutinynet/start-daemon.sh [extra daemon args]
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"
mkdir -p "$NLTG_DIR"
# The template's relative paths (Database "Data Source=nltg.db", Serilog "logs/log-.txt") resolve against the
# working directory, not the configuration directory (NL-306), so run from the configuration directory
cd "$NLTG_DIR"
if [[ ! -f "$NLTG_PASSWORD_FILE" ]]; then
    (umask 077; openssl rand -hex 24 > "$NLTG_PASSWORD_FILE")
fi
"$NLTG_DAEMON_BIN" --network "$NLTG_NETWORK" --password-file "$NLTG_PASSWORD_FILE" "$@" 2>&1 \
    | tee -a "$NLTG_DIR/daemon.out"
