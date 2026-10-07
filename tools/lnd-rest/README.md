# NLightning LND REST gateway

`nltg-lnd-rest` serves LND's REST API in front of NLightning's LND-compatible
gRPC listener (`LndGrpc`), so clients that speak only LND's REST, such as
[Ride The Lightning](https://github.com/Ride-The-Lightning/RTL) (RTL), work
against an NLightning node. It is a separate executable next to the daemon,
like LND's own REST proxy is next to its gRPC server.

What it serves:

- **Every REST route LND v0.21.4-beta registers**, from LND's own generated
  grpc-gateway handlers (`*.pb.gw.go` of the pinned `lnd` Go module): `lnrpc`
  Lightning, State and WalletUnlocker, and the sub-servers of LND's release
  build: `routerrpc`, `invoicesrpc`, `walletrpc`, `signrpc`, `chainrpc`
  (ChainNotifier and ChainKit), `verrpc`, `peersrpc`, `autopilotrpc`,
  `wtclientrpc`, `watchtowerrpc` and `neutrinorpc` (172 routes). Paths, HTTP
  methods, query parameters and body bindings are LND's.
- **LND's JSON**, with LND's REST marshal options: proto field names,
  every field emitted, 64-bit integers as strings, `bytes` as base64, enums
  by name. Errors use grpc-gateway's body `{"code", "message", "details"}` and
  its gRPC-to-HTTP status mapping (UNIMPLEMENTED is 501, PERMISSION_DENIED 403,
  UNAUTHENTICATED 401, UNAVAILABLE 503).
- **Server streams** as LND sends them: newline-delimited `{"result": ...}`
  objects over HTTP, and LND's WebSocket API (LND's own proxy code): open a
  WebSocket with `?method=GET|POST|DELETE`, send the request as the first
  message (`{}` for none), then read `{"result": ...}` or `{"error": ...}`
  messages. Client-streaming calls (`/v1/channels/acceptor`,
  `/v2/router/htlcinterceptor`) work over WebSocket as in LND.

A method the node does not implement answers 501 exactly as it does over
gRPC; the gateway adds no logic of its own. The node's coverage is in
`docs/agents/LND_GRPC_PLAN.md`.

## Security model

- **No credential of its own.** The client's `Grpc-Metadata-macaroon`
  header (hex) reaches the node unchanged as the `macaroon` gRPC metadata,
  as in LND; the node verifies it. On a WebSocket the macaroon may also come
  in the `Sec-WebSocket-Protocol` field as `Grpc-Metadata-Macaroon+<hex>`
  (LND's browser workaround). A request without a macaroon is refused by the
  node (401).
- **Verified backend TLS.** The node's `tls.cert` is the only trust anchor
  for the backend connection (`--tls-cert`); `--tls-server-name` sets the
  name checked when the dialed address is not in the certificate.
- **Its own TLS listener**, default `127.0.0.1:8080` (LND's REST port). On
  the first start it writes a self-signed ECDSA P-256 certificate valid for
  14 months (LND's default) to `--tls-dir`: `tls.cert` (0644) and `tls.key`
  (0600) in a 0700 directory, covering `localhost`, the host name,
  `127.0.0.1`, `::1`, a specific listen IP and `--tls-extra-ip` /
  `--tls-extra-domain`. It is reused on later starts and replaced when
  expired. `--rest-tls-cert`/`--rest-tls-key` use a provided certificate
  instead. A private key readable by group or others is refused.
  HTTP/1.1 only, TLS 1.2 or newer.
- **Plain HTTP only on loopback.** `--no-tls` is refused unless `--listen`
  is a loopback address. A non-loopback listener is always TLS, and the
  macaroon is what authorizes each call, as with LND's REST.
- **CORS off** unless `--cors-origin` lists origins (`*` for any), with LND's
  `restcors` behavior.
- **Limits:** request headers 256 KiB read within `--read-header-timeout`
  (10 s), a request body at most `--max-body-bytes` (32 MiB) read within
  `--body-read-timeout` (30 s) before the call starts, gRPC messages at most
  `--max-msg-bytes` (200 MiB, LND's limit), at most `--max-conns` (512) open
  connections, idle keep-alive connections closed after `--idle-timeout`
  (2 min), WebSocket messages at most 4 MiB with pings every
  `--ws-ping-interval` (30 s) and `--ws-pong-wait` (5 s) to answer. Streams
  have no time limit.
- **Graceful shutdown** on SIGINT/SIGTERM: no new connections, running
  calls get `--shutdown-timeout` (10 s), then every stream and WebSocket is
  ended and the backend connection closed.
- `--log-requests` logs method, path, status and duration per request; it
  never logs headers (the macaroon), query strings or bodies.

## Build

Use Go 1.26.8 or newer (LND v0.21.4-beta's minimum; with an older Go 1.21+
the `go` command downloads the toolchain itself). From the repository root:

```bash
cd tools/lnd-rest
go build -trimpath -o nltg-lnd-rest .
```

The `lnd` module is pinned to v0.21.4-beta in `go.mod`, the version of the
node's protobufs, with LND's own `replace` of `google.golang.org/protobuf`
(its hex-display fork, which LND's generated code needs; the REST options do
not use the hex option, so `bytes` stay base64 as in LND). The dedicated
GitHub Actions workflow (`.github/workflows/lnd-rest.yml`) checks formatting
and modules, runs `go vet` and the tests under the race detector, and builds
the executable. Locally:

```bash
go mod verify
go vet ./...
go test -race -count=1 -timeout=5m ./...
```

The tests run the gateway against a fake TLS gRPC backend: every REST route
of LND's service definitions reaches its gRPC method (read from the pinned
module's `*.yaml`), the JSON shapes, query bindings, macaroon pass-through,
NDJSON and WebSocket streams, TLS generation and verification, CORS, limits
and shutdown.

## Run against an NLightning node

The node must have its LND gRPC listener on (`LndGrpc:Enabled`); its data
directory (`<configPath>/lnd-grpc`, or `LndGrpc:DataDirectory`) holds
`tls.cert` and the `admin`, `readonly` and `invoice` macaroons. Signet
example, the node's gRPC on `127.0.0.1:10029` and its data directory
`~/.nltg/signet-loop/lnd-grpc`:

```bash
lnd_grpc_dir="$HOME/.nltg/signet-loop/lnd-grpc"
./nltg-lnd-rest \
  --listen 127.0.0.1:8080 \
  --tls-dir "$HOME/.nltg/signet-loop/lnd-rest" \
  --backend 127.0.0.1:10029 \
  --tls-cert "$lnd_grpc_dir/tls.cert"
```

Use another `--listen` port when 8080 is taken. Check it with the read-only
macaroon:

```bash
mac=$(xxd -p -c 10000 "$lnd_grpc_dir/readonly.macaroon")
curl --cacert "$HOME/.nltg/signet-loop/lnd-rest/tls.cert" \
  -H "Grpc-Metadata-macaroon: $mac" https://127.0.0.1:8080/v1/getinfo
```

A server stream over WebSocket (here with the `websocat` tool; any client
works):

```bash
echo '{}' | websocat -n -H "Grpc-Metadata-Macaroon: $mac" \
  "wss://127.0.0.1:8080/v1/invoices/subscribe?method=GET"
```

`--version` prints the gateway's and the served LND API's versions;
`--help` lists every flag.

## Ride The Lightning

RTL's LND support is REST only: it calls `lnServerUrl` with the
`Grpc-Metadata-macaroon` header, read from `<macaroonPath>/admin.macaroon`.
In `RTL-Config.json`:

```json
{
  "nodes": [
    {
      "index": 1,
      "lnNode": "nltg-signet",
      "lnImplementation": "LND",
      "authentication": {
        "macaroonPath": "/home/me/.nltg/signet-loop/lnd-grpc"
      },
      "settings": {
        "lnServerUrl": "https://127.0.0.1:8080",
        "channelBackupPath": "/home/me/rtl-backup",
        "blockExplorerUrl": "https://mempool.space/signet"
      }
    }
  ]
}
```

(`LN_SERVER_URL` and `MACAROON_PATH` set the same from the environment.)
The node's `lnd-grpc` directory already holds `admin.macaroon`, which gives
RTL every operation (opens, closes, payments, on-chain sends). For a
read-only RTL, point `macaroonPath` at a directory of its own whose
`admin.macaroon` is a copy of (or link to) the node's `readonly.macaroon`:
RTL then shows everything and the node refuses its writes with 403.

**Certificate:** RTL does not verify the LND REST certificate (its LND
requests use `rejectUnauthorized: false`), so the self-signed certificate
works with no setup, and also gives no protection against an interception
between RTL and the gateway. Run RTL on the same machine and keep the
gateway on loopback; across machines, put both on a trusted network or
tunnel. Other clients should trust `--tls-dir/tls.cert` (curl `--cacert`,
Node `NODE_EXTRA_CA_CERTS`, the system store).

What RTL shows against NLightning (checked with RTL 0.15.13 on signet,
2026-10-07): the dashboard, on-chain (receive, send, sweep, UTXOs), channels
(open, pending, closed, active HTLCs), peers, payments, invoices, transaction
lookup, sign/verify message, routing (forwarding history, routing and
non-routing peers), reports, graph lookups and fee rates load. The node does
not implement `GetNetworkInfo` (`/v1/graph/info`, a 501 toast on every page
and three errors on the Network page), the channel backup RPCs
(`/v1/channels/backup`, RTL logs an error at start and its backup action
fails) or `QueryRoutes` (the Query Routes page). Wallet creation and unlock
pages do not apply: the node has no `WalletUnlocker` service.

## Differences from LND's REST listener

None in routes, JSON or streaming. Operationally: one process per node, its
own certificate (LND reuses its gRPC certificate), and the listener refuses
plain HTTP off loopback. As in LND, a WebSocket handshake that offers the
macaroon in `Sec-WebSocket-Protocol` is not answered with a selected
subprotocol, so clients that insist on one (browsers, Node's `ws`) must send
the macaroon in the `Grpc-Metadata-Macaroon` header instead where they can.
