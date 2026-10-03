# Trampoline proof harness (NL-875 TR5)

In-process, four-node harness for the trampoline proofs of `docs/agents/TRAMPOLINE_PLAN.md` §3 TR5. Fast tests
only: no Docker, no cluster.

## Topology

```
A ──(A–T, A funds)── T ──(T–X, T funds)── X ──(X–C, X funds)── C
A ──(A–T #2, optional)── T
T ──(T–X #2, optional)── X ──(X–C #2, optional)── C
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
| `SecondLegChannels` | off | Adds `TrampolineX2ChannelId` and `XCarol2ChannelId`, so T's leg can split. |
| `LogLevel` | off | Every node's log lines at that level go to the test's output (`HarnessLoggerProvider`; framework categories from Warning). |
| `SteppedClock` | on | Every node shares `harness.Clock`, a `SteppedTimeProvider`. MPP timers fire only from `AdvanceAsync`. |
| `ConfigureNode(node)` | | Node options before the start: routing, `Node:Trampoline`-style options, keysend and so on. |
| `ConfigureServices(node, services)` | | Last changes to a node's services, applied on every start, restarts included. Phase 2 registers the relay engine and leg sender on T here. |

**Drive:**

- `PumpAsync()` delivers messages until every queue is empty and every scheduler is idle.
- `PumpUntilAsync(task)` pumps while an `IPaymentService` call runs, then returns its result.
- `AdvanceAsync(by)` moves the clock, waits for every switch, then pumps.
- `RestartAsync(node)` and then `ReconnectAsync(node)` restart a node; the restart also runs the daemon's next startup
  steps when the node has them (payment reconciliation, then `TrampolineRelayService.StartAsync`). Restart only at a
  quiescent point: nothing is
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
- `PlanRelayAsync(A, T, C, invoice, trampolineFee, cltvDelta, nextNodeId?)` builds the inner onion with T as an
  intermediate trampoline node (`outgoing_node_id`, the relay engine's input); send its parts with
  `SendTrampolinePartAsync(plan, channel, [T], part)`.
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

## Phase 2 (done)

`../TrampolineRelayE2ETests` runs the production relay engine (`AddTrampolineRelayServices` on T through
`ConfigureServices`), leg sender (T's `PaymentService`, routing over the graph through X) and payer (A's
`PaymentService`) together:

1. single part: fees, relay row, leg row, `TrampolineRelaySettled`, A's `PaymentTrampolineHops`;
2. MPP on both legs (`SecondAliceTrampolineChannel` + `SecondLegChannels`), C settles once;
3. T's NODE|26, A's cached policy and retry, the failed relay replaced (`RemoveFailedAsync`);
4. C's error read by A at C's trampoline index;
5. T restarted while collecting (hand-built parts), 6. while sending (C's switch suspended);
7. refusals (CLTV, `MaxRelaysInFlight = 0`, `mpp_timeout` at T) read at the trampoline layer, index 0;
8. an unknown next node (`unknown_next_trampoline`, no retry) and an unreachable one (`temporary_trampoline_failure`,
   one retry);
9. `Node:Payments:Trampoline=Auto` picking T from a peer manager stand-in (A sees no graph);
10. (a) a blinded recipient without bit 57: T pays `recipient_blinded_paths` (C's `BlindedPathBuilder` path through X).

Not covered: (b) blinded hops as trampoline hops. With C's builder path X (the introduction node) gets the trampoline
onion but its recipient data names the X–C channel by `short_channel_id`, which the relay engine does not resolve
("lacks its relay instructions"); resolved by hand, X then applies its own `Node:Trampoline` fee and delta to a hop
whose price the recipient fixed in `payment_relay`, and refuses with NODE|26.

Phase 2 found two product bugs, fixed with the scenarios: the relay kept its own trampoline fee and delta out of the
leg's budget, so a payer paying exactly the policy of a NODE|26 got no route past T's peers (1, 3); and a last
trampoline layer naming `recipient_blinded_paths` was refused as a final payload (10a).

## Gaps

- No BOLT 12 or onion messages: the nodes exchange channel messages only.
- There is no `channel_reestablish` retransmission. Restart and reconnect only at quiescent points, as in
  `ThreeNodeHarness`.
- Tests that need X's or C's relay behaviour must register it on that node too, through
  `ConfigureServices(node, ...)`.