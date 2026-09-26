# Mainnet gossip probe (BOLT 7 at mainnet scale)

Status 2026-09-26, branch `wip/fafo-mainnet-gossip` (from `wip/fafo` @ `f0c2c28`). The first test of NLightning's
BOLT 7 stack against real mainnet peers and the whole mainnet graph, without a mainnet bitcoind. It found and fixed
five interop and efficiency problems (two of them cut every CLN and LND peer off), and measured memory, database and
sync behaviour at mainnet size. Mainnet gossip stays off by default (plan D12); the probe overrides it explicitly.

Issue IDs: the probe's findings start at NL-400 (NL-379..NL-399 are reserved for the O7 anchors wave's ledger);
`docs/agents/ISSUES.md` is not edited on this branch, the proposed entries are at the end.

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
| NL-400 | High (interop) | fixed (c68e61f) | Core Lightning's `reply_channel_range` sequence has unsorted ids and a `number_of_blocks` that wraps in 32 bits (`918664 + 4294956303`), the next reply starting at the wrapped end, below the previous `first_blocknum`. `RangeReplyCollector` rejected it: the sync with every CLN peer ended with a warning. Now LND's continuity rule (a reply may start at the previous reply's u32 end or the block before it) is accepted, and ids inside the query are kept even outside their own reply's claimed blocks. Upstream CLN issue worth reporting (both nodes, versions unknown). |
| NL-401 | High (interop) | fixed (58541c6) | LND answers queries with pre-2022 `channel_update`s without `htlc_maximum_msat` (128 bytes). The payload parser rejected them and `MessageService` warned and closed the connection: every LND peer that stored one was cut during the sync. Malformed 256/257/258 are now ignored with one warning per connection. |
| NL-402 | Medium (efficiency) | fixed (c287316) | Each sync peer diffed its range reply once, at the start, and then asked for everything the others had delivered meanwhile: 4.9x the graph downloaded, LND syncs of 11 min. Each batch is now diffed again before it goes out (1.9x, 67 s). |
| NL-403 | High (mainnet only) | fixed (dc5b24a) | `SecureKeyManager.FromFilePath` compared NBitcoin's network name (`Main`) with `mainnet`: a mainnet node could not read its own key file after the first start. |
| NL-404 | Low | fixed (924df96) | With `gossip_queries_ex`, unknown channels whose both update timestamps are stale or missing are not asked for (`Gossip:SkipChannelsStaleFor`, 14 days; LND's zombie rule). Mainnet peers report almost none today (see NL-406). |
| NL-405 | Low (memory) | fixed (e771eb4) | `OriginTrackingGossipIngress` recorded up to 100,000 origins (about 15 MB) while the relay of others' gossip was off. |
| NL-406 | Medium (memory, spec) | documented | 24 % of the graph (9,774 channels) are announcements without any `channel_update`, sent by CLN (mostly on our `gossip_timestamp_filter`) against BOLT 7's MUST NOT. We store them; they are removed only by the pruner's stale rule 28 days after they arrive (and only while blocks come), and come back with the next CLN connection. Proposal: keep an announcement without update in a bounded pending cache (like the orphan cache, TTL) and store it only with its first update; or a zombie index (LND) of pruned SCIDs. Upstream CLN issue worth reporting. |
| NL-407 | Low (interop) | documented | ACINQ (Eclair) stops answering `query_short_channel_ids` after the fourth 200-channel query from a peer without channels; we end the querying of that connection after 2 min. To check against Eclair's source (a per-peer query budget?) and with a peer we have a channel with. |
| NL-408 | Low (efficiency) | documented | The initial sync orphans nearly every `channel_update` (workers race the announcement of the same channel) and replays it. Partitioning the ingress queue by short channel id would keep a channel's messages on one worker. |

Carried and confirmed: NL-373 (memory budget: budget the GC heap, see above), NL-376 (the soak's WAL concern does not
reproduce here: the WAL stays at 5.8 MiB and the database file is checkpointed; RSS flat at 400 MB over the hour), NL-099.

## What remains for mainnet gossip (D12)

1. **Chain verification.** `Gossip:AssumeChannelValid` is a probe/light-node mode: the verify run found no invalid
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
4. **Relay on mainnet** was not tested (the probe is a leech by design): the relay's pacing and filters against real
   mainnet peers, and our query replies to them (served only for chain-checked channels, so a probe with
   `AssumeChannelValid` serves nothing), still need a run with a verifying node.
5. The 24 h Mutinynet soak (NL-376) and a long mainnet run (days) for slow growth: this hour showed a flat RSS and
   a bounded WAL.
