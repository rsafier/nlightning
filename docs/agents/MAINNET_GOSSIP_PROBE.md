# Mainnet gossip probe (BOLT 7 at mainnet scale)

Status 2026-09-26, branch `wip/fafo-mainnet-gossip` (from `wip/fafo` @ `f0c2c28`). The first test of NLightning's
BOLT 7 stack against real mainnet peers and the whole mainnet graph, without a mainnet bitcoind. It found and fixed
five interop and efficiency problems (two of them cut every CLN and LND peer off), and measured memory, database and
sync behaviour at mainnet size. Mainnet gossip was off by default then (plan D12; the graph and the sync were opened on
mainnet in wave d12, see "D12 runs" at the end); the probe sets every gossip switch explicitly.

Issue IDs: the probe's findings start at NL-400 (NL-379..NL-399 were reserved for the O7 anchors wave's ledger).
Integrated on `wip/fafo` (2026-09-27, both probe branches cherry-picked with `-x` after wave O7b, last code commit
`bc3a2c3`); the entries NL-400..NL-416 (NL-409 unused) are in `docs/agents/ISSUES.md`. The SHAs in the issue tables
below are the `wip/fafo` ones, with the probe branch's in parentheses.

## Method

- **Harness:** `tools/NLightning.GossipProbe` (a console tool, not product code; `README.md` there). It builds the
  node from the daemon's own composition (`AddNltgNodeServices`, as the Docker test node does) for **mainnet**, with a
  SQLite database and a throwaway key in `~/.nltg-gossip-probe/`, a stub chain (`IBlockchainMonitor` proxy with the
  tip read once from mempool.space, no blocks, throws on any publish; `IFundingOutputLookup` stub that counts calls:
  0 in every run), `Gossip:Enabled`/`SyncEnabled`/`AssumeChannelValid` on, `RelayEnabled`, public channels and
  `Node:EnableHtlcs` off, listening on 127.0.0.1 only. It never opens a channel, sends an HTLC or announces itself.
  `Gossip:SyncPeers` = 5, so every peer runs a range sync and every implementation's replies are checked.
- **Peers** (IPv4 clearnet, node ids and addresses from `mempool.space/api/v1/lightning/nodes/<id>`): ACINQ (Eclair,
  `03864ef0…@3.33.236.230:9735`), bfx-lnd0 (LND, `033d8656…@34.65.85.39:9735`), LNBiG Hub-1 (LND,
  `034ea80f…@213.174.156.79:9735`), Blockstream Store (Core Lightning, `02df5ffe…@35.232.170.67:9735`), noserver4u
  (Core Lightning, `0380ef02…@89.58.53.211:9735`). All five offer `gossip_queries`; Eclair and both CLN nodes also
  `gossip_queries_ex`.
