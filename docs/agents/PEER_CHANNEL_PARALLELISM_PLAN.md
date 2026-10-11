# Per-channel parallel processing of one peer's messages (plan)

Status: **phases P1-P3 built** (branch `wip/peer-parallel-impl`, 2026-10-11; record at the end). The plan's proposed
IDs NL-1361..NL-1368 were taken meanwhile and were re-filed as NL-1396..NL-1403 (§9 uses the new ones). Owner decisions
of 2026-10-11: PostgreSQL is the production backend and the baseline; SQLite's single-writer serialization is not worked
on (P0's SQLite fix, NL-1397/NL-1398, is wontfix) and SQLite may keep serial behaviour.

Background: the channel-scale benchmark (NL-1357, `docs/agents/CHANNEL_SCALE.md` on `wip/channel-scale`) measured about
17 payments/s between a hub and one live peer over 1,000 channels on SQLite, and sending 8 payments at once added only
latency (p50 40 ms one at a time, 413 ms eight at once). BOLT 2 requires order only per channel (and FIFO on the wire);
LND runs each channel link on its own goroutine. Target: throughput grows with the number of busy channels to one peer,
with about 20 payments/s per channel as the floor, on SQLite and on Postgres, and most of all for hub and LSP peers with
many channels.

## 1. The premise, settled

**Question:** does the per-channel lock (`IChannelLockProvider`) already protect all per-channel state, so that only the
peer's inbound loop serializes work across a peer's channels?

**Answer: yes for channel state, with three conditions.** Every piece of state that channel message handling touches
is either per channel under that channel's lock, per message scope, or node-wide with its own synchronization; nothing
reuses a unit of work or EF context across messages. Two facts prove it more strongly than reading each class: (a) the
send side already mutates a peer's channels concurrently with that peer's inbound loop, under the channel lock only
(`CommitScheduler`, `ChannelOperationsService`, the HTLC switch, the block handlers, the fee-update scheduler, the HTLC
deadline monitor, the failure service, IPC); (b) two peers' inbound loops already run concurrently through the same
singletons, so any state that is safe across two peers is safe across two channels of one peer unless it is keyed by
peer. The per-peer keyed state is listed in §1.2; all of it is synchronized. The three conditions are not about
correctness of channel state but about what concurrency costs: SQLite writers collide and the SQLite provider sleeps
150 ms per collision (§4.1, measured), the durable signer guard blocks thread-pool threads (§4.3), and the open flow
under temporary channel ids relies on the loop for ordering across two ids (§3, rule R4).

### 1.1 Evidence, item by item

| State | Where | Scope | Synchronization | Verdict |
|---|---|---|---|---|
| Unit of work / EF context | `ChannelManager.HandleChannelMessageAsync` creates one DI scope per message (`ChannelManager.cs:437`), the batch path too (`:530`), `CommitScheduler.SignNowAsync` its own (`CommitScheduler.cs:110`); `IUnitOfWork` is `AddScoped` (`Infrastructure.Repositories/DependencyInjection.cs:44`), `NLightningDbContext` `AddDbContext` (scoped, `Persistence/DependencyInjection.cs:59`) | per message | none needed | safe: no context is shared between two messages |
| `ChannelDomainEventQueue` | `AddScoped` (`CommitmentEngineServiceCollectionExtensions.cs:44`), drained after the lock in the same call (`ChannelManager.cs:511`, `RaiseDomainEventsAsync` `:1425`) | per message | none needed | safe |
| `ChannelStateTransitionService` | `AddScoped` (`:45`); per-scope fields only (`_consumingSigningWorkflow`) | per message | none needed | safe |
| `CommitScheduler` | singleton (`:70`); `ConcurrentDictionary<ChannelId,…> _scheduled`, rounds on the thread pool with `ExecutionContext.SuppressFlow` (`CommitScheduler.cs:78-90`), channel lock in `SignNowAsync` (`:111`) | per channel | concurrent dictionary + channel lock | safe; already concurrent with every inbound loop |
| `ChannelOperationsService` | singleton; channel lock (`ChannelOperationsService.cs:256,280`), publishes under it (`:296`) | per channel | channel lock | safe; already concurrent |
| `IChannelMemoryRepository` | `ConcurrentDictionary` for channels, states and temporary channels keyed `(peer, tempId)` (`ChannelMemoryRepository.cs:38-42`); `ChannelModel` instances are mutated only under their channel lock; lock-free readers see an immutable `ChannelCommitments` snapshot swapped by reference | per channel / per (peer, temp id) | concurrent dictionaries + channel lock | safe |
| `ChannelLockProvider` | one `SemaphoreSlim` per id behind a global `Lock` that guards only the table (`ChannelLockProvider.cs`) | per channel | yes | safe; the only two-lock holder is `funding_created` (temporary then real, `ChannelManager.cs:1385-1408`) |
| `ReestablishTracker` | `ConcurrentDictionary<ChannelId, Entry>` with compare-and-set (`ReestablishTracker.cs:32,59`); `ResetPeer` iterates and removes per entry | per channel (entry carries the peer) | yes | safe |
| Liveness probes | `ConnectedPeerLivenessProbe` `ConcurrentDictionary<ChannelId,…>` (`:26`); `ReestablishGatedLivenessProbe`, `LinkUpReplayingPeerLivenessProbe` are stateless decorators (the replay is scheduled per channel in the background) | per channel | yes | safe |
| `QuiescenceService` / `IStfuReleaseScheduler` | per-channel entries in a `ConcurrentDictionary` plus a `Lock _sync` (`QuiescenceService.cs:59,69`); the stfu release runs on the thread pool under the channel lock, after the transition's replies (`ScheduleRelease`, `:283-296`) | per channel | yes | safe; ordering after `revoke_and_ack` is the channel lock plus outbox FIFO, not the loop |
| `InteractiveTxDriver` | `ConcurrentDictionary<ChannelId, ChannelEntry>` (`InteractiveTxDriver.cs:54`), one negotiation per channel under its lock | per channel | yes | safe |
| `ClosingNegotiationRegistry` | `ConcurrentDictionary` per channel and per shutdown script (`ClosingNegotiationRegistry.cs:22,26`) | per channel / node | yes | safe |
| `ChannelAnnouncement2Service` (taproot gossip MuSig2 sessions) | `ConcurrentDictionary<ChannelId, Session>` (`:72`), reset per peer connection | per channel | yes | safe |
| `ChannelOpenDecisionGate` | `ConcurrentDictionary` of deciders (`ChannelOpenDecisionGate.cs:16`); awaited under the temporary channel's lock on the inbound loop (`IChannelOpenDecisionGate.cs:9`) | node | yes | safe; today a slow decider stalls all of that peer's channels |
| `LiquidityAdsService` sale slots | `Dictionary<CompactPubKey,int> _salesByPeer` under `Lock _salesLock` (`LiquidityAdsService.cs:36-37`) | per peer | lock | safe; per-peer limit holds under concurrency |
| Anchor reserve, UTXO locks, fee inputs | `AnchorReserveService` gate and pending lock (`AnchorReserveService.cs:42,47`), `UtxoMemoryRepository._reservationLock` (`:26`) | node | yes | safe |
| `LocalLightningSigner` | `ConcurrentDictionary` per channel for signing info, commitment numbers, data loss, broadcast marks, and one `Lock` per channel for the commitment guard (`LocalLightningSigner.cs:46-61,1411`) | per channel | yes | safe, but see the durable guard below |
| Durable signer guard (NL-1345) | `ChannelSignerGuardStore.Load`/`Raise`: a new scope and unit of work per call, run with `Task.Run(...).GetAwaiter().GetResult()` (sync over async), one conditional `UPDATE` per field, atomic per row (`ChannelSignerGuardStore.cs`) | per channel row | row-level | correct, but each guarded operation blocks a thread-pool thread for a DB round trip (§4.3) |
| `HtlcSwitch` | `KeyedAsyncLock` per incoming HTLC and per payment hash, `ConcurrentDictionary` HTLC sets (`HtlcSwitch.cs:158-183`) | per HTLC / per payment hash | yes | safe; already fed by several peers' loops and replays at once |
| `LndIndexAllocator` | singleton (`DependencyInjection.cs:40`), one `SemaphoreSlim Gate` held from the index assignment to the end of the save, only for saves that add an invoice or payment or settle an invoice (`UnitOfWork.cs:379-430`) | node | yes | safe; a node-wide serialization point for those saves (§4.4) |
| Accounting staging and sealer | events staged in the scope's unit of work, committed in the transition's save; the sealer reads committed, unsealed rows in id order and seals a late commit in the next round (`AccountingEventSealerService.cs` header) | per save / node | the sealer's round gate | safe; ledger order across channels is commit order, as across peers today |
| Write fence (NL-1341) | `CheckSaveAsync` inside every save's transaction (`UnitOfWork.cs:456,484`), `CheckEffectAsync(PeerSend)` before every outbox send | per save | none in our code | safe if the fence implementation does not take an exclusive lock (rule R13) |
| `PeerOutbox` | one unbounded FIFO channel, `SingleWriter = false`, enqueue never blocks (`PeerOutbox.cs`) | per connection | channel | safe for several writers; order within a channel holds because each channel enqueues under its lock |
| `PeerManager` session table, `_currentSession` | `ConcurrentDictionary` sessions, `_sessionLock` for install/remove, `AsyncLocal<PeerSession?>` set by the inbound loop (`PeerManager.cs:129,1172`) and read when a reply is raised (`:1560`) | per connection | yes | needs care: every lane must set it (rule R1) |
| Batch grouping | `InboundBatchState` local to the inbound loop (`PeerManager.cs:1181,1322-1436`) | per connection | single reader | stays on the dispatcher (rule R6) |
| `ChannelManager._connectionGate` | a global `Lock` around `Publish` and `OnPeerConnectionChanged` (`ChannelManager.cs:108-126,725-731`) | node | lock | safe; in-memory only, short |

Comments that state the per-peer serialization as an assumption (all to be revisited by the implementation):
`ChannelLockProvider.cs:11` ("the per-peer inbound loop already serializes the open flow's messages"),
`IChannelOpenDecisionGate.cs:9`, `ClosingFeeEstimator.cs:15` and `ChannelCloseOptions.cs:47` (a wait under the lock
stalls the peer's loop), `ChannelFailureService.cs:283` and `AnchorCpfpService.cs:45` (work kept off the loop),
`StartBatchMessage.cs:14`, `CommitmentSignedBatch.cs:11`, `IChannelManager.cs:38` (batches built by the loop),
`ChannelManager.cs:737` (a reestablish still queued in the loop is not counted).

### 1.2 What really is per peer

Only these depend on the peer's messages being handled one at a time, and each gets a rule in §3:
the connection prologue (`OnPeerConnectedAsync`: tracker reset, announcement sessions reset, our `channel_reestablish`
for every channel, stored errors) and epilogue (`OnPeerDisconnectedAsync`: revert of uncommitted updates, interactive-tx
cleanup), both run by the loop before the first and after the last message; the `start_batch` grouping; the
`AsyncLocal` connection the replies go to; the order between two channel ids of one open (temporary id, then real id);
the disconnect that stops the loop (queued messages dropped); and the next connection's loop waiting for the previous
one.

## 2. Today's ordering path

1. Transport read loop to `MessageService`'s per-peer consumer, which deserializes frames in arrival order and raises
   handlers off the read loop (`Infrastructure/Protocol/Services/MessageService.cs:24,200`). `PeerService.HandleMessage`
   (`PeerService.cs:376`): init, then channel messages (stfu and 259/260 included) to `OnChannelMessageReceived`; the
   peer's `error`/`warning` to `OnAttentionMessageReceived`; gossip queries to the sync service; 258 to the update
   service and the graph; peer storage and onion messages to their services. Pings and pongs never reach the channel
   path.
2. `PeerManager.QueueInboundMessage` (`PeerManager.cs:1155`) writes into the session's bounded channel (1,024,
   `PeerManager.cs:81,1775`); when full the consumer thread blocks in `TryQueueInbound`
   (`WriteAsync(...).GetAwaiter().GetResult()`, `:1853`): backpressure to the peer's socket.
3. The inbound loop (`ProcessInboundMessagesAsync`, `:1169`) is started after the previous connection's loop finished
   (`TryInstallSession`, `:1003-1006`; `StartInboundLoop`, `:1826`). It sets `_currentSession` (`:1172`), runs the
   prologue `StartReestablishAsync` (`:1213`, `ChannelManager.OnPeerConnectedAsync` `:610`: per channel, under its lock,
   one at a time), then reads one message at a time (`:1183`): `start_batch` grouping (`:1322-1436`) or
   `ProcessChannelMessageAsync` (`:1438`). A failure is mapped by `HandleChannelMessageFailure` (`:1467`): a failed
   channel's `error` (connection kept), `error` + disconnect, `warning` (+ close when asked), or an internal error's
   disconnect. In `finally`, `RevertPeerUpdatesAsync` (`:1292`).
4. `ChannelManager.HandleChannelMessageAsync` (`:430`): new scope, the channel lock (`:442`; two locks for
   `funding_created`), dispatch to the handler, persist (one save per transition, before any send), link-up, reestablish
   completion, announcements, close advance, `RaiseResponseMessages` under the lock (`:479`); after the lock:
   `ScheduleCommit` (`:486`), the failure broadcast (`:493`) and, in `finally`, `RaiseDomainEventsAsync` (`:511`), which
   **awaits** the HTLC switch for every event (a final-hop fulfill or a forward takes channel locks and saves) before the
   loop takes the next message.
5. Replies: `OnResponseMessageReady` to `PeerManager.HandleResponseMessageReady` (`:1554`), which enqueues on the
   `_currentSession` connection's `PeerOutbox`, or the peer's current one for raises from elsewhere.
6. Send side, off the loop: `CommitScheduler` (debounced 10 ms per channel), `ChannelOperationsService`
   (`IChannelOperations`, used by the switch and IPC), `IStfuReleaseScheduler`, link-up replays, the failure service, the
   deadline monitor and the fee-update scheduler, each under the channel lock, publishing through
   `IChannelMessagePublisher.Publish` (drops normal-operation messages of a channel not reestablished on the current
   connection, `ChannelManager.cs:108`).
7. `PeerOutbox`: one FIFO send loop per connection; the write fence is asked before each send.

## 3. Design: per-channel lanes behind a per-connection dispatcher

A connection with lanes enabled replaces the single inbound loop by a **dispatcher** and **lanes**:

- The dispatcher is the session's single reader of the bounded inbound channel. It runs the prologue, then reads in
  arrival order, groups `start_batch` members, and routes each item (a message or a complete batch) to a lane. It never
  awaits a handler.
- A lane is a FIFO of work items with one worker task while it has work. A lane handles one item at a time, exactly as
  the loop does today: `ChannelManager.HandleChannelMessageAsync` (or the batch call) including its post-lock work
  (`ScheduleCommit`, the failure broadcast, the awaited switch events). Messages of one lane are strictly FIFO;
  different lanes run concurrently.
- Lanes: one **peer lane** (serial, carries everything that is not a steady channel, see R4) and one **channel lane**
  per channel id that is eligible for its own lane.
- Connection-level traffic is unchanged: init, ping/pong, the peer's warnings and errors, gossip, peer storage and onion
  messages never enter the dispatcher (§2 step 1).
- Replies keep the single ordered `PeerOutbox`: a channel's replies are enqueued under its lock, so each channel's wire
  order is its handling order; replies of different channels interleave in completion order, which BOLT allows.
- Proposed home: a `PeerChannelDispatcher` class in `Application/Node/Services/` owned by `PeerSession`, so the
  two- and three-node harnesses (which today call `ChannelManager` directly, `TwoNodeHarness.cs:437`) can deliver
  through it too.

### 3.1 Rules

| # | Case | Rule |
|---|---|---|
| R1 | Reply routing (`_currentSession`) | Each lane worker sets `_currentSession` to its session before handling (lanes are started by the dispatcher; never rely on flow alone). A reply raised by a lane goes to that lane's connection, never a newer one. |
| R2 | Prologue: our `channel_reestablish` and stored errors | Unchanged in phase 1: the dispatcher runs `StartReestablishAsync` before it routes the first message (messages queue meanwhile). Phase 4 may move each channel's part into its lane's first item (NL-1401); the per-peer part (tracker reset, announcement resets) stays first and serial. The reestablish deadline is armed after the prologue, as today. |
| R3 | Gating before the peer's reestablish | Unchanged and per channel: `ThrowIfNotReestablished` and `Publish` read the per-channel tracker. A channel lane may run while another channel still waits for its reestablish. |
| R4 | Lane assignment and the open flow (temporary ids, `open_channel`/`accept_channel`/`funding_created`/`funding_signed`, `open_channel2`/`accept_channel2`, the dual-funded interactive-tx before the channel is registered, `IChannelOpenDecisionGate`) | A channel id gets a lane **at its first sight on the connection** and keeps it for the connection (sticky). It gets its own channel lane only if, at first sight, it is a registered, non-temporary channel in the memory repository; everything else (temporary ids, unknown ids, all-zero ids, open messages) goes to the peer lane. So every open flow runs serially on the peer lane exactly as today, the temporary-to-real handover never crosses two lanes inside one connection, and a channel opened on this connection stays on the peer lane until the next connection. **Barrier:** a new channel lane starts only after the peer lane has finished the items dispatched before it (rare: once per channel per connection), so a channel registered by a peer-lane handler is never touched by its new lane early. A slow open decider now stalls only the peer lane, not the steady channels. |
| R5 | One negotiation per channel (interactive-tx, splice, dual-fund RBF, quiescence) | Per channel already (driver and quiescence keyed by channel id, under its lock). All messages of a spliced channel carry its id, so they stay in its lane. |
| R6 | `start_batch` (splice `commitment_signed` batches) | Grouped by the dispatcher exactly as today (a batch never spans connections; another channel's message inside a batch is the same warning + close). The complete batch is one work item of the channel's lane, so it is a barrier across the channel's fundings and nothing of that channel runs between its members. |
| R7 | Quiescence (`stfu`) and the stfu release after `revoke_and_ack` | Per channel; no change. The release already runs off the loop under the channel lock. |
| R8 | Failures: `ChannelFailedException` | Per channel: the error is enqueued, the connection kept, other lanes go on; later messages of that channel get the stored error again (unchanged). |
| R9 | Failures that disconnect (`ChannelErrorException`, a warning with `CloseConnection`, an internal error, a malformed batch) and the reestablish deadline | The lane enqueues the disconnect and closes the session: the dispatcher stops routing, queued items of every lane are dropped, each lane finishes the item it is handling. A reply another lane raises after the disconnect is dropped by the outbox. This is no new failure class: it is a connection that dropped between a save and its send, which every transition tolerates (persist before send, the reestablish retransmits; `Publish` drops normal-operation messages of a connection that changed). An all-zero channel id error is never produced for a channel-scoped failure (unchanged mapping, `PeerManager.cs:1497,1513`). |
| R10 | Connection end and replacement | The session's "inbound loop" task becomes dispatcher + all lanes; `RevertPeerUpdatesAsync` runs after every lane finished; the next connection's dispatcher starts only after that task completed (as today), so no message of an old connection is handled after one of the new connection, and replies raised by an old lane never go out on the new connection (R1 plus `Publish`'s gate). |
| R11 | HTLC switch work after a message (final hop, forward, MPP, trampoline) | Stays inside the lane item (the lane awaits `RaiseDomainEventsAsync` before its next item), so per channel the semantics are today's. Across channels of one peer the switch now runs concurrently, as it already does across peers: MPP sets are guarded by the per-payment-hash lock and the invoice settle stays the set's commit point; a forward from lane A onto channel B takes B's lock (never two locks: lanes release their lock before the switch runs). |
| R12 | Background services acting on a peer's channels (fee-update scheduler, HTLC deadline monitor, failure service, anchors CPFP, commit scheduler, link-up replays) | No change: they already run concurrently with the loop under channel locks. |
| R13 | Write fence implementations | `INodeWriteFence.CheckSaveAsync` must not serialize saves (no exclusive row lock or process-wide lock per save; a shared lock such as Postgres `FOR SHARE`/`FOR KEY SHARE` on an epoch row is fine). Document it on the port and test that two saves overlap through a fence (NL-1403). |
| R14 | Lock order and permits | Unchanged lock rules (never two channel locks except `funding_created`'s temporary-then-real; only enqueue under a lock). New: a lane takes its concurrency permits **before** the channel lock and never waits for a permit while holding a lock; the switch work holds the permit but no lock. Deadlock argument: a permit holder only waits on channel locks, and every channel-lock holder either holds a permit and progresses or is a background worker that needs no permit. |
| R15 | Gossip 259/260 on the channel path, `channel_update` 258, peer storage, onion messages | 259/260 follow their channel id (R4). 258, peer storage and onion messages are not on the channel path (no change). |
| R16 | The peer's `error`/`warning` | Not on the channel path today (`OnAttentionMessageReceived`); no change. |

### 3.2 Concurrency cap and backpressure

- `MaxConcurrentPerPeer` (default 8): lanes of one connection handling an item at once (a `SemaphoreSlim` per session).
- `MaxConcurrent` node-wide (default 32): lane items in flight across all peers, kept well under the database pool
  (Npgsql's default 100; each item holds one context and the signer guard opens more, §4.3) and the thread-pool's warm
  threads. Take the per-peer permit first, then the node-wide one, both before the lock (R14).
- Backpressure: the bounded inbound channel stays (1,024). The dispatcher additionally counts items queued in lanes and
  stops reading when the session's total reaches the same bound, so a peer can never queue more than today, and the
  receive path blocks as today. Idle lanes are dropped (the lane table holds only lanes with queued work), so 10,000
  channels cost nothing while idle.
- Fairness: one item per permit acquisition; a busy channel cannot hold a permit across items.

### 3.3 Feature flag

`Node:ChannelLanes:Enabled` (default `false`), `Node:ChannelLanes:MaxConcurrentPerPeer` (8),
`Node:ChannelLanes:MaxConcurrent` (32), bound in `NodeOptions` (no init-only or `required` members, for the AOT binder),
validated at the start and by `--check-config` (1..256 and 1..1024). Read when a session is created: a connection is
laned or not for its lifetime. With the flag off the code path is exactly today's loop (the dispatcher with one lane is
not enough to claim equivalence; keep the old loop until the flag is the default). Both nodes need it for symmetric
gains; against LND or CLN only our side matters.

## 4. Database side

### 4.1 SQLite (measured): writers collide and the provider sleeps 150 ms per collision

- Setup: `PRAGMA synchronous=FULL` per connection (`Persistence/Interceptors/SqliteDurabilityInterceptor.cs`); EF creates
  the database in WAL mode (`Infrastructure.Persistence/CLAUDE.md`, NL-810); no native busy timeout is set; EF opens
  write transactions with `BEGIN IMMEDIATE` (Microsoft.Data.Sqlite 10.0.12 `SqliteTransaction`: Serializable and not
  deferred). The configuration template uses `Data Source=nltg.db;Cache=Shared`
  (`Daemon/Extensions/NodeConfigurationExtensions.cs:644`); the test nodes use a private cache.
- Microsoft.Data.Sqlite 10.0.12 retries a busy step in managed code: `while (IsBusy(rc = sqlite3_step(stmt))) {
  sqlite3_reset(stmt); Thread.Sleep(150); }` up to the command timeout, where busy is `SQLITE_BUSY`, `SQLITE_LOCKED` or
  `SQLITE_LOCKED_SHAREDCACHE` (`SqliteDataReader.NextResult`, decompiled). Every collision between two writers of one
  node costs at least 150 ms and blocks a thread.
- Measured (2026-10-10, this machine, the NL-1357 live benchmark at `b0e33729` with 200 channels to one peer, 300
  payments, `dotnet-stack` sampled 6 times during the 8-at-once phase): 14 thread stacks were in that `Thread.Sleep(150)`,
  8 under `BEGIN IMMEDIATE` of an EF save and 6 under an EF batch write; 4 of the 7 inbound-loop samples were inside
  `UnitOfWork.SaveChangesAsync`. Results of that run: one at a time 18.4/s (p50 40 ms, **p95 196 ms**), eight at once
  15.5/s (p50 496 ms, p95 975 ms). The one-at-a-time p95 is one 150 ms sleep on top of the median. Writers that collide
  today, without lanes: the inbound loop, the commit scheduler rounds, the switch and payment saves, the signer guard
  raises and the accounting sealer.
- Raw commit cost here is small: a Python `sqlite3` loop of `BEGIN IMMEDIATE`/insert/`COMMIT` with `synchronous=FULL`
  ran 21,500 commits/s in WAL on APFS with one writer and 7,300/s with eight threads contending (5,100 and 3,200 in a
  rollback journal). (macOS `fsync` does not flush the drive cache; on Linux with a real flush a commit costs more.)
  So fsync is not what limits 17 payments/s; contention and per-message work are.
- Consequence: **lanes on SQLite without a fix would make things worse** (more concurrent writers, more 150 ms sleeps).
  Prerequisite NL-1397: (a) set `PRAGMA busy_timeout` (for example 5,000 ms) in the durability interceptor, so SQLite's
  own handler waits with short sleeps and the provider's 150 ms loop is reached only on a real timeout; (b) a
  process-wide asynchronous SQLite write gate (one `SemaphoreSlim`) around `UnitOfWork` saves and the guard store's
  raise, so writers queue FIFO without sleeping; (c) drop `Cache=Shared` from the template (NL-1398: shared-cache table
  locks return `SQLITE_LOCKED_SHAREDCACHE`, which the busy handler does not serve, and existing configuration files need
  a start-time warning).
- Gain on SQLite: saves still run one at a time, so lanes help by overlapping everything else (EF change tracking and
  materialization, reads, signing and verification, the switch, scope creation, the guard reads) with the single writer.
  Upper bound: one writer at write transactions per payment x time inside the write lock. Rough estimate, to be measured
  in phase 0: 10-15 write transactions per payment per node (transitions plus guard raises) at 0.3-1.5 ms each inside
  the lock gives roughly 50-300 payments/s per node, so the 20/s-per-channel floor holds up to about 3-15 busy channels
  per SQLite node. Not linear; Postgres is the target for hubs.

### 4.2 Postgres

- Saves of different channels write disjoint rows: channel, commitment, HTLC, revocation-log and signer-guard rows are
  per channel; HTLC origins and forward circuits per HTLC; invoices and payments are serialized by the switch's
  per-payment-hash lock and the payment service; accounting rows are insert-only. No concurrency tokens on these
  entities (only `SigningWorkflow` and `VlsChannelMapping` have them). So concurrent transactions do not conflict and
  commit in parallel (Postgres group commit). Phase 0 verifies with a concurrent-save test that no deadlock or
  serialization failure appears (EF does not enable a retry strategy here).
- Expected gain: close to linear in busy channels until CPU (signing and verification), the pool, or the LND index gate
  (§4.4) limits it. The 20/s floor per channel is expected to hold well past 8 busy channels; phase 0 measures.

### 4.3 Durable signer guard (NL-1345, NL-1347) and other sync-over-async

Every guarded signer operation reads the guard row, and each released secret or peer signature writes it, through
`Task.Run(...).GetAwaiter().GetResult()` with a new scope per call; `ChannelSigningInfoSource` does the same for the
signer's lazy load; a fenced synchronous save or signature blocks on the fence's `ValueTask`. With lanes, K concurrent
items can block K thread-pool threads on database round trips; past the pool's warm threads the pool injects threads
slowly (hill climbing) and latency jumps. Mitigations: the node-wide cap (§3.2), a raised minimum thread count while
lanes are on, and NL-1399 (an asynchronous guard path, or the NL-1347 epoch check that skips the read). On SQLite the
guard's raise is a writer and must take the write gate (§4.1).

### 4.4 LND index allocator

`LndIndexAllocator.Gate` is held from the index assignment through the whole save (including the fence check and the
commit) for every save that adds an invoice or a payment or settles an invoice (`UnitOfWork.cs:409-430`). The settle of
a final-hop payment is in the fulfill's save, so with lanes every final-hop receive in the node serializes on one
commit at a time. At a few milliseconds per commit this caps receives at a few hundred per second node-wide; NL-1402
measures it and, if needed, moves the indexes to a database-side counter on Postgres (gap-free in commit order is the
requirement, so a plain sequence is not enough).

### 4.5 Accounting feed and sealer

Rows stay staged in the transition's save. The sealer seals committed rows in id order and seals a late commit in a
later round (dense `LedgerSeq` in seal order, linear chain), which already holds with several peers saving at once. With
lanes the ledger order of one peer's channels follows commit order instead of the old message order; nothing in the
books depends on cross-channel order (posting is per event). No change.

## 5. Phases

| Phase | Content | Size |
|---|---|---|
| P0 | Measurement and SQLite contention fix, independent of lanes: per-item handler time and busy-retry counters (a `DbCommandInterceptor` that counts steps over 140 ms, `nlightning.channels.inbound.handle_ms`), NL-1397 (busy timeout + async write gate), NL-1398 (template), the scale benchmark extension NL-1400 (scenarios in §6.3). Expected on its own: the one-at-a-time p95 back near the p50, and eight-at-once no longer worse than one at a time. | M (2-3 days) |
| P1 | `PeerChannelDispatcher` with peer lane and sticky channel lanes (R1-R11, R14), caps and backpressure (§3.2), the flag off by default, the old loop kept for the flag off. Unit tests on the dispatcher with fakes. | L (4-6 days) |
| P2 | Harness integration: `TwoNodeHarness`/`ThreeNodeHarness` and the splice/dual-fund harnesses deliver through the dispatcher when asked; the flag-on runs of the existing suites; crash injection; new concurrency tests (§6.2). | L (4-5 days) |
| P3 | Thread-pool and database hardening found in P2: NL-1399 (async guard path or warm-pool policy), NL-1402 if the LND gate shows, NL-1403 (fence contract), Postgres concurrency test. | M (2-4 days) |
| P4 | Optional: per-channel reestablish prologue (NL-1401; today 3.4 s for 1,000 channels on one connection), so a reconnecting hub peer's channels become usable in parallel. | M (2-3 days) |
| P5 | Proof: full cluster matrix with the flag on (`scripts/run-cluster.sh --matrix` with an environment switch the test node reads), the scale scenarios on SQLite and Postgres, record in `CHANNEL_SCALE.md` and this plan; the owner decides the default. | M (2-3 days plus matrix wall time) |

## 6. Test plan

### 6.1 Existing suites, with the flag off (unchanged) and on

- Application.Tests: `Node/Managers/PeerManager*Tests` (ordering, inbound restart, batches, reestablish deadline,
  connect races; `PeerManagerTests.Given_EventAndReplyInterleave_*` is the known NL-565 racer and stays in
  `timing-serial`), `Channels/Reestablish`, `Channels/Quiescence`, `Channels/Splicing` (batches, RBF, restarts),
  `Channels/DualFunding` (incl. NL-512's `DualFundSafetyTests`), `Channels/Acceptance`, `Channels/Close`,
  `Channels/Safety`, `Channels/Harness/TwoNodeHarnessTests`, `Payments/Switch/ThreeNodeSwitchTests`,
  `Payments/Send/PaymentHarnessTests`, MPP and trampoline e2e, `Gossip/TaprootGossipProofTests`.
- Integration.Tests non-Docker: the persistence crash-injection tests on SQLite (N5), `Persistence/DurableSignerGuardTests`,
  `UnitOfWorkWriteFenceTests`, the default-run scale tests.
- Cluster: the full default matrix (`lnd`, `cln`, `gossip`, `eclair`, `eclair2`, `ldk`, `day0`, `onchain`, `anchors`,
  `faults`, `abcd`, `taproot`, `postgres`, `cashu`) with the flag on in the test node (Postgres crash injection runs in
  `postgres`), Tor stays Docker-only.

### 6.2 New tests

- Dispatcher unit tests (fakes): per-channel FIFO under random interleaving of 50 channels (property test against a
  sequential model); different channels overlap (a blocked handler on channel A does not stop channel B); sticky
  assignment and the peer-lane barrier for a channel registered during the connection; temporary-id open then real-id
  traffic; a batch of channel X with another channel's message inside (warning + close, as today); permits never taken
  under a lock; caps respected; backpressure (the session total never exceeds 1,024 and the receive path waits).
- Many channels, one peer, in-process (two-node harness through the dispatcher, SQLite file): 50 channels, HTLCs added,
  fulfilled and failed on all of them interleaved from both sides, invariants of both engines checked after each
  quiescent point (the existing two-engine invariant checker).
- Disconnect and reconnect with work in flight: kill the connection while 20 lanes are mid-transition (crash points
  before and after the save, the `CrashableTcpService` pattern), reconnect, reestablish, and require identical balances
  and no lost or duplicated HTLC on both sides; the old connection's late replies never appear on the new one.
- A failing lane: a `ChannelFailedException` on one channel while others keep paying; an error-with-disconnect on one
  channel drops the others' queued items and they recover on the next connection.
- Splice and quiescence on one channel while 10 other channels of the same peer carry payments.
- MPP across 4 channels of one peer with the flag on (set completes once, settle is the commit point), and a forward
  in on channel A and out on channel B of the same peer.
- A slow open decider (`IChannelOpenDecisionGate`) does not stall steady channels.
- Postgres: concurrent saves of distinct channels through the write fence (no deadlock, no serialization failure,
  overlapping transactions observed).

### 6.3 Benchmark (NL-1400, extends `test/NLightning.Integration.Tests/Scale/`)

Each on SQLite and Postgres, flag off and on, reporting payments/s, p50/p95, busy retries, thread-pool queue length and
the per-item handler time:
(a) one peer with 1, 8, 50, 200 and 1,000 busy channels (concurrency equal to the busy channels, capped at 64);
(b) 10, 25 and 50 peers with 4 busy channels each;
(c) one channel under concurrent load (8 and 32 payments in flight) to check the per-channel floor and that lanes do not
slow a single channel.
Run the two nodes in separate processes for the final numbers (one process shares CPU and the thread pool between hub
and peer).

## 7. Risks

- **SQLite regression** if lanes ship without NL-1397: more concurrent writers, more 150 ms sleeps (measured). P0 first.
- **Thread-pool starvation** from synchronous database waits in the signer guard and signing-info source (§4.3).
- **Hidden order dependencies** across channels of one peer that no test covers yet (§1 found none in the code; the
  property test and the flag-on matrix are the net). The sticky assignment and the peer-lane barrier keep the open flow
  exactly as today, which removes the most delicate case.
- **Reply interleaving** across channels changes the byte order on the wire versus today; peers must accept it (BOLT
  does; LND, CLN, Eclair and LDK interleave links themselves). Proven by the matrix.
- **Write fence implementations** that lock per save would serialize everything (R13).
- **Remote signer modes** (native remote signer, VLS) may serialize signing requests on one connection; lanes then add
  little there. Measure before enabling the flag for those modes.
- **Postgres pool exhaustion** if the caps are raised carelessly (each item holds a context; guard calls take more).
- Debugging cost: per-peer logs interleave by channel; log the lane (channel id) on every handler log scope.

## 8. Open questions for the owner

1. Approve P0 (SQLite contention fix) as a stand-alone change first? It is a measured problem today, flag or not.
2. Drop `Cache=Shared` from the configuration template and warn on existing files (NL-1398)?
3. Sticky lane assignment means channels opened on the current connection stay on the serial peer lane until the next
   reconnect. Acceptable, or should a channel move to its own lane at `channel_ready` behind a barrier?
4. Default caps: 8 per peer, 32 node-wide? Should the node-wide cap follow the configured database pool size?
5. Accept that on SQLite the gain is bounded by one writer (estimated 3-15 busy channels at the 20/s floor), with
   Postgres the recommended backend for hub and LSP nodes?
6. Should the flag be refused, or allowed, with the remote signer modes until they are measured?
7. Is the per-channel reestablish prologue (P4) in scope for this work, or a follow-up?

## 9. Issues (filed in `docs/agents/ISSUES.md`, re-numbered from NL-1361..NL-1368)

- **NL-1396 One peer's channel messages are handled one at a time** (feature, medium): the plan above; per-channel
  lanes behind a per-connection dispatcher, flag `Node:ChannelLanes:Enabled` off by default. Evidence: NL-1357 live
  benchmark, 17/s over one connection and no gain from concurrency. Blocked by NL-1397.
- **NL-1397 SQLite writers collide and the provider sleeps 150 ms per busy retry** (performance, medium):
  Microsoft.Data.Sqlite 10.0.12 `SqliteDataReader.NextResult` sleeps 150 ms per `SQLITE_BUSY`/`LOCKED`; no busy timeout
  is set; 14 sampled stacks in that sleep during the benchmark's 8-at-once phase, one-at-a-time p95 196 ms against a
  p50 of 40 ms. Fix sketch: `PRAGMA busy_timeout` in `SqliteDurabilityInterceptor` plus a process-wide async SQLite
  write gate around saves and the guard store's raise; counters for busy retries.
- **NL-1398 The configuration template opens SQLite with `Cache=Shared`** (tech-debt, low): shared-cache table locks
  return `SQLITE_LOCKED_SHAREDCACHE`, which SQLite's busy handler does not serve, so they always take the 150 ms retry.
  Fix sketch: drop it from the template, warn at start when a configuration still has it.
- **NL-1399 The durable signer guard and the signing-info source block thread-pool threads on database round trips**
  (performance, low; related NL-1347): `Task.Run(...).GetAwaiter().GetResult()` per guarded operation; with concurrent
  lanes this can starve the pool. Fix sketch: an asynchronous guard path for the async callers, or the NL-1347 epoch
  check, and a minimum-thread policy while lanes are on.
- **NL-1400 Scale benchmark: concurrency scenarios and handler-time metrics** (test, low): scenarios (a)-(c) of §6.3
  on SQLite and Postgres, nodes in separate processes for final numbers; per-item handler time and busy-retry metrics.
- **NL-1401 The reestablish prologue of a connection is serial over all of the peer's channels** (performance, low):
  `OnPeerConnectedAsync` builds every channel's `channel_reestablish` one at a time before the first message is read
  (3.4 s for 1,000 channels, NL-1357). Fix sketch: each channel lane's first item sends its own reestablish; the
  per-peer resets stay first.
- **NL-1402 The LND index gate serializes every save that settles an invoice or adds a payment** (performance, low):
  `LndIndexAllocator.Gate` is held across the whole save; with parallel lanes all final-hop receives of the node commit
  one at a time. Fix sketch: measure under lanes; on Postgres take the indexes from a counter row updated inside the
  save's transaction (gap-free in commit order).
- **NL-1403 The write fence contract does not say that a save check must not serialize saves** (tech-debt, low):
  document on `INodeWriteFence.CheckSaveAsync` that implementations may take shared, not exclusive, locks; add a test
  that two saves overlap through a fence.

## 10. Record (2026-10-11, `wip/peer-parallel-impl`)

**Built.** P1 (`src/NLightning.Application/Node/Managers/PeerManager.Lanes.cs`): the dispatcher, the sticky lanes with the
peer-lane barrier (R1-R11, R14), the caps and backpressure of §3.2, and the plain loop kept unchanged for a connection
without lanes. Differences from §3: the flag is `Node:ChannelLanes:Enabled` (`bool?`; unset = on for
`Database:Provider` Postgres, off for SQLite and SQL Server, `ChannelLaneOptions.DefaultFor`; a `PeerManager` built
without options keeps lanes off); the `start_batch` warnings are items of the batch channel's lane (they follow that
channel's queued messages; a closing one also stops the dispatcher); P0's SQLite parts and §4.1 were dropped (owner
decision); the harnesses (P2) were not moved onto the dispatcher, because they never used `PeerManager`: the lane
behaviour is proven by the dispatcher tests, every existing `PeerManagerTests`/`PeerManagerBatchTests` case run again
with lanes on (`PeerManagerLanedTests`, `PeerManagerBatchLanedTests`), and two real nodes over one connection
(`Integration.Tests/Scale/ChannelLanesLivePeerTests`: 20 channels, 48 payments 16 at once with lanes on and off, and the
peer restarted while 16 payments are in flight with no HTLC lost or doubled), on SQLite and Postgres; the cluster suites
run with lanes on through `NLTG_CHANNEL_LANES=true` (read by `NLightningTestNode`). P3 found and fixed NL-1404 (Npgsql's
`DISCARD ALL` before every pooled connection reuse: `No Reset On Close=true` by default) and filed what limits the rate
next: durable commits per payment (NL-1405, with the signer guard's own commits, NL-1399) and the workstation GC
(NL-1406). P4 (NL-1401) and the rest of NL-1400 are follow-ups.

**Measurements** (Postgres, 1,000 channels to one live peer, median of three interleaved runs; full table in
`docs/agents/CHANNEL_SCALE.md` "Per-peer parallelism"): lanes off (as before) 8.6 / 16.0 / 20.2 / 9.8 / 22.2 / 27.8
payments/s at 1 / 4 / 8 / 16 / 32 / 64 at once, p50 77 / 215 / 386 / 1,022 / 1,319 / 1,803 ms; lanes on (shipped)
10.1 / 26.5 / 33.6 / 61.1 / 69.2 / 53.5 payments/s, p50 93 / 126 / 188 / 238 / 370 / 996 ms; with `DOTNET_gcServer=1`
9.6 / 29.4 / 39.2 / 90.5 / 108.2 / 94.5. The shared host's load moved single runs by up to 2x.

**Left serial on purpose.** The prologue (our `channel_reestablish` for every channel, NL-1401), the open flow under
temporary ids and every channel first seen unregistered on a connection (the peer lane), the `start_batch` grouping
(the dispatcher), the connection-level messages (they never reach the dispatcher), the one `PeerOutbox` per connection,
and every connection on SQLite unless `Node:ChannelLanes:Enabled=true`.

**Default.** On for Postgres, with the gates in the commit record: the unit and integration suites with the laned
variants, and the cluster matrix `lnd,cln,abcd,postgres` with `NLTG_CHANNEL_LANES=true`. The owner runs the full
matrix.
