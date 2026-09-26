#!/usr/bin/env bash
# Shared settings for the Mutinynet smoke scripts. Sourced, not run.
# MUTINYNET_DIR   local Mutinynet bitcoind checkout (default ~/mutinynet; .env holds RPCUSER/RPCPASSWORD)
# NLTG_NETWORK    network name passed to nltg (default mutinynet -> config dir ~/.nltg/mutinynet)
# NLTG_BUILD      build configuration of the daemon/CLI binaries (default Release)
# NLTG_FRAMEWORK  target framework of the binaries (default net10.0)
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MUTINYNET_DIR="${MUTINYNET_DIR:-$HOME/mutinynet}"
NLTG_NETWORK="${NLTG_NETWORK:-mutinynet}"
NLTG_BUILD="${NLTG_BUILD:-Release}"
NLTG_FRAMEWORK="${NLTG_FRAMEWORK:-net10.0}"
NLTG_DIR="$HOME/.nltg/$NLTG_NETWORK"
NLTG_PASSWORD_FILE="${NLTG_PASSWORD_FILE:-$NLTG_DIR/.password}"
# NLTG_DAEMON_BIN/NLTG_CLIENT_BIN may point elsewhere (soak-gossip.sh runs a staged copy of the build)
NLTG_DAEMON_BIN="${NLTG_DAEMON_BIN:-$repo_root/src/NLightning.Daemon/bin/$NLTG_BUILD/$NLTG_FRAMEWORK/NLightning.Daemon}"
NLTG_CLIENT_BIN="${NLTG_CLIENT_BIN:-$repo_root/src/NLightning.Client/bin/$NLTG_BUILD/$NLTG_FRAMEWORK/NLightning.Client}"
FAUCET_URL="${FAUCET_URL:-https://faucet.mutinynet.com}"
FAUCET_NODE="${FAUCET_NODE:-02465ed5be53d04fde66c9418ff14a5f2267723810176c9212b722e542dc1afb1b@45.79.52.207:9735}"
export repo_root MUTINYNET_DIR NLTG_NETWORK NLTG_BUILD NLTG_FRAMEWORK NLTG_DIR NLTG_PASSWORD_FILE NLTG_DAEMON_BIN \
    NLTG_CLIENT_BIN FAUCET_URL FAUCET_NODE
