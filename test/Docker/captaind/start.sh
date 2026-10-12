#!/bin/sh
# captaind create on an empty data directory (the config's data_dir), then exec captaind start so SIGTERM reaches it.
set -e
export RUST_BACKTRACE=full
CONFIG_PATH="${CAPTAIND_CONFIG_PATH:?CAPTAIND_CONFIG_PATH must name captaind's configuration}"
DATA_DIR=$(sed -n 's/^data_dir *= *"\(.*\)"/\1/p' "$CONFIG_PATH" | head -n 1)
if [ ! -f "${DATA_DIR}/mnemonic" ]; then
  /usr/local/bin/captaind --config "$CONFIG_PATH" create
fi
exec /usr/local/bin/captaind --config "$CONFIG_PATH" start
