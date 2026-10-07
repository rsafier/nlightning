# PAYROUTE_PLAN — full IPC control over the payment path (`payroute`, NL-1082)

Status: Phase A + B implemented on `wip/payroute` (from `wip/fafo` at `b6adfa2e`, 2026-10-05); phase C (attach
routes to an in-flight payment, NL-1276) on `wip/u-nl1276` (2026-10-07), record in §6.

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
  a still-in-flight manual payment is Phase C (§6): `payroute --attach` (ClientCommand 56) and LND's
  `SendToRouteV2` with the hash of a payment in flight.
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
  supported; `RefusedWhileDraining` refuses it while draining. A hop's `outgoingShortChannelId`
  is `BLOCKxTXxOUTPUT` or its u64 number (NL-1085). `getroute --json` (client only,
  `GetRouteJsonPrinter`) prints a quote with each hop's incoming view (`shortChannelId`,
  `amountMsat`, `cltvExpiry`, as the text form) and its outgoing view in this schema's terms
  (`outgoingShortChannelId` = the next hop's incoming channel, omitted on the final hop;
  `amountToForwardMsat`/`outgoingCltvValue` = the next hop's incoming amount/expiry, the
  final hop's own), so the help's recipe is a key-for-key copy:
  `nltg getroute <node> <msat> --final-cltv <delta> --json | jq -c '[{firstHopChannel:.channelId,
  firstHopAmountMsat:.amountMsat,firstHopCltv:.cltvExpiry,hops:[.hops[]|{nodeId,
  outgoingShortChannelId,amountToForwardMsat,outgoingCltvValue}]}|.hops[-1]|=del(.outgoingShortChannelId)]'`
  (not for a `--trampoline` quote).

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
| Payee's 60 s `mpp_timeout` window | All shards offered in one call, sequentially — inside the window by construction. A phase C attach must come within `Node:Payments:PayRouteAttachWindow` (60 s) of the oldest part still in flight, else it is refused as too late (§6). |
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
- Cluster (`Docker/PayRouteFlowTests.cs`, lnd suite, 60/60 green 2026-10-05): pay alice's
  invoice over a `getroute` quote verbatim (`FinalCltvDelta` must match the invoice's); a
  hand-built two-shard set over two channels (each too small alone) settles together. The
  first runs found two real direct-route edge cases (both fixed, 5772c2af): a one-hop route's
  only hop IS the payee (no first-hop CLTV lowering), and getroute's final hop names its
  incoming channel while payroute's final hop omits the scid.

## 6. Phase C — attach routes to an in-flight payment (NL-1276, as built)

Useful when one shard failed while the payee holds the others and its `mpp_timeout` window is still open: the
caller replaces the failed shard without giving up the held ones. Built on `wip/u-nl1276` (a8d67864, 2026-10-07).

- **Surfaces.** `PayRouteRequest.Attach` (`PayRouteAttachMode`: `Never` (default, phase A/B: refused while a
  payment of the hash is in flight), `Required` (attach only), `IfInFlight` (attach when a payroute session of
  the hash is open, else start one)) and `PayRouteRequest.IndependentShards` (LND form, below).
  - IPC: `ClientCommand.PayRouteAttach = 56` (52-55 are reserved for the silent payments commands,
    `SILENT_PAYMENTS_PLAN.md` §3.9), request `PayRouteAttachIpcRequest` (keys 0-6 as `PayRouteIpcRequest`'s
    0-6; no label or tags: the row keeps the first call's), response `PayRouteIpcResponse`; client handler
    `PayRouteAttachClientHandler` (mapped by `PayRouteClientHandler` with `Required`), IPC handler
    `PayRouteAttachIpcHandler`, refused while draining. CLI: `nltg payroute ... --attach` (a flag; `--label`/`--tag`
    refused with it).
  - LND: `routerrpc.SendToRouteV2` sends every call as `IfInFlight` + `IndependentShards`, so an MPP set sent over
    several calls (ln-service's multi-path pay sends shards in parallel and replaces a failed one) joins the
    payroute payment of the hash, as LND registers each call as one more attempt of the payment.
- **Service** (`PaymentService.PayRoute.cs`). The stateless validation of §3 runs first (shape, first hops, CLTV,
  policies, liquidity of the call's routes on our channels as they are now, in-flight HTLCs included); then, under
  the payment hash's lock, `StartOrAttachPayRouteAsync` attaches to the registered session or starts one.
  `AttachPayRouteLockedAsync` checks, nothing offered on a refusal:
  - the session is a payroute (`ManualRoutes`) session, not keysend (else `InvalidOperationException`);
  - the same payment secret (the invoice's, or the raw form's; null is the all-zero secret), the same total and
    the same payee as the session's; at most 256 parts in the session (the part-row index is a byte);
  - `Required` only: the oldest part still in flight was offered at most `Node:Payments:PayRouteAttachWindow`
    (`PaymentSendOptions.PayRouteAttachWindow`, default 60 s, BOLT 4's `mpp_timeout`) ago ("Too late");
  - delivery: with the parts in flight the routes deliver at least the total (`Required`; a replacement that leaves
    the set short would only be failed by the payee's timer) or at most the total (`IndependentShards`: LND's
    "attempted value exceeds payment amount");
  - fees: the parts in flight plus the routes within `MaxFee ??` the session's limit.
  Nothing in flight: `Required` is refused (`InvalidOperationException`: "already succeeded" for a settled payment,
  else "No payroute payment ... in flight"); `IfInFlight` starts a session (the failed row is replaced, phase A).
  The attached round is one more `RunManualRoundAsync` (onions, the row kept as the parts are in flight, offers in
  order); the session's limits, fee accounting (`RecordedFeesInFlightMsat` at the fulfill), `MovePrimaryPartAsync`
  and the no-retry rule are unchanged.
- **Linger rule.** None: a session lives exactly as long as one of its parts is in flight (a session without one is
  decided and removed under the hash's lock, so an attach that finds it registered always joins live parts). When
  every part has failed, the payee holds nothing any more, so a new call without `--attach` (or an LND shard) starts
  a new attempt over the failed row, which the payee treats the same. A restart ends every session (memory only):
  an attach after it is refused, and the parts in flight are reconciled as before (NL-321).
- **Answers.** Every call reports only its own routes (`RouteOutcome.Index` within the call; a route left unoffered
  after an offer of unknown outcome is reported failed). A `Never`/`Required` call waits for the payment's outcome
  (its timeout); an `IndependentShards` call answers once its own parts are resolved (`PaymentSession.
  WhenPartsResolvedAsync`, woken by `SignalPartsChanged` on every part failure and by the session's end), so a
  failed shard is answered at once while its siblings stay held, and a held shard answers when the payee settles or
  fails the set.
- **Proofs** (in-process, real crypto and onions): `Application.Tests/Payments/Send/PayRouteTests.Attach.cs` (5):
  one shard failed at Carol and one held, a replacement attached completes the set (each call reports its own
  routes; the money moves once); refusals before anything is offered (another secret, another total, short of the
  total, nothing in flight, past the window); a settled payment refused; an attached replacement that fails too,
  then the payee's `mpp_timeout` (per-route attribution: Carol at index 0 on both failed shards, the payee at index 1
  on the held one); the LND form (one shard per call, a failed shard answered at once with Carol's failure while
  the other is held, an over-total shard refused, the replacement settling the set).
  `LndGrpc.Tests/LndGrpcHostTests.SendToRouteShards.cs` (3): two parallel `SendToRouteV2` shards of one hash both
  reach payroute as attachable shards with the mpp record and each answers its own HTLC (the failed one at LND's
  index), a refused attach is `InvalidArgument`, a settled payment `FailedPrecondition`.
  `Daemon.Tests/Ipc/Handlers/PayRouteAttachIpcHandlerTests` (5): the request over MessagePack to `Required`, the
  refusals as `invalid_operation`, ClientCommand 56 and the drain refusal, the wire layout and the CLI flag.
- **Not done.** No cluster proof against LND's own multi-path `SendToRouteV2` client (bos/ln-service); a cluster run
  of `PayRouteFlowTests` is unchanged by this work.

## 7. Ledger

NL-1082 (epic), NL-1083 (the implementation: service, IPC, CLI, tests), NL-1276 (phase C).
