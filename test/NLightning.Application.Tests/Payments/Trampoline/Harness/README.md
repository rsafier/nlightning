# Trampoline proof harness (NL-875 TR5)

In-process, four-node harness for the trampoline proofs of `docs/agents/TRAMPOLINE_PLAN.md` §3 TR5. Fast tests
only: no Docker, no cluster.

## Topology

```
A ──(A–T, A funds)── T ──(T–X, T funds)── X ──(X–C, X funds)── C
A ──(A–T #2, optional)── T
```

- Each channel is 2,000,000 sat with 800,000 sat pushed to the fundee, so both directions have liquidity.
- Every node is a `Channels/Harness/SwitchNode`, the same node `ThreeNodeHarness` uses. It runs the production
  `ChannelManager`, `ChannelOperationsService`, `CommitScheduler` and `HtlcSwitch`, real signers, real Sphinx,
  trampoline and error onions, and its own SQLite file with the real repositories.
- The migrations run once per test process into a template database. Each node starts on a copy of it.
- Forwarding policies are distinct, so a fee mix-up shows:
  - T: 1,000 msat + 100 ppm, delta 40 (`TrampolineHarness.TrampolineRouting`).
  - X: 2,000 msat + 500 ppm, delta 34 (`XRouting`).
- By default T and C advertise `trampoline_routing`, with `AllowExperimentalFeatures`. C's BOLT 11 invoices carry
  bit 57.

## API

**Build** with `TrampolineHarness.CreateAsync(new TrampolineHarnessOptions { ... })`:

| Option | Default | What it sets |
|---|---|---|
| `Trampoline` | `T \| C` | Which nodes advertise bit 57 (`TrampolineHarnessNodes` flags). |
| `PaymentSenders` | none | Which nodes run `AddPaymentSendServices`. This gives them `IPaymentService` (and so `ITrampolineLegSender` once TR4 implements it on `PaymentService`). |
| `GraphViewers` | every node | Which payment senders see `BuildGraph()`: every channel with both policies, and every node's `node_announcement` features, so T and C carry bit 57 there. |
| `SecondAliceTrampolineChannel` | off | Adds `AliceTrampoline2ChannelId`, for MPP over two first hops. |
| `SteppedClock` | on | Every node shares `harness.Clock`, a `SteppedTimeProvider`. MPP timers fire only from `AdvanceAsync`. |
| `ConfigureNode(node)` | | Node options before the start: routing, `Node:Trampoline`-style options, keysend and so on. |
| `ConfigureServices(node, services)` | | Last changes to a node's services, applied on every start, restarts included. Phase 2 registers the relay engine and leg sender on T here. |

**Drive:**

- `PumpAsync()` delivers messages until every queue is empty and every scheduler is idle.
- `PumpUntilAsync(task)` pumps while an `IPaymentService` call runs, then returns its result.
- `AdvanceAsync(by)` moves the clock, waits for every switch, then pumps.
- `RestartAsync(node)` and then `ReconnectAsync(node)` restart a node. Restart only at a quiescent point: nothing is
  retransmitted.
- `Disconnect(a, b)` and `ReconnectLinkAsync(a, b)` drop and restore one link.

**Pay by hand.** These helpers are in `TrampolineHarness.Onions.cs`:

- `CreateInvoiceAsync(C, amount)` and `Decode(invoice)` create and read a BOLT 11 invoice.
- `BuildRoute(hops, amount, finalCltv, hash, secret, total)` builds a plain route with each forwarder's policy.
- `OfferAsync(payer, firstChannel, route, finalPayloadOverride)` sends it.
- `PlanFinalTrampolineAsync(A, C, invoice, amount?, total?, secret?)` builds the inner onion with C as the final
  trampoline hop, plus a random outer secret.
- `SendTrampolinePartAsync(plan, firstChannel, [T, X, C], part, outerTotal?)` sends one part. In it, T and X are plain
  outer hops.
- For other inner routes, use `BuildTrampolineOnionAsync(payer, hash, (node, payload)...)` with `OuterTrampolinePayload`.
- `DecryptTrampolineFailure(A, onion, plan.Onion, failed)` decrypts a failure with both layers.

**Inspect.** These helpers are in `TrampolineHarness.Rows.cs`; each reads through a fresh unit of work:

- `GetInvoiceAsync`, `GetPaymentAsync`, `GetRelayAsync` (`TrampolineRelays` and its parts), `GetTrampolineHopsAsync`
  (`PaymentTrampolineHops`).
- `LiveHtlcs()` and `AssertQuiescent()`. The latter checks for no HTLC and no nested channel lock.
- `SwitchOf(node)` gives `HeldPaymentHashes` and `WhenIdleAsync`.
- `node.PaymentHandler` records the outcomes of `HtlcOrigin.Local` HTLCs.
- `node.Events`, `node.Received`, `node.Dropped` and `harness.Sent` log the switch events and the wire.

## Phase 1 (done)

`TrampolineHarnessTests` checks the composition:

- the topology and the bit 57 advertisement;
- T paying C over the graph through X with the production `PaymentService` (the relay's outgoing leg will route this
  way);
- A paying C through T and X;
- a restarted forwarder.

`../TrampolineTargetE2ETests` proves the TR2 target end to end, with A's onions built by hand:

