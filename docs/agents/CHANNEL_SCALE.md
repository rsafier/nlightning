# Channel-count scale benchmark (NL-1357)

How one node behaves with 1,000 to 10,000 Open channels across up to 1,000 peers: start to ready, memory, the work
every block does, the linear scans on hot paths, reconnecting a peer with 1,000 channels, and payments with the rest of
the channels idle. The benchmark lives in `test/NLightning.Integration.Tests/Scale/`; this page records the method, the
numbers (2026-10-10, branch `wip/channel-scale`) and what was fixed.

## Method

- **The node** (`ScaleNode`) is the daemon's own composition (`AddNltgNodeServices`) and runs the daemon's start steps in
  the daemon's order (`NltgDaemonService`, as `NLightningTestNode` does), each one timed. bitcoind is replaced by
  `ScaleChain`, an in-memory regtest chain with a transaction index (every lookup is a dictionary hit, so the chain never
  dominates); blocks are handed to the monitor as ZMQ would. Fees are `FeeEstimation:Source=Fixed`. The node logs
  Warning and above only (10,000 "Loaded channel" Information lines would time the logger) and counts the lines.
- **The seed** (`ChannelScaleSeeder`) writes, through the production repositories, what a v1 anchors open and
  `channel_ready` leave: the channel row with both key sets (our keys from the node's own signer at key index 1..N, the
  peer's from its own signer for a live peer or random valid keys for an offline one), the funding output and its
  outpoint watch, a completed funding transaction watch, the real short channel id (the funding transactions are mined
  on the `ScaleChain`, 200 per block), the first commitment snapshot with real per-commitment points, and the peer row.
  On 10 % of the channels with offline peers, 1 to 3 HTLCs we offered are locked into both commitments through the real
  commitment engine (with their `HtlcOrigin`), far from any deadline. Commitment signatures are stand-ins of the right
  shape (nothing verifies them unless a channel is force-closed). For a live peer both databases get the two ends of the
  same channels, so the channels reestablish and carry payments for real.
- **Peers**: N/10 peers (100, 500, 1,000), offline at a closed loopback port (each dial is refused at once and the
  reconnect backoff takes over). `NLTG_SCALE_UNREACHABLE=blackhole` (live test) puts them at `10.255.255.1:9735`, which
  never answers (each dial waits `Node:NetworkTimeout`).
- **Starts**: the first start after the seed adopts the database into the signing enrollment (NL-1340) and writes the
  accounting cutover, one-time costs reported apart; the measured start is a restart on a fresh service graph.
- **Memory**: the managed heap after a full GC before and after the restart (plus the signer's lazy load of every
  channel); the per-channel cost is the slope between sizes. RSS is reported but noisy (one test process, several runs).
- **Machine**: macOS arm64 (28 cores), SQLite on APFS (`synchronous=FULL`), Postgres 17 in a local container
  (OrbStack), net10.0 Release. Other lanes' test runs shared the machine, so take single numbers within about 20 %.

Run it (`-explicit only`; sizes and Postgres from the environment):

```bash
NLTG_SCALE_SIZES=1000,5000,10000 \
NLTG_SCALE_POSTGRES="Host=127.0.0.1;Port=25432;Username=...;Password=..." \
  dotnet run --project test/NLightning.Integration.Tests -c Release -f net10.0 --no-build -- \
  -method '*ChannelScaleBenchmarkTests.Given_1000To*' -explicit only
NLTG_SCALE_LIVE_TOTAL=10000 NLTG_SCALE_LIVE_CHANNELS=1000 NLTG_SCALE_PAYMENTS=200 [NLTG_SCALE_UNREACHABLE=blackhole] \
  dotnet run --project test/NLightning.Integration.Tests -c Release -f net10.0 --no-build -- \
  -method '*ChannelScaleLivePeerTests.Given_Thousands*' -explicit only
```

The default run holds two fast variants of the same paths (`Given_200SeededChannels_*`, a 100-channel live peer with
5 payments; about 8 s together).

## Results

### Restart (after the fixes)

| | SQLite 1K | SQLite 5K | SQLite 10K | Postgres 1K | Postgres 5K | Postgres 10K |
|---|---|---|---|---|---|---|
| restart, total | 0.77 s | 3.1 s | 6.6 s | 3.9 s | 21.6 s | 39.7 s |
| of which peer manager (load, register, dial) | 0.61 s | 2.9 s | 6.4 s | 3.7 s | 21.4 s | 39.5 s |
| of which chain monitor start | 0.11 s | 0.13 s | 0.13 s | 0.12 s | 0.12 s | 0.13 s |
| retired SCID map load | 0.4 ms | 0.9 ms | 2.1 ms | 0.6 ms | 0.8 ms | 1.3 ms |
| every other step together | < 60 ms | < 50 ms | < 50 ms | < 50 ms | < 30 ms | < 50 ms |
| signer's lazy load of every channel (first use) | 0.43 s | 2.0 s | 4.0 s | 1.7 s | 9.5 s | 17.8 s |
| first start only: signing enrollment adoption | 1.1 s | 5.2 s | 10.4 s | 1.1 s | 5.2 s | 10.6 s |
| first start only: accounting cutover | 0.57 s | 2.0 s | 3.9 s | 2.6 s | 12.6 s | 25.0 s |
| managed heap growth over the restart | 3.4 MiB | 12.2 MiB | 21.2 MiB | 3.7 MiB | 12.2 MiB | 19.6 MiB |

The managed cost of a loaded channel is about **2 KiB** (the slope of 3.4 → 21.2 MiB over 9,000 channels); RSS grew
about 50 MiB at 10,000 channels.

Peer-manager breakdown at 10,000 (diagnostics after the start): the startup load of every channel
(`GetPeersForStartupAsync`, one consistent read per channel, NL-810) 4.8 s on SQLite and 26 s on Postgres; the state
reload each channel's registration repeats for its pending HTLC events (`ChannelManager.QueuePendingDomainEventsAsync`)
1.9 s and 14 s. Both are linear: Postgres pays about 30 round trips per channel at 0.3 ms each through the container.

### Every block (10,000 channels, 1,000 offline peers, 1,900 HTLCs in flight)

| | SQLite | Postgres |
|---|---|---|
| chain monitor, block of 500 transactions | 3-5 ms | 8-14 ms |
| on-chain executor round (runs after each block) | 2-5 ms | 4-7 ms |
| HTLC deadline round | 0.7-1.3 ms | 1.3-1.7 ms |
| update_fee round | 0.9-3.5 ms | 1.5-3.2 ms |
| `FindChannels` by peer (linear scan of the memory repository) | 133 µs | 129 µs |

The first block after a start took 300-460 ms in some runs (first-use costs), the later ones the figures above.

### A live peer with 1,000 of the 10,000 channels (SQLite)

| | |
|---|---|
| hub restart | 6.7 s |
| first of the 1,000 channels usable (both `channel_reestablish` handled) | 8.7 s after the start began |
| all 1,000 usable on both ends | 10.1 s (about 3.4 s of reestablish for 1,000 channels on one connection) |
| invoice creation on the peer (1,000 channels with the hub) | 0.7-5 ms |
| payments hub → peer, one at a time | 17.4/s, p50 40 ms, p95 54 ms |
| payments hub → peer, 8 at once | 17.4/s, p50 413 ms, p95 959 ms |

Idle channels do not slow payments down (10,000 channels with 50 live: 16/s; 1,000 with 50 live: 19/s, one at a
time). Concurrency does not raise the rate over one peer connection: each peer's messages are handled in order on one
loop and every transition is its own fsynced save.

With 190 peers that never answer (`blackhole`) stored before the live peer, the live peer's channels were usable
**166.6 s** after the start before the dial-order fix and **16.3 s** after it (the start itself waits
`StartupDialWait`, `Node:NetworkTimeout` = 15 s, for the dials; NL-576). With 1,000 such peers the old order would
have taken about 15 minutes (16 dials at a time, 15 s each).

## What was fixed

| ID | Hot spot | Before | After |
|---|---|---|---|
| NL-1358 | `RetiredScidMap.LoadAsync` loaded every ready channel and read each one's fundings through one unit of work; each read scanned (and change-detected) every row tracked so far | 1.2 s / 14.3 s / 55.6 s at 1K / 5K / 10K: quadratic | 0.4-2 ms (reads only channels with a retired funding) |
| NL-1359 | The on-chain executor's round after every block: the funding reconfirm check read one watch per Open channel (10,000 queries per block), and the once-per-process spend replay and the splice reorg check read every channel's fundings through one unit of work (quadratic) | 1.5 s per block at 1K, 6.7-7.8 s per block at 5K, first round 41 s at 5K (it delays the on-chain resolution of a force-closed channel by as much) | 2-11 ms per block, first round included |
| NL-1360 | Payment planning ran the liquidity estimator's binary search (about 30 dry-run `SendAdd`s, half of them throwing) on every channel to the first hop, for every payment | 1,000 channels to the payee: 6.5-7.2/s, p50 109-123 ms | 13-17/s, p50 40 ms (the answer is kept per immutable snapshot) |
| NL-1357 | Startup dials went in database order, 16 at a time: a live peer listed behind peers that never answer waited a timeout per round of 16 | 166.6 s with 190 dead peers listed first | at the start (peers seen last are dialed first) |

## Open follow-ups

1. **Startup reads every channel three times** (NL-1354, low). The peer manager's load (`GetPeersForStartupAsync`, one consistent read
   per channel), the registration's state reload for pending HTLC events (the load already read the settled-HTLC
   archive and drops it) and the signer's lazy load of its signing info on first use. Linear, but 6.6 s + 4 s at 10,000
   on SQLite and 40 s + 18 s on Postgres. Fix sketch: hand the loaded settled archive to the registration, and load the
   signing info set-based once at startup instead of one query per channel.
