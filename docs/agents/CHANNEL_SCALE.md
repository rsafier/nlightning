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

Filed with NL-1357 (no IDs of their own yet):

1. **Startup reads every channel three times.** The peer manager's load (`GetPeersForStartupAsync`, one consistent read
   per channel), the registration's state reload for pending HTLC events (the load already read the settled-HTLC
   archive and drops it) and the signer's lazy load of its signing info on first use. Linear, but 6.6 s + 4 s at 10,000
   on SQLite and 40 s + 18 s on Postgres. Fix sketch: hand the loaded settled archive to the registration, and load the
   signing info set-based once at startup instead of one query per channel.
2. **The on-chain executor's background round outlives the node's stop.** `OnchainResolutionExecutor` has no stop;
   a round scheduled by the last block runs on after the service provider is disposed (logged
   `ObjectDisposedException`; in the benchmark it recreated an empty SQLite file after the database was deleted).
   Fix sketch: a stop that cancels and awaits the loop, called by the host before the provider is disposed.
3. **Readiness waits `StartupDialWait` when any stored peer does not answer.** With dead peers stored, every start
   waits the full 15 s before the chain monitor starts (by design, NL-576), whatever the channel count.
4. **One peer connection caps payments at about 17/s** with SQLite here (ordered per-peer processing, one fsynced save
   per transition); more concurrency only adds latency.