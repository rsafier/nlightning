# Tor support (NL-569)

NLightning reaches Tor v3 onion services through Tor's SOCKS5 port, can run its own v3 onion service through Tor's
control port, and has a Tor-only mode in which no peer connection, fee request or Esplora request leaves the host
except through Tor. Configuration section `Node:Tor` (`src/NLightning.Domain/Node/Options/TorOptions.cs`), off by
default. The useful parts of `rsafier/TorHiddenServiceHelper` (a .NET 6 hosted service: control port client, `ADD_ONION`
with a persisted key) were rewritten for this, not referenced: that package's control client parses one TCP read as a
whole reply, authenticates with COOKIE or a password only, detaches its services and blocks on `.Result`.

## Modes

| `Node:Tor:Mode` | Outbound `.onion` | Outbound IPv4/IPv6/DNS | Onion service (`OnionServiceEnabled` unset) | BOLT 10 DNS seeds | Fee / Esplora HTTP |
|---|---|---|---|---|---|
| `Off` (default) | refused: "set Node:Tor:Mode" | direct | off | as configured | direct |
| `Hybrid` | through Tor | direct | off (set `OnionServiceEnabled` true to publish one) | as configured | direct |
| `TorOnly` | through Tor | through Tor (exit), host names resolved by Tor | on | skipped (logged once) | through Tor |

`Hybrid` is the "clearnet node that can peer with Tor-only nodes" setting. `TorOnly` is the private node.

## Configuration

```json
"Node": {
  "ListenAddresses": [ "127.0.0.1:9735" ],
  "Tor": {
    "Mode": "TorOnly",
    "SocksProxy": "127.0.0.1:9050",
    "StreamIsolation": true,
    "ConnectTimeout": "00:01:00",
    "Control": "127.0.0.1:9051",
    "ControlPassword": null,
    "ControlCookieFile": null,
    "OnionServiceEnabled": null,
    "OnionServicePort": 9735,
    "OnionServiceTarget": null,
    "OnionServiceKeyFile": "tor_onion_v3.key",
    "AnnounceOnionService": true
  }
}
```

- `SocksProxy` and `Control` take `host:port`, `[ipv6]:port`, a host name (`tor:9050` in Docker) or `unix:/path`
  (`SocksPort unix:/run/tor/socks`, Debian's `ControlSocket /run/tor/control`).
- `StreamIsolation`: every peer connection sends fresh random SOCKS5 credentials, so Tor's `IsolateSOCKSAuth` (on by
  default on every `SocksPort`) puts each peer on its own circuit and no exit sees two of our peers on one circuit.
- `ConnectTimeout` replaces `Node:NetworkTimeout` for connections through Tor (a rendezvous takes seconds); bootstrap
  dials through Tor wait at least this long.
- Control authentication: `ControlPassword` (torrc `HashedControlPassword`) when set; otherwise the cookie Tor names in
  `PROTOCOLINFO` (or `ControlCookieFile`) by SAFECOOKIE, which also checks that the control port belongs to the Tor that
  wrote the cookie, falling back to COOKIE; otherwise NULL when Tor allows it. The node's user must be able to read the
  cookie (Debian: add it to the `debian-tor` group; or `CookieAuthFileGroupReadable 1`).
- `OnionServiceTarget` defaults to the first `ListenAddresses` entry with `0.0.0.0`/`[::]` replaced by loopback.
- `OnionServiceKeyFile` (relative paths resolve against the configuration directory, next to the node key): created with
  mode 0600 on the first start from Tor's `ADD_ONION NEW:ED25519-V3` reply, before the address is used; every later
  start hands it back, so the onion address never changes. **The key is the address: back it up with the node key.** A
  key file that cannot be read stops the onion service (logged) instead of creating a new address.

Minimal `torrc` for C Tor 0.4.8 (the maintained series):

```
SocksPort 127.0.0.1:9050
ControlPort 127.0.0.1:9051
CookieAuthentication 1
```

## Behaviour

- **Outbound** (`Infrastructure/Transport/Services/TcpService` → `Transport/Tor/TorSocksDialer` → `Socks5Client`): RFC 1928
  `CONNECT` with the host as a domain name (ATYP 3) for onions and host names, never resolved locally; RFC 1929
  credentials for isolation. Tor's replies, including the proposal 304 extended onion errors (`0xF0` descriptor not
  found … `0xF7` introduction timed out, sent when the `SocksPort` has `ExtendedErrors`), come back as `Socks5Exception`
  (a `ConnectionException`) with the reason in the message: `connect` prints it.