2. **The on-chain executor's background round outlived the node's stop** (NL-1355, fixed). A round scheduled by the last
   block ran on after the service provider was disposed (logged `ObjectDisposedException`; in the benchmark it
   recreated an empty SQLite file after the database was deleted). `OnchainResolutionExecutor.StopAsync` now refuses
   new rounds, cancels the running one and waits for it (bounded), and nothing it does after the stop is saved,
   published or raised; the daemon, `NLightningTestNode` and `ScaleNode` call it with the safety services, before the
   chain monitor and the peers stop (`IChannelFailureService.StopAsync` likewise waits for its block-driven work and
   the anchor CPFP rounds).
3. **Readiness waits `StartupDialWait` when any stored peer does not answer** (NL-576, by design). With dead peers stored, every start
   waits the full 15 s before the chain monitor starts (by design, NL-576), whatever the channel count.
4. **One peer connection caps payments at about 17/s** (NL-1396, fixed for Postgres) with SQLite here (ordered per-peer
   processing, one fsynced save per transition); more concurrency only adds latency. Per-channel lanes lift it on
   Postgres ("Per-peer parallelism" below); SQLite keeps the serial loop by default (owner decision 2026-10-11).

## Per-peer parallelism (NL-1396, Postgres, 2026-10-11)

Plan: `docs/agents/PEER_CHANNEL_PARALLELISM_PLAN.md`. A connection's channel messages used to be handled one at a time
by one inbound loop, so payments over many channels of one peer were serialized there. With per-channel lanes
(`Node:ChannelLanes:Enabled`, on by default for Postgres) the loop is a dispatcher that hands each registered channel's
messages to that channel's own FIFO lane (at most `MaxConcurrentPerPeer` 8 per connection and `MaxConcurrent` 32
node-wide at once) and everything else (the open flow under temporary ids, unknown ids) to one serial peer lane.

