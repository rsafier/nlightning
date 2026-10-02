# Accounting plan (NL-602)

Status: **A1 done, A2 built, A3 built** (2026-10-02): the feed with every writer, the sealer, the cutover and the flat-startup proof (A1); the operational books with posting rules, projector, rebuild, reconcile, reports, exports and IPC 41-45 (A2); the financial profile (A3-T0..T7 on `wip/acct-a3`: labels and tags, prices and back-valuation, classification rules and overrides, the financial projector with FIFO/LIFO/HIFO lots and gains, signed period closes, financial reports and exports, the config template and the security review; §9 "A3 record"). A3's Docker smoke was skipped on 2026-10-02 by owner decision and the open follow-ups are listed in the A3 record. The §8 recommendations (D-A1..D-A13) hold at their recommendations; the owner has not overridden any.

## 1. Goal

First-class accounting built into NLightning. It is not a plugin, and an operator can turn it off with one flag. There are two depths:

- **Operational** (the default): a node operator's view in sats/msat. Routing revenue, payment fees, on-chain fees, per-channel profitability, liquidity cost, and where every sat went. It is cheap, needs no fiat data and no setup.
- **Financial** (opt-in, the CFO view): proper double-entry books. It adds classification rules, fiat valuation at the time of each event, cost-basis lots, realized and unrealized gains, period close, an audit trail, and export to hledger, beancount and CSV for an external general ledger.

Every node gets the core event feed (§3), with or without the books turned on.

## 2. What exists today (survey 2026-10-02)

- **Plugin framework.** It is a stub and is not kept (NL-151). `IDaemonPlugin`/`IDaemonContext` (`src/NLightning.Daemon.Plugins/`) and `PluginLoaderService` were never registered. A runtime assembly loader is ruled out for two reasons:
  - It cannot work in the AOT / `Release.Native` builds.
  - It would add an attack surface: arbitrary code in the process that holds the keys, which the security review flagged.

  Accounting is therefore compiled in, and the stub is deleted (§7).
- **No accounting-level events exist.** The only hooks are:
  - `IHtlcSwitch` domain events. These are at-least-once: they are replayed at startup, on link-up and on every block until irrevocable.
  - `ILocalPaymentHtlcHandler`, which fires per outgoing part, not per payment.
  - `IChannelMemoryRepository.OnChannelOpened/Updated/Upgraded`.
  - `IBlockchainMonitor.OnWalletMovementDetected`, which covers incoming wallet outputs only and has no subscriber.
- **History that is kept:**
  - `Invoices`, except expired Open BOLT 12 ones (NL-448).
  - `Payments`, `PaymentParts` and `PaymentHops`.
  - `ForwardCircuits`, never deleted (fee = in − out).
  - `Channels`, never deleted.
  - `ChannelFundings` (splice deltas).
  - `ChannelCloses`, `OutputResolutions`, and `BroadcastTransactions` (never deleted; they keep the raw tx and its purpose).
- **History that is lost or never stored:**
  - Wallet UTXO rows are **deleted on spend**, so there is no wallet transaction history (NL-603).
  - Absolute on-chain fees are not stored. Only the feerate and the raw tx are, so working out a fee needs the input values (NL-604).
  - The push amount is folded into the opening balance and not stored (NL-605).
  - Archived HTLC rows are pruned (NL-243), so per-part receive history is lost and only the invoice total survives.
- **Invoices and payments carry no operator label or tags.** The financial profile needs them to classify revenue and spending (§6.2).
- **IPC.** `ClientCommand` is a closed enum with 41 the next free value. The accounting commands are ordinary built-in commands (§7).

## 3. Architecture

```
 NLightning daemon process (one binary, AOT-compatible, no runtime code loading)
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ Core (always on)                                                         │
 │   HtlcSwitch / PaymentService / channel open-splice-close / BOLT 5 /     │
 │   chain monitor / WalletSpendService                                     │
 │        │  same SaveChangesAsync as the state change (transactional outbox)│
 │        ▼                                                                 │
 │   AccountingEvents table ──► EventSealer (dense LedgerSeq + hash chain)  │
 │        │                                                                 │
 │        └─► IAccountingEventFeed (cursor read + wake-up), Snapshot timer  │
 │                                                                          │
 │ Accounting books (Accounting:Enabled, unset = on)                        │
 │   Projector(cursor) ─► profile + rules ─► double-entry journal tables    │
 │   ─► reports, reconcile, hledger/beancount/CSV export                    │
 │                                                                          │
 │ IPC 41-45 (accounting commands) ─► nltg accounting ...                   │
 └──────────────────────────────────────────────────────────────────────────┘
          │ exports / IPC feed
          ▼
   any external general ledger or tax tool (operator's choice)
```

Four principles:

1. **The core records facts; the books hold the opinions.** The feed records what happened to our money, in msat, with deterministic keys, inside the same database transaction as the state change. It never decides accounts, fiat or tax treatment. Every accounting opinion lives in the books layer, and the books can be rebuilt from the feed.
2. **The feed is a transactional outbox, not an event bus.** Domain events are at-least-once and replayed, so subscribing to them directly would double count. The outbox row is written in the save that commits the fact, at the point where the transition happens once (inside its "already transitioned" guard):
   - the invoice settle in the fulfill's save
   - a payment's completion
   - a circuit's resolution
   - a funding's lock
   - a close's classification
   - an output's resolution
   - a wallet movement, in the block's unit of work

   Each row has an **`EventKey`** derived from the fact alone, such as `inv:{hash}:settled`, `fwd:{inChan}:{inHtlc}:settled` or `wallet:{txid}:{vout}:in`. There is deliberately **no unique constraint** on the key: a constraint violation would fail the core save the row rides in, and an accounting row must never cost a fulfill or a block. If a fact is ever written twice, the sealer keeps the first row and marks the others `Duplicate` (never read by the books, metered as a bug signal).
3. **Readers follow commit order, not insert order.** Identity sequences can commit out of order across concurrent saves, and a cursor on them would skip a late commit. So:
   - Rows are inserted with a null `LedgerSeq`.
   - A single background `EventSealer` assigns a dense `LedgerSeq` in commit order, plus `PrevHash`/`Hash`, a SHA-256 chain over the canonical row bytes.
   - Consumers read `LedgerSeq > cursor`.

   The hash chain gives an audit trail without putting it on the hot path.
4. **Reorgs are corrected with compensating entries, never deletions.**
   - On-chain events carry the block height.
   - `OnBlockDisconnected` writes `*:reversed` events with their own keys.
   - A `Finality` field (`Unconfirmed` / `Confirmed` / `Irrevocable` at 100 blocks or BOLT 5 irrevocable) lets the books post at confirmation, and lets the financial profile choose to wait instead (D-A4).

**Code placement** follows the repo's layering. No new project and no new dependency direction is needed:

| Layer | Contents |
|---|---|
| Domain `Accounting/` | events, kinds, key builders, chart of accounts, posting rules, lot engine, report models, ports. All pure. |
| Application `Accounting/` | writers' helpers, `EventSealer`, snapshot timer, backfill, projector, reports, reconcile, valuation job |
| Persistence | entities and repositories off `IUnitOfWork`, migrations for all three providers |
| Infrastructure | `IPriceSource` implementations (CSV, HTTP). The HTTP client goes through Tor in `TorOnly`, like the fee client (NL-578). |
| Daemon / Client / Transport.Ipc | the IPC commands of §7 |

## 4. Event catalogue (core, A1)

Amounts are signed **msat from our point of view**: positive means more of our money, negative less. Fee fields are always positive.

Every row stores these fields:

- `Kind`, `EventKey`, `OccurredAt` (UTC, sub-second), `BlockHeight`
- `ChannelId`, `Scid`, `PaymentHash`, `TxId`, `Vout`, `Counterparty` (node id)
- `AmountMsat`, `FeeMsat`, `Finality`
- `Payload`: versioned JSON with the kind's details
- `LedgerSeq`, `PrevHash`, `Hash`, filled in by the sealer

| Kind | Written where (in which save) | Amount / fee | Notes |
|---|---|---|---|
| `InvoiceSettled` | `HtlcSwitch.SettleInvoiceAsync` (the fulfill save) | +received; fee 0 | Covers bolt11, keysend (custom records in the payload) and BOLT 12 (offer id, payer note, quantity). MPP is one event per invoice. If the payer overpaid, the invoice amount is recorded too. |
| `PaymentSucceeded` | `PaymentService` completion save | −(amount+fee); fee = route fee | One per payment, not per part. Covers destination, invoice description and hash, offer id, and keysend. A payment to our own invoice is flagged `SelfPayment` (rebalance). |
| `PaymentFailed` | `PaymentService` | 0 | Informational only; it never posts money. |
| `ForwardSettled` | Circuit → Fulfilled (the incoming fulfill's save) | +fee = in − out | Records the in and out channels and amounts. |
| `ForwardLostOnchain` | Resolver path where we paid downstream on chain but lost upstream, or the reverse | ± | Loss event. Rare but real. |
| `ChannelFunded` | Funding confirmation (`ChannelFundings` lock save) | −our contribution; fee = our share of the funding tx fee | Records push (NL-605), dual-fund shares, public/private, and whether it is an anchors channel. The contribution moves wallet → channel; the push is a separate `PushSent`/`PushReceived`. |
| `SpliceLocked` | `IChannelFundingDbRepository.ApplyLockAsync` save | ±delta; fee = our share | Splice in and out. RBF siblings that never lock emit nothing except the wallet release. |
| `ChannelClosedMutual` | Close tx confirmation | channel → wallet; fee = closing fee if we pay it | Fees are settled on the closing tx, not on the commitment. |
| `ChannelForceClosed` | `OnchainChannelWatcher` classification save | moves the channel balance to `pending-onchain` | `Kind`: Local, Remote, Revoked or Future. Includes trimmed and dust HTLCs lost to fees. |
| `OutputResolved` | `OnchainResolutionExecutor` save | pending-onchain → wallet; fee | Records the descriptor (to_local, HTLC timeout or success, claim, sweep, anchor) and maturity. An HTLC output settles its in-flight HTLC as fulfilled or timed out. |
| `PenaltyClaimed` / `BreachLoss` | `RevokedCommitResolver` | +gain / −loss | |
| `AnchorCpfpFee`, `SweepFeeBump` | `AnchorCpfpService`, `SweepScheduler`, on confirmation | fee only | Only the confirmed replacement counts; replaced rows emit nothing. |
| `WalletReceived` | Chain monitor wallet staging (block UoW) | +amount | External deposits only. Change outputs and close outputs are linked to their spend or close event instead of being counted twice (`Source` in the payload). |
| `WalletSent` | `WalletSpendService` row, on confirmation | −amount; fee | `withdraw`. |
| `*Reversed` | `OnBlockDisconnected` | the negation | Keyed `{originalKey}:rev:{height}`. |
| `Snapshot` | Hourly or daily timer, and on demand | — | Per-channel local, remote and in-flight balances, plus wallet, pending sweeps and the anchors reserve. The books reconcile against it (§5). |

**In-flight HTLCs** are not booked by default. Money is booked when it becomes irrevocably ours or stops being ours, which is at settle or fulfill. The snapshot reports in-flight amounts, so the balance sheet can still show an "in transit" line; the risk-weighted view is a financial-profile report (§6.2).

**Backfill (as built, A1-T6: a cutover, not a reconstruction).** Wallet history cannot be rebuilt (a spent UTXO row is deleted), so the feed starts with opening balances at a cutover point, and the history before it is written as memo events. `Application/Accounting/Backfill/AccountingBackfillService` (Domain port `IAccountingBackfill`, registered by `AddAccountingServices`):

- **Cutover**, once per node, in `NltgDaemonService.ExecuteAsync` (and `NLightningTestNode.StartAsync`) before `PeerManager.StartAsync` and the chain monitor, so no peer message or block changes the database while it reads. It reads the database through a scoped unit of work (never memory) and writes in one save, flagged `Backfilled`, `Final`, kind `OpeningBalance`, keys `AccountingEventKeys.OpeningBalance(bucket)`:
  - `channel:{channelId}`: every channel past its funding confirmation (ReadyForUs..Closing or Failed, short channel id set) that is not Closed/Stale/OnchainResolving, at our **gross** local balance (NL-062). A channel still waiting for its funding gets nothing: its live `ChannelFunded` moves the contribution later, and the wallet opening still holds the inputs, which `WalletOutputSpent` removes at confirmation.
  - `wallet`: every `Utxos` row (locked ones included), details `utxoCount`, `lockedMsat`.
  - `pending:{channelId}`: per channel in `OnchainResolving`, the value of its unresolved (Pending/Waiting/Broadcast) outputs that count the way the resolution writer counts them (a commitment output that `OnchainAccounting.CountsAtClose`, any other `DelayedToLocal` output). The same save writes a synthetic `ChannelForceClosed` of 0 under the live key `ChannelForceClosed(channelId, closeTxId)`, details `countedVouts` (those commitment vouts, the writer's format), `openingBalance=true`, `closeKind`, `pendingMsat=0`, so every later `OutputResolved` takes its value out of the bucket the opening filled (proof: `OnchainAccountingTests.Given_ACloseFromBeforeTheFeed_*`).
  - Last, the marker `open:cutover` (`AccountingEventKeys.Cutover()`, amount 0, details `cutoverAt`, `blockHeight` (the `BlockchainStates` height), the counts and totals). Once it exists a start costs one indexed `EventKey` lookup.
  - When the feed already has events but no marker (a node that ran the feed before the backfill existed), no opening balance is written (it would count those facts twice): only the marker with `skippedOpening=true`, and a warning. A failed cutover is logged and the daemon starts anyway, but the backfill holds the feed's gate (`Domain/Accounting/Services/AccountingFeedGate`, NL-619): the feed's repository drops every live event (only `open:` keys pass) and the memo pass does not start, so the next start's cutover finds an empty feed and writes the opening balances as of that moment, which contain everything that happened meanwhile.
- **Memo history**, in the background after the chain monitor started (`StartMemoBackfill`/`StopAsync`, cancellable): settled invoices (`SettledAt` at or before the cutover; `IInvoiceDbRepository.ListSettledAsync`, a stable set), succeeded and failed payments (`CompletedAt` at or before it), fulfilled forwards (`ResolvedAt` at or before it), `ChannelFunded` (with its push) of every channel that got an opening balance or was Closed at the cutover (never a spliced one: its funding outpoint is the splice's), and `ChannelClosedMutual` of every channel Closed at the cutover with a closing transaction and no `ChannelCloses` row. Each built by the live writer's helper (`PaymentAccountingEvents`, `ChannelAccountingEvents.BuildChannelFundedAsync`/`BuildMutualClose`) under the **same key and kind**, flagged `Backfilled`, details `memo=true` (and without the details the backfill cannot know, such as `parts`): P&L statistics the books never apply to a bucket. Pages of 500 source rows, one scope and save per page; a key already in the feed is skipped (a live event, or a page saved before a cancellation), so the pass resumes at the next start; the marker `open:memo:complete` ends it for good. A memo source added after that first pass has its own marker `open:memo:complete:<source>` (`AccountingBackfillService.LaterMemoSources`, append-only; NL-682): a node whose first pass completed before the source existed runs that source alone at its next start, once (one indexed key lookup per marker at every start); a fresh node's first pass covers it and writes both markers. The first such source is `forceclose` (NL-624's force closes from before the cutover); its `ChannelForceClosed` also carries `capacitySat` and `openedAtHeight` (the original funding's block, from the initial `ChannelFundings` row when the channel was spliced), which the channels report takes when no `ChannelFunded` gives them. Fees come from what the tables store (`BroadcastTransactions.FeeSat`, interactive-tx rows); nothing is recomputed from bitcoind.
- Metrics: `nlightning.accounting.backfill.opening` (tag `bucket`) and `nlightning.accounting.backfill.memo` (tag `source`); logs at the cutover (counts and totals per bucket kind), the skip, each memo page (debug) and the end.

## 5. Correctness invariant (the proof for every phase)

> For every asset bucket (each channel's local balance, wallet, pending-onchain), the sum of posted events equals the live snapshot, to the msat, at every quiescent point.

**In tests**, it runs as an assertion in:

- the in-process harnesses: `TwoNodeHarness`, `ThreeNodeSwitchTests` with SQLite restarts, `PaymentHarnessTests`
- a seeded random-workload simulator: open, pay, receive, forward, MPP, fail, restart mid-flow, splice, mutual close
- the Docker on-chain suite: force close, HTLC on chain, penalty, anchors CPFP, reorg

**In production**, the books run the same reconciliation on every `Snapshot`. A mismatch is:

- metered as `nlightning.accounting.reconcile.drift_msat`
- logged with the bucket
- shown by `nltg accounting reconcile`

A mismatch is a bug, never an adjustment.

## 6. The books

### 6.1 Operational profile (default): accounts and posting rules (A2 as designed)

**Accounts.** One aggregate account per bucket; per-channel views come from the events' details (reports), not from sub-accounts, because a multi-part payment or invoice spans channels in one event. Names are hledger-style and configurable per role (`Accounting:AccountNames:<Role>`):

| Role | Default name | Kind |
|---|---|---|
| Channels | `assets:lightning:channels` | asset: our gross local balance of every channel past funding confirmation |
| Pending | `assets:onchain:pending` | asset: force-closed funds not yet in the wallet |
| Wallet | `assets:onchain:wallet` | asset: confirmed wallet outputs |
| Clearing | `assets:onchain:clearing` | asset, nets to zero: see below |
| Received | `income:lightning:received` | income |
| Routing | `income:lightning:routing` | income |
| PushReceived | `income:lightning:push` | income |
| OnchainGain | `income:onchain:gain` | income (penalties, outputs of the peer we claimed) |
| Sent | `expenses:lightning:sent` | expense |
| RoutingFees | `expenses:lightning:routing-fees` | expense |
| Rebalance | `expenses:lightning:rebalance` | expense (self-payments) |
| PushSent | `expenses:lightning:push` | expense |
| FeeFunding, FeeSplice, FeeClose, FeeCommitment, FeeSweep, FeeCpfp, FeeWithdraw | `expenses:onchain:fees:{funding,splice,close,commitment,sweep,cpfp,withdraw}` | expense |
| LossOnchain | `expenses:losses:onchain` | expense (trimmed/dust value, given-up outputs, breaches, forwards lost) |
| TransfersIn / TransfersOut | `equity:transfers:in` / `equity:transfers:out` | equity (external deposits / withdrawals) |
| Opening | `equity:opening-balances` | equity (cutover) |

**The clearing account.** The wallet events (`WalletReceived`, `WalletOutputSpent`) are the only postings to `Wallet`, at the UTXO level. Every other event that moves value to or from the wallet posts that side to `Clearing` instead. A funding then posts `Clearing −(contribution + fee)`, and the wallet events of the same transaction post `Clearing +spent −change`, which nets to zero. A non-zero clearing balance after the confirmations settle is a reconcile finding (an output the books cannot see, such as a splice-out to an address outside the wallet).

**Rules** (amounts are the event's msat; Dr = debit, positive; Cr = credit, negative; every entry sums to zero):

| Event | Postings |
|---|---|
| `InvoiceSettled` | Dr Channels a; Cr Received a (Cr Rebalance a when `selfPayment`: our own invoice paid by our rebalance, NL-609) |
| `PaymentSucceeded` (amount a = −AmountMsat − fee) | Cr Channels (a + fee); Dr Sent a; Dr RoutingFees fee. With `selfPayment` (a rebalance, NL-609): Cr Channels (a + fee); Dr Rebalance (a + fee), so with its `InvoiceSettled` only the route fee stays in Rebalance |
| `PaymentFailed` | none |
| `ForwardSettled` | Dr Channels fee; Cr Routing fee |
| `ForwardLostOnchain` | Cr Channels v; Dr LossOnchain v |
| `ChannelFunded` | Dr Channels c; Dr FeeFunding fee; Cr Clearing (c + fee) |
| `PushSent` / `PushReceived` | Cr Channels p; Dr PushSent p / Dr Channels p; Cr PushReceived p |
| `SpliceLocked` (delta d, fee already out of d) | Dr Channels d; Dr FeeSplice fee; Cr Clearing (d + fee) |
| `ChannelClosedMutual` (AmountMsat = −balance) | Cr Channels balance; Dr FeeClose fee; Dr Clearing (balance − fee) |
| `ChannelForceClosed` (AmountMsat = −B; details `pendingMsat`, `lostMsat`) | Cr Channels B; Dr Pending pendingMsat; Dr FeeCommitment fee; Dr LossOnchain lostMsat (Cr OnchainGain when negative). An event flagged `openingBalance` (backfill) posts nothing. |
| `OutputResolved` / `PenaltyClaimed` / `BreachLoss` / `OutputIgnored` | from the details: Cr Pending `pendingOutMsat`; Dr Pending `pendingInMsat`; Dr Clearing `walletMsat`; Dr FeeSweep fee. The difference d = debits − credits balances the entry: d > 0 → Cr Channels when `valueBookedBy` is set (incoming HTLC whose income an invoice or forward already booked), else Cr OnchainGain; d < 0 → Dr Channels when `valueBookedBy` is set (our offered HTLC the peer claimed: its payment or forward already took it out of Channels), else Dr LossOnchain. A row merged into our CPFP child (note "merged") posts its d to Clearing. |
| `AnchorCpfpFee` | Dr FeeCpfp fee; Cr Clearing fee |
| `SweepFeeBump` | none (the resolution's fee already holds the whole fee, `includesFeeBump`); fee breakdown report only |
| `WalletReceived` | Dr Wallet v; Cr TransfersIn v when `source=external`, else Cr Clearing v |
| `WalletOutputSpent` | Cr Wallet v; Dr Clearing v |
| `WalletSent` (external amount x) | Dr TransfersOut x; Dr FeeWithdraw fee; Cr Clearing (x + fee) |
| `OpeningBalance` | Dr the bucket's account v; Cr Opening v (marker and memo rows: none) |
| any event with `memo=true` | none (statistics only) |
| `Reversal` | the exact negation of the postings of the entry it reverses (`reverses` key); none if that entry posted nothing |

**Reconcile** (every `SnapshotInterval` and on demand): `Channels` = Σ gross local balances of channels past funding confirmation and not on chain; `Pending` = Σ of our unresolved counted outputs (the snapshot applies `OnchainAccounting.CountsAtClose`, the rule of the close's `countedVouts`; the outputs booked only once claimed are reported apart as `PendingUncountedMsat`, NL-618); `Wallet` = the confirmed wallet balance; `Clearing` = 0 (allowing the outputs of unconfirmed transactions). Differences are reported per bucket and metered, never posted.

**Reports**, each as IPC plus CLI output and CSV:

- balance sheet (account balances at a time), income statement by period
- per-channel view from the event details: routing earned in and out, rebalance cost, open, splice and close fees, lifetime yield on capital
- per-peer summary, fee breakdown (including `SweepFeeBump`), plain register with filters

Commands: `export --format hledger|beancount|csv`, `reconcile`, `rebuild`.

### 6.2 Financial profile (opt-in, `Accounting:Profile=Financial`)

- **Labels and tags at the source (core change).** Optional `--label` and `--tag k=v` on `createinvoice`, `payinvoice`, `keysend`, `createoffer`, `payoffer`, `withdraw` and `openchannel`, persisted on the row and copied into the event payload.
- **Classification rules.** First match wins, on kind, tag, label regex, counterparty or offer id. Each rule maps to accounts, for example `tag:customer=* → income:sales` or `label:~/payroll/ → expenses:payroll`.
  - Unmatched entries go to `*:unclassified` and are listed for review.
  - **Manual reclassification** is stored as an override keyed by `EventKey`. It is the only state the books cannot rebuild from the feed, so it is included in the node's database backups.
- **Fiat valuation.**
  - Each posting gets the BTC price at `OccurredAt` in the base currency (D-A1).
  - Prices come through `IPriceSource`: an operator CSV, a cached HTTP provider, or none.
  - The price source never blocks the node. Missing prices are filled in later by a back-valuation job, and reports flag unvalued rows.
  - The price is stored with the entry, so reports are reproducible.
- **Cost basis.**
  - A lot is created for each sat acquisition: `WalletReceived` at fiat cost, and `InvoiceSettled` as income at fair value.
  - Lots are relieved on disposals (payments, fees, withdrawals) under FIFO, LIFO, HIFO or specific-id (D-A2).
  - This yields realized and unrealized gains; hledger output carries `@@` costs and `P` directives.
  - Moving sats between our own wallet, channel and pending buckets is a **transfer, not a disposal**; only the fee disposes. This is the most common mistake in crypto accounting tools.
- **Period close and lock.** `nltg accounting close 2026-09` locks the period. Later corrections post into the open period as adjustments and are never back-dated.
- **Audit.** The hash-chained feed and signed period-close digests, with an optional OpenTimestamps anchor of each period root later. `nltg accounting verify` re-walks the chain. Snapshot-based reports cover settlement states and risk-weighted capital (an incoming HTLC with a known preimage counts at near-full value, an outgoing in-flight HTLC at a discount, and the remote balance is excluded).

### 6.3 Storage

The journal tables live in the **node's own `NLightningDbContext`**, with ordinary migrations for all three providers. This gives one database, one backup and no extra connection string. The tables:

- `AccountingEvents` (the core feed)
- `AccountingEntries` / `AccountingPostings`
- `AccountingCursor`
- `AccountingOverrides`
- `AccountingLots`
- `AccountingPrices`
- `AccountingPeriods`
- `AccountingRules` (D-A10)

The journal is a projection of the feed plus the overrides, so `nltg accounting rebuild` can always regenerate it. The projector saves its cursor (`LastLedgerSeq`) in the same save as the entries it produced, which makes the projection exactly-once.

## 7. Wiring, flag and IPC

- **Flag.** `Accounting:Enabled` (unset = on) controls the books: projector, reports, valuation and lots. `false` stops them; the tables and the cursor stay, so turning the books back on catches up from the cursor. `Accounting:Profile` is `Operational` (default) or `Financial`.

  The **feed itself is always on**, because the books cannot be rebuilt for a period the feed did not record (D-A5).
- **Registration.** `AddAccountingServices(configuration)` sits in the Application layer's `DependencyInjection.cs`, is called from `AddNltgNodeServices`, and is therefore also in the Docker test node (NL-156).
  - The sealer, the snapshot timer and the projector are hosted services. They start after `NltgDaemonService` has loaded the channels and started the chain monitor, and stop before it.
  - As built (A1-T4/T5): `AddAccountingServices()` (no configuration argument) is called by `AddApplicationServices`, and `AddNltgNodeServices` binds `AccountingOptions` from `Accounting` (`SealInterval` 5 s, `SealBatchSize` 500, `SnapshotInterval` 1 h for the books, `Enabled` for the books) and registers IPC 41/42 (`AddAccountingIpcServices`). The sealer is a singleton timer loop, not a hosted service: `NltgDaemonService` (and the Docker test node) call `Start()` after the chain monitor and `StopAsync()` before it.
  - An exception in the books is logged and metered and stops only the books, never the node. The feed writers are part of the core saves and fail with them.
- **IPC (proposed numbers; next free is 41):**

| # | Command | What it does |
|---|---|---|
| 41 | `listaccountingevents` | the raw feed, paged by `LedgerSeq` (built, A1-T5): `[--after <seq>] [--limit <n>] [--kind <kind>[,...]] [--channel <id or scid>] [--since <time>] [--until <time>]`; the daemon seals what was committed first (`SealNowAsync`), then answers the page, `NextAfter` (the next `--after`), `HasMore` and the sealed tip; each event carries its key, kind, time (ms), block, signed amount, fee, channel and scid, payment hash, txid:vout, counterparty, finality, flags, details and chain hash |
| 42 | `accountingsnapshot` | the live balances by bucket (built, A1-T5): one bucket per channel (state, peer, capacity, gross local and remote balance, in-flight HTLCs each way; for a force-closed channel our unspent outputs as pending on chain, HTLC outputs apart) and the wallet (confirmed, unconfirmed, locked), with totals; not persisted |
| 43 | `accountingreport` | built (A2): `nltg accounting report <balance|income|channels|peers|fees|register>` with period, channel, account, kind and paging filters; projects what is sealed first; refuses when the books are off; A3-T6 adds `--book financial` (`balance`, `income`, `register`, `gains`, `unrealized`, `lots`, `unvalued`, `unclassified`, `risk`) with `--currency`, `--price` and `--by` |
| 44 | `accountingexport` | built (A2): hledger, beancount or CSV in exact msat, streamed to the client in pages by ledger sequence; `nltg accounting export --format ... [--output f]` writes the file on the client side (the daemon writes no file); A3-T6 adds `--book financial [--currency]` (hledger `@@` and `P`, beancount cost and `price`, CSV with fiat columns) |
| 45 | `accountingadmin` | built (A2): `nltg accounting reconcile|rebuild|verify` (verify walks the feed's hash chain and reports the first break; works with the books off); `close` and `classify` come with A3 (A3-T3 classify 5, A3-T2 prices 10-12, A3-T4 `lots import` 13, A3-T5 close 20-22) |

  The client gets the `nltg accounting <sub>` verb family. Every command uses the existing cookie authentication.
- **The old plugin stub is deleted (NL-151).** This removes:
  - `NLightning.Daemon.Plugins` (project, sln entries, Daemon reference)
  - `PluginLoaderService` and `PluginEntry`
  - `IControlClient`
  - the other dead types NL-151 lists

  A later out-of-process extension story (a read-only feed consumer over IPC) needs no in-process loader, and the IPC feed (41) already serves it.

## 8. Decisions (owner)

| ID | Question | Recommendation |
|---|---|---|
| D-A1 | Base fiat currency and price source | USD. Operator CSV plus one cached HTTP source. Never on the hot path, and through Tor in `TorOnly`. |
| D-A2 | Default cost-basis method | FIFO, which is the most widely accepted for tax. Configurable per node. |
| D-A3 | Storage | The node DB and `NLightningDbContext`, with normal migrations (§6.3). |
| D-A4 | Post on-chain events at 1 confirmation (with reversal) or at N | 1 confirmation with compensating reversals. Financial reports can filter on `Finality`. |
| D-A5 | Defaults | The feed is always on, on every network, mainnet included. The books are on by default with `Operational`; `Accounting:Enabled=false` turns them off. `Financial` is opt-in. |
| D-A6 | Export delivery | Stream to the client by default. The daemon writes to disk only under the config directory, so no arbitrary paths. |
| D-A7 | How the financial books relate to the operational ones | A second book next to the operational one, never instead of it: `Profile=Financial` adds a financial projector that reads the **operational entries** in ledger order (not the raw feed) and writes its own entries and postings, told apart by a `Book` column (0 operational, 1 financial) with its own cursor row. Reconcile keeps using the operational book. |
| D-A8 | A locked period against `rebuild` | A closed period is never rewritten. The financial rebuild starts from the state stored at the last close (balances, open lots, digest) and replays only the open period; the operational rebuild is unchanged (it has no judgement in it). Anything that would change a closed period (a new override, a rule change, a price filled in later) posts an **adjustment** in the open period, dated now. |
| D-A9 | Cost basis of the opening balances (the cutover) | Opening lots at the BTC price of the cutover time, flagged `basisEstimated=true`. Before the first period close the operator may replace them with `nltg accounting lots import <csv>` (date, sats, fiat cost); after it, only through an adjustment. |
| D-A10 | Classification rules: format and storage | A database table `AccountingRules`, managed over IPC 45 (`classify rule add/list/remove/test`), ordered by priority, first match wins. Match on kind, label regex (`RegexOptions.NonBacktracking`, 100 ms timeout), tag key and value glob, counterparty, offer id, channel; the target is one financial account name. In the database so it is in the node's backups and changes without a restart. |
| D-A11 | Price sources and precision | An operator CSV (`<configPath>/prices.csv`, `unixSeconds,price`) and one HTTP source, mempool.space's historical price API (`/api/v1/historical-price?currency=USD&timestamp=t`, the host we already use for fees, through Tor in `TorOnly`), queried only by the back-valuation job. A posting takes the nearest price at or before `OccurredAt` within `Accounting:Prices:MaxAge` (26 h), else it stays unvalued. Prices and fiat amounts are `decimal` (8 places stored, rounded to the currency's minor unit only in reports). |
| D-A12 | Which events open and close lots | Acquisitions open a lot at the fiat value of the time: a deposit (`WalletReceived` from outside), and income at fair value (`InvoiceSettled`, `PushReceived`, routing fees earned, `OnchainGain`). Disposals relieve lots: payments (amount and fee), every fee kind, withdrawals to outside, `PushSent`, losses (`LossOnchain`, breach losses at zero proceeds). Everything between our own wallet, channels, pending and clearing is a transfer: the lot moves, nothing is realized, only its fee disposes. Methods FIFO, LIFO, HIFO; specific-id is left out of A3. |
| D-A13 | Signing the period close | The close digest is SHA-256 over the period, its last `LedgerSeq`, the feed's chain hash at that sequence and the hashes of the period's financial entries and of the open lots at the close; it is signed with the node key (`ILightningSigner.SignNodeMessage`), so anyone with the node id can check it. An OpenTimestamps anchor stays a later option. |

## 9. Phases

Each phase ends with its proof, in the repo's usual style.

### A0: decisions and ledger

- The owner answers §8.
- Ledger entries are already open: NL-602 (epic), NL-603..NL-605 (data gaps), and NL-151 (stub deletion).

### A1: core feed

Tasks:

- **T1:** Domain `Accounting/`: `AccountingEvent`, `AccountingEventKind`, key builders and the `IAccountingEventWriter` port. Persistence: `AccountingEvents` and migration `AddAccountingEvents` for all three providers.
- **T2:** Writers in the saves listed in §4: switch, payments, circuits, fundings and splices, close, watcher and resolver executor, sweep and CPFP confirmation, wallet monitor, withdraw.
- **T3:** Data gaps:
  - NL-605: push amount.
  - NL-604: `BroadcastTransactions.FeeSat` and `OurInputSat`, computed when we build the tx (we know our inputs).
  - NL-603: a `WalletTransactions` history instead of delete-only UTXO rows.
- **T4:** `EventSealer` (dense `LedgerSeq`, hash chain), the `Snapshot` timer, and reorg reversals.
- **T5:** IPC 41 and 42, plus the client verbs.
- **T6:** One-shot backfill.

Proofs:

- Replay and idempotency tests covering startup replay, link-up replay and per-block re-resolution, with no duplicates.
- The §5 invariant in the harnesses and in a new `Long` simulator.
- Docker on-chain: force close, penalty and reorg.
- Round trips on all three providers.

### A2: books, operational profile

Tasks:

- Projector with an exactly-once cursor.
- Default chart.
- Reports (§6.1) and exports.
- IPC 43-45 (reconcile, rebuild).
- The `Accounting:Enabled` flag.
- Deletion of the plugin stub (NL-151).

Proofs:

- Golden-file hledger and beancount exports from the simulator, checked by `hledger check` and `bean-check` in CI where the tools are available.
- A rebuild equals the incremental projection.
- Reconcile is clean across the Docker ABCD and on-chain suites.
- Flag off, then on, catches up.

### A3: financial profile

**Scope status (2026-10-02):** built (A3-T0..T7, see "A3 record" after A3-T7); originally broken down into tasks for hand-off. Owner decisions D-A1, D-A2 and D-A7..D-A13 (§8) hold at their recommendations unless the owner says otherwise. `verify` of the feed's hash chain was built in A2 (IPC 45); A3 adds only the signed period digests. Cite NL-602 in commits; open a new `NL-###` for each bug or gap found, as A1/A2 did (the accounting follow-ups still open are NL-606..NL-613 and NL-620: read them first, several touch what A3 values).

**Base:** branch `claude/youthful-hamilton-x4ngo7` at or after `79ed8b1a` (A1 + A2 + NL-615..NL-619). Standard cycle: net10.0 tests, no Docker unless a task says so, `dotnet format` clean.

**Order:** A3-T0 first (alone: it owns every schema change, so the lanes never fight over the three model snapshots). Then A3-T1, A3-T2 and A3-T3 in parallel. A3-T4 after T2 and T3. Then A3-T5 and A3-T6 in parallel. A3-T7 last.

#### A3-T0: schema (one migration, all three providers)

Migration `AddAccountingFinancial` (`./scripts/add_migration.sh`, Postgres/Sqlite/SqlServer committed together, `HasPendingModelChanges` false for all three):

- **Labels and tags:** nullable `Label` (UTF-8, at most 256 bytes) and `Tags` (text, a canonical `k=v` list, at most 1 KiB) on `Invoices`, `Payments`, `Offers`, `Channels` and `BroadcastTransactions` (the withdraw row). Domain models and repositories carry them; `ChannelRoundTripTests` and the payment/invoice round trips extend to them.
- **`AccountingEntries`/`AccountingPostings`:** a `Book` column (byte, 0 operational, default 0 for existing rows) and, on postings, nullable `FiatAmount` (decimal), `FiatCurrency` (3 chars) and `PriceId`. The cursor table gets one row per book.
- **New tables:** `AccountingPrices` (currency, time, price, source, fetched at; unique currency + time), `AccountingRules` (id, priority, match fields of D-A10, target account, enabled, created at), `AccountingOverrides` (event key → target account, note, created at; unique key), `AccountingLots` (id, acquired at, source entry, sats or msat remaining and original, fiat cost, currency, `basisEstimated`, closed by period), `AccountingLotReliefs` (lot, disposing entry, msat, fiat cost relieved, proceeds), `AccountingPeriods` (period id `YYYY-MM` or a date range, closed at, last `LedgerSeq`, chain hash, digest, signature, state).
- Repositories on `IUnitOfWork` with a throwing default (as `AccountingBooksDbRepository`), and the `IUnitOfWork` wrappers in the tests (`CrashingUnitOfWork`, `HookedUnitOfWork`, the harness stores) forward them.
- Proof: seeded-migration round trip on SQLite (`Integration.Tests/Persistence`), plus the Postgres one in `Docker/PostgresTests` when Docker is run.

#### A3-T1: labels and tags at the source

- IPC: the next free key of each request (today `CreateInvoiceIpcRequest` 3, `PayInvoiceIpcRequest` 5, `KeysendIpcRequest` 5, `PayOfferIpcRequest` 7, `WithdrawIpcRequest` 3, `OpenChannelIpcRequest` 7, `CreateOfferIpcRequest` 6) for `Label` and `Tags`; client flags `--label <text>` and repeatable `--tag k=v` on `createinvoice`, `payinvoice`, `keysend`, `createoffer`, `payoffer`, `withdraw`, `openchannel`. Validation (shared, Domain): label at most 256 UTF-8 bytes, no control characters; at most 16 tags, key `[a-z0-9_.-]{1,32}`, value at most 128 bytes.
- The services persist them on the row (`InvoiceService`, `PaymentService` incl. keysend and offers, `OfferService`, the open path, `WalletSpendService`), and the list commands show them.
- The writers copy them into the event's details (`label`, `tag.<k>`; constants in `AccountingDetailKeys`): `InvoiceSettled` from the invoice (a BOLT 12 invoice from its offer), `PaymentSucceeded`/`PaymentFailed` from the payment, `ChannelFunded` from the channel, `WalletSent` from the broadcast row.
- Proof: IPC round trip with labels and tags (Daemon.Tests), and the events carrying them on SQLite (Integration).
- **As built (lane acct-a3-t1-labels):** shared rules `Domain/Accounting/Labels/SourceLabelRules` + `SourceLabels` (canonical tags `k=v` per line, sorted by key; control characters refused in labels and values; a key at most once; the 1 KiB list limit checked too), detail keys `label` and `tag.<k>` (`AccountingDetailKeys.Label`/`TagPrefix`, read back with `SourceLabels.FromDetails`). IPC keys as listed above (`Label` then `Tags`, a `List<string>` of `k=v`); responses `InvoiceInfo` 13/14, `PaymentInfo` 16/17, `OfferInfo` 14/15, `ChannelInfo` 25/26. A BOLT 12 invoice copies its offer's label and tags when it is issued (the writer reads the invoice row). The CLI takes `--label`/`--tag` out before each command's parser (`Client/Handlers/LabelOptions`) and checks them with the same Domain rules; the daemon checks again (`SourceLabelsGuard`) before any service call. Proofs: `Daemon.Tests/Ipc/Handlers/SourceLabelsIpcRoundTripTests`, `Client/LabelOptionsTests`; on SQLite `Integration/Persistence/AccountingBackfillTests` (invoice and payments), `ChannelFundedAccountingPersistenceTests` (reloaded channel), `ChainMonitorAccountingTests` (withdrawal), and the harnesses `OfferHarnessTests` and `DualFundHarnessTests` (SQLite nodes).

#### A3-T2: prices and back-valuation

- Domain: `IPriceSource` (`GetPriceAsync(currency, time)` → price or null), `AccountingPrice`. Infrastructure.Bitcoin (the HTTP client setup next to the fee service, Tor-aware): `CsvPriceSource` (read once and on change of the file's mtime), `HttpPriceSource` (D-A11), `CompositePriceSource` (CSV first).
- Options `Accounting:Prices` (`Currency` USD, `Source` `Csv`/`Http`/`Both`/`None`, `Url`, `CsvFile`, `MaxAge` 26 h, `FetchInterval`, `MaxFetchesPerRound`).
- `PriceValuationService` (Application, singleton timer like the sealer, started after the projector, never on a hot path): finds financial postings without a price, groups them by hour, fetches each hour once, stores the price, fills `FiatAmount`/`PriceId` in one save per batch. A posting in a closed period is never filled: it raises an adjustment through A3-T5's rule.
- IPC 45: `prices import <csv>` (client reads the file, sends rows), `prices list`, `prices fetch --since`.
- Proof: the nearest-price rule at the `MaxAge` boundary, CSV parse errors reported by line, the HTTP source against a fake handler, back-valuation filling and never touching a closed period, and no request at all with `Source=None`.
- As built (lane acct-a3-t2-prices): Domain `Accounting/Prices/` (`AccountingPriceOptions` bound from `Accounting:Prices`, `IPriceSource`, `AccountingPriceCsv`, `AccountingValuation`), Infrastructure.Bitcoin `Accounting/Prices/` (`CsvPriceSource`, `HttpPriceSource`, `CompositePriceSource`; `AddAccountingPriceSources()` next to `AddFeeServices`, the HTTP client through `TorHttpHandler` and built only when the HTTP source is configured), Application `Accounting/Prices/PriceValuationService` (also `IAccountingPrices`), IPC 45 actions 10-12 (request key 10), client `accounting prices import|list|fetch`. Readings of the plan: `Source` defaults to `Both` (the file first, then mempool.space); a posting waits for the price of its own UTC hour while a fetching source may still answer it, and takes D-A11's nearest price within `MaxAge` once that hour is two hours old (`RecentHourWait`); an imported or fetched time keeps its first price (reproducible reports); `prices fetch` asks at most 744 hours (31 days). The back-valuation holds the books' write lock from its closed-period check to its save (NL-661) and clears `Unvalued` (NL-666). Details: `src/NLightning.Application/CLAUDE.md` "Accounting financial profile: prices and back-valuation".

#### A3-T3: financial chart, classification and overrides

- `Accounting:Profile` (`Operational` default, `Financial`). The financial chart (Domain, configurable names like `AccountNames`): assets as the operational buckets; `income:sales` (default for received payments), `income:routing`, `income:other`; `expenses:fees:*` (one per operational fee role), `expenses:payments` (default for sent payments), `expenses:losses`; `income:unclassified` and `expenses:unclassified`; `equity:opening-balances`; `income:gains:realized`, `expenses:losses:realized`.
- `ClassificationEngine` (pure, Domain): given an operational entry and its event, the first matching enabled rule (D-A10), else an override by event key (overrides win over rules), else the default account of the kind; returns the account and why (rule id, override, default).
- IPC 45: `classify rule add|list|remove|test`, `classify set <event key> <account>`, `classify list --unclassified`.
- Proof: the rule table (each match field, priority, disabled rule, regex timeout), override precedence, and the unclassified listing.
- As built (lane a3-t3): Domain `Accounting/Financial/Classification/` (`FinancialChart`, `ClassificationEngine`, validators), Application `Accounting/Financial/AccountingClassificationService`, IPC 45 action 5 (`AccountingClassifyAction`, request key 2, response key 5), client `accounting classify ...` (plus `rule enable|disable`, `unset` and `list` of the overrides). Readings of §8: overrides win over rules; only the income, expense and transfer lines are classified (fees, assets and opening balances keep their chart account); a rule or override never targets `assets:*`; pushes default to the unclassified accounts; both halves of a self-payment default to `equity:transfers:rebalance` (D-A12); the unclassified listing classifies the projected operational entries with the rules in effect now. Details: `src/NLightning.Domain/CLAUDE.md` and `src/NLightning.Application/CLAUDE.md` "Accounting financial classification"; follow-ups NL-645, NL-646.

#### A3-T4: financial projector, lots and gains

- `FinancialProjector` (Application, next to the operational projector, same cursor pattern, own cursor row): reads operational entries in ledger order, classifies (A3-T3), values (A3-T2 prices, or leaves the posting unvalued), and maintains the lots (D-A12) with `Accounting:CostBasis` (`Fifo` default, `Lifo`, `Hifo`): a transfer moves value without touching lots; a disposal relieves lots in method order and posts realized gain or loss; an acquisition opens a lot. One save per batch: entries, postings, lots, reliefs, cursor.
- Opening balances: lots at the cutover price (D-A9), `basisEstimated`; `lots import` replaces them before the first close.
- Reversals (reorgs): the projection of a reversal in the open period rebuilds the lots of the open period from the period's start state (cheap: one period); a reversal of a closed period's fact is an adjustment (A3-T5).
- Unvalued postings keep their msat and are valued later; gains of a disposal whose lots are unvalued are reported as pending valuation, never as zero.
- Proof (the plan's own): hand-computed fixtures for FIFO, LIFO and HIFO; a deposit, a channel open, payments, a close and a withdrawal where only the fees and the payments dispose; a rebalance (only the fee disposes); a reorg reversal in the open period; rebuild equals incremental; the operational book unchanged by the financial one.
- As built (lane acct-a3-t4): `Application/Accounting/Financial/FinancialBooksProjector` (the `IFinancialBooksProjector` of A3-T5/T6 and the `IAccountingLots` import, registered by `AddFinancialBooksProjector()` before the period services; started after the operational books) over the pure Domain `Accounting/Financial/Lots/` (`FinancialLotPool`, `FinancialEntryPlanner`, `FinancialLotRules`, `AccountingLotCsv`), IPC 45 action 13 `lots import` (request and response key 13) and the client's `accounting lots import <file>`. Readings of the plan: (1) every msat line is valued at the market price of its time, and a fiat-only `assets:cost-basis` line (a new chart account) carries the difference to the lots' cost with the realized gain (`income:gains:realized`) or loss (`expenses:losses:realized`) line, so each entry balances in fiat and the assets' total is the open lots' cost; lots are one node-wide pool, so the single asset accounts hold the market value of their flows (NL-657); (2) a loss disposes at zero proceeds, a rebalance's halves are transfers (only the route fee disposes); (3) the price rule is D-A11's with the back-valuation's own-hour wait; an entry without a usable price is projected unvalued (`PendingValuation`) and the back-valuation, when it values one of its lines, lowers the financial cursor in its save, so the projector rolls the open entries back (`RollbackOpenEntriesAsync`) and projects them again: late prices, a reorg's reversal of an open-period fact and an open-period reclassification (`classify set|unset`, rule changes) all rebuild the open period's lots from that point, never a closed one; (4) a reversal of a closed period's fact posts the fact's financial lines negated at their values in the open period, flagged `Adjustment`; (5) the lot import is refused after the first close and unless the lots hold the opening balances to the sat; it rebuilds the financial book from the start, and the cutover marker's entry moves the assets from the opening market value to the imported cost. Follow-ups NL-657..NL-659. Details: `src/NLightning.Application/CLAUDE.md` "Accounting financial projector, lots and gains".

#### A3-T5: period close, lock and signed digests

- `nltg accounting close <period> [--force]` (IPC 45 `close`): refuses while the period has unvalued postings or unclassified entries unless `--force`, seals and projects first, writes the `AccountingPeriods` row with the digest and signature (D-A13), and marks the period's entries and lots closed. `close list`, `close show <period>`.
- The lock: no write of the operational or financial projector, of an override or of a price changes a closed period; each becomes an adjustment entry in the open period (D-A8). `rebuild --book financial` starts from the last close.
- `verify` (IPC 45) also checks every close: the digest recomputed from the stored entries and lots, the signature against our node id, and the chain hash against the feed.
- Proof: period-lock tests (each late write lands as an adjustment), tampering with a closed entry, a lot or the stored digest detected by `verify`, and a rebuild after a close equal to the incremental books.
- As built (lane acct-a3-t5-close): `Application/Accounting/Financial/AccountingPeriodService` (close, list, show, verify of the closes, financial rebuild; the lock as `IAccountingAdjustmentSink`), Domain `Accounting/Financial/` (`AccountingPeriodRange`, `AccountingClosingState`, `AccountingCloseDigest`, `IFinancialBooksProjector` for A3-T4 with the off `NullFinancialBooksProjector`), IPC 45 actions 20-22 and keys 20-22, `rebuild --book financial`. Readings of the plan: closes are contiguous (the first may start anywhere no financial entry precedes) and close only ended periods; the lock covers every time before the last close's end; the close covers the financial book only (the operational book stays a pure projection of the feed: its late facts reach the financial book as `LateFact` adjustments); adjustments survive a financial rebuild (they are dated when made); the digest also chains the previous close's digest and the node id. Details: `src/NLightning.Application/CLAUDE.md` "Accounting period close".

#### A3-T6: financial reports and exports

- Reports (IPC 43, `--book financial` and `--currency`): balance sheet and income statement in msat and fiat, realized gains by period, unrealized gains at a given price, open lots, unvalued and unclassified rows, and the risk-weighted capital view from the snapshot (§6.2 Audit).
- Exports (IPC 44): hledger with `@@` costs and `P` price directives, beancount with cost `{}` and `price` lines, CSV with fiat columns; golden files checked by `bean-check` (hledger where available).
- Proof: golden files for the A3-T4 fixtures, and report totals equal to the books.
- As built (lane acct-a3-t6-reports): Application `Accounting/Reports/Financial/` (`AccountingFinancialReportService`) and `Accounting/Export/Financial/` (`AccountingFinancialExportService`, the pure `AccountingFinancialExportFormatter` that A3-T4's golden files also use), IPC 43/44 with `--book financial` and `--currency`, the report kinds `balance`, `income`, `register`, `gains` (`--by month|quarter|year|total`, short and long term at 365 days), `unrealized` and `lots` (`--price`, else the latest stored price), `unvalued`, `unclassified` and `risk` (the snapshot through `AccountingRiskCapital` with `AccountingRiskWeights`). Readings: a line not valued in the export's currency is written in msat only with its fiat in a comment; a valued entry whose fiat does not sum to zero gets an `equity:fiat-rounding` residual line in the journals (CSV keeps the book's rows); the reports and exports are refused while the financial book is off. Proof: `FinancialBooksFixture` (a hand-computed book on SQLite), golden `Golden/financial.{journal,beancount,csv}` checked by `hledger check --strict` 1.52 and `bean-check` 3.2 (by hand and by the `Explicit` `Given_TheRealTools_*`) and in CI by `JournalBalanceChecker`. Follow-ups NL-665 (the operational export's `commodity 1 msat` header, fixed: `commodity 1. msat`), NL-667 (fixed with NL-660). Details: `src/NLightning.Application/CLAUDE.md` "Accounting financial reports and exports".

#### A3-T7: integration

- The daemon config template (`Accounting:Profile`, `CostBasis`, `Prices`), `SECURITY_REVIEW.md` (the price source's privacy, regex DoS, the lot import file), this plan's status, `CLAUDE.md` and `src/NLightning.Application/CLAUDE.md`, the ledger (NL-602 to fixed when A3 closes, with its follow-ups), and one Docker smoke on the ABCD or LND suite with `Profile=Financial` (reconcile clean, financial books balanced).
- As built (lane acct-a3-t7, documentation and configuration only):
  - **Config template** (`NodeConfigurationExtensions.CreateDefaultConfigJson`, every network): `Accounting` gains `Profile` `Operational`, `CostBasis` `Fifo` and a `Prices` section with every `AccountingPriceOptions` default (`Currency` USD, `Source` `Both`, `Url` mempool.space's `/api/v1/historical-price`, `CsvFile` `prices.csv` (anchored to the configuration directory since A3-T2), `MaxAge` 26 h, `FetchInterval` 10 min, `MaxFetchesPerRound` 24). The values equal the code defaults, so existing files behave the same; the template only makes them visible. Proof: `Daemon.Tests/Extensions/AccountingConfigTemplateTests` (every `Prices` key is an option at its default and valid; the `Accounting` keys bind at their defaults; the node services bind them).
  - **Security review:** `docs/agents/SECURITY_REVIEW.md` "Accounting" (SR-20..SR-27): what the books store, the price source's privacy (the hours asked mirror the node's active hours; clearnet outside `TorOnly`) and integrity, regex DoS bounds, the prices CSV, the price and lot imports over IPC, the client's export files and the period-close signature. Three are proposed for the ledger (SR-21, SR-22, SR-26).
  - **Docker test node:** `Accounting:Prices:Source=None` in `NLightningTestNode`'s configuration (NL-641), so a Docker run with `Profile=Financial` never asks mempool.space; a test imports its prices.
  - **Client help** lists the financial report kinds, `--book financial` on export and `accounting lots import`.
  - **Docker smoke: not run.** Skipped on 2026-10-02 by owner decision (no Docker in the A3 lanes); the financial books were proven by the non-Docker suites (SQLite harnesses, golden files, IPC round trips). It stays to be run with the next Docker pass: the ABCD or LND suite with `Accounting:Profile=Financial`, prices imported, reconcile clean and the financial book balanced in fiat.

#### A3 record (2026-10-02, `wip/acct-a3`)

- **Built:** T0 `5e3ffb0b` (migration `AddAccountingFinancial`, the only A3 migration); the review fixes NL-621..NL-628 and the rebalance recording NL-609 (merges `42915f80`, `64cb9db0`, `5812ac5e`); T1 `27e8cb95`, T3 `214dd51b` (with the rebalance-fee split), T2 `8b5b0f75`, T5 `d7a9d1f9`, T6 `53daea21` (merges), integration fixes `afce146a`; T4 `77f455b0` (financial projector, lots and gains, IPC 45 action 13); T7 `503b2a6b` (merge `c62d861b`); the final integration's review fixes NL-670 (`94af679d`: one self-payment rule for both halves of a rebalance, we are the payee) and NL-671..NL-673 (`ecd983b1`: late facts flagged `LateFact`, rolled back and staged again with the open period and valued at their own time; a debit of the opening balances, the reversal of a pre-feed wallet receive, relieves the opening lots at cost). No `ClientCommand` was added (IPC 45 actions: classify 5, prices 10-12, lots import 13, close 20-22; next free command stays 46).
- **Tests at the integration** (`afce146a`, net10.0, non-Docker): 12,948 passed, 6 skipped, 0 failed; `HasPendingModelChanges` false on all three providers.
- **Tests at the final integration** (`ecd983b1`, net10.0, non-Docker): 13,033 passed, 6 skipped, 0 failed; `HasPendingModelChanges` false on all three providers; NL-602 closed in the ledger.
- **Deferred (ledger):** NL-645 (offer-id rules cannot match paid offers), NL-657..NL-659 (A3-T4 follow-ups; NL-657: lots are one node-wide pool, so the asset accounts carry the market value of their flows), NL-662 (`ResetToCloseAsync` proven on SQLite only); fixed after A3 on `wip/nl660`: NL-660 (a reclassification of a closed period's entry posts its adjustment in the open period), NL-665 (the operational hledger export declares `commodity 1. msat`, checked by hledger 1.52), NL-667 (the unclassified report leaves out a closed entry reclassified out of the unclassified accounts); NL-681 (a forced close's unvalued line reclassified moves its fiat at the adjustment's time); test flakes NL-620, NL-653. The A1 data follow-ups NL-606..NL-608 and NL-610..NL-613 stay open and are valued as recorded. From the final integration's reviews: NL-674 (the lot kind follows the operational role, not the classified account: a withdrawal classified as a transfer to our own cold storage still disposes; an owner decision on D-A12), NL-675 (the reversal of a closed period's acquisition realizes a gain against older lots), NL-680 (a forced close of a still-unvalued late fact). The A3-T7 Docker smoke is NL-676; the security findings SR-21, SR-22 and SR-26 are NL-677, NL-678 and NL-679.
- **Out of A3 by decision:** specific-id cost basis (D-A12), the OpenTimestamps anchor of a close (D-A13), tax forms and multi-node consolidation (§11).

The order is A1 → A2 → A3. A1-T3 (the data gaps) can start right away, in parallel with the rest of A1.

## 10. Startup and scale

Nothing at startup may cost more as history grows. A node with a million settled HTLCs and a million feed rows starts as fast as a new one.

- **The existing HTLC replay is already bounded.** `ChannelDomainEvents.DerivePending` gets only open HTLC records and settled ones not yet pruned, and `HtlcSwitch` prunes a settled HTLC once both sides are done. The writers of §4 sit at the transitions, not in the replay paths.
- **The sealer** reads only unsealed rows (`LedgerSeq IS NULL`, index on `LedgerSeq`), in batches, and continues from the chain tip (the row with the highest `LedgerSeq`). Its work is proportional to what was written since its last pass.
- **The books** read only events after their cursor (`LedgerSeq > cursor`), in pages, and save the cursor in the same save as the entries they produced.
- **Reconcile** compares the live snapshot with a **running balance per bucket** (each channel, wallet, pending on-chain), updated in the same save as the entries and the cursor. It never sums the history. A full re-sum from the feed runs only on demand (`accounting verify`, `accounting rebuild`).
- **Hash chain verification** (`verify`) walks the chain in pages and can start from a checkpoint (a period-close digest), so it is not run at startup.
- **Backfill** (as built): the cutover's opening balances are one save before the peers start, once per node (a marker lookup afterwards); the memo history runs once, in batches that save their progress and can resume, in the background after startup; it never holds up the node.
- **Proof (A1):** a startup test with a million feed rows and a cursor at the tip shows the startup cost of the feed and the books stays flat. As built for the feed (A1-T6): `Integration.Tests/Persistence/AccountingStartupScaleTests` checks with `EXPLAIN QUERY PLAN` on 50,000 sealed rows (default run) that the marker lookup (`IX_AccountingEvents_EventKey`), the unsealed batch and the chain tip (`IX_AccountingEvents_LedgerSeq`) never scan the table nor sort in a temporary B-tree, and its `Explicit` `Category=Long` test times the startup path (marker lookup + an empty sealer round): 2.0 ms on 10,000 rows, 3.1 ms on 1,000,000 (median of 25, SQLite file, linux-x64). The books' cursor half is A2's.

## 11. Risks and notes

- **Double counting through replays** is the main risk. Mitigation: writers at the transitions (inside their guards), a deterministic `EventKey` with duplicates marked by the sealer, and the §5 invariant everywhere.
- **Commitment fee and reserve.** The funder's balance pays the commitment fee only if the channel force-closes. The operational books keep the gross local balance and book the actual fee at close. A "spendable" view (minus reserve, commitment fee and anchors) is a report column, not a ledger entry.
- **Anchors (330 sat × 2).** The funder's anchors belong to the force-close flow. They are either swept (see NL-601 for the bookkeeping) or lost to dust, and are booked through `OutputResolved` or `expenses:losses:dust`.
- **Security and privacy.** No runtime code loading keeps the AOT build and the attack surface unchanged. The feed holds counterparties, payment hashes and labels, and IPC 41-45 sit behind the existing cookie authentication. Exports stream to the client or land under the config directory only, never at an arbitrary daemon-side path. The price source leaks nothing beyond "this node wants the BTC price at time t" and goes through Tor in `TorOnly`. All of this is in `SECURITY_REVIEW.md` "Accounting" (SR-20..SR-27, written in A3-T7).
- **Performance.** One extra row per money event, inside an existing save. The sealer and projector run off the hot path, and snapshots are hourly by default.
- **Not in scope:** tax-form generation, invoicing or customer management, consolidating several nodes or other money rails (done by an external ledger fed by the exports or the IPC feed), and liquidity-ad lease accounting until leases exist.
