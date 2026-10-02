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
| `TorOnly` | through Tor | through Tor (exit), host names resolved by Tor; loopback and private-network IPs direct | on | through Tor (`Bootstrap:TorNameServer`, NL-571) | through Tor |

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
    "AllowUnauthenticatedControlPort": false,
    "OnionServiceEnabled": null,
    "OnionServicePort": 9735,
    "OnionServiceTarget": null,
    "OnionServiceKeyFile": "tor_onion_v3.key",
    "OnionServiceClientAuthKeys": [],
    "OnionServicePoWEnabled": null,
    "OnionServicePoWQueueRate": null,
    "OnionServicePoWQueueBurst": null,
    "AnnounceOnionService": true,
    "AllowClearnetListen": false
  }
},
"FeeEstimation": {
  "Source": "Bitcoind"
}
```

- `SocksProxy` and `Control` take `host:port`, `[ipv6]:port`, a host name (`tor:9050` in Docker) or `unix:/path`
  (`SocksPort unix:/run/tor/socks`, Debian's `ControlSocket /run/tor/control`).
- `StreamIsolation`: every peer connection sends fresh random SOCKS5 credentials, so Tor's `IsolateSOCKSAuth` (on by
  default on every `SocksPort`) puts each peer on its own circuit and no exit sees two of our peers on one circuit.
- `ConnectTimeout` replaces `Node:NetworkTimeout` for connections through Tor (a rendezvous takes seconds); bootstrap
  dials through Tor wait at least this long. Once connected, a connection through Tor (dialed through the SOCKS port, or
  received from loopback while our onion service is on) waits max(`NetworkTimeout`, `ConnectTimeout` / 2), 30 s by
  default, for the BOLT 8 handshake, the init exchange and each pong, instead of `NetworkTimeout` (NL-590).
- Control authentication: `ControlPassword` (torrc `HashedControlPassword`) when set; otherwise the cookie Tor names in
  `PROTOCOLINFO` (or `ControlCookieFile`) by SAFECOOKIE, which also checks that the control port belongs to the Tor that
  wrote the cookie. Plain COOKIE is never used (it hands the file's bytes to whatever listens on the port; a port offering
  only COOKIE is refused), and a port that asks for nothing (NULL) is refused unless `AllowUnauthenticatedControlPort` is
  true, because our onion service key goes to it in `ADD_ONION` (NL-575). A password proves nothing about the other end
  either, so prefer the cookie. The node's user must be able to read the cookie (Debian: add it to the `debian-tor`
  group; or `CookieAuthFileGroupReadable 1`).
- **Prefer a Unix control socket** (`Control: "unix:/run/tor/control"`, torrc `ControlSocket /run/tor/control` with
  `CookieAuthentication 1`): only local users with the socket's permissions can reach it, so no other process can pose as
  Tor on a TCP port that Tor failed to bind.
- `OnionServiceTarget` defaults to the first `ListenAddresses` entry with `0.0.0.0`/`[::]` replaced by loopback; it must
  be `host:port` (a `unix:` target is refused: the node listens on TCP only, NL-585).
- `AllowClearnetListen`: in `TorOnly` a `ListenAddresses` entry that is not loopback (the template's `0.0.0.0:9735`
  included) is a configuration error, since such a listener is reachable without Tor; set `ListenAddresses` to
  `127.0.0.1:9735`, or this to true for a node that is meant to be reachable both ways (NL-577).
- `FeeEstimation:Source`: in `TorOnly` the `Http` source goes through a Tor exit, which fee APIs often block or
  rate-limit, and a failed request leaves the node on `FeeEstimation:FallbackFeeRatePerKw` without a word; the start logs
  a warning. Use `Bitcoind` (`estimatesmartfee` on our own bitcoind) (NL-578).
- `OnionServiceKeyFile` (relative paths resolve against the configuration directory, next to the node key): created with
  mode 0600 on the first start from Tor's `ADD_ONION NEW:ED25519-V3` reply, before the address is used; every later
  start hands it back, so the onion address never changes. **The key is the address: back it up with the node key.** A
  key file that cannot be read stops the onion service (logged) instead of creating a new address.
- `OnionServiceClientAuthKeys` (NL-573): the base32 x25519 public keys (rend-spec-v3 §G.1.2, 52 characters) of the
  clients allowed to reach the service. Empty (the default) leaves the service public; with keys, `ADD_ONION` carries
  `Flags=V3Auth` and one `ClientAuthV3=` per key, so a client without the matching private key cannot even fetch the
  descriptor — a private node, its peers only. Each peer puts its private key in its own Tor by `ONION_CLIENT_AUTH_ADD`
  (control port) or a `ClientOnionAuthDir` in `torrc`. Bad keys are refused at start-up.
- `OnionServicePoWEnabled` and the optional `OnionServicePoWQueueRate`/`OnionServicePoWQueueBurst` tuning (NL-573):
  turn on the service's proof-of-work defenses (rend-spec-v3 §7.3, a first anti-DoS line on the introduction points),
  sent as `PoWDefensesEnabled=`/`PoWQueueRate=`/`PoWQueueBurst=` on `ADD_ONION`, which Tor takes from 0.4.9 (on older
  Tor the service is not added, with an error naming the torrc route). Unset leaves Tor's default (off). The torrc
  equivalent works on every version: `HiddenServicePoWDefensesEnabled <HiddenServiceDir> 1`.

Minimal `torrc` for C Tor 0.4.8 (the maintained series):

```
SocksPort 127.0.0.1:9050
ControlPort 127.0.0.1:9051
CookieAuthentication 1
```

or, better, a control socket (`Node:Tor:Control` `unix:/run/tor/control`):

```
SocksPort 127.0.0.1:9050
ControlSocket /run/tor/control
ControlSocketsGroupWritable 1
CookieAuthentication 1
CookieAuthFileGroupReadable 1
```

## Behaviour

- **Outbound** (`Infrastructure/Transport/Services/TcpService` → `Transport/Tor/TorSocksDialer` → `Socks5Client`): RFC 1928
  `CONNECT` with the host as a domain name (ATYP 3) for onions and host names, never resolved locally; RFC 1929
  credentials for isolation.
- **Loopback and LAN peers in `TorOnly`** are dialed directly (NL-588): an IP literal in 127.0.0.0/8, ::1, 10/8,
  172.16/12, 192.168/16, 169.254/16, fe80::/10 or fc00::/7. Tor refuses such targets anyway
  (`ClientRejectInternalAddresses`), and the connection never leaves the host or the LAN, so it tells no one outside
  about the node. Carrier-grade NAT space (100.64/10) is the provider's network and goes through Tor, and so does every
  host name (write `127.0.0.1`, not `localhost`, for a local peer). This is LND's
  `tor.skip-proxy-for-clearnet-targets` limited to addresses that cannot leave the LAN. Tor's replies, including the proposal 304 extended onion errors (`0xF0` descriptor not
  found … `0xF7` introduction timed out, sent when the `SocksPort` has `ExtendedErrors`), come back as `Socks5Exception`
  (a `ConnectionException`) with the reason in the message: `connect` prints it.
- **Addresses**: `PeerAddress` holds IPv4, IPv6, Tor v3 `.onion` (version and SHA3-256 checksum checked, Tor v2 refused
  with the reason) and DNS host names, unresolved. `connect <pubkey>@<56 chars>.onion:9735` works from the CLI. An
  outbound onion peer is saved with type `TorV3` and redialed through Tor by the reconnect loop.
- **Onion service** (`Transport/Tor/TorOnionService`): started by the daemon after the peer manager's listener, in the
  background (a Tor that is not up yet only delays it, 5 s to 5 min backoff); added without `Detach`, so it lives as long
  as our control connection: a node that dies takes its service down, and when Tor restarts the service is added again
  with the same key, after the backoff delay (never straight away: a port that closes right after every registration is
  not redialed in a tight loop; a connection that held a minute resets the backoff, NL-583). Stopping the daemon closes
  the connection first. The key file is checked at every start: group or other permission bits are logged as a warning
  (chmod 600 it; NL-584). Control replies are logged with every `PrivateKey=`/`ED25519-V3:` value redacted (NL-581).
- **Inbound** onion connections arrive from Tor on loopback. With Tor on, such a peer is saved at the dialable address of
  its `node_announcement` (its onion service first in `TorOnly`) and dialed back by the reconnect loop (NL-579); a peer
  without an announcement in our graph is saved inbound-only (NL-497) and reconnects to us.
- **Startup**: stored peers are dialed in parallel, and the peer manager waits for them at most `Node:NetworkTimeout`
  before the node goes on (chain monitor, HTLC deadline monitor); an onion dial that takes longer continues in the
  background and a failure goes to the reconnect loop (NL-576).
- **node_announcement**: our onion address (`AnnounceOnionService`) is added to `Gossip:AnnounceAddresses` through the
  Domain port `IAnnouncedAddressSource`; `NodeAnnouncementService` re-signs as soon as the service comes up (it only
  announces once we have an announced channel). A mistyped `.onion` in `Gossip:AnnounceAddresses` is a configuration
  error (checksum).
- **Graph peers**: the bootstrap's graph top-up (`GraphPeerCandidateSelector`) takes a node's onion service as a fallback
  in `Hybrid` and first in `TorOnly`; the peer manager's "dialable address from the graph" (NL-514) and static channel
  backups (`ConnectableAddresses`) keep Tor v3 addresses too.
- **`info`** shows the Tor mode and `pubkey@<onion>:<port>` once Tor accepted the service.
- **Start-up warnings** (`TorStartupChecks`) in `TorOnly`: a non-loopback listen address allowed by
  `AllowClearnetListen`, clearnet entries in `Gossip:AnnounceAddresses`, fee estimates over HTTP, or no onion service at
  all. Tor-only mode without the SOCKS dialer registered refuses to build the HTTP handler (never a clearnet fallback,
  NL-580).

## What Tor-only mode does not cover

- bitcoind RPC/ZMQ connections are made as configured (normally local); a remote bitcoind is reached directly.
- BOLT 10 DNS seeds (NL-571): a seed is asked for SRV records, which Tor's own SOCKS resolution (`RESOLVE`, A/AAAA
  only) cannot carry. In `TorOnly` the seeds are therefore asked with a plain DNS-over-TCP query to
  `Node:Bootstrap:TorNameServer` (default `soa.nodes.lightning.directory:53`, LND's `tor.dns`), sent through the SOCKS
  port like any peer connection — the node's seed interest is visible to that resolver and the exit, never to a
  clearnet resolver of the host. Set the option empty to go back to skipping the seeds. Outside `TorOnly` nothing
  changes (the system or configured resolvers, clearnet included in `Hybrid`).
- Dialing someone else's *authorized* onion service is configured in Tor itself: `ONION_CLIENT_AUTH_ADD` on the control
  port, or a `ClientOnionAuthDir` in `torrc`. Only the service side of client authorization is ours
  (`OnionServiceClientAuthKeys`, NL-573).

## Arti

Arti speaks SOCKS5 (and honours isolation by credentials) but not the C Tor control protocol. Point `SocksProxy` at it,
set `OnionServiceEnabled` false, host the onion service in Arti's own configuration and announce it with
`Gossip:AnnounceAddresses`.

## Tests

- Unit and in-process: `test/NLightning.Domain.Tests/{Crypto/Hashes/Sha3Tests,Gossip/OnionV3AddressTests,Node/Options/TorOptionsTests}`,
  `test/NLightning.Infrastructure.Tests/Transport/Tor/` (SOCKS5 client, control client incl. SAFECOOKIE and the refused
  COOKIE-only/NULL ports, redaction, a faulted client and bounded lines, onion service lifecycle incl. Tor restart, the
  closing port's backoff and a corrupt or shared key, the configured client authorization and PoW defenses on the exact
  `ADD_ONION` line (NL-573), `TcpService` routes per mode incl. direct loopback in `TorOnly`, the
  Tor network timeout, the Tor-only HTTP handler, the start-up checks) over the fakes
  `test/NLightning.Tests.Utils/Mocks/{FakeSocks5Proxy,FakeTorControlPort}`,
  `test/NLightning.Infrastructure.Tests/Protocol/Dns/` (the DNS-over-TCP wire codec, and the seed resolver end to end
  through the SOCKS5 proxy, NL-571),
  `PeerManagerConnectTests.Given_ATorOnlyNode_When_ItDialsAnOnionPeer_*`: two real peer managers, the BOLT 8 handshake
  and init through the SOCKS5 tunnel, and `PeerManagerTests.Tor.cs` (startup dials of unreachable peers, an inbound
  onion-service peer saved at its announced onion).
- Live (`Explicit`): `Transport/Tor/TorLiveTests` against a real Tor, `NLTG_TEST_TOR_CONTROL`, `NLTG_TEST_TOR_SOCKS`
  (and `NLTG_TEST_TOR_NETWORK=1` for a Tor with network access, which then must connect to our own onion end to end):
  `dotnet run --project test/NLightning.Infrastructure.Tests -f net10.0 -- -class NLightning.Infrastructure.Tests.Transport.Tor.TorLiveTests -explicit only`.
  Run on 2026-09-30 against Tor 0.4.8.10 with TCP and Unix control and SOCKS sockets: SAFECOOKIE, `ADD_ONION` with the
  saved key (Tor answers 550 collision to a second registration while ours runs), the same address after a restart, and
  Tor's real SOCKS5 refusal (`0x06`, no circuits in that sandbox). The Docker proof against CLN came later (NL-572,
  next item).
- Docker interop with CLN over Tor (NL-572, `test/NLightning.Integration.Tests/Docker/Interop/Tor/ClnTorInteropTests`,
  fixture `Fixtures/TorInteropFixture`, trait `Category=Interop.Tor`; `scripts/run-interop.sh tor`, 3.5-5 min from
  the host; **needs Internet**: the onion services are on the public Tor network). A C Tor client (`nltg-tor`, image
  `nltg-tor:alpine3.22` built from `test/Docker/tor` when missing: Alpine's tor, control port with a hashed password,
  `SocksPort`/`ControlPort` published on the host's `127.0.0.1`) hosts CLN's onion service from `torrc`
  (`HiddenServiceDir`); CLN v26.06.8 (`nltg-tor-cln`) shares the Tor container's network namespace, listens on
  `127.0.0.1:9735` only (so the onion service is the only way in; CLN lists such a peer at `127.0.0.1:<port>`) and
  dials onions through `--proxy=127.0.0.1:9050`. Our node's onion service is registered through the control port with
  the password, its target `host.docker.internal` as resolved in the Tor container, and our listener on `0.0.0.0`
  (`AllowClearnetListen` in `TorOnly`); `NLightningTestNode` starts and stops `ITorOnionService` as the daemon does.
  Proven on 2026-10-02 (Tor 0.4.8 on Alpine 3.22, OrbStack): (1) `Hybrid`, no onion service of ours: we dial CLN's
  onion, fund a channel (500k, 150k pushed) and pay both ways; (2) `TorOnly` with our onion service: the same, then
  Tor is killed (`SIGKILL`, the container's shell starts it again) and our node re-adds its onion service with the
  same key and address, redials CLN's onion through the reconnect backoff, reestablishes and pays both ways again;
  (3) `TorOnly`: CLN dials our onion service (`connect` to `<ours>.onion`), funds a channel to us over that
  connection, and payments work both ways. Not covered: LND (`tor.active`, `tor.v3`; the same fixture shape with
  LND in Tor's network namespace would do), CLN's own `statictor`/`autotor` service (CLN's onion is a `torrc` one
  here), a private Tor network (chutney) for a hermetic run, and an inbound onion peer arriving from loopback (here
  Tor is in a container, so inbound connections come from `host.docker.internal`'s address, not loopback, and the
  NL-579 announced-onion rule is not exercised).