**Setup.** `ChannelScaleLivePeerTests.Given_Thousands*` with 1,100 hub channels, 1,000 of them to the live peer, 10
offline peers, 200 payments per round, both nodes on one local Postgres 16.2 container (OrbStack, `max_connections`
500, Npgsql pool 200) in one test process. New knobs: `NLTG_SCALE_POSTGRES` (connection string without a database: both
nodes get a database of their own, dropped at the end), `NLTG_SCALE_PARALLEL` (`1,4,8,16,32,64`), `NLTG_SCALE_LANES`
(`true`/`false`), `NLTG_SCALE_CONFIG` (`key=value;...` for both nodes), `NLTG_SCALE_PG_STATEMENTS=1` (the round's top
`pg_stat_statements`, the extension must be loaded), `NLTG_SCALE_MARKER` (a file naming the running round, for a
profiler started from outside). Each round also reports the process CPU per payment (both nodes are in the process) and
the two databases' busy time and transactions per payment (`pg_stat_database`). The machine (28 cores) was shared with
other lanes' test runs (load average 4 to 37), so single runs vary by up to 2x: the tables give the median of three
runs per configuration, run interleaved.

**Base commit** (`1a76e5da` + the plan, lanes code absent; one run): 6.7, 19.2, 22.2, 25.9 and 34.9 payments/s at 1, 4,
8, 16 and 32 at once, p50 75, 187, 358, 612 and 860 ms.

