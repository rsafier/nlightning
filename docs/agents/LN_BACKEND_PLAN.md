# LN_BACKEND_PLAN — the Bark/ASP Lightning backend (NL-1148)

Status: waves A1, A2 and B complete on `wip/nltg-ln-backend` (from `wip/nl995`, which carries the
hold invoices NL-995 this builds on). 2026-10-05. Wave C complete on `wip/lnbackend-wavec` (2026-10-06):
a real bark wallet pays (BOLT 11, in flight past the retry window, BOLT 12) and receives through captaind
on us — see "Wave C record".

The node serves the two gRPC contracts Second's captaind (the Bark ASP server) drives a Lightning
node through, so an **unmodified** captaind uses NLightning as its Lightning rail:

- **`hold.Hold`** (`Protos/hold.proto`, vendored from BoltzExchange/hold, MIT) — the receive side:
  `Invoice`/`Inject` a hold invoice by payment hash, `Track`/`TrackAll` its
  `UNPAID → ACCEPTED → PAID | CANCELLED` states, `Settle` with the preimage, `Cancel`.
- **`cln.Node`** (`Protos/cln_node.proto`, minimal) — the pay side and liveness: `Getinfo`,
  `Xpay`, `ListPays` and (wave C) `FetchInvoice`, with CLN's exact method names and field numbers
  (copied from ElementsProject/lightning; diffed against bark's `cln-rpc/protos/node.proto`).

Both live on one Kestrel listener behind `LnBackend:Enabled` (`LnBackendHost`), with the Cashu
payment processor's TLS story: `LnBackend:TlsDirectory` (server.pem/server.key, client certs
signed by its ca.pem), h2c on loopback only with `AllowInsecureLoopback`, refused on mainnet
without `AllowMainnet`.

## Configuring the Xpay retry window (NL-1153)

`LnBackend:MaxXpayRetryFor` is the operator's maximum `cln.Node.Xpay` retry window in seconds.
Its default is 300; values outside 1 through 3,600 are rejected when the backend is enabled.
To allow captaind to request more than its default five minutes, merge this setting into the
existing `LnBackend` section of the node's `appsettings.json`, then restart the daemon:

```json
{
  "LnBackend": {
    "MaxXpayRetryFor": 900
  }
}
```

This fragment sets only the retry cap. The existing `Enabled`, network opt-in, and mutual TLS
settings still configure the listener. With a cap of 900, a request whose `retry_for` is 600 gets
a 600-second `PayInvoiceOptions.Timeout`; 5,000 is capped at 900. A missing or zero `retry_for`
uses 60 seconds, or the configured cap if it is lower. Match captaind's `cln_xpay_max_retry_for`
to the node's cap when the ASP should use its full requested window.

The limit stops new attempts; HTLCs already in flight resolve through the existing payment and
`ListPays` reconciliation path. It does not turn a pending payment into a failed payment when
the window ends, or cancel it when its gRPC caller disconnects. Validation on 2026-10-07:
backend tests 114/114 passed, including configuration mapping, cap/default/boundary cases
and a real HTTP/2 host forwarding a 600-second request under a configured 900-second cap.
Release net10.0/net11.0 solution builds had zero warnings/errors.

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

## Wave C record (2026-10-06, `wip/lnbackend-wavec`)

**The proof** — `Docker/Bark/BarkWalletFlowTests` (cluster, lnd suite; one fact, about 2.2 min):
a real bark wallet — the `bark` CLI of bark 2c5f0fcb, built into `nltg-captaind` beside captaind
(`test/Docker/captaind/Dockerfile`, its own layer; `Nodes/Bark/BarkWalletNode`, an exec-driven pod on
captaind's chain) — against an unmodified captaind whose `[[cln_array]]` is our LN backend:

1. **Board**: on-chain funds from captaind's chain, `bark board`, spendable after
   `required_board_confirmations`.
2. **Pay (BOLT 11)**: `bark lightning pay invoice <LND invoice> --wait` → captaind's xpay on us → our
   node pays alice over a channel → the wallet's send is `paid` with LND's preimage; our payment is
   `Succeeded` (label `ln-backend`) and our `ListPays` COMPLETE with the preimage.
3. **Pay, in flight past the retry window**: alice holds the HTLC (hold invoice) and the wallet asks
   `--retry-for 3`; our xpay answers DEADLINE_EXCEEDED after 3 s; 33 s later (several reconciliation
   rounds at test cadence) our payment is still InFlight, `ListPays` PENDING and the wallet's send
   `payment-initiated` (never revocable); alice settles and the wallet's send completes with her
   preimage.
4. **Pay a BOLT 12 offer** of a second NLightning node: captaind's `FetchInvoice` on us fetches the
   invoice over onion messages, the wallet pays it, captaind xpays the `lni` string, we pay it over the
   paths our fetch verified; `ListPays` COMPLETE with the `bolt12` field.
5. **Receive with the wallet's own claim**: `bark lightning invoice` (captaind's `hold.Invoice` on us),
   LND pays, our invoice Held, `bark lightning claim --wait` (PrepareLightningReceiveClaim +
   ClaimLightningReceive: arkoor package, musig2 nonces) → captaind settles our hold invoice → LND's
   payment Succeeded with the wallet's preimage, our invoice Settled. This replaces wave B's
   settlement-WAL shortcut as the receive proof (`BarkAspFlowTests` keeps the WAL seam).

