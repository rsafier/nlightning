# Accounting plan (NL-602)

Status: **plan, not started** (2026-10-02, revised the same day). Open owner decisions are marked **D-Ax** in §8.

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
2. **The feed is a transactional outbox, not an event bus.** Domain events are at-least-once and replayed, so subscribing to them directly would double count. The outbox row is written in the save that commits the fact:
   - the invoice settle in the fulfill's save
   - a payment's completion
   - a circuit's resolution
   - a funding's lock
   - a close's classification
   - an output's resolution
   - a wallet movement, in the block's unit of work

   Each row has a **unique `EventKey`**, such as `inv:{hash}:settled`, `fwd:{inChan}:{inHtlc}:settled` or `tx:{txid}:{vout}:wallet-in`. Writers insert if absent inside their save, so a replay hits the unique index and does nothing.
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

**Backfill.** The first start of a node that already has history runs a one-shot backfill:

- It writes events from the existing tables: `Invoices`, `Payments`, `ForwardCircuits`, `Channels`/`ChannelFundings`/`ChannelCloses`, `OutputResolutions` and `BroadcastTransactions`.
- It recomputes fees from the raw txs, using bitcoind `getrawtransaction` for prevouts.
- Backfilled events are flagged `Backfilled`.
- Wallet history from before NL-603 cannot be rebuilt from our tables. The backfill instead posts an `OpeningBalance` event for the wallet at the current snapshot, and says so.

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

### 6.1 Operational profile (default)

The default chart of accounts uses hledger names, and every name is configurable. The commodity is `msat`, displayed as sats.

```
assets:lightning:channel:<scid|short channel id>
assets:onchain:wallet
assets:onchain:pending              ; force-closed funds in timelocks / sweeps
income:lightning:routing            ; ForwardSettled fee
income:lightning:received           ; InvoiceSettled (op profile: one bucket)
income:onchain:penalty
expenses:lightning:sent             ; PaymentSucceeded amount (op profile: one bucket)
expenses:lightning:routing-fees     ; fees we paid; SelfPayment → expenses:lightning:rebalance
expenses:onchain:fees:{funding,splice,close,sweep,cpfp,withdraw}
expenses:lightning:push             ; push we gave (or income: if received)
expenses:losses:{htlc-onchain,breach,dust}
equity:opening-balances             ; backfill / wallet opening
equity:transfers                    ; wallet deposits/withdrawals to external wallets
```

**Reports**, available over IPC, as CLI tables and as CSV:

- balance sheet
- income statement by period
- per-channel P&L: routing earned in and out, rebalance cost, open and close fees, lifetime yield on deployed capital (APR)
- per-peer summary
- fee and cost breakdown
- plain register with filters

Commands: `export --format hledger|beancount|csv`, `reconcile`.

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

The journal is a projection of the feed plus the overrides, so `nltg accounting rebuild` can always regenerate it. The projector saves its cursor (`LastLedgerSeq`) in the same save as the entries it produced, which makes the projection exactly-once.

## 7. Wiring, flag and IPC

- **Flag.** `Accounting:Enabled` (unset = on) controls the books: projector, reports, valuation and lots. `false` stops them; the tables and the cursor stay, so turning the books back on catches up from the cursor. `Accounting:Profile` is `Operational` (default) or `Financial`.

  The **feed itself is always on**, because the books cannot be rebuilt for a period the feed did not record (D-A5).
- **Registration.** `AddAccountingServices(configuration)` sits in the Application layer's `DependencyInjection.cs`, is called from `AddNltgNodeServices`, and is therefore also in the Docker test node (NL-156).
  - The sealer, the snapshot timer and the projector are hosted services. They start after `NltgDaemonService` has loaded the channels and started the chain monitor, and stop before it.
  - An exception in the books is logged and metered and stops only the books, never the node. The feed writers are part of the core saves and fail with them.
- **IPC (proposed numbers; next free is 41):**

| # | Command | What it does |
|---|---|---|
| 41 | `listaccountingevents` | the raw feed, paged by `LedgerSeq`, with filters |
| 42 | `accountingsnapshot` | the live balances by bucket |
| 43 | `accountingreport` | kind = balance, income, channels, peers, fees or register; period; profile |
| 44 | `accountingexport` | hledger, beancount or CSV, written by the daemon to a path under the config directory, or streamed to the client |
| 45 | `accountingadmin` | subcommands reconcile, rebuild, verify, close, classify |

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

Tasks:

- Labels and tags on the IPC commands.
- Classification rules and overrides.
- `IPriceSource` and back-valuation.
- Lots and gains.
- Period close and lock.
- `verify`.

Proofs:

- Hand-computed lot and gain fixtures: FIFO and HIFO, and transfers that are not disposals.
- Period-lock tests.
- Hash-chain tamper tests.

The order is A1 → A2 → A3. A1-T3 (the data gaps) can start right away, in parallel with the rest of A1.

## 10. Risks and notes

- **Double counting through replays** is the main risk. Mitigation: a unique `EventKey` written in the same save, and the §5 invariant everywhere.
- **Commitment fee and reserve.** The funder's balance pays the commitment fee only if the channel force-closes. The operational books keep the gross local balance and book the actual fee at close. A "spendable" view (minus reserve, commitment fee and anchors) is a report column, not a ledger entry.
- **Anchors (330 sat × 2).** The funder's anchors belong to the force-close flow. They are either swept (see NL-601 for the bookkeeping) or lost to dust, and are booked through `OutputResolved` or `expenses:losses:dust`.
- **Security and privacy.** No runtime code loading keeps the AOT build and the attack surface unchanged. The feed holds counterparties, payment hashes and labels, and IPC 41-45 sit behind the existing cookie authentication. Exports stream to the client or land under the config directory only, never at an arbitrary daemon-side path. The price source leaks nothing beyond "this node wants the BTC price at time t" and goes through Tor in `TorOnly`. All of this goes into `SECURITY_REVIEW.md` with A2.
- **Performance.** One extra row per money event, inside an existing save. The sealer and projector run off the hot path, and snapshots are hourly by default.
- **Not in scope:** tax-form generation, invoicing or customer management, consolidating several nodes or other money rails (done by an external ledger fed by the exports or the IPC feed), and liquidity-ad lease accounting until leases exist.
