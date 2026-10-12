#!/usr/bin/env bash
# Faucet calls that need no login (faucet.mutinynet.com, IP rate-limited to 60 a day):
#   faucet.sh invoice <sats>        POST /api/bolt11: an invoice of the faucet LND node, for us to pay
#   faucet.sh withdraw <bolt11>     LNURL-withdraw (GET /api/lnurlw, then its callback): the faucet pays our invoice
#                                   (it needs an amount; the call returns after the faucet's payment attempt)
# The on-chain, channel and plain lightning endpoints need a GitHub JWT or an L402 token (see ~/mutinynet/README.md).
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"
case "${1:-}" in
    invoice)
        curl -sf -X POST -H 'content-type: application/json' -d "{\"amount_sats\":${2:?sats}}" \
            "$FAUCET_URL/api/bolt11" | jq -r .bolt11
        ;;
    withdraw)
        bolt11="${2:?bolt11}"
        k1="$(curl -sf "$FAUCET_URL/api/lnurlw" | jq -r .k1)"
        curl -s -m 120 -G "$FAUCET_URL/api/lnurlw/callback" --data-urlencode "k1=$k1" --data-urlencode "pr=$bolt11"
        echo
        ;;
    *)
        echo "usage: $0 invoice <sats> | withdraw <bolt11>" >&2
        exit 2
        ;;
esac