- **Sampling:** every minute: graph counts, the `NLightning.Gossip` meter (MeterListener), per-peer sync state and
  traffic (decorators of the peer services' `IGossipIngress`/`IGossipSyncService`), every `reply_channel_range`
  header with its timestamps, RSS, managed heap, GC, CPU, `probe.db` and WAL. A `dotnet-gcdump` of the live process at
  18 min. `verify` checked 300 random stored channels against mempool.space's Esplora API (2 requests/s).
- **Runs** (raw logs, CSVs, `summary.json` and the console output in `~/.nltg-gossip-probe/runs/<UTC>-<label>/`, the
  fresh run in `~/.nltg-gossip-probe/fresh-fixes/runs/`; run1's database is `~/.nltg-gossip-probe/probe.db`):

| Run | Binary | Duration | Purpose |
|---|---|---|---|
| `…-smoke1-before-fixes`, `…-smoke2-before-fixes` | before any fix | 3 and 1.5 min | found NL-400, NL-401 |
| `20260926T214440Z-smoke3-nl400-nl401` | NL-400, NL-401 fixed | 2 min | first full graph |
| `20260926T214757Z-run1` | NL-400, NL-401 fixed | 60.1 min (stop: plateau 15 min) | the main measurement |
| `20260926T215833Z-verify` | NL-403 fixed | 10 min | Esplora cross-check of 300 channels (`20260926T215707Z-verify` is the first attempt, which found NL-403) |
| `fresh-fixes/…/20260926T221912Z-fresh-fixes` | all fixes | 20.0 min (plateau 10 min) | fresh node with NL-402..405 |
| `20260926T222325Z-timestamps-check` | all fixes, 3 peers (`/tmp` database) | 1 min | the peers' `reply_channel_range` timestamps (NL-404) |
| `20260926T222518Z-no-policy-diagnosis` | all fixes (`/tmp` database) | 3 min | where the channels without policy come from (NL-406) |
| `20260926T224813Z-restart` | all fixes | 15.1 min | restart on run1's database |

## Results

### Graph size and completeness

| | Probe (run1, 60 min) | mempool.space (latest statistics, 2026-08-20) |
|---|---|---|
| Channels | 40,457 | 33,101 |
| … with both policies / one / none (same graph 15 min later, restart run) | 23,922 / 6,767 / 9,778 | |
| Channels with at least one policy (routable) | 30,689 | |
| Nodes (channel ends) | 16,035 | 16,420 (incl. 2,014 unannounced) |
| Nodes with a `node_announcement` | 9,938 | about 14,400 (Tor + clearnet + both) |
| Policies (channel directions) | 54,590 | |
| Capacity from the `htlc_maximum_msat` estimate | 3,341 BTC over 30,689 channels | 3,750 BTC |

- **Peers' own range lists** (`reply_channel_range`): ACINQ 23,796, bfx-lnd0 34,107, LNBiG 38,527, Blockstream Store
  30,608, noserver4u 30,676; our graph is their union. Every final reply reached the queried end; no zlib (encoding 1)
  reply from any peer.
- **Verify** (300 random stored channels, all `Assumed`): **300/300 funding outputs exist, are the P2WSH 2-of-2 of the
  announced bitcoin keys and are unspent**; none spent (peers stop serving closed channels). 87 of the 300 (29 %)
  had no policy. Capacity estimate (`EstimatedCapacityMsat` / real capacity, 213 channels with a policy): p10 0.94,
  median 0.99, p90 1.00, never above the capacity.
- **The 9,774 channels without any policy** (24 %; NL-406): no `channel_update` for them ever arrived. They were
  first announced by the CLN peers (noserver4u 5,996, Blockstream 3,495, LND 283), mostly right after our
  `gossip_timestamp_filter` (Blockstream Store sent 9,781 announcements with about 900 updates in the minute after
  it). Their funding outputs are unspent, their nodes long gone (sampled on mempool.space: last update 2018-2024, no
  active channel). BOLT 7: "If a `channel_announcement` has no corresponding `channel_update`s: MUST NOT send the
  `channel_announcement`". They are never routed through (no policy), but cost memory and database rows, and the 6,097
  unannounced nodes are exactly their ends (only 52 channel ends of channels with a policy lack an announcement).
- Mainnet peers' `reply_channel_range` timestamps (`gossip_queries_ex`) mark almost no channel stale (0-2 per reply),
  so NL-404 (skip stale unknown channels) removes little today; the zombies above arrive outside the range sync.

### Time to sync and traffic

| | run1 (NL-400/401 only) | fresh-fixes (all fixes) |
|---|---|---|
| 50 % / 99 % of the final channels | 1.0 / 2.0 min | 1.0 / 2.0 min |
| Slowest range sync (completed) | 674.7 s (LND, 156 queries) | 66.8 s |
| Messages received / applied (first 20 min) | 548,799 / 111,157 after 60 min (4.9x) | 205,806 / 107,057 (1.9x) |
| channel_announcements received / applied | 154,428 / 40,458 | 73,161 / 40,447 |
| Queries asked of the LND peers | 156 + 158 (about 31,000 channels each) | 15 + 15 (3,000 each) |

- Before NL-400 the two CLN syncs ended at the first reply with a warning (`first_blocknum … is lower than the
  previous reply's`), and before NL-401 bfx-lnd0 was disconnected about 15 s into the sync (a 128-byte `channel_update`); the
  graph then reached 18,015 channels in 3 min instead of 40,449 in 2 min.
- **Eclair (ACINQ) stops answering `query_short_channel_ids`** after 4 queries of 200 channels (800 announcements) in
  every run: the fifth gets no `reply_short_channel_ids_end` within `SyncReplyTimeout` (2 min), the connection's
  query slot is closed and it gets the live filter; its live gossip keeps flowing (9,000 updates over the hour).
  Possibly a per-peer query limit on a node we have no channel with (NL-407, not diagnosed).
- LND answers a 200-channel query in about 4 s (its outbound gossip rate limit), so a full sync from LND alone takes
  about 11 minutes.
- Nearly every `channel_update` of the initial sync is first orphaned (53,593 in run1): announcements and updates
  of one channel are validated by different workers in parallel (12 on this machine), so the update often wins the
  race; it is replayed once the announcement is stored. Correct, but a second pass per update (NL-408, low).
- Honest traffic and the limits: no peer banned, no misbehaviour, no queue drop in run1 (the smoke run before NL-402,
  with five full syncs at once, dropped 788 messages of one CLN peer at its 2,000-message queue, re-queried by the
  missed-SCID retry), no orphan-cache or rate-limit overflow. `node_announcement` rate limit: 33,694 refusals (the newest kept and applied 10 min later; the kept set
  peaked at 579 of 10,000); `channel_update` keep-alive rule 4,691, stale 10,190, outdated 4,035; 38 node
  announcements more than 14 days in the future. One B7-CA-04 blacklisting (`828074x797x0`, two validly signed
  announcements of one funding output by different node pairs: a 2024 mainnet test, "channel node switching - node
  7/8"; 1 channel forgotten, as specified).
- No warning or error logged besides the `AssumeChannelValid` startup warning and the B7-CA-04 notice; the only
  warning sent was NL-401's, once per run (one 128-byte `channel_update` from an LND peer, connection kept); 0
  disconnections of the five peers in 60 min.

### Memory, CPU and database

| | Value |
|---|---|
| Live managed heap at 18 min (`dotnet-gcdump`, after a full GC) | 114.8 MB (874,759 objects) |
| `GraphStore` memory estimate | 99 MB (G-D's calibration holds: 2.5 KB per channel) |
| GC heap after the last GC (steady) | 200-230 MB |
| RSS peak / steady | 425 MB / 400 MB |
| CPU | 89-98 % of one core in the first minute, 1 % steady |
| `probe.db` (SQLite) / WAL | 50.2 MiB / 5.8 MiB, WAL bounded (checkpoints seen: the file changes, the WAL stays at 5.8 MiB) |
| Write-behind | 570 flushes, 7.7 s in total, max 0.20 s, pending changes at most 618 |
| Final flush at stop | 58 changes in 4.6 ms |

- Compared with G-D's synthetic 200,000 channels (475 MiB store): mainnet is 40,457 channels, a fifth of that, and
  the measured live heap matches the estimate. The RSS is about twice the live heap: GC segments not returned plus
  the runtime; `Gossip:MaxMemoryMb` (NL-373) should budget the GC heap, not only the store.
- In the heap the origin tracker of the relay held about 15 MB while the relay was off (fixed, NL-405); the
  duplicate filter (`RecentMessageCache`, 50,000 hashes) about 3 MB, the rate limiter's buckets about 2 MB.

### Restart

Run `20260926T224813Z-restart` (all fixes) on run1's database, started 5 s after run1 stopped, 15.1 min:

| | Value |
|---|---|
| Graph load (`GraphStore.LoadAsync`, 40,457 channels, 9,938 nodes, 54,590 policies) | 0.55 s |
| Heap retained by the loaded graph (full GC before and after) / RSS growth | 128 MB / 91 MB |
| RSS after the load / at 15 min | 296 MB / 350 MB |
| Range syncs | 4 completed in at most 12.9 s; Eclair again timed out a query |
| Asked for after the restart | LND: 0 and 1 channels (no timestamps: unknown channels only); CLN: 2,139 and 2,141, Eclair 1,562 (known channels whose peer timestamp is newer than our policy, mostly directions we have none for) |
| Received / applied | 50,307 / 1,695 |
| … of which `channel_announcement`s CLN re-sent, all already known | 32,022 (the NL-406 announcements again, 12,401 and 19,621) |
| Final graph | 40,467 channels, 54,611 policies |

So a restart re-downloads only deltas from LND and the `gossip_queries_ex` peers, but Core Lightning streams its
announcements without updates again on every connection (NL-406), which the duplicate filter drops cheaply.
The peer manager reconnected the five stored peers by itself at start. `probe.db` did not grow (50.2 MiB); the WAL was
checkpointed at the restart (0.5 MiB) and stayed under 4 MiB.

## Issues found

| ID | Severity | Status | Problem |
|---|---|---|---|
| NL-400 | High (interop) | fixed (df56139; c68e61f) | Core Lightning's `reply_channel_range` sequence has unsorted ids and a `number_of_blocks` that wraps in 32 bits (`918664 + 4294956303`), the next reply starting at the wrapped end, below the previous `first_blocknum`. `RangeReplyCollector` rejected it: the sync with every CLN peer ended with a warning. Now LND's continuity rule (a reply may start at the previous reply's u32 end or the block before it) is accepted, and ids inside the query are kept even outside their own reply's claimed blocks. Upstream CLN issue worth reporting (both nodes, versions unknown). |
| NL-401 | High (interop) | fixed (b4d0e1c; 58541c6) | LND answers queries with pre-2022 `channel_update`s without `htlc_maximum_msat` (128 bytes). The payload parser rejected them and `MessageService` warned and closed the connection: every LND peer that stored one was cut during the sync. Malformed 256/257/258 are now ignored with one warning per connection. |
| NL-402 | Medium (efficiency) | fixed (41d2e45; c287316) | Each sync peer diffed its range reply once, at the start, and then asked for everything the others had delivered meanwhile: 4.9x the graph downloaded, LND syncs of 11 min. Each batch is now diffed again before it goes out (1.9x, 67 s). |
| NL-403 | High (mainnet only) | fixed (7f72785; dc5b24a) | `SecureKeyManager.FromFilePath` compared NBitcoin's network name (`Main`) with `mainnet`: a mainnet node could not read its own key file after the first start. |
| NL-404 | Low | fixed (e64b5f4; 924df96) | With `gossip_queries_ex`, unknown channels whose both update timestamps are stale or missing are not asked for (`Gossip:SkipChannelsStaleFor`, 14 days; LND's zombie rule). Mainnet peers report almost none today (see NL-406). |
| NL-405 | Low (memory) | fixed (63ecfbe; e771eb4) | `OriginTrackingGossipIngress` recorded up to 100,000 origins (about 15 MB) while the relay of others' gossip was off. |
| NL-406 | Medium (memory, spec) | open | 24 % of the graph (9,774 channels) are announcements without any `channel_update`, sent by CLN (mostly on our `gossip_timestamp_filter`) against BOLT 7's MUST NOT. We store them; they are removed only by the pruner's stale rule 28 days after they arrive (and only while blocks come), and come back with the next CLN connection. Proposal: keep an announcement without update in a bounded pending cache (like the orphan cache, TTL) and store it only with its first update; or a zombie index (LND) of pruned SCIDs. Upstream CLN issue worth reporting. |
| NL-407 | Low (interop) | open | ACINQ (Eclair) stops answering `query_short_channel_ids` after the fourth 200-channel query from a peer without channels; we end the querying of that connection after 2 min. To check against Eclair's source (a per-peer query budget?) and with a peer we have a channel with. |
| NL-408 | Low (efficiency) | open | The initial sync orphans nearly every `channel_update` (workers race the announcement of the same channel) and replays it. Partitioning the ingress queue by short channel id would keep a channel's messages on one worker. |

Carried and confirmed: NL-373 (memory budget: budget the GC heap, see above), NL-376 (the soak's WAL concern does not
reproduce here: the WAL stays at 5.8 MiB and the database file is checkpointed; RSS flat at 400 MB over the hour), NL-099.

## What remains for mainnet gossip (D12)

1. **Chain verification** (measured since against an unpruned node: see "Verified run (real bitcoind)" below). `Gossip:AssumeChannelValid` is a probe/light-node mode: the verify run found no invalid
   channel in 300, but anyone can inject channels, their capacity is estimated and closed channels are only pruned
   when stale. Full D3 verification needs `getblock <hash> 1` for every channel's funding block (heights from 2018)
   plus `gettxout`:
   - an **unpruned node**: the mainnet chain is roughly 0.7-0.8 TB in 2026 (estimate, not measured here; this machine
     has 109 GB free), IBD 1-3 days depending on CPU, disk and bandwidth (`-dbcache` large);
   - a **pruned node** (`prune=550`+): about 12 GB of chainstate plus the kept blocks, but `getblock` fails below the
     prune height, so almost every channel would be `Unverified` (`FundingValidation=SkipUnavailable`); `gettxout`
     still works for any unspent output;
   - **assumeutxo** (Bitcoin Core 28+: `loadtxoutset` of a published UTXO snapshot, about 9 GB): usable in about an
     hour plus catching up from the snapshot height, background validation afterwards; pruning allowed, so the same
     `getblock` gap as a pruned node for old channels.
   A pruned or assumeutxo node could still verify every channel if the SCID → txid step came from elsewhere: take
   the txid from an index (an Esplora/Electrum server, untrusted) and check `gettxout txid vout` against our own UTXO
   set (the output exists, is unspent, pays the 2-of-2 of the announced keys, and its confirmations put it at the
   SCID's height). Only the transaction index inside the block stays unchecked. Proposed as the D3 variant for nodes
   without an archival bitcoind (new `IFundingOutputLookup`).
2. **NL-406** (announcements without updates) before relay is enabled on mainnet, so we never store or later serve
   what BOLT 7 says must not be sent.
3. **Memory budget** (NL-373) with the measured mainnet numbers: about 115 MB live heap, 400 MB RSS.
4. **Relay on mainnet** (done 2026-09-28, see "Relay proof (NL-417, 2026-09-28)" below; relay on by default on mainnet since) was not tested at first (the probe is a leech by design): the relay's pacing and filters against real
   mainnet peers, and our query replies to them (served only for chain-checked channels, so a probe with
   `AssumeChannelValid` serves nothing), still need a run with a verifying node. Since lane nl360 (NL-360) a slow
   peer can no longer grow our memory: the outbox holds at most 10,000 gossip messages / 4 MiB per connection (about
   9 MB of heap) and the relay pauses that connection, resuming at half, and drops its backlog only after 10 min
   without progress. A relay run should record `nlightning.gossip.outbox.refused`, `relay.paused`, `relay.stalled`
   and `describegraph`'s `Relay:` line next to RSS.
5. The 24 h Mutinynet soak (NL-376) and a long mainnet run (days) for slow growth: this hour showed a flat RSS and
   a bounded WAL.

## Verified run (real bitcoind)

Status 2026-09-27, branch `wip/fafo-mainnet-gossip-verified` (from `wip/fafo-mainnet-gossip`). The same probe and
peers, but with `Gossip:AssumeChannelValid` **off**: every `channel_announcement` goes through the product's D3 path
against the owner's **unpruned mainnet bitcoind** on the LAN (Bitcoin Core; no `txindex` needed). New issue IDs
start at NL-410 (NL-388..NL-399 may be taken by another wave); `docs/agents/ISSUES.md` was not edited on the branch (see the integration note at the top).

### Method

- **Harness:** `tools/NLightning.GossipProbe run --chain rpc` (README there). The node is built as before, but the
  chain side is the product's: `BitcoinChainService` (NBitcoin `RPCClient`) and `FundingOutputLookup`
  (`getblockcount`, `getblockhash`, `getblock <hash> 1`, `gettxout`; LRU of 256 txid lists), with
  `Gossip:ChainLookupConcurrency` = **3** for these runs (with the block follower and the per-sample
  `getblockchaininfo`, at most 4-5 RPCs in flight) and the default `ChainLookupsPerSecond` = 50 (never binding).
  The probe only decorates the two services to count and time every call (`lookups.csv`, `summary.json` → `chain`)
  and counts every HTTP request of the process through `System.Net.Http`'s meter (the RPCs; the peers are plain TCP).
  `IBlockchainMonitor` is `RpcBlockFollower`, a poller: every 15 s it reads the blocks connected since the start (at
  most 10 per poll, `getblock` verbosity 0, one at a time, no history) and raises `OnBlockInputs`, so the
  `GraphPruner`'s spend detection runs on real blocks; `LastProcessedBlockHeight` (the range sync's end) is
  bitcoind's header height, so a bitcoind in IBD still gets the whole graph offered.
- **RPCs:** read-only only (`getblockchaininfo`, `getblockcount`, `getblockhash`, `getblock` 0/1, `gettxout`); no
  wallet call, no rescan, nothing published (the follower throws on any publish). The credentials go from the mode-600
  env file into the node's in-memory configuration only.
- **NL-276 check:** a host `dotnet` process reached the LAN bitcoind (`GossipProbe chaininfo`: `getblockchaininfo`
  in 226 ms, `getblock` of a 5,860-tx block in 148 ms), so no container was needed on this Mac.
- **Runs** (raw data in `~/.nltg-gossip-probe/verified/runs/<UTC>-<label>/`: `probe.log`, `samples.csv`,
  `peers.csv`, `range-replies.csv`, `lookups.csv`, `summary.json`; console output in
  `~/.nltg-gossip-probe/verified/console-*.txt`; the smoke run in `/tmp/nltg-verified/smoke/runs/`. The
  AssumeChannelValid database `~/.nltg-gossip-probe/probe.db` is untouched, for comparison):

| Run | Binary | Duration | bitcoind | Purpose |
|---|---|---|---|---|
| `20260926T233109Z-smoke` (fresh `/tmp` database) | before NL-410..412 | 5.1 min | **in IBD**: blocks 966,116 → 968,655 of 968,752 headers (about 500 blocks/min); IBD ended at the stop | IBD behaviour, SCIDs above the tip |
| `20260926T233806Z-verified-run1` (fresh database) | before NL-410..412 | 60.1 min (plateau from 19 min) | synced (968,754 → 968,763) | the main measurement |
| `20260927T003924Z-verified-restart` (run1's database) | NL-410..412 fixed | 8.1 min | synced | reload of a verified graph, deltas, the fixes |

### Results

**Verified graph vs the AssumeChannelValid run**

| | AssumeChannelValid run1 (2026-09-26 21:47 UTC) | Verified run1 (23:38) | Verified, after the restart (00:47) |
|---|---|---|---|
| Channels | 40,457 (all `Assumed`) | **39,663** (all `Verified`, capacity from the chain) | 40,347 |
| … with two / one / no policy | 23,922 / 6,767 / 9,778 | 23,811 / 6,761 / 9,091 | 23,813 / 6,762 / 9,772 |
| Nodes with a `node_announcement` | 9,938 | 9,926 | 12,499 |
| Policies | 54,590 | 54,383 | 54,388 |
| Capacity | 3,341 BTC (estimate from `htlc_maximum_msat`) | **3,747.6 BTC** (funding outputs) | 3,754.6 BTC |

- The routable part (channels with a policy) matches within 0.4 %: 30,572 verified vs 30,689 assumed, two hours
  apart. The 685 channels the restart added are all without a policy and first announced by the two CLN peers:
  NL-406 announcements that run1 dropped at a full per-peer queue (CLN's unsolicited flood, below) and that no other
  peer serves; the restart got them from CLN again.
- **Rejected by the chain: only spent outputs.** `OutputSpentOrMissing` 135 lookups (124 distinct channels, SCID
  heights 832,652..968,734, median 967,218: mostly closed in the last ten days and still served by the peers) in
  run1, 143 in the restart (the duplicate filter is memory only, so every restart looks the peers' closed channels
  up again). **0 `ScriptMismatch`, 0 `AmountMismatch`, 0 `TransactionIndexOutOfRange`, 0 `BlockUnavailable`**: no
  mainnet peer relayed an announcement that contradicts the chain.
- **Above the tip / IBD** (smoke run): 4 lookups were `BlockNotFound` (SCIDs at 968,134..968,539 while bitcoind was
  at 966,xxx): deferred, never scored or rejected (G-A lane A3's rule holds in the product: `BlockNotFound` is
  transient, `GossipIngress` defers it, `FundingOutputLookupTests` covers it). They were not looked up again within
  the 5 minutes: a retry goes to the back of the ingress queue, which held 4,000-5,000 messages at 11 lookups/s
  (about 7 minutes), so the effective retry delay is 30 s plus the queue. 26 lookups (0.8 %) ended `ChainMoved`:
  `BitcoinChainService.GetUnspentOutputAsync` derives the output's height from `gettxout`'s confirmations and a
  separate `getblockcount`, which races a bitcoind connecting ~8 blocks/s (NL-413).
- **Transient:** one `ChainUnavailable` in 269,394 requests (bitcoind closed a kept-alive HTTP connection; the lookup
  was deferred and succeeded later). `OutputSpentInMempool`: 184 lookups, all of **one** channel (`968539x658x1`, a
  close waiting in the mempool): 10 retries 30 s apart, given up (the 15 `retries_exhausted` drops), marked missed,
  re-queried and looked up again, about 3 lookups a minute for the whole hour (NL-414).
- **Peers:** 0 disconnections, 0 bans, 0 `chain_mismatch` rejections, no warning sent over a chain result. A spent
  output is `Limited(funding_spent)`, never counted against the peer; the ban path (script, amount or index
  contradictions) never fired. One B7-CA-04 conflict (`828074x797x0`) in the restart, as in the earlier runs.

**Time and RPC load (run1, concurrency 3)**

| | Value |
|---|---|
| Funding lookups | 40,262 (39,942 found, 135 spent, 184 spent in the mempool, 1 unavailable) |
| Time to 50 / 90 / 99 % of the final graph | 9.0 / 17.0 / 19.0 min (AssumeChannelValid: 1.0 / 2.0 min to 50 / 99 %) |
| Lookup throughput | 2,000-2,500 per minute (about 37/s) for 17 min, then about 10 per minute |
| Lookup time (the wait for the limits included) | p50 379 ms, p90 465 ms, p99 592 ms, max 1.5 s |
| HTTP requests (RPCs) | 269,394; 13,700-17,200 per minute during the sync (about 250/s), about 30 per minute after it |
| RPCs per lookup | 6.7 (5 on a cached block, 7 on a miss; 84 % of lookups missed the 256-block cache) |
| RPC latency (all) | p50 6.0 ms, p90 27 ms, p99 100 ms; one 27.9 s outlier (the dropped connection) |
| `getblock <hash> 1` (`GetBlockTxIdsAsync`, 33,953 calls) | p50 42 ms, p90 69 ms, p99 144 ms |
| `gettxout` (+ `getblockcount`) | p50 12 ms, p90 18 ms, p99 108 ms |
| Data read (estimate, not measured) | about 34,000 txid lists of 2,000-6,000 txids (about 67 bytes each in JSON): roughly 5-10 GB over the LAN in 17 min |
| During IBD (smoke) | 11 lookups/s at the same concurrency; RPC p50 6 ms but p99 600 ms (bitcoind busy connecting blocks); IBD kept its pace |

The lookups are bound by the concurrency (3 lookups of about 80 ms of RPC round trips each), not by the rate limit.
bitcoind was not visibly affected: it kept connecting blocks at full speed during the smoke run and answered p99
within 100 ms once synced. At the default concurrency 4 the whole graph would take about 13-14 minutes.

**Spent channels**

- At lookup: the 124-143 announcements of already closed channels were rejected (`funding_spent`), not stored, and
  kept in the duplicate filter, so their re-sends in the same process are dropped without a lookup.
- After storing: the follower raised 8 new blocks (64,451 spent outpoints) in the hour; the `GraphPruner` matched 3
  graph channels (`968722x2786x1`, `957204x1875x0`, `968706x196x1`) and marked them spent at the spending block,
  for removal 72 blocks later (not reached in the run). No bitcoind call is needed for this (D4).
- A close still in the mempool is not spent yet: NL-414.

**Memory, CPU, database**

| | Verified run1 | AssumeChannelValid run1 |
|---|---|---|
| RSS peak / after the sync | 630 MB / 456 MB (at 60 min, after a gen-2 GC) | 425 MB / 400 MB |
| GC heap size during / after the sync | 408-425 MB / 255 MB | 200-230 MB |
| CPU | 18-36 % of one core for 18 min, 1 % after | 89-98 % in the first minute, 1 % after |
| `probe.db` / WAL | 49.7 MiB / 5.2 MiB | 50.2 MiB / 5.8 MiB |
| Restart: graph load (39,663 channels) / retained heap | 0.58 s / 138 MB | 0.55 s / 128 MB |
| Restart: funding lookups | 829 (the 685 new channels and 143 spent re-sends; the stored channels' funding txids are persisted, NL-352) | 0 |

The extra ~200 MB during a verified sync is garbage: every `getblock <hash> 1` answer (100-400 KB of JSON parsed into
Newtonsoft `JToken`s) lands on the large object heap (NL-416).

**Sync traffic with verification:** 142,240 `channel_announcement`s received for 39,663 channels (3.6x; 79,024
`already_known`), because each of the five sync peers was asked for 20,000-25,000 channels: NL-402's per-batch
re-diff skips channels already **stored**, but with the chain check the store lags the queue by minutes (NL-415).
The duplicates cost bandwidth and ingress work, not lookups (the duplicate filter and the store check run before the
lookup: 40,262 lookups for 39,663 channels). 28,785 messages were dropped at a full per-peer queue, almost all from
the two CLN peers' unsolicited announcement floods (18,969 and 9,816; NL-406). With bitcoind synced the backpressure
never timed out; in the IBD smoke run every sync peer's `query_short_channel_ids` waited the full 2 minutes for the
**global** queue and then went out anyway (NL-412). Eclair answered 109 paced queries during run1 (NL-407 does not
reproduce at this pace; one later query, at 00:20, and one in the restart timed out).

### Issues found (NL-410..NL-416)

| ID | Severity | Status | Problem |
|---|---|---|---|
| NL-410 | Medium | fixed (788d2d3; ed2ddb1) | A `channel_update` dropped at a full queue whose channel's announcement was stored was never asked for again: `GossipSyncManager.RetryMissedShortChannelIds` dropped every missed SCID already in the graph, and a peer without `gossip_queries_ex` never offers the update again. With the chain check the store lags the queue, so updates are orphaned (56,093 in run1) or dropped far more often than with AssumeChannelValid. Now a stored channel stays in the retry until it has both policies (or is spent); one query per miss. Test `Given_ADroppedUpdateOfAStoredChannel_When_Retried_Then_ItIsQueriedAgainUntilBothPoliciesAreKnown`. |
| NL-411 | Low (RPC load) | fixed (1a77f88; 5222fcd) | `FundingOutputLookup` called `getblockhash` before every txid-list fetch although `GetBlockTxIdsAsync` reads the hash itself: 7 RPCs per lookup on a cache miss (84 % of mainnet lookups), now 6 (about 14 % fewer RPCs for a full sync). A cached list is still checked against `getblockhash` first. Test `Given_AHeightNotCached_When_Lookup_Then_NoGetBlockHashBeforeTheTxIdList`. |
| NL-412 | Medium (sync speed, drops) | fixed (788d2d3; ed2ddb1) | The sync's backpressure (NL-353) waited for the **whole** ingress queue to be at most half of one peer's capacity (1,000 messages), at most `SyncReplyTimeout` (2 min), then queried anyway. With the chain check the ingress drains at the lookup rate, so one peer's flood (CLN) held every other sync peer back 2 minutes per 200-channel batch, and the query then went out into a queue that could drop its answer (IBD smoke: every peer, every batch). Now the querier waits for the queried peer's own queue (`GossipIngress.QueuedCountOf`), where the answer goes, without a time limit (logged once after the timeout). Test `Given_PerPeerQueueDepths_When_Syncing_Then_OnlyThePeersOwnQueueHoldsTheQueryBackWithoutTimeLimit`. |
| NL-413 | Low | open | `BitcoinChainService.GetUnspentOutputAsync` computes the output's height as `getblockcount - confirmations + 1` with a `getblockcount` after `gettxout`: a block connected in between shifts it by one and the lookup answers `ChainMoved` (transient, deferred). 0.8 % of lookups while bitcoind connected ~8 blocks/s in IBD, none once synced. Fix: the height of `gettxout`'s `bestblock` (`getblockheader`), the same RPC count. |
| NL-414 | Low (RPC load) | open | A channel whose funding output a mempool transaction spends (`OutputSpentInMempool`) is looked up every `RetryDelay` for `MaxRetries`, then dropped as missed, re-queried from a peer and looked up again, for as long as the close stays unconfirmed (days for a low-fee close): 184 lookups (about 1,200 RPCs) for one channel in the hour. Proposal: keep mempool-spent SCIDs aside and look them up again once per new block. |
| NL-415 | Medium (bandwidth) | open | With the chain check, five concurrent sync peers were each asked for 20,000-25,000 channels (3.6x the graph received; 1.9x with AssumeChannelValid): NL-402's re-diff sees only stored channels while thousands wait in the ingress for their lookup. Proposal: the ingress keeps the SCIDs whose `channel_announcement` is queued, deferred or being looked up, and the re-diff skips them; or fewer concurrent range syncs while the chain is checked. |
| NL-416 | Low (memory) | open | A verified sync peaks at 630 MB RSS (GC heap 425 MB) against 425 MB with AssumeChannelValid: `BitcoinChainService.GetBlockTxIdsAsync` parses each `getblock <hash> 1` answer (100-400 KB) into a `JToken` tree on the large object heap; it is returned after the sync (456 MB RSS, 255 MB heap). For NL-373: budget the sync's garbage, or read the txids with a streaming `JsonReader`. |

Confirmed: NL-406 (all 9,091 channels without an update are **unspent on chain**, so D3 does not remove them; the
CLN peers streamed them unsolicited again, 28,785 dropped at their full queues in run1 and 5,211 in the restart),
NL-352 (funding txids persisted: the restart looked up only new channels). Probe-only fix: the probe now also stops
gracefully on SIGTERM (a background-started process ignores SIGINT; run1 therefore ran to its 60-minute stop rule).

### What this means for D12

- **Full D3 verification against a local unpruned node is practical at mainnet scale.** The whole graph (about
  40,000 channels) is verified in about 18 minutes at concurrency 3 (about 13-14 at the default 4) with about 270,000
  small read-only RPCs (about 250/s during the sync, p99 100 ms; about 14 % fewer with NL-411) and roughly 5-10 GB of
  `getblock` JSON over the LAN; a restart costs only the new channels (829 lookups). bitcoind kept up, even during
  IBD. A node on the same host as bitcoind would see lower latencies; one on a slow link would want a cheaper txid
  read (`getblock <hash> 0`, or a persistent txid cache).
- **An unpruned node in IBD** is handled by design: SCIDs above its tip are deferred, never rejected or scored, and
  looked up again (after the retry delay plus the ingress queue); outputs spent after the node's tip look unspent
  until it catches up, and the pruner marks them spent from the blocks as they are connected. Only the first half
  was observed (5-minute smoke; bitcoind left IBD during it).
- **Remaining before enabling mainnet gossip:** NL-406 (announcements without updates: verified unspent, so only the
  pending-cache or zombie-index proposal removes them), NL-415 (redundant sync downloads with verification), NL-414
  (mempool closes looked up repeatedly), NL-413, the memory budget with the verified numbers (NL-373, NL-416: 630 MB
  peak, 456 MB after), relay against mainnet peers (untested; a verified node serves `IsChainChecked` channels, so a
  relay run is now possible), the pruned/assumeutxo variant (the Esplora-txid plus local `gettxout` lookup proposed
  above, untested) and the long soaks (NL-376).

## D12 runs (wave d12, 2026-09-27)

The integrated wave d12 build (`wip/fafo` @ `8b97462`: lanes Z1 graph hygiene, Z2 memory budget, Z3 verified-sync
efficiency, Z4 pruned funding lookup) against the same five peers and the same unpruned mainnet bitcoind, read-only
RPCs, `Gossip:ChainLookupConcurrency` 3 (at most 4-5 RPCs in flight with the follower and the sampler). Raw data in
`~/.nltg-gossip-probe/d12-verified/runs/` (console output in `console-*.txt` there). The Docker suites ran on the same
machine during the verified run (CPU contention; the lookups are RPC-bound, so the timing comparison still holds).

| Run | Duration | Purpose |
|---|---|---|
| `20260927T030027Z-d12-verified` (fresh database, `AssumeChannelValid` off) | 30.1 min (99 % at 13.0 min, plateau from 20 min) | the D12 verified measurement |
| `20260927T033114Z-d12-relay` (relay toward ACINQ) | 3.0 min, stopped | ACINQ (Eclair) sends no `gossip_timestamp_filter` to us: nothing can be relayed to it |
| `20260927T033500Z-d12-relay-blockstream` (restart of the verified database, relay toward Blockstream Store) | 20.1 min | the relay run |

**Verified run, compared with `20260926T233806Z-verified-run1`:**

| | Verified run1 (before d12) | d12 verified |
|---|---|---|
| Graph channels (all `Verified`) | 39,663 (9,091 without any policy) | **30,537**, every one with a policy (23,755 two, 6,782 one); the announcements without update wait in the pending index (NL-406: **9,775** after the restart below, about 4 MB), outside the graph, never looked up, served or relayed |
| Policies / announced nodes | 54,383 / 9,926 | 54,292 / 9,916 |
| Time to 50 / 90 / 99 % of the final graph | 9.0 / 17.0 / 19.0 min | **7.0 / 12.0 / 13.0 min** |
| Funding lookups | 40,262 (184 spent in the mempool, 26 `ChainMoved`) | **30,717** (30,537 found, 180 spent; 0 mempool-spent, 0 `ChainMoved`, 0 unavailable) |
| HTTP requests (RPCs) / per lookup | 269,394 / 6.7 | **179,210 / 5.8** (13-14.8k per minute during the sync, 5-30 per minute after) |
| Lookup time p50 / p90 / p99 | 379 / 465 / 592 ms | 352 / 426 / 523 ms |
| `channel_announcement`s received | 142,240 (3.6x the graph; 79,024 `already_known`) | **92,653** (2.3x the 40,312 announcements known, graph plus pending; 26,672 `already_known`) |
| … of which from the three queried peers (LND x2, Eclair) | 20,000-25,000 each | 7,651 + 8,804 + 6,802 = 23,257 in all (NL-415) |
| … unsolicited from the two CLN peers | 18,969 + 9,816 dropped at a full queue | 33,213 + 36,183 received, 25,490 refused at the door; 32,137 dropped at a full peer queue in all |
| Orphaned `channel_update`s | nearly every update of the sync (NL-408) | **540** (per-scid partitioned workers); 20,876 orphaned `node_announcement`s are nodes whose only channels are pending (replayed on promotion, else expire) |
| RSS peak / at the end | 630 MB / 456 MB (60 min, after a gen-2 GC) | **610 MB / 610 MB** (30 min; GC heap 426 MB peak, 415 MB at the end); graph estimate 83 MB |
| `Gossip:MaxMemoryMb` (1,024 MB) | not enforced | never crossed: 0 refusals, no budget warning |
| CPU | 18-36 % for 18 min | 26-35 % for 13 min, then 1 % |
| `probe.db` / WAL | 49.7 / 5.2 MiB | 42.6 / 5.2 MiB |
| Peers | 0 disconnections, 0 bans | **0 disconnections, 0 bans, 0 warnings** in either direction, 0 chain contradictions |

- The RSS at the end is still the verified-sync garbage (NL-416: `getblock` JSON on the LOH, not returned within the
  30 minutes); it stays 40 % below the budget, and the budget refuses only new channels and nodes, so crossing it
  would slow the graph's growth, not break the node.
- The restart (relay run, same database): graph load 0.72 s (114 MB retained heap), 156 lookups (spent channels the
  peers still serve; the duplicate filter is memory only), 33,601 `already_known` announcements (the CLN peers stream
  the graph again whatever filter we send), 9,775 announcements back in the pending index, RSS 373-492 MB.

**Relay run.** `tools/NLightning.GossipProbe run --relay-to <peer>` turns `Gossip:RelayEnabled` on with a peer
directory that lists only that peer (the others stay sync peers), and records every relay send, outbox refusal and
echo to origin. The node has no channel and announces nothing. Result over 20 min toward Blockstream Store (and
3 min toward ACINQ): **0 messages relayed**, 0 outbox refusals, 0 echoes, relay backlog (`relay_pending`) at most 192
and draining, outbox gossip 0, 0 disconnections and 0 warnings from any peer. None of the five peers subscribes to
our gossip: Eclair and both LND nodes send no `gossip_timestamp_filter` at all (LND only to its few active sync peers),
and both CLN nodes send `first_timestamp = 0xFFFFFFFF` (nothing matches), which our relay honours (BOLT 7 B7-RL-01:
nothing before the peer's filter, only messages inside it). So the relay toward mainnet peers is safe but **still
unexercised**: a leech with no channels is not a gossip source for them. Proving it needs a node the peer has a
channel with, or a peer we control that asks for everything (NL-417).

**D12 decision:** see the BOLT 7 plan, "D12 wave record".

## 24 h mainnet soak (NL-376, 2026-09-27/28)

The long run the D12 record asked for (NL-376): `tools/NLightning.GossipProbe` built at `d929b879` (staged under
`~/.nltg-gossip-probe/soak/bin`), mainnet, `--chain rpc` against the owner's unpruned bitcoind (verified sync,
`Gossip:AssumeChannelValid` off), relay off, SQLite, the same five sync peers as the verified run (ACINQ (Eclair),
bfx-lnd0 and LNBiG-Hub-1 (LND), Blockstream Store and noserver4u (CLN)). Started 2026-09-27 14:28:06 UTC on the
database of the earlier runs, ended by its own 24 h timer at 2026-09-28 14:28:25 UTC. 288 samples, one every
5 minutes, in `~/.nltg-gossip-probe/soak/runs/20260927T142806Z-mainnet-soak-24h/` (`samples.csv`, `peers.csv`,
`lookups.csv`, `range-replies.csv`, `probe.log`).

| | Start (first sample, 5 min) | End (24 h) |
|---|---|---|
| Graph channels (all chain-verified) | 11,809 | **30,673** (peak 30,673) |
| Announcements without an update, held outside the graph (NL-406) | 6,868 | 9,762 (peak 9,784) |
| Graph nodes / announced | 5,701 / 5,539 | 10,000 / 9,929 |
| Channel policies | 18,714 | 54,576 |
| Channels pruned as spent on chain | 0 | 110 |
| Gossip messages received / accepted | 66,876 / 36,071 | 2,818,158 / 330,866 |
| Funding lookups (bitcoind) | 11,822 | 32,701, 0 HTTP failures |
| Block height followed | 968,850 | 969,006 (every block, level with bitcoind) |
| Database / WAL | 16.8 / 5.0 MiB | 43.0 / 5.3 MiB |
| Peers connected | 5 | 5 |

- **Sync:** `sync_complete` after 15 minutes; after that the graph grows only with new gossip.
- **Memory:**
  - Resident size 447 to 637 MB over the whole run: median 584 MB, 624 MB at the end, and flat after the first 2 hours with no upward trend.
  - Managed heap 180 to 352 MB.
  - `Gossip:MaxMemoryMb` (1,024 MB) was never crossed, and nothing was refused.
- **CPU:** median 1.2 % of one core, peak 35 % during the initial sync.
- **Writes:**
  - Graph flushes take at most 0.43 s.
  - The write-behind queue peaked at 522 and drained.
  - The ingress queue peaked at 7,513 in the initial sync and sat at 0 afterwards.
- **WAL:** stays at 5.0 to 5.3 MiB, so the WAL growth NL-376 was opened for does not reproduce over 24 h.
- **Peers:**
  - Two disconnections in 24 h, each followed by a reconnect:
    - noserver4u at 15:16 UTC (no `pong` within our timeout);
    - ACINQ at 14:11 UTC on day 2 (the peer closed the stream).
  - No bans and no warnings from any peer.
  - The CLN peers keep streaming the graph (Blockstream Store sent 533k `channel_update`s, noserver4u 526k). That is most of the 2.35M rejected messages, which were already known or stale.
- **Log:** 30 warnings and 2 errors.
  - Warnings:
    - 14 are EF migration notices at start;
    - 14 are the dropped-message counter (every 1,000, mostly in the first minutes);
    - 2 are the disconnections.
  - Errors: the 2 disconnections above.
  - A remote end-of-stream is logged at Error level. That is louder than it deserves; a Warning would do (NL-532).
    Fixed in lane accrbf: a remote close is logged at Information, a missed `pong` or a reset at Warning.

NL-376 is closed with this run and the Mutinynet 24 h soak (`MUTINYNET.md`, "Gossip soak (G5-T5)"). What mainnet
gossip still lacks is the relay proof from a node with a public channel (NL-417); the outbox pause (NL-360) landed in
lane nl360 (outbox cap on by default, relay paused on a full outbox, resumed at half, stalled after 10 min without
progress). A relay run through the BOLT 10 bootstrap peers could find small LND nodes that pick the probe as an
active syncer and subscribe to live gossip (`gossip_timestamp_filter(now, max)`, no backlog); whether any does is
chance, and the probe cannot do it yet: `--bootstrap` refuses `--relay-to`, and `--relay-to` names one peer (a
"relay to every connected peer" mode is needed). The backlog and the pause under real TCP need a peer we run that
asks for everything (`gossip_timestamp_filter(0, 0xFFFFFFFF)`), e.g. a second NLightning node pointed at the probe
with `--relay-to` naming it.

## BOLT 10 bootstrap run (2026-09-28)

Owner request: boot the mainnet gossip node and let it find its peers from the DNS seeds alone. Branch
`wip/fafo-b10main` (from `wip/fafo` at `6a0c3352`), after the owner reversed D-B10-1 (bootstrap on by default on
mainnet, NL-113).

### Method

- **Harness:** `tools/NLightning.GossipProbe run --bootstrap --chain rpc` (README, "BOLT 10 bootstrap run"). No
  `--peer`: the probe starts the product's `PeerBootstrapService` right after `PeerManager.StartAsync`, as
  `NltgDaemonService` does, with `Node:Bootstrap` at its mainnet default (enabled by the unset key, the default seeds
  `nodes.lightning.directory` and `nodes.lightning.wiki`, `MinPeers` 3, `MaxPeersFromBootstrap` 8, `StartupDelay`
  15 s, the system resolver then the public fallback 1.1.1.1/8.8.8.8, TCP). The probe never dials. Gossip as in the
  verified runs: sync on (`Gossip:SyncPeers` at the product default, 3), relay off, `AssumeChannelValid` off, every
  funding output checked against the owner's unpruned bitcoind (read-only RPCs). No channel, no funds, no wallet
  spend.
- **Node:** a fresh directory, database and **fresh probe key** each (`~/.nltg-gossip-probe/b10-bootstrap/`, node id
  `027e6056...aca56`; `~/.nltg-gossip-probe/b10-bootstrap-2/`, `02af8619...44f96`), so the node knew no peer and no
  graph node. Binaries staged under `~/.nltg-gossip-probe/bin-b10` (run 1, the lane's working tree with the
  on-by-default and fallback changes) and `bin-b10b` (rerun, with NL-546's fix, `10822bdd`). The old soak data under
  `~/.nltg-gossip-probe/soak` was not touched.
- **Runs** (raw data in `<dir>/runs/<UTC>-<label>/`: `bootstrap-seeds.csv`, `bootstrap-dials.csv`,
  `bootstrap-runs.csv`, `bootstrap-peers.csv`, `samples.csv`, `peers.csv`, `lookups.csv`, `range-replies.csv`,
  `probe.log`, `summary.json`; console in `<dir>/console-*.txt`):

| Run | Binary | Duration | Purpose |
|---|---|---|---|
| `20260928T193558Z-b10-bootstrap` (fresh database and key) | `bin-b10` | 40.1 min | the main run |
| `20260928T201716Z-b10-restart` (run 1's database) | `bin-b10` | 4.1 min | restart with the bootstrapped peers saved |
| `20260928T202217Z-b10-rerun` (fresh database and key) | `bin-b10b` (NL-546 fixed) | 15.1 min | the seed client with concurrent target lookups |

### Results

| | Run 1 (40 min) | Rerun (15 min, NL-546 fixed) |
|---|---|---|
| Seed the bootstrap asked (random order) | `nodes.lightning.wiki` | `nodes.lightning.directory` |
| System resolver's answer | Timeout (10 s, the per-seed limit) | SERVFAIL |
| Fallback resolvers' answer | 25 candidates, 0 rejected | 25 candidates, 0 rejected |
| Seed query time (system + fallback) | 19.5 s | 10.3 s |
| Second seed asked | no (25 candidates are enough for 8 peers x 3) | no |
| Candidates collected / selected / dialed | 25 / 25 / 10 | 25 / 22 / 10 |
| Dials connected / failed / timed out | 8 / 2 / 0 | 8 / 2 / 0 |
| Dial time of a connection (TCP + BOLT 8 + init) | 0.04-0.72 s | 0.04-0.65 s |
| Time to the first peer (from the bootstrap's start, 15 s start delay included) | 35 s | 26 s |
| Time to the third peer (`MinPeers`: the loop ends) | 36 s | 26 s |
| Peers connected | 8 from 0:36 to the end, no disconnection | 8 from 0:26 to the end, no disconnection |
| `sync_complete` (first sample) | 12.0 min | 11.0 min |
| Graph at the end: channels (all chain-verified) / announced nodes / policies | 30,570 / 9,622 / 54,344 | 30,558 / 9,559 / 54,309 |
| Announcements held without an update (NL-406) | 8,461 | 24,927 (15 min) |
| Time to 50 / 90 / 99 % of the final channels | 6.5 / 11.0 / 12.5 min | 6.0 / 10.0 / 11.0 min |
| Funding lookups (bitcoind) | 30,579 (Found 30,570, spent/missing 3, mempool-spent 6) | 30,814 (Found 30,558, spent/missing 256) |
| Resident size (median / max / at the end) | 606 / 634 / 474 MB | 601 / 651 / 648 MB |
| Managed heap max / CPU max (one core) | 448 MB / 55 % | 446 MB / 61 % |
| Database / WAL at the end | 42.4 / 5.3 MiB | 42.3 / 7.1 MiB |
| Warnings / errors logged | 32 / 0 (21 EF migration notices, 11 dropped-message counters) | 26 / 0 |
| `Gossip:MaxMemoryMb` crossed | no | no |

The graph matches the 24 h soak's (30,673 channels, 9,929 announced nodes at its end) after about 12 minutes.

**Restart** (run 1's database, the 8 bootstrapped peers saved as ordinary `Peers` rows): `PeerManager.StartAsync`
dialed them, all 8 reconnected, the bootstrap's first gate found 8 peers connected (`MinPeers` 3) and ended without a
DNS query. The graph (30,570 channels) reloaded in 0.5 s; `sync_complete` again after 1.1 min.

**Seeds** (the census after each run asks both seeds once more through the product's seed client):

| Seed | System resolver (this Mac: the home router and the ISP's IPv6 resolver) | Fallback (1.1.1.1) |
|---|---|---|
| `nodes.lightning.directory` | SERVFAIL in every query | 25 SRV records, no glue; 25 IPv4 (run 1) or 22 IPv4 + 3 IPv6 (rerun); ports 9735, 9766 (run 1), 9735, 9889 (rerun) |
| `nodes.lightning.wiki` | no answer within the per-seed 10 s (Timeout) in every query | 25 SRV records, no glue; 25 IPv4; ports 9735, 9745 (run 1 census), 6669, 9735, 9736, 9835 (rerun census), 9740 and 10016 among run 1's dials |

Without the fallback (D-B10-7) the mainnet default would have found no peer on this host. Both seeds answer the bare
root; neither sends address glue, so every target costs an A and an AAAA query.

**Peers discovered** (implementation from the peer's `node_announcement`: LND sets bit 2023; the others are not told
apart):

| Run | Peer (alias) | Address | Implementation |
|---|---|---|---|
| 1 | Johoe | 95.217.32.30:9735 | LND |
| 1 | jbs node | 152.67.125.92:9735 | LND |
| 1 | IBEX_SB | 34.74.1.106:9735 | LND |
| 1 | Jamaussie | 45.32.245.8:9735 | LND |
| 1 | zlnd0 | 54.87.193.89:9735 | LND |
| 1 | LOUDFELONY | 34.7.82.59:9735 | not LND (the alias has the form of CLN's default) |
| 1 | Electrum Trampoline | 195.201.207.61:9740 | not LND |
| 1 | spotlight.soy | 160.16.59.243:9735 | not LND |
| rerun | PaidlyInteractive⚡, PaidlyInteractive2⚡ | 3.71.156.219, 18.192.92.189 :9735 | LND |
| rerun | LQWD-HongKong | 18.163.90.95:9735 | LND |
| rerun | senza | 3.220.219.249:9735 | LND |
| rerun | Fedimint-Gateway-V1 | 64.23.148.40:9735 | LND |
| rerun | Lightning.Video | 5.161.58.236:9735 | LND |
| rerun | BitcoinOnLinux | 62.80.227.49:9735 | LND |
| rerun | satellite-api-mainnet | 35.197.62.113:9735 | not LND |

The four failed dials: two refused TCP connections (54.201.244.204:10016, 65.7.8.70:9735), one peer that closed the
connection before its `init` (170.75.163.209), one connect error (195.49.96.165); each endpoint is skipped for the
rest of the process. The sync peers of run 1 at the end were Electrum Trampoline, jbs node and zlnd0 (the
rotation also ran a range sync on Johoe).

### Anomalies and issues

- **NL-546 (fixed, `10822bdd`):** the seed client resolved the 25 targets one after the other (50 TCP queries,
  about 8.5 s per seed through 1.1.1.1); the fallback part of a seed query now takes about 1 s (census: 4.5 s for
  `nodes.lightning.directory` with the router's SERVFAIL, was 10.3 s; `DnsSeedLiveTests` 0.66 s for 25 targets).
- The system resolver's failure still costs up to the per-seed timeout (10 s) before the fallback when it hangs
  (`nodes.lightning.wiki`): that is the price of asking it first (D-B10-7).
- Only one seed is asked when it returns enough candidates (`MaxPeersFromBootstrap x 3` = 24); the second seed is
  asked only when the first falls short. As designed; the census shows both seeds answer.
- No IPv6 peer was dialed: run 1 had no IPv6 candidate, the rerun's 3 IPv6 candidates were not needed.
- The peers' `init` features as the probe reads them (`PeerModel.Features`) never show bit 2023 even for LND nodes,
  whose `node_announcement` has it; the implementation guess uses the announcement.
- As in the earlier runs, the initial sync drops gossip on full ingress queues (10,585 in run 1, 4,180 in the rerun)
  and asks for those channels again; the dropped-message warnings are the 11 and 5 GossipIngress warnings.
- Known gap at the time of the run: a node that restarts with a graph but none of its saved peers reachable did not
  bootstrap (the graph knows addresses) and connected to no graph node either (NL-543; fixed since by the graph
  top-up, `d4eb9582`: the bootstrap now dials graph nodes first and falls back to the seeds).

## Relay proof (NL-417, 2026-09-28)

Owner request: prove the relay of other nodes' gossip on mainnet with two local probes and, if clean, turn it on by
default on mainnet. Branch `wip/fafo-nl417` (from `wip/fafo` at `61b74291`). Mainnet, gossip only: no channel, no
funds, read-only RPCs.

### Method

- **Harness** (README, "Two-probe relay proof"): `--relay-to-all` turns the relay on toward every connected peer
  (the product's peer directory) and writes `relay.csv` (the relay's `GetStatus()`, i.e. `describegraph`'s `Relay:`
  line, plus the relayed, `outbox.refused`, `relay.paused`, `relay.stalled` counters and the drops) and
  `relay-peers.csv` (per peer: its filter, 256/258/257 sent, refused, echoes, the outbox depth and progress); `--sink`
  connects to one peer, syncs nothing itself and sends `gossip_timestamp_filter(0, 0xFFFFFFFF)` at `init`, checking
  every received message in arrival order (`sink.csv`); `--read-phases` puts a throttled TCP proxy between the sink
  and the relayer (`proxy.csv`); `--set` overrides configuration keys.
- **Relayer A:** `run --bootstrap --relay-to-all --chain rpc` in `~/.nltg-gossip-probe/r417-a` on a copy
  (`sqlite3 .backup`) of the b10 run's verified database (30,570 chain-checked channels, its 8 bootstrap peers saved),
  with a new key (`037d1c0c...2c839`), listening on `127.0.0.1:19736`; every funding output checked against the
  owner's bitcoind; the bootstrap reconnected its 8 mainnet peers (LND, CLN and others) and kept them.
  `Gossip:RelayStallTimeout` = 90 s and `Node:NetworkTimeout` = 5 min for the run (below).
- **Sink B:** `run --sink --peer <A>@127.0.0.1:19736 --chain stub` (`AssumeChannelValid`: it only measures
  reception), a fresh directory and key per run. Run 1: full speed. Run 2: `--read-phases 240:50000,150:0,0:max`
  (50 kB/s, about 175 messages/s, for 4 min; no reading for 150 s, longer than the 90 s stall timeout; then
  unthrottled), with `Node:NetworkTimeout` 5 min on both probes: a sink that stops reading never answers a `ping`
  (the ping is stuck behind the paused stream too), so with the default 15 s timeout the connection closes at the
  next ping instead of stalling. That is the normal end of a peer that stops reading (the stall is for a peer that
  keeps answering pings but reads no gossip, and for the snapshot it holds).
- Binaries staged under `~/.nltg-gossip-probe/bin-417*` (`bin-417f` = the final build, `4ba8b967`). Raw data in
  `~/.nltg-gossip-probe/r417-a/runs/`, `r417-b*/runs/` (console output in `console-*.txt` there).

| Run (A) | Build | Sinks | Purpose |
|---|---|---|---|
| `20260928T230114Z-r417-relayer` | before the fixes | `r417-b` full speed | first proof: found NL-548 (1) |
| `20260928T230658Z-r417-relayer-fixed` | NL-548 (1) | `r417-b2` full speed, `r417-b-slow` slow | found NL-548 (2) and (3) |
| `20260928T233414Z-r417-relayer-final` | NL-548 (1), (2) | `r417-b3` full speed | confirmed (2), found (3) again |
| `20260928T234702Z-r417-relayer-v3` | all fixes (`4ba8b967`) | `r417-b4` full speed and `r417-b-slow2` slow, at the same time | the final proof |

### Results (final build)

| Sink | Full speed (`r417-b4`) | Slow reader (`r417-b-slow2`) |
|---|---|---|
| Messages received (256 / 258 / 257) | 96,085 (30,589 / 55,401 / 10,095) | 53,689 (19,858 / 33,465 / 366) |
| Channels at the end vs A's graph (30,598) | **30,589 (99.97 %)**, 54,375 policies, 9,624 announced nodes | 18,629 (the backlog was ended by the stall, as designed) |
| Time to the backlog | 50 % in 0.8 min, 99 % in 1.5 min (1,000 messages/s, the backlog pace) | 175 messages/s while throttled |
| `channel_update` before its `channel_announcement` | **0** | **0** |
| `node_announcement` before a channel of its node | **0** | **0** |
| Duplicates | 10 (NL-549) | 0 |
| Older update after a newer one | 0 | 0 |
| Sink RSS | 372 MB | 319 MB |

| Relayer A (15.7 min, both sinks) | |
|---|---|
| Relayed (256 / 258 / 257) | 50,447 / 88,866 / 10,461 |
| Outbox refusals, pauses | 7, 7 (all to the slow sink) |
| Outbox at most | 10,000 messages (the cap), 2.2 MB (the byte cap never bound) |
| Resume | at or below 5,000 messages (half the cap), the refused backlog message first |
| Stall | 1, 90 s after the sink stopped reading: backlog ended, 1,017 pending messages dropped (`relay_stalled`), connection kept, live relay resumed once the proxy read again (`relay.paused_connections` back to 0) |
| RSS min / median / max | 318 / 523 / 568 MB (the NL-417 fix run, 26.6 min: 320 / 570 / 636 MB); managed heap at most 570 MB; `Gossip:MaxMemoryMb` never crossed |
| CPU | at most 70 % of one core (the backlogs), then 2-3 % |
| Warnings / errors | 1 EF Core query notice / 0 |
| Mainnet peers | 8 connected the whole run, 0 disconnections, 0 bans, 0 warnings in either direction |

**Mainnet subscribers.** Most of A's mainnet peers send no filter (LND only filters its few active sync peers) or
CLN's `0xFFFFFFFF` "nothing". In the 26.6 min fix run one LND peer, Jamaussie (`03b95562...`, found by the BOLT 10
bootstrap), sent `gossip_timestamp_filter(now, 0xFFFFFFFF)` after 9 minutes and received 1,883 live messages (2
announcements, 1,513 updates, 368 node announcements) over 17 minutes without a warning or a disconnection. Two of
them were its own `node_announcement` sent back (`echo_to_origin`): it sent them while ours were already queued, a
race origin suppression cannot close. In the final run no mainnet peer subscribed. So the relay toward real mainnet
peers is exercised live, but only lightly; the load is proven by the sinks.

### Issues found

- **NL-548 (fixed):** (1) during a backlog, the connection's flush sent newer versions of channels the backlog had
  not reached: 188 updates before their announcement at full speed (`4a3f6145`, the flush now waits for the
  backlog); (2) after the stall, live updates and node announcements of channels the peer never got: 288 and 76
  (`db6eb3e4`, the relay remembers where the backlog stopped and sends the announcement first); (3) 3 node
  announcements of nodes whose only channels were spent (still in the graph for 72 blocks, never relayed)
  (`4ba8b967`). Each fix has a test that fails without it.
- **NL-549 (open, low):** a change between the relay's last collect and the backlog's snapshot is sent twice (10
  duplicates in 96,085).
- Probe only: stub mode built the bitcoind client through the NL-415 pending-channel view and failed at start (fixed
  in the probe); the stub sink logs one error at stop (the peer manager's revert on disconnect asks the stub bitcoind).

### Decision

Clean against every criterion (full graph, no ordering violation, memory bounded by the outbox cap, pause, resume
and stall correct, no error, no mainnet peer disconnection or ban): **relay of other nodes' gossip is on by default
on mainnet** (`Gossip:RelayEnabled` unset = on everywhere, template `true`; owner decision 2026-09-28, `7ceb31ce`).
`AllowPublicChannelsOnMainnet` stays false.