- single part;
- MPP over two A–T channels, as one outer set and as two outer payments with their own trampoline onions;
- C's wrong-secret and `mpp_timeout` failures, which A reads at the trampoline layer;
- the feature off on C, which gives `invalid_onion_payload` at the outer layer, index 2;
- C restarting with one part held, after which the second part completes the set.

## Phase 2 (after TR3 and TR4 merge)

Every scenario below needs these settings first:

- `PaymentSenders = A | T`.
- On T, `ConfigureServices` registers the relay engine: `TrampolineRelayService` as `ITrampolineRelayIngress`
  (scoped or singleton; the switch resolves it per part), `ITrampolineHtlcHandler` (singleton; `HtlcSwitch` takes it
  as its last optional constructor argument, and `SwitchNode` builds `HtlcSwitch` through DI) and
  `ITrampolineLegObserver`.
- Use the layer's `Add…` extension if TR3 provides one.
- Set T's `Node:Trampoline` policy in `ConfigureNode`, as `node.Options.Trampoline`, or in `ConfigureServices`,
  wherever TR3 binds it.

Do not commit skipped or failing placeholders for these.

1. **A pays C through the T relay, single part.** A uses `IPaymentService.PayInvoiceAsync(bolt11, null, new
   PayInvoiceOptions { TrampolineNode = T.NodeId })`, through `PumpUntilAsync`. Assert:
   - C's invoice is `Settled`;
   - T's `TrampolineRelays` row is `Fulfilled` with the fee (`GetRelayAsync`);
   - T's leg payment has `IsTrampolineRelay` and is not booked as our spend;
   - A's `PaymentTrampolineHops` rows (`GetTrampolineHopsAsync`);
   - the `TrampolineRelaySettled` accounting event on T (`InScopeAsync(u =>
     u.AccountingEventDbRepository.GetUnsealedAsync(...))`).
2. **MPP on both legs.** Add `SecondAliceTrampolineChannel` and set A's `MaxParts`. To force a split on A, cap A's
   channels or pick amounts above one channel's balance. T's leg splits only when `recipient_features` has
   `basic_mpp`, which C's bit 57 invoice has. The harness may need a second T–X or X–C channel. Add it the way
   `OpenChannelAsync` opens `AliceTrampoline2`: a new option and a new channel id or scid.
3. **NODE|26 refusal and retry.** Give T a policy above A's default budget (1,000 msat + 1,000 ppm, delta 576). Assert
   that A's first attempt fails with `trampoline_fee_or_expiry_insufficient` (0x201A) carrying T's policy, that the
   retry succeeds, and the attempt count. A `temporary_trampoline_failure` retry needs X's link down for the first
   attempt: `Disconnect(T, X)`, then `ReconnectLinkAsync`.
4. **Restart T while Collecting.** Send A's first part by hand (as `SendTrampolinePartAsync` does, but with a relay
   payload for T: `OutgoingNodeIdTlv(C)` with C's inner final payload in the next layer, built through
   `BuildTrampolineOnionAsync`). Then `RestartAsync(T)` and `ReconnectAsync(T)`, and send the second part. Assert the
   timer was restored, the parts were found again, and a single relay.
5. **Restart T while Sending.** Block the leg at X: suspend C's switch (`C.SwitchSuspended = true`) so C holds the
   HTLC, then restart T. After `ReconnectAsync`, payment reconciliation and the switch replay finish the relay. Then
   resume C (`SwitchSuspended = false`, `ReplayPendingEventsAsync`) and assert exactly one fulfill upstream per part.
6. **T pays `recipient_blinded_paths` (TLV 22 + 21).** C needs a BOLT 12 invoice without bit 57, or a blinded path
   from `BlindedPathBuilder`. The harness has no onion-message transport, so build C's blinded payment path directly
   (`BlindedPathBuilder` on C, as `Payments/Send/BlindedSendThreeNodeTests` does) and put it in A's inner payload for
   T.
7. **BOLT 12 blinded trampoline hops.** C's offer invoice paths double as trampoline hops (TR2, M5). The harness has
   no `invoice_request` exchange, because `ThreeNodeHarness`-style nodes carry channel messages only. Make C's
   `Bolt12` invoice through `OfferInvoiceFactory`/`InvoiceRequestHandler` in-process, or extend `Route` with an
   onion-message path (`IPeerOnionMessageOutbox` fake). The cheapest option is to build the blinded path with
   `BlindedPathBuilder` and pay it with `PayBlindedAsync` plus the TR4 trampoline option.
8. **Refusals.** For each refusal below, assert C never sees an HTLC and A decrypts the code at the trampoline layer,
   index 0 (T):
   - CLTV: give A's inner payload for T too small a CLTV margin (below T's `CltvExpiryDelta`);
   - fee: incoming minus outgoing is below T's fee;
   - `MaxRelaysInFlight`: hold one relay at C (suspend C's switch), then start a second payment with another invoice.
9. **MPP timeout at T.** Send one part of two to T by hand, then `AdvanceAsync(MppTimeout)`. Assert every part fails
   with `mpp_timeout` at the trampoline layer from T, and the relay row is `Failed`.

## Gaps

- No BOLT 12 or onion messages: the nodes exchange channel messages only.
- There is no `channel_reestablish` retransmission. Restart and reconnect only at quiescent points, as in
  `ThreeNodeHarness`.
- Tests that need X's or C's relay behaviour must register it on that node too, through
  `ConfigureServices(node, ...)`.