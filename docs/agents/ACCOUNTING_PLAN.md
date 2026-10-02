# Accounting plan (NL-602)

Status: **plan, not started** (2026-10-02). Owner decisions still open are marked **D-Ax** in §8.

## 1. Goal

First-class accounting for NLightning, in two depths:

- **Operational** (default when the plugin is on): a node operator's view, in sats/msat. It covers routing revenue, payment fees, on-chain fees, per-channel profitability, liquidity cost and where every sat went. It is cheap, needs no fiat data and requires no setup.
- **Financial** (opt-in, the CFO view): proper double-entry books. It adds classification rules, fiat valuation at the time of each event, cost-basis lots, realized and unrealized gains, period close, an audit trail and hledger export. This is the sub-ledger that feeds a company general ledger (LayeredAccounting).

Every application gets the core event feed (§3), whether or not it turns accounting on. The feed is cheap, it is the node's own audit data, and every depth of accounting builds on it.

## 2. What exists today (survey 2026-10-02)

### NLightning

- **The plugin framework is a stub (NL-151).** It has `IDaemonPlugin` and `IDaemonContext` in `src/NLightning.Daemon.Plugins/`, and `PluginLoaderService` is never registered. The loader has several gaps:
  - No dependency resolver.
  - No phase that registers services: plugins start after the container is built.
  - No hooks, no IPC extension, and `PluginEntry.ConfigSection` is unused.
  - Nothing implements `IDaemonContext`.

  The `AssemblyLoadContext` idea can be kept; the interfaces should not.
- **No accounting-level events exist.** The only hooks are these:
  - `IHtlcSwitch` domain events. They are at-least-once: replayed at startup, on link-up and every block until irrevocable.
  - `ILocalPaymentHtlcHandler`, which fires per outgoing part, not per payment.
  - `IChannelMemoryRepository.OnChannelOpened/Updated/Upgraded`.
  - `IBlockchainMonitor.OnWalletMovementDetected`, which covers incoming wallet outputs only and has no subscriber.
- **History that is kept:**
  - `Invoices` (except expired Open BOLT 12 ones, NL-448).
  - `Payments`, `PaymentParts` and `PaymentHops`.
  - `ForwardCircuits` (never deleted; fee = in − out).
  - `Channels` (never deleted) and `ChannelFundings` (splice deltas).
  - `ChannelCloses`, `OutputResolutions`, and `BroadcastTransactions` (never deleted, with the raw tx and its purpose).
- **History that is lost or never stored:**
  - Wallet UTXO rows are **deleted on spend**, so there is no wallet transaction history (NL-603).
  - Absolute on-chain fees are not stored. Only the feerate and the raw tx are, so the fee needs the input values (NL-604).
  - The push amount is folded into the opening balance and not stored (NL-605).
  - Archived HTLC rows are pruned (NL-243), so per-part receive history is lost and only the invoice total survives.
- **Invoices and payments carry no operator label or tags.** The financial profile needs these to classify revenue and spending (§6.2).
- **The CLI is hard-coded.** `ClientCommand` is a closed enum (next free 41) and `IIpcCommandHandler` is internal, so today a plugin cannot add a command without core changes.

### LayeredAccounting (`MonumentalSystems/LayeredAccounting`)

It is an early MVP scaffold of about 4.2k lines and 96 tests, none of which cover grains or reports.

- **What is usable:**
  - The small domain: `Transaction`, `Posting`, `AccountName` and `Money`.
  - The hledger `JournalWriter` and `JournalParser`.
  - The source-adapter shape: a mapper plus a sync grain, as `MercurySyncGrain` does it.
  - The Lightning-first spec `LayeredAccounting_RealTimeAudit_FeatureSpec.md`. It calls for a hash-chained event log, snapshots, settlement states, risk-weighted capital attribution and sub-second times.