- **Addresses**: `PeerAddress` holds IPv4, IPv6, Tor v3 `.onion` (version and SHA3-256 checksum checked, Tor v2 refused
  with the reason) and DNS host names, unresolved. `connect <pubkey>@<56 chars>.onion:9735` works from the CLI. An
  outbound onion peer is saved with type `TorV3` and redialed through Tor by the reconnect loop.
- **Onion service** (`Transport/Tor/TorOnionService`): started by the daemon after the peer manager's listener, in the
  background (a Tor that is not up yet only delays it, 5 s to 5 min backoff); added without `Detach`, so it lives as long
  as our control connection: a node that dies takes its service down, and when Tor restarts the service is added again
  with the same key. Stopping the daemon closes the connection first.
- **Inbound** onion connections arrive from Tor on loopback; such peers are saved inbound-only (NL-497) and reconnect to
  us.
- **node_announcement**: our onion address (`AnnounceOnionService`) is added to `Gossip:AnnounceAddresses` through the
  Domain port `IAnnouncedAddressSource`; `NodeAnnouncementService` re-signs as soon as the service comes up (it only
  announces once we have an announced channel). A mistyped `.onion` in `Gossip:AnnounceAddresses` is a configuration
  error (checksum).
- **Graph peers**: the bootstrap's graph top-up (`GraphPeerCandidateSelector`) takes a node's onion service as a fallback
  in `Hybrid` and first in `TorOnly`; the peer manager's "dialable address from the graph" (NL-514) and static channel
  backups (`ConnectableAddresses`) keep Tor v3 addresses too.
- **`info`** shows the Tor mode and `pubkey@<onion>:<port>` once Tor accepted the service.
- **Start-up warnings** (`TorStartupChecks`) in `TorOnly`: a non-loopback listen address, clearnet entries in
  `Gossip:AnnounceAddresses`, or no onion service at all.

## What Tor-only mode does not cover

- bitcoind RPC/ZMQ connections are made as configured (normally local); a remote bitcoind is reached directly.
- BOLT 10 DNS seeds are skipped (SRV lookups cannot go through Tor's SOCKS port): a new Tor-only node needs its first
  peer by `connect`, or a graph from a previous run (NL-571).
- Onion service client authorization (`ClientAuthV3`) and Tor's PoW defenses (`HiddenServicePoWDefensesEnabled`) are not
  configurable through us (NL-573); for those, host the service in `torrc` with `HiddenServiceDir`/`HiddenServicePort`,
  set `OnionServiceEnabled` false and put the address in `Gossip:AnnounceAddresses`.

## Arti

Arti speaks SOCKS5 (and honours isolation by credentials) but not the C Tor control protocol. Point `SocksProxy` at it,
set `OnionServiceEnabled` false, host the onion service in Arti's own configuration and announce it with
`Gossip:AnnounceAddresses`.

## Tests

- Unit and in-process: `test/NLightning.Domain.Tests/{Crypto/Hashes/Sha3Tests,Gossip/OnionV3AddressTests,Node/Options/TorOptionsTests}`,
  `test/NLightning.Infrastructure.Tests/Transport/Tor/` (SOCKS5 client, control client incl. SAFECOOKIE, onion service
  lifecycle incl. Tor restart and a corrupt key, `TcpService` routes per mode, the Tor-only HTTP handler) over the fakes
  `test/NLightning.Tests.Utils/Mocks/{FakeSocks5Proxy,FakeTorControlPort}`, and
  `PeerManagerConnectTests.Given_ATorOnlyNode_When_ItDialsAnOnionPeer_*`: two real peer managers, the BOLT 8 handshake
  and init through the SOCKS5 tunnel.
- Live (`Explicit`): `Transport/Tor/TorLiveTests` against a real Tor, `NLTG_TEST_TOR_CONTROL`, `NLTG_TEST_TOR_SOCKS`
  (and `NLTG_TEST_TOR_NETWORK=1` for a Tor with network access, which then must connect to our own onion end to end):
  `dotnet run --project test/NLightning.Infrastructure.Tests -f net10.0 -- -class NLightning.Infrastructure.Tests.Transport.Tor.TorLiveTests -explicit only`.
  Run on 2026-09-30 against Tor 0.4.8.10 with TCP and Unix control and SOCKS sockets: SAFECOOKIE, `ADD_ONION` with the
  saved key (Tor answers 550 collision to a second registration while ours runs), the same address after a restart, and
  Tor's real SOCKS5 refusal (`0x06`, no circuits in that sandbox). No Docker proof against LND/CLN over Tor yet (NL-572).
