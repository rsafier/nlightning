# NLightning Lightning Node Connect bridge

`nltg-lnc` connects an LNC-capable client to NLightning's LND-compatible gRPC
listener through Lightning Labs' encrypted mailbox transport. The bridge and
client both dial outward, so no inbound Internet port is required on the node.
The mailbox relay carries encrypted traffic. The bridge then forwards RPCs over
verified TLS using a dedicated, permission-scoped backend macaroon for each
session.

This executable is separate from the daemon. It does not require
`LndGrpc:EnableSigner`, and it does not implement Lightning Terminal's own
application services. A client's required RPCs must exist in NLightning; the
bridge preserves backend errors such as `UNIMPLEMENTED`. Each protobuf message
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

The first pairing persists the remote client's identity and erases the original
pairing entropy from the session record. Subsequent connections and bridge
restarts accept that identity. A different device needs its own session. The
client must preserve its LNC transport identity to reconnect.

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
application-specific clients require their own trials.