- **Blockers for a Lightning feed:**
  - Orleans attributes even in Domain.
  - `Currency` is a closed enum with no msat. SATS is written as `N0`, which rounds msat away, and unknown units parse as USD.
  - Transactions carry a date only.
  - The ledger is append-only with no reversal entries.
  - There is no ledger-level external-id dedup.
  - Storage is in memory only, and every append rewrites the whole list.
  - `ISyncGrain` has a single implementation, so a "lightning" key would resolve to the Mercury grain.
  - No price, cost or lot support.

## 3. Architecture

```
 NLightning daemon process
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ Core (always on)                                                         │
 │   HtlcSwitch / PaymentService / channel open-splice-close / BOLT 5 /     │
 │   chain monitor / WalletSpendService                                     │
 │        │  same SaveChangesAsync as the state change (transactional outbox)│
 │        ▼                                                                 │
 │   AccountingEvents table ──► EventSealer (dense LedgerSeq + hash chain)  │
 │        │                                                                 │
 │        ├─► IAccountingEventFeed (in-process: cursor read + wake-up)      │
 │        └─► IPC 41 listaccountingevents / 42 accountingsnapshot           │
 │                                                                          │
 │ Plugin: NLightning.Accounting (opt-in, Plugins:Accounting)               │
 │   Projector(cursor) ─► rules/profile ─► double-entry journal (own        │
 │   DbContext) ─► reports, hledger/CSV export, IPC via plugin command 43   │
 └──────────────────────────────────────────────────────────────────────────┘
          │ feed (IPC client lib, or exported .journal)
          ▼
 LayeredAccounting (separate process, Orleans): "lightning" layer next to
 "mercury", consolidated company books (general ledger)
```

The design follows four principles:

1. **The core produces facts and the plugin produces books.** The core records what happened to our money, in msat, with deterministic keys, inside the same database transaction as the state change. It never decides accounts, fiat or tax treatment. Every accounting opinion lives in the plugin and can be rebuilt from the feed.
2. **The feed is a transactional outbox, not an event bus.** The domain events are at-least-once and replayed, so subscribing to them directly would double count. The outbox row is written in the save that commits the fact: the invoice settle in the fulfill's save, a payment's completion, a circuit's resolution, a funding's lock, a close's classification, an output's resolution, a wallet movement in the block's unit of work. Each row has a **unique `EventKey`**, such as `inv:{hash}:settled`, `fwd:{inChan}:{inHtlc}:settled` or `tx:{txid}:{vout}:wallet-in`. A replay hits the unique index and becomes a no-op, so writers must insert-if-absent inside their save.
3. **Readers follow commit order, not insert order.** Identity sequences can commit out of order across concurrent saves, so a cursor reader on them would skip a late commit. Rows are therefore inserted with a null `LedgerSeq`. A single background `EventSealer` assigns a dense `LedgerSeq` in commit order, along with `PrevHash`/`Hash`, a SHA-256 chain over the canonical row bytes. Consumers read `LedgerSeq > cursor`. The sealer also gives the hash chain the audit spec asks for, without putting it on the hot path.
4. **Reorgs are corrected with compensating entries, never deletions.** On-chain events carry the block height. `OnBlockDisconnected` writes `*:reversed` events with their own keys. A `Finality` field (`Unconfirmed` / `Confirmed` / `Irrevocable` at 100 blocks or BOLT 5 irrevocable) lets the books post at confirmation and lets the financial profile choose to wait (D-A4).

## 4. Event catalogue (core, A1)

Amounts are signed **msat from our point of view**: positive means more of our money, negative less, and fee fields are always positive. Every row stores these fields:

- `Kind` and `EventKey`.
- `OccurredAt` (UTC with sub-second precision) and `BlockHeight`.
- `ChannelId` and `Scid`.
- `PaymentHash`, `TxId`, `Vout` and `Counterparty` (node id).
- `AmountMsat` and `FeeMsat`.
- `Finality`.
- `Payload`: versioned JSON holding the kind-specific details.
- `LedgerSeq`, `PrevHash` and `Hash`, filled in by the sealer.