**In-flight semantics (what captaind does).** `server/src/ln/cln/xpay.rs`: xpay is spawned
fire-and-forget; whatever the call returns (preimage or any error), captaind immediately runs
`sync_payment_attempt_status`: `ListPays` by payment hash, then the latest row by `created_index`
(unwrapped with `expect`). No row → the attempt fails (the user's HTLC VTXOs become revocable); PENDING →
Submitted (kept open); COMPLETE → Succeeded only with a 32-byte preimage that hashes to the payment
hash; FAILED → Failed. A periodic loop re-checks open attempts from `retry_for` + 15 s after creation,
backing off from `invoice_check_base_delay` to `max_invoice_check_delay`. So the contract is: a payment
with an HTLC out must always be listed and never FAILED — our InFlight → PENDING, and our Failed only
once no part is in flight, already met it; DEADLINE_EXCEEDED for an xpay still in flight is safe (the
error is ignored). Changed: the payment no longer takes the call's cancellation (NL-1150, as CLN's xpay
keeps running when its client leaves). captaind's other `cln.Node` calls: `Getinfo` (network check, and
our block height to size xpay's `maxdelay`), `ListPays`, `Xpay`, and `FetchInvoice` for offers (NL-1151,
now served).

**Findings.**

1. **NL-1149 (fixed):** `hold.Invoice` ignored `min_final_cltv_expiry`: every backend hold invoice
   carried our `c` = 40 while captaind asks for the user's delta + 40, so the inbound HTLC never left
   room for the wallet's grant and captaind refused the claim ("Requested HTLC recv expiry too close to
   inbound HTLC expiry"). No real receive could complete; wave B hid it by hand-picking the expiry.
2. **NL-1152 (fixed):** our method was `Listpays`, CLN's `ListPays`; captaind reached it only because
   ASP.NET Core routes gRPC paths case-insensitively.
3. **NL-1151 (fixed):** BOLT 12 pay through captaind (FetchInvoice + xpay of `lni`). Fetched invoices
   live in memory until expiry; since NL-1157 an `lni` the memory lacks (a restart in between) is
   verified statelessly — our `invreq_metadata` commits to the request fields and derives
   `invreq_payer_id` with our key — and paid.
4. **NL-1154 (fixed):** our row reads `Failed` between two attempts while `PaymentService` still retries
   (NL-999); `ListPays` (and `Xpay`'s mapping) now report it PENDING while `IsPaying` is true, so captaind
   never sees FAILED while a retry may still put an HTLC out. An empty `ListPays` (which fails the attempt)
   can only mean nothing was offered: the row is persisted `InFlight` before the first offer and survives
   restarts.
5. **Liveness after `retry_for`:** our `PayInvoiceOptions.Timeout` stops new attempts at the deadline and
   the parts in flight resolve on their own; our understanding of CLN's xpay is the same (no new attempts
   after `retry_for`), and either way captaind only reconciles by `ListPays`, so this is liveness, not
   safety. Retrying in the background past the window is not done.
6. **NL-1153:** the original fixed 300-second `retry_for` cap is now configurable through
   `LnBackend:MaxXpayRetryFor` (default 300, valid range 1–3,600 seconds); see the configuration
   section above.
7. Test-only: captaind's chain is its own (Core 31), so the proof keeps it at our height — captaind
   compares its tip with our `Getinfo` height (xpay's `maxdelay`) and requires the inbound HTLC's expiry
   (our chain) to clear its own tip + `c` ("Incoming HTLC expiry height doesn't fit").

**Still open upstream:** bark's stale `http://` example (finding 1 of wave B), and a first-class
NLightning backend module in their tree if the two-proto shim ever diverges.
