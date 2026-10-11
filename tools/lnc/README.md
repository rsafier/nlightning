# NLightning Lightning Node Connect bridge

`nltg-lnc` connects an LNC-capable client to NLightning's LND-compatible gRPC
listener through Lightning Labs' encrypted mailbox transport. The bridge and
client both dial outward, so no inbound Internet port is required on the node.
The mailbox relay carries encrypted traffic. The bridge then forwards RPCs over
verified TLS using a dedicated, permission-scoped backend macaroon for each
session.

This executable is separate from the daemon. It does not require
`LndGrpc:EnableSigner`. Of Lightning Terminal's own application services it
implements the litrpc status and Autopilot calls the Terminal web app uses,
including AutoFees autopilot sessions behind a firewall (see "Lightning
Terminal" below); Loop, Pool, Faraday and Taproot Assets are not provided. A client's required
RPCs must exist in NLightning; the bridge preserves backend errors such as
`UNIMPLEMENTED`. Each protobuf message
is limited to 32 MiB in either direction; larger messages fail with
`RESOURCE_EXHAUSTED`. Large graph responses may need backend response limits.

## Build

Use Go 1.25.10 or newer. From the repository root:

```bash
cd tools/lnc
go build -o nltg-lnc .
```

The mailbox dependency is pinned in `go.mod`; `go.sum` records dependency checksums.
The dedicated GitHub Actions workflow verifies formatting and modules, runs
`go vet`, race-detector tests, and builds the executable. To run these checks locally:

```bash
go mod verify
go vet ./...
go test -race -count=1 -timeout=5m ./...
go build -trimpath -o nltg-lnc .
```
NLightning's server uses LND 0.21.4-beta protobufs. The Go adapter forwards protobuf
frames without translating schemas, so its upstream LND Go dependency does not
replace that backend API version.

## Prepare a Signet or Mutinynet node

Use an already running, synchronized NLightning node on your machine. In its
network-specific `appsettings.json`, enable the LND gRPC listener:

```json
{
  "LndGrpc": {
    "Enabled": true,
    "ListenAddress": "127.0.0.1",
    "Port": 10009
  }
}
```

Merge this section into the existing settings and restart the daemon. The first
start creates `tls.cert` and `admin.macaroon` under
`~/.nltg/signet/lnd-grpc` for Signet, or `~/.nltg/mutinynet/lnd-grpc` for Mutinynet.
A configured `LndGrpc:DataDirectory` changes this location. Keep macaroon checks
on. The bridge uses verified backend TLS, so dial a hostname or IP in the node's
certificate; `--tls-server-name` can supply its certificate identity when the
connection address differs.

The commands below run on the node machine, from `tools/lnc`. For Mutinynet change
`lnc_node_dir` to the corresponding directory:

```bash
lnc_node_dir="$HOME/.nltg/signet"
lnc_state_dir="$lnc_node_dir/lnc-state"
```

If the backend requires client certificates through `LndGrpc:ClientCaPath`, this
bridge needs mutual TLS support before it can connect. The current setup above
uses server TLS and macaroons.

## Create and pair a session

Create a read-only session first:

```bash
./nltg-lnc create \
  --state-dir "$lnc_state_dir" \
  --backend 127.0.0.1:10009 \
  --tls-cert "$lnc_node_dir/lnd-grpc/tls.cert" \
  --admin-macaroon "$lnc_node_dir/lnd-grpc/admin.macaroon" \
  --name signet-readonly --profile readonly --ttl 24h
```

The TTL defaults to 24 hours and must be positive and at most 365 days.
The JSON response contains the session ID, pairing phrase, relay, expiry and
allowed RPC methods. Treat the phrase as a credential and enter it only in the
intended LNC client. It is printed at creation; `list` does not reveal it later.
To authorize wallet operations, create a separate session with `--profile wallet`.
That profile can move funds; the returned permission list identifies its exact
RPC surface. No client receives the node's admin macaroon.

> **Warning: `--profile admin` can open and close channels and move funds.**
> It grants everything in `wallet` plus channel and fee management:
> `OpenChannel`, `OpenChannelSync`, `BatchOpenChannel`, `CloseChannel` (force
> closes included), `UpdateChannelPolicy`, `EstimateFee` and walletrpc
> `ListUnspent`/`EstimateFee`. A client holding it can commit the node's
> on-chain funds to channels with any peer and close them. Create it only for a
> client you control, with a short `--ttl`, and revoke it when done.

The admin profile is still a scoped, per-session macaroon like the others,
never the node's admin macaroon. It never grants macaroon administration
(`BakeMacaroon`, `DeleteMacaroonID`, `ListMacaroonIDs`), `signrpc`, the
walletrpc PSBT, lease and signing calls, the channel acceptor or the HTLC
interceptor.

Start the transport process:

```bash
./nltg-lnc serve \
  --state-dir "$lnc_state_dir" \
  --backend 127.0.0.1:10009 \
  --tls-cert "$lnc_node_dir/lnd-grpc/tls.cert"
```

The default relay is `mailbox.terminal.lightning.today:443`. A custom relay can be
selected with `--relay host:port`; for a relay with a private TLS trust anchor,
use `--relay-tls-cert /path/to/relay-ca.pem`. Use the same relay when creating and
serving the sessions and configure the client for it as well.

The bridge negotiates handshake version 2 with the default upstream LNC client.
The client initially offers version 0, accepts the responder's version 2 reply,
and completes version 2 with persistent remote identity binding. No client
handshake-version override is needed.

The first pairing persists the remote client's identity. The pairing phrase
stays valid until the first RPC that presents the session credential over that
identity (`list` shows `"confirmed": true` from then on); that RPC erases the
pairing entropy from the session record. Until then the phrase can pair again
and replaces the unconfirmed identity, so a client whose handshake completed on
the bridge but failed on its own side (and so never kept its keys) is not locked
out. After confirmation, connections and bridge restarts accept only that
identity. A different device needs its own session. The client must preserve its
LNC transport identity to reconnect. Sessions created before this behavior have
no stored phrase once paired and cannot be paired again; create a new one.

## Lightning Terminal

[Lightning Terminal](https://terminal.lightning.engineering) works through the
stock LNC WASM client (lnc-web). Against this bridge it needs, and gets:

- **The session macaroon in the handshake.** Like litd, the bridge sends the
  session's scoped backend macaroon in the Noise handshake auth data as
  `Macaroon: <hex>`. The WASM client refuses any other form ("authdata does not
  contain a macaroon": the client then retries forever while the bridge already
  bound its identity), reads the permissions (`lnc.hasPerms`) and expiry from it,
  and sends it back as per-RPC metadata. The bridge still forwards with its own
  copy of that credential and rejects any other. The node admin macaroon is never
  sent.
- **litrpc answered by the bridge.** After `GetInfo` Terminal refuses a
  node whose macaroon lacks `/litrpc.Autopilot/ListAutopilotSessions` ("Custodial
  accounts are not currently supported"), and it reads `litrpc.Status` to decide
  which pages to offer. New sessions of every profile carry
  `/litrpc.Status/SubServerStatus`, `/litrpc.Autopilot/ListAutopilotSessions`,
  `/litrpc.Autopilot/ListAutopilotFeatures` and `/litrpc.Firewall/ListActions`;
  `wallet` and `admin` sessions also carry `/litrpc.Autopilot/AddAutopilotSession`
  and `/litrpc.Autopilot/RevokeAutopilotSession` (all baked with
  `allow_external_permissions`; the node never serves them). The bridge answers
  them itself and never forwards a litrpc call: SubServerStatus reports `lnd`
  and `lit` running and `loop`, `pool`, `faraday`, `taproot-assets` and
  `accounts` disabled (litd has no separate autopilot or firewall entry); the
  Autopilot calls are described below. Every other call goes to the node.

What works, on the node's LND-compatible API: pairing and reconnect, node info
and balances (`GetInfo`, `ChannelBalance`, `WalletBalance`), channels (open,
pending, closed), peers, payments, invoices, forwarding history, the fee report
(`FeeReport`, each channel's policy and the day/week/month forwarding fees;
NL-1239), on-chain transactions, node lookups and the invoice, transaction,
channel and HTLC subscriptions. Signet is supported by Terminal.

Channel and fee management (NL-1241) needs a `--profile admin` session:
Terminal connects the peer (`ConnectPeer`), opens with `BatchOpenChannel`
(one channel per batch, with `use_base_fee`/`use_fee_rate` and
`sat_per_vbyte`), closes with `CloseChannel` (`force` and `sat_per_vbyte`,
waiting for `close_pending`) and changes fees with `UpdateChannelPolicy`
(`chan_point`, `base_fee_msat`, `fee_rate_ppm`, `time_lock_delta`). The node
funds one channel per transaction, so a Terminal batch of several channels (its
assistant's multi-open) is refused with `UNIMPLEMENTED` before anything is
funded. With a `readonly` or `wallet` session these buttons are shown but the
node refuses the calls.

What does not: Loop, Pool, Faraday and Taproot Assets (reported off; their pages
show nothing or an error; every `taprpc`/`mintrpc` call stays unimplemented),
AutoOpen (below), multi-channel `BatchOpenChannel` batches and
`walletrpc.GetTransaction` (not implemented by the node), and
`QueryRoutes` and lnrpc `ListUnspent` (granted to every profile, not
implemented by the node; Terminal calls `QueryRoutes` only from its assistant).
Terminal shows write buttons for a read-only session too (its macaroon holds
`uri` permissions, which the WASM client does not count as read-only); the node
refuses those calls. Sessions created before this change lack the litrpc
permissions and are refused by Terminal: create a new session.

### Autopilot (NL-1240)

The bridge emulates litd's Autopilot for the AutoFees feature: Terminal's
Autopilot page lists the features, enables AutoFees with its rules, lists and
revokes autopilot sessions and shows AutoFees' fee changes from the firewall's
action log. Research, the security model and every rule are in
[`docs/agents/LNC_AUTOPILOT_PLAN.md`](../../docs/agents/LNC_AUTOPILOT_PLAN.md).

Setup:

1. Once, with the admin macaroon, bake the autopilot macaroon (the four AutoFees
   node methods only: `ListChannels`, `FeeReport`, `ForwardingHistory`,
   `UpdateChannelPolicy`; stored in the state directory as `autopilot.json`):

   ```bash
   ./nltg-lnc autopilot-init \
     --state-dir "$lnc_state_dir" \
     --backend 127.0.0.1:10009 \
     --tls-cert "$lnc_node_dir/lnd-grpc/tls.cert" \
     --admin-macaroon "$lnc_node_dir/lnd-grpc/admin.macaroon"
   ```

   `--rotate` replaces it, deletes the old root key and revokes every autopilot
   session.
2. Start `serve` with `--autopilot-server mainnet` (Lightning Labs'
   `autopilot.lightning.finance:12010`), `testnet`
   (`test.autopilot.lightning.finance:12010`) or a `host:port`
   (`--autopilot-tls-cert` for a server with a private CA). Like litd, there is
   no default for other networks: Lightning Labs runs no signet autopilot
   server. Without the flag the Autopilot page loads with no features, the
   action log still answers, and enabling a feature fails with
   `FailedPrecondition`.
3. Pair Terminal with a new `--profile wallet` (or `admin`) session.

When Terminal enables AutoFees the bridge validates the rules against the
server's limits, registers a session with the autopilot server and serves it on
the mailbox for the server's key. That session never reaches the node directly:
each call must carry litd's meta caveat naming AutoFees, may only be one of the
four methods, passes the rules (rate limit, history limit, channel policy
bounds, channel and peer restrictions; never a global or `create_missing_edge`
update), goes to the node with the session's own scoped macaroon, and returns
through litd's privacy mapper (pseudonymous node keys, channel IDs and channel
points, fuzzed amounts and timestamps, unlisted fields dropped). Every call is
recorded (`actions.json`); pseudonyms are kept per session group
(`privacy-<group>.json`). `revoke --id` on an autopilot session disables it
locally only (the shared autopilot root key stays); serve then tells the server.
`list` shows each session's `type` (`lnc` or `autopilot`).

Not supported: AutoOpen (reported as requiring an upgrade: the node has no
`BatchOpenChannel` and the bridge enforces neither `channel-constraint` nor
`on-chain-budget`), `no_privacy_mapper`, session-wide rules and the dev mailbox.
On signet Terminal's session badge stays "Pending": the browser asks the
autopilot server's REST API for it, and Terminal points at localhost on networks
other than mainnet and testnet.

To see what a client calls, start `serve` with:

- `--log-rpc`: one line per RPC to stderr with the session ID, method, final
  status code and duration, plus one line per Noise handshake (ok, or the failed
  step). Payloads, metadata and credentials are never logged.
- `--log-mailbox`: the mailbox library's debug log (stream setup, retries,
  handshake start and end with the client's public key). It names stream IDs and
  public keys only.

## List, revoke and expire

```bash
./nltg-lnc list --state-dir "$lnc_state_dir"

./nltg-lnc revoke \
  --state-dir "$lnc_state_dir" \
  --backend 127.0.0.1:10009 \
  --tls-cert "$lnc_node_dir/lnd-grpc/tls.cert" \
  --admin-macaroon "$lnc_node_dir/lnd-grpc/admin.macaroon" \
  --id SESSION_ID_FROM_CREATE
```

`list` returns sanitized JSON without the pairing phrase, private transport key
or macaroon. `revoke` first disables the session locally and then deletes its
unique backend macaroon root key. If the backend is unavailable, the command
reports that local access was disabled; run `revoke` again when the backend
returns to finish deleting the root key. Backend connection attempts are bounded
to 20 seconds.

The serving process notices session changes
once per second, closes revoked or expired connections, and picks up newly
created sessions. Expiry is also recorded in the backend macaroon's time caveat.
Expiry does not automatically delete the backend root key. Periodically list
expired sessions and revoke them to remove their root keys and stored secrets.
Only one `serve` process may use a state directory at a time.

Session directories must be private (mode `0700`) and session files mode `0600`.
They contain transport keys and scoped macaroons. Keep the directory available
across restarts; deleting it loses the pairing identity. Use a supervisor to
restart `serve` alongside the node. Administrative credentials are needed for
`create` and `revoke`, not for the long-running transport process.

## Test on your real node

Pair the read-only session and inspect node information and balances. Check that
creating an invoice is denied under that session. Then pair a wallet session,
create a small invoice, pay it from a peer, and send a small payment outward.
Exercise any subscriptions your chosen client uses. Restart the bridge and
reconnect with the same client identity. Finally revoke the session and confirm
that its client cannot resume access.

The real-node regtest proof passed pairing over gRPC and WebSockets, read-only
write denial, invoice and payment streams against LND, restart/reconnect, active
subscription revocation, expiry reconnection denial, and session isolation.
The final Go race-detector suite passed 19 tests. Generic bidirectional forwarding
is covered separately by unit tests; the real-node proof exercised server streams.
Signet and Mutinynet have not been tested for this change.

Full results and reproduction instructions are recorded in
[`docs/agents/LNC_PLAN.md`](../../docs/agents/LNC_PLAN.md). Client compatibility
still depends on the RPCs it uses; stock Lightning Terminal services and
application-specific clients require their own trials; the Lightning Terminal
section above lists what that web app gets.