| Kind | Written where (in which save) | Amount / fee | Notes |
|---|---|---|---|
| `InvoiceSettled` | `HtlcSwitch.SettleInvoiceAsync` (fulfill save) | +received; fee 0 | Covers bolt11, keysend (custom records in payload) and BOLT 12 (offer id, payer note, quantity). MPP is one event per invoice. Also records the invoice amount if overpaid. |
| `PaymentSucceeded` | `PaymentService` completion save | −(amount+fee); fee = route fee | Per payment, not per part. Includes the destination, the invoice description and hash, the offer id, and keysend. A payment to our own invoice is flagged `SelfPayment` (rebalance). |
| `PaymentFailed` | `PaymentService` | 0 | Informational only; never posts money. |
| `ForwardSettled` | Circuit → Fulfilled (save of the incoming fulfill) | +fee = in−out | Records the in and out channel and amounts. |
| `ForwardLostOnchain` | The resolver path where we paid downstream on chain but lost upstream, or the reverse | ± | A loss event, rare but real. |
| `ChannelFunded` | Funding confirmation (`ChannelFundings` lock save) | −our contribution; fee = our share of the funding tx fee | Records push (NL-605), dual-fund shares, public/private, and the anchors type. Our contribution moves wallet → channel; the push is a separate `PushSent`/`PushReceived`. |
| `SpliceLocked` | `IChannelFundingDbRepository.ApplyLockAsync` save | ±delta; fee = our share | Covers splice in and out. RBF siblings that never lock emit nothing, apart from wallet release. |
| `ChannelClosedMutual` | Close tx confirmation | channel → wallet; fee = closing fee if we pay it | Fees are settled on the closing tx, not the commitment. |
| `ChannelForceClosed` | `OnchainChannelWatcher` classification save | moves the channel balance to `pending-onchain` | `Kind`: Local/Remote/Revoked/Future. Includes trimmed and dust HTLCs lost to fees. |
| `OutputResolved` | `OnchainResolutionExecutor` save | pending-onchain → wallet; fee | Records the descriptor (to_local, HTLC timeout or success, claim, sweep, anchor) and maturity. HTLC outputs settle the in-flight HTLC as fulfilled or timed out. |
| `PenaltyClaimed` / `BreachLoss` | `RevokedCommitResolver` | +gain / −loss | |
| `AnchorCpfpFee`, `SweepFeeBump` | `AnchorCpfpService`, `SweepScheduler` on confirmation | fee only | Only the confirmed replacement counts. Replaced rows emit nothing. |
| `WalletReceived` | Chain monitor wallet staging (block UoW) | +amount | External deposits only. Change outputs and close outputs are linked to their spend or close event, not double counted (the payload has `Source`). |
| `WalletSent` | `WalletSpendService` row on confirmation | −amount; fee | Covers `withdraw`. |
| `*Reversed` | `OnBlockDisconnected` | the negation | Keyed `{originalKey}:rev:{height}`. |
| `Snapshot` | Hourly or daily timer and on demand | — | The payload has per-channel local/remote/in-flight, wallet, pending sweeps and the anchors reserve. The plugin reconciles against it (§5). |

**In-flight HTLCs** are not booked by default. Money is booked when it is irrevocably ours or no longer ours: at settle or fulfill. The snapshot reports in-flight amounts so a balance sheet can show "in transit" (the spec's settlement states and risk weights are a financial-profile report, §6.2).

**Backfill.** When the feed is first enabled on an existing node, a one-shot backfill writes historical events from the existing tables:

- `Invoices`, `Payments`, `ForwardCircuits`, `Channels`/`ChannelFundings`/`ChannelCloses`, `OutputResolutions` and `BroadcastTransactions`.
- Fees are recomputed from raw txs, with prevouts from bitcoind `getrawtransaction`.

Backfilled events are flagged `Backfilled`. Wallet history from before NL-603 cannot be rebuilt from our tables. The backfill posts an `OpeningBalance` event for the wallet at the current snapshot and says so.

## 5. Correctness invariant (the proof for every phase)

> For every asset bucket (each channel's local balance, wallet, pending-onchain), the sum of posted events equals the live snapshot, to the msat, at every quiescent point.

- **Tests:** the invariant runs as an assertion in the in-process harnesses: `TwoNodeHarness`, `ThreeNodeSwitchTests` with SQLite restarts, `PaymentHarnessTests`. It also runs in a seeded random-workload simulator (open, pay, receive, forward, MPP, fail, restart mid-flow, splice, mutual close) and in the Docker on-chain suite (force close, HTLC on chain, penalty, anchors CPFP, reorg).
- **Production:** the plugin runs the same reconciliation on every `Snapshot`. A mismatch is metered (`nlightning.accounting.reconcile.drift_msat`), logged with the bucket, and shown by `accounting reconcile`. A mismatch is a bug, never an adjustment.

## 6. The accounting plugin (`NLightning.Accounting`)

### 6.1 Operational profile (default)

The default chart of accounts uses hledger names, and every name is configurable. Postings are in the `msat` commodity, displayed as sats.

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

**Reports**, each as IPC plus CLI output and CSV:

- Balance sheet, and income statement by period.
- Per-channel P&L: routing earned in/out, rebalance cost, open and close fees, and lifetime yield on deployed capital (APR).
- Per-peer summary.
- Fee and cost breakdown.
- Plain ledger register with filters.
- `export --format hledger|csv|beancount`.
- `reconcile`.

### 6.2 Financial profile (opt-in, `Accounting:Profile=Financial`)

- **Labels and tags at the source (core change):** optional `--label` and `--tag k=v` on `createinvoice`, `payinvoice`, `keysend`, `createoffer`, `payoffer`, `withdraw` and `openchannel`. They are persisted on the row and copied into the event payload.
- **Classification rules:** first match wins, on kind, tag, label regex, counterparty or offer id, and they map to accounts (e.g. `tag:customer=* → income:sales`, `label:~/payroll/ → expenses:payroll`). Unmatched entries go to `*:unclassified` and are listed for review. **Manual reclassification** is stored as an override keyed by `EventKey`. It is the only non-derivable plugin state, so it is backed up with the node.
- **Fiat valuation:**
  - Each posting gets the BTC price at `OccurredAt` in the base currency (D-A1).
  - The price source is a pluggable `IPriceSource`: an operator CSV, a cached HTTP provider, or none.
  - The source never blocks the node. Missing prices are filled in later by a back-valuation job, and reports flag unvalued rows.
  - The price is stored with the entry, so reports are reproducible.
- **Cost basis:** a lot per sat acquisition (`WalletReceived` at fiat cost, `InvoiceSettled` as income at fair value). Lots are relieved on disposals (payments, fees, withdrawals) under FIFO, LIFO, HIFO or specific-id (D-A2). This gives realized and unrealized gains, and hledger output uses `@@` costs and `P` directives.

  Moving sats between our own wallet, channel and pending buckets is a **transfer, not a disposal**; only fees dispose. This is the most common mistake in crypto accounting tools.
- **Period close and lock:** `accounting close 2026-09` locks the period. Later corrections post into the open period as adjustments, never back-dated.
- **Audit:** the hash-chained feed (`LedgerSeq` + `Hash`) and signed period-close digests, with optional OpenTimestamps of the period root later. `accounting verify` re-walks the chain. Spec reports include settlement states and risk-weighted capital attribution from snapshots.

### 6.3 Storage

The plugin's journal is a **projection** of the feed plus the overrides, so it can always be rebuilt (`accounting rebuild`). Recommended (D-A3): the plugin has its own `AccountingDbContext` in the **node's database and provider**, with its own migration history table. That gives operators one backup and one connection string. Per the repo rules it ships migrations for all three providers; SQLite and Postgres are tested first. The cursor (`LastLedgerSeq`) is saved in the same save as the entries it produced, which makes the projection exactly-once.

## 7. Plugin host v2 (replaces the stub, closes NL-151)

Accounting is the first real plugin, so the host is designed around its needs.

- **Contract assembly `NLightning.Plugins.Abstractions`** (rename `Daemon.Plugins`, delete `IControlClient`/`IDaemonContext`). Its interfaces:
  - `INltgPlugin { string Name; void ConfigureServices(IServiceCollection, PluginContext) }`. It runs **before** the container is built. `PluginContext` holds the plugin's `IConfigurationSection` (`Plugins:<Name>`), the network, the data directory and the logger factory. The plugin registers its own hosted services, options and handlers.
  - `IAccountingEventFeed` (read with cursor, `WaitForNewAsync`) and `INodeSnapshotSource`. These are read-only ports. Plugins never get `IUnitOfWork` or the core DbContext.
  - `IPluginCommandHandler { string Plugin; string Command; Task<PluginCommandResult> HandleAsync(PluginCommandRequest) }`.
- **IPC: one new core command, `Plugin` (43).** It carries `{plugin, command, args: string[], payload: bytes}` and returns `{table | json | text}`. The CLI maps `nltg plugin <name> <cmd> ...` and a convenience alias `nltg accounting ...`. Plugins then never touch `ClientCommand` again, and client and daemon stay version-locked only on the envelope.
- **Loading:**
  - **v1:** compiled in. `AddNltgNodeServices` calls `AddPlugins(configuration)`, which activates the in-tree plugins listed under `Plugins:<Name>:Enabled`. Accounting ships in the daemon and is off by default (D-A5).
  - **v2:** external assemblies through `AssemblyLoadContext` with `AssemblyDependencyResolver`. `NLightning.Plugins.Abstractions`, `Microsoft.Extensions.*` and `NLightning.Domain` are forced to the default context so the types unify. This waits until a second plugin needs it, because AOT and `Release.Native` builds cannot load assemblies, so the in-tree path must stay first-class anyway.
- **Ordering:** plugin hosted services start after `NltgDaemonService` and stop before it. A plugin failure is logged and isolated: it never stops the node.

## 8. Decisions (owner)

| ID | Question | Recommendation |
|---|---|---|
| D-A1 | Base fiat currency and price source | USD. Operator CSV plus one cached HTTP source. Never on the hot path. |
| D-A2 | Default cost-basis method | FIFO (widest tax acceptance); per-ledger setting. |
| D-A3 | Plugin storage | Node DB, separate DbContext and migration history (§6.3). |
| D-A4 | Post on-chain events at 1 confirmation with reversal, or at N | 1 confirmation with compensating reversals; financial reports can filter on `Finality`. |
| D-A5 | Feed on by default? Plugin on by default? | Feed always on, on every network, mainnet included (cheap, audit value). Plugin off by default, `Operational` when enabled. |
| D-A6 | LayeredAccounting's role | Company general ledger and consolidator (Orleans, multi-rail). NLightning.Accounting is the Lightning **sub-ledger**. They share one Orleans-free `LayeredAccounting.Core` package (domain + hledger I/O) so there is a single journal format. |
| D-A7 | How LayeredAccounting reads the node | v1: `LightningSyncGrain` using the NLightning IPC client library (IPC 41/42) on the same host. Remote: plugin-exported `.journal` files, or a later authenticated read-only HTTP feed. |

## 9. Phases

Each phase ends with its proof in the repo's usual style.

### A0: decisions and ledger

- Owner answers on §8.
- Open NL-602 (epic), NL-603..NL-605 (data gaps), and NL-151 re-scoped to A2.

### A1: core feed (NLightning)

Tasks:

- **T1:** Domain `Accounting/` (`AccountingEvent`, `AccountingEventKind`, key builders, `IAccountingEventWriter` port). Persistence `AccountingEvents` + migration `AddAccountingEvents` (3 providers).
- **T2:** Writers in the saves listed in §4: switch, payments, circuits, fundings and splices, close, watcher and resolver executor, sweep and CPFP confirmation, wallet monitor, withdraw.
- **T3:** Data gaps:
  - NL-605: persist the push amount.
  - NL-604: `BroadcastTransactions.FeeSat` and `OurInputSat`, computed when we build the tx, since we know our inputs.
  - NL-603: a `WalletTransactions` history in place of delete-only UTXO rows.
- **T4:** `EventSealer` (dense `LedgerSeq`, hash chain), `Snapshot` timer, reorg reversals.
- **T5:** `IAccountingEventFeed`, `INodeSnapshotSource`, IPC `listaccountingevents` (41, paged by `LedgerSeq`, filters) and `accountingsnapshot` (42).
- **T6:** One-shot backfill.

Proof:

- Replay and idempotency tests: startup replay, link-up replay, per-block re-resolution, all with no duplicates.
- §5 invariant in the harnesses and in a new `Long` simulator.
- Docker on-chain force close, penalty and reorg.
- Three-provider round trip.

### A2: plugin host v2

Tasks: `Plugins.Abstractions`, `AddPlugins`, `Plugin` IPC (43) with CLI passthrough, a sample no-op plugin in tests.

Proof: plugin registration and isolation tests, plus an IPC round trip.

### A3: accounting plugin, operational profile

Tasks: projector with exactly-once cursor, default chart, reports (§6.1), export (hledger, CSV), `accounting reconcile|rebuild`.

Proof:

- Golden-file hledger exports from the simulator.
- `hledger check` and `hledger balance` against them in CI, where hledger is available.
- Rebuild equals the incremental projection.
- Reconcile is clean across the Docker ABCD and on-chain suites.

### A4: financial profile

Tasks:

- Labels and tags on the IPC commands (core).
- Classification rules and overrides.
- `IPriceSource` and back-valuation.
- Lots and gains.
- Period close and lock.
- `accounting verify`.

Proof:

- Hand-computed fixtures for lots and gains: FIFO and HIFO, and transfers that are not disposals.
- Period-lock tests.
- Hash-chain tamper tests.

### A5: LayeredAccounting integration (in that repo)

Tasks:

- **L1:** extract an Orleans-free `LayeredAccounting.Core`. Orleans serializes it through surrogates, which already exist for some types.
- **L2:** `Money` over a commodity string with exact integer msat for `BTC`/`sat`/`msat`. Fix the writer and parser (no rounding, unknown commodities are errors, not USD) and add `P` and `@@`.
- **L3:** timestamps (`DateTimeOffset`, with the date kept for journal headers), ledger-level `ExternalId` idempotency, reversal entries.
- **L4:** durable storage. Event-sourced ledger grain on a real provider, or the journal files as the store (`JournalFileManager` exists).
- **L5:** source registration that does not collide (`[GrainType]` per source, or `ILightningSyncGrain`), and a configurable ledger name.
- **L6:** `LightningSyncGrain` reading IPC 41/42, layer `lightning`, posting the plugin's classified entries (detailed or daily-summarized, per setting) with `ExternalId = EventKey`.

Proof:

- TestCluster grain tests (none exist today).
- An end-to-end run (regtest node, payments, LayeredAccounting balance sheet) where the "lightning" layer equals the node's `accounting` balance sheet.

The order is A1 → A2 ∥ A3 → A4, with A5 L1–L5 in parallel from day one; L6 needs A1-T5.

## 10. Risks and notes

- **Double counting through replays** is the dominant risk. Mitigation: unique `EventKey` in the same save, and the §5 invariant everywhere.
- **Commitment fee and reserve.** The funder's balance pays the commitment fee only if the channel force-closes. The operational books keep the gross local balance and book the actual fee at close. A "spendable" view (minus reserve, commitment fee and anchors) is a report column, not a ledger entry.
- **Anchors (330 sat × 2).** The funder's anchors are part of the force-close flow: swept (NL-601 bookkeeping) or lost to dust. They are booked through `OutputResolved`/`expenses:losses:dust`.
- **Privacy.** The feed holds counterparties, payment hashes and labels. IPC 41/42 use the existing cookie authentication. Exports are operator-initiated files, so document them in `SECURITY_REVIEW.md`.
- **Performance.** One extra row per money event in an existing save. The sealer runs off-path. Snapshots run hourly by default.
- **Not in scope:** tax-form generation, invoicing or customer management, multi-node consolidation inside NLightning (that is LayeredAccounting's job), and liquidity-ad lease accounting until leases exist.
