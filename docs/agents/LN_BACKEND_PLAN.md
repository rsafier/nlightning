# LN_BACKEND_PLAN — the Bark/ASP Lightning backend (NL-1148)

Status: waves A1, A2 and B complete on `wip/nltg-ln-backend` (from `wip/nl995`, which carries the
hold invoices NL-995 this builds on). 2026-10-05.

The node serves the two gRPC contracts Second's captaind (the Bark ASP server) drives a Lightning
node through, so an **unmodified** captaind uses NLightning as its Lightning rail:

- **`hold.Hold`** (`Protos/hold.proto`, vendored from BoltzExchange/hold, MIT) — the receive side:
  `Invoice`/`Inject` a hold invoice by payment hash, `Track`/`TrackAll` its
  `UNPAID → ACCEPTED → PAID | CANCELLED` states, `Settle` with the preimage, `Cancel`.
- **`cln.Node`** (`Protos/cln_node.proto`, minimal) — the pay side and liveness: `getinfo`,
  `xpay`, `listpays`, with CLN's exact field numbers (copied from ElementsProject/lightning).

Both live on one Kestrel listener behind `LnBackend:Enabled` (`LnBackendHost`), with the Cashu
payment processor's TLS story: `LnBackend:TlsDirectory` (server.pem/server.key, client certs
signed by its ca.pem), h2c on loopback only with `AllowInsecureLoopback`, refused on mainnet
without `AllowMainnet`.

## State mapping and semantics

Our `Open → Held → Settled|Canceled` is their `UNPAID → ACCEPTED → PAID | CANCELLED` (a legacy
`Accepted` row — fulfill already persisted — reports `PAID`). Settle arrives **by preimage** and we
derive the hash, exactly captaind's shape. TrackAll streams a snapshot then event-driven state
changes plus a 5 s re-read (cancels raise no event). The backend labels its invoices
`ln-backend`, which is also the List/TrackAll filter.

## The proof (wave B, cluster, lnd suite)

`Docker/Bark/BarkAspFlowTests.cs` + `Nodes/Bark/CaptaindNode.cs` (captaind pod, image
`nltg-captaind:latest`, bark master 2c5f0fcb): captaind runs its own bitcoind (Core 31.1, it
requires ≥ 31) and Postgres beside the fixture; unmodified, configured only with the two URIs of
our backend, it creates a hold invoice through `hold.Invoice`, LND alice pays it over a channel to
us, `TrackAll` reports ACCEPTED, the test writes the preimage to captaind's settlement WAL (the
cross-process seam watchmand uses), and captaind itself settles through us — LND's payment
completes with that preimage. lnd suite 61/61.

## Findings (the honest list)

1. **captaind cannot use `http://` (h2c) Lightning URIs**: tonic 0.14 builds the full TLS client
   config (and parses the client key) eagerly whatever the scheme — their default TOML's `http://`
   example is stale. The mTLS TLS directory is required for captaind deployments.
2. bark's `hold.ListRequest` gained `payment_hashes = 3` (batch pull) — served.
3. `CancelLightningReceive` is disabled server-side in captaind (no cancel proof possible).
4. captaind requires bitcoind ≥ 31; the proof gives it its own chain (the two tips stay ordered).
5. Wave A's tests found four real service bugs before the proof ran: TrackAll's
   `PeriodicTimer` single-waiter fault (the stream died after its first event), proto3
   `optional` expiry read as 0, an amountless Inject reaching the service, and the backend's
   invoices carrying no label (List/TrackAll never saw them).

## Wave C (deferred)

The real bark-wallet claim side (`ClaimLightningReceive`: arkoor package, musig2 nonces, taproot
trees — a bark client container as in Second's `ark_demo.sh`), xpay through captaind (needs user
VTXO collateral), and the upstream conversations: bark's stale `http://` example, and a
first-class NLightning backend module in their tree if the two-proto shim ever diverges.