| at once | lanes off (as before) | | lanes on | | lanes on, `No Reset On Close` (shipped) | | shipped + `DOTNET_gcServer=1` | |
|---|---|---|---|---|---|---|---|---|
| | payments/s | p50 / p95 ms | payments/s | p50 / p95 ms | payments/s | p50 / p95 ms | payments/s | p50 / p95 ms |
| 1 | 8.6 | 77 / 102 | 11.8 | 77 / 99 | 10.1 | 93 / 119 | 9.6 | 80 / 191 |
| 4 | 16.0 | 215 / 493 | 29.6 | 130 / 177 | 26.5 | 126 / 343 | 29.4 | 115 / 229 |
| 8 | 20.2 | 386 / 588 | 31.0 | 198 / 513 | 33.6 | 188 / 418 | 39.2 | 152 / 277 |
| 16 | 9.8 (21.7 best) | 1,022 / 5,119 | 45.4 (70.3 best) | 320 / 553 | 61.1 | 238 / 428 | 90.5 | 162 / 304 |
| 32 | 22.2 | 1,319 / 1,982 | 72.3 | 390 / 805 | 69.2 | 370 / 817 | 108.2 | 256 / 640 |
| 64 | 27.8 | 1,803 / 2,566 | 85.0 | 640 / 1,262 | 53.5 (74.2 best) | 996 / 1,818 | 94.5 (148.5 best) | 348 / 1,394 |

Reading it: with lanes off, latency grows about linearly with the payments in flight (the serial loop) and the rate
stays in the 20s; with lanes on, the rate grows with the payments in flight up to 85-110/s at 32-64 and latency grows
far slower: about 3x the rate at 16 to 64 at once, about 2x at 4 and 8. Reestablishing the 1,000 channels after the hub's
start gains a little: all usable on both ends a median 12.5 s after the start with lanes (10.4-16.8 s over 11 runs),
14.8 s without (12.5-25.2 s over 15 runs); the peer's 1,000 `channel_reestablish` are handled on their own lanes, but
our own `channel_reestablish` prologue is still serial (NL-1401).
Correctness under lanes: `Scale/ChannelLanesLivePeerTests` (20 channels over one connection, 48 payments 16 at once,
both ends agree on every channel; and the peer restarted while 16 payments are in flight: no HTLC lost or doubled, what
left the hub equals what the peer's settled invoices received) on SQLite and on Postgres.

**What limits it next** (measured with lanes on, 32-128 at once):

1. **Npgsql's connection reset** (NL-1404, fixed): EF takes a pooled connection around nearly every query and Npgsql
   sent `DISCARD ALL` before each reuse: 145 of the 311 statements per payment of the two nodes (`pg_stat_statements`,
   0.27-0.32 ms of server time each). `No Reset On Close=true` (our default for Postgres) halves the database's
   transactions per payment (409 → 209 at one at a time, 168 → 83 at 32). The wall-clock effect was within the host's
   noise in these runs (2-run and 3-run A/B tests both ways); the saving is database CPU that a busier database needs.
2. **Durable commits** (NL-1405): at 64 at once the database's active backends wait mostly on the WAL flush
   (`LWLock:WALWrite` 220 and `IO:WALSync` 53 of 417 samples of `pg_stat_activity`): about 9-10 explicit commits per
   payment for the two nodes (each transition's save, plus the durable signer guard's own `UPDATE channel_signer_guards`
   commits, NL-1399), on a Docker-on-macOS volume. The rate plateaus there (85-110/s) with only 4-6 cores busy.
3. **Workstation GC** (NL-1406): the daemon and the test host run the workstation (concurrent) GC; with
   `DOTNET_gcServer=1` the same build reached 90-150/s at 16-64 at once (thread snapshots without it show many threads
   paused in `PollGC`). Not changed: server GC trades memory for it, an operator's choice for a hub.
4. Per-channel signing (commitment and HTLC signatures, `CommitmentSigningService`, `LocalLightningSigner`) and EF Core
   materialization are most of the CPU; 50-60 ms of process CPU per payment for both nodes at 32 at once.

Not limits: the per-peer lane cap (8 or 64 gave the same rate at 32 at once), the per-payment-hash lock, the commit
scheduler, the outbox (one send loop per connection) and the LND index gate (not seen in thread or wait samples).