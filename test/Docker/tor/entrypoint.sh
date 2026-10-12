#!/bin/sh
# Writes torrc (control port password from TOR_CONTROL_PASSWORD) and keeps tor running: tor is a child of this shell,
# so a test can kill it (`pkill -x tor`, a Tor restart) without stopping the container and the peer that shares its
# network namespace. The data directory, and with it the peer's onion service key, survives the restart.
set -eu
: "${TOR_CONTROL_PASSWORD:?TOR_CONTROL_PASSWORD is required}"
mkdir -p /tor/peer
chmod 700 /tor /tor/peer
hash="$(tor --quiet --hash-password "$TOR_CONTROL_PASSWORD" | tail -n 1)"
cat > /etc/tor/torrc <<TORRC
DataDirectory /tor
# Published on the host's 127.0.0.1 by the fixture; the peer in this network namespace uses 127.0.0.1:9050
SocksPort 0.0.0.0:9050 ExtendedErrors
SocksPolicy accept *
ControlPort 0.0.0.0:9051
HashedControlPassword $hash
CookieAuthentication 0
# The peer's onion service: the peer listens on 127.0.0.1:9735 in this network namespace
HiddenServiceDir /tor/peer
HiddenServicePort 9735 127.0.0.1:9735
Log notice stdout
TORRC
tor --version
while true; do
    tor -f /etc/tor/torrc || true
    echo "tor exited; starting it again in 1 s"
    sleep 1
done
