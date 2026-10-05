# PAYROUTE_PLAN — full IPC control over the payment path (`payroute`, NL-1145)

Status: Phase A + B implemented on `wip/payroute` (from `wip/fafo` at `b6adfa2e`, 2026-10-05).

The IPC client can pay over exactly the routes it supplies — a single route or an MPP shard
set — with the daemon never re-planning. This plan records the design (approved 2026-10-05),
the as-built state and the edge-case catalog.

## 1. Semantics

- **Client-driven only**: the daemon offers exactly the supplied route(s), waits for outcomes,
  reports. Never re-plans, never retries, never substitutes channels. A failure (decrypted
  error onion with failing-hop attribution, or our engine's refusal rule) ends that route;
  the caller decides what is next. Mission control still records (it only learns).
- **Two identity forms**: `bolt11` (hash/secret/amount/`basic_mpp` derive from the invoice) or
  raw `payment_hash` + optional `payment_secret` + explicit `total_msat` (the LND
  `SendToRoute` form; with no secret the final hop carries the all-zero secret — documented).
- **One call = one attempt**: N routes = N HTLCs offered immediately, sequentially. A later
  call for the same invoice replaces the failed row (existing semantics). Attaching shards to
  a still-in-flight manual payment is Phase C (deferred, §6).
- Everything downstream of planning is reused verbatim: `PaymentOnionFactory` (fresh session
  key per shard — the receiver's per-HTLC replay store is never hit), `OfferHtlcAsync`, part
  persistence (NL-321), outcome handling, restart reconciliation, accounting/labels.

## 2. IPC surface (ClientCommand 48)

- Request keys: 0 `Bolt11`, 1 `PaymentHash`, 2 `PaymentSecret`, 3 `TotalMsatMsat`,
  4 `Routes[]`, 5 `TimeoutSeconds` (nullable on the wire — an init default deserializes as 0,
  MsgPack017), 6 `MaxFeeMsat`, 7 `Label`, 8 `Tags`.
- Route: 0 `FirstHopChannel` (64-hex `ChannelId` or `BLOCKxTXxOUTPUT` scid/alias, resolved
  like `payinvoice --out`), 1 `FirstHopAmountMsat`, 2 `FirstHopCltv` (absolute),
  3 `Hops[]`. Hop (mirrors `getroute`'s so its answer round-trips): 0 `NodeId`,
  1 `OutgoingShortChannelId` (null on the final hop), 2 `AmountToForwardMsat`,
  3 `OutgoingCltvValue` (absolute). Hops ordered our-peer-first, payee-last.
- Response: 0 `Payment` (`PaymentInfoClientResponse` — preimage, failure code/source/reason),
  1 `RouteOutcomes[]` (Index, Status, HtlcId, FailureCode, FailureSourceIndex, FailureReason).
- CLI: `nltg payroute <bolt11> --routes <file|->` / `--payment-hash [--payment-secret]
  [--total-msat]`; JSON routes (camelCase, case-insensitive), `--routes -` reads stdin; labels
  supported; `RefusedWhileDraining` refuses it while draining.

## 3. Service layer

`IPaymentService.PayRouteAsync(PayRouteRequest, PayInvoiceOptions, ct)` (partial
`PaymentService.PayRoute.cs`). `PaymentSession.SuppliedRoutes` +
`RunManualRoundAsync` in `RunRoundsAsync`: build every route's onion, `PersistRoundAsync`,
offer in order; a refused offer marks that route failed and continues; when nothing is in
flight afterwards the payment is decided, else the parts' outcomes complete it.
`HandleSessionFailureAsync` forces `retry = false` for manual sessions (mission control still
learns through the same `Decide` call). Session `MaxParts = MaxAttempts = routes.Count`.
`PaymentPart.Failure` captures the per-route failure (code, source index, reason) for the
response.

### Validation (before anything is offered)

1. Shape: 1..128 routes; exactly one identity; `PaymentRoute` ctor invariants per route.
2. Identity: invoice decode (expiry, features; blinded-path invoices refused — pay those with
   `payinvoice`); self-payment refused; `basic_mpp` required for a shard set (invoice form).
3. First hop: each route's channel is ours, open, with commitments, peer == hops[0], link
   alive (`IPeerLivenessProbe`).
4. CLTV: `height < firstHopCltv ≤ height + Routing.MaxCltvExpiryDistance`; strict decrease
   over the forwarding hops — **the payee's expiry may equal the last forwarding hop's** (they
   are the same HTLC; the getroute/planner shape); final ≥ `height + min_final_delta`.
5. Fees: Σ(first-hop − delivered) ≤ `MaxFee ??` the node's default policy.
6. Forwarding policies the graph knows: `ForwardingFee.PaysSufficientFee` per intermediate
   hop + the policy's HTLC min/max. Unknown scids (private channels) allowed — the hop
   enforces on the wire and failures return cleanly.
7. Liquidity: `LocalLiquidityEstimator` per first-hop channel **across the routes sharing it**
   (balance, reserves, commitment fees, in-flight caps, `max_accepted_htlcs`, dust).
8. MPP arithmetic: Σ delivered ≥ total (over-delivery allowed and logged); every route reports
   the same total (by construction — one value).

## 4. MPP edge-case catalog (resolutions)

| Edge | Resolution |
|---|---|
| Shard fails while siblings held at payee | No retry; payment waits; all resolved w/o fulfill → Failed (held ones fail `mpp_timeout` at the payee and decrypt as such — pinned by `PayRouteTests` test 7 with per-route attribution). Client re-calls with a replacement set → replaces the failed row (test 8). |
| Payee's 60 s `mpp_timeout` window | All shards offered in one call, sequentially — inside the window by construction. Phase C attach must beat the timer (documented). |
| Same channel on multiple shards | Allowed; the liquidity estimator sums them; pinned by test 6 (two shards over one channel). |
| Duplicate route reuse across shards | Allowed; fresh onion per shard (factory session keys). |
| Shard-set settle atomicity vs outcomes | When the payment succeeds, every offered route was fulfilled by the payee, also the ones whose fulfill arrived after the session completed: they report Succeeded and their stored part rows are settled (`SettleLeftoverPartRowsAsync`). |
| `total_msat` consistency | One value by construction (invoice amount or the explicit field). |
| Over-delivery (Σ > total) | Allowed and logged (receiver semantics are ≥). |
| Late shard after the payee settled | Fails `0x400F` upstream — surfaces as that route's failure code. |
| No `basic_mpp` payee | Invoice form: refused up front. Raw form: not verifiable — documented, the payee fails it back. |
| Daemon restart mid shard set | Inherited semantics: outcomes reconcile from stored part rows (NL-321); nothing re-sends; un-completable sets time out at the payee → Failed → client re-calls. |
| >256 part rows per attempt | Impossible: cap 128 routes. |
| Keysend / blinded-path / trampoline manual routes | Out of scope v1 (their final-hop payloads differ structurally; the dedicated APIs exist). |

## 5. Tests

- In-process (`Application.Tests/Payments/Send/PayRouteTests.cs`, 8): single-route success
  (invoice + raw forms), failure attribution (`ForwardInterceptor` → code + source index),
  pre-offer liquidity refusal, a 16-case validation matrix, two-shard MPP over one channel,
  one-shard-fails-one-held → `mpp_timeout` with per-route attribution, replace-over-failed-row.
  (`HarnessForwardingSwitch` gained the mpp hold timer/`WhenIdleAsync` for these.)
- IPC (`Daemon.Tests`): `PayRouteIpcHandlerTests` (mapping, both forms, errors, the
  `ClientCommand.PayRoute == 48` pin, draining), `PayRouteMessagePackTests` (round trips +
  defaults-null), CLI (`PayRouteCommandTests` 32 cases + usage rows).
- Cluster (`Docker/PayRouteFlowTests.cs`, lnd suite): pay alice's invoice over a `getroute`
  quote verbatim; a hand-built two-shard set over two channels (each too small alone).

## 6. Phase C — deferred (design only)

`payroute --attach <payment-hash>`: add replacement shards to a still-in-flight manual
payment (same secret/total), useful when one shard failed while siblings are held at the
payee and the 60 s window is still open. Needs session-lookup-by-hash for an open manual
session, additive `SuppliedRoutes`, and a linger rule (keep the session open until deadline
or an explicit done). Deliberately gated on A/B learnings.

## 7. Ledger

NL-1145 (epic), NL-1146 (the implementation: service, IPC, CLI, tests).
