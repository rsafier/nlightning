# NLightning.GossipProbe

A test harness, not product code: it runs NLightning's BOLT 7 gossip stack against real **mainnet** peers as a
gossip-only leech and samples it, to test the graph at mainnet scale without a mainnet bitcoind. Results and the
method are in `docs/agents/MAINNET_GOSSIP_PROBE.md`.

## What it runs

- The daemon's own composition (`AddNltgNodeServices`, the same the Docker test node uses) on mainnet, with a SQLite
  database `probe.db` and a throwaway node key `node-key.json` (generated at the first run, password
  `nltg-gossip-probe`, no funds, never used for a channel) in the probe directory (default `~/.nltg-gossip-probe/`,
  never `~/.nltg/mainnet`).
- No bitcoind: `IBlockchainMonitor` is a stub that reports the mainnet tip read from the Esplora API at start (for
  `query_channel_range(0, tip + 1)`), raises no block and throws on any publish; `IFundingOutputLookup` is a stub that
  counts calls (it must stay at 0).
- Gossip settings: `Gossip:Enabled`, `SyncEnabled` and `AssumeChannelValid` on (channel announcements accepted on
  their signatures), `RelayEnabled` off (we relay nothing to mainnet peers), `AcceptPublicChannels` and
  `AllowPublicChannelsOnMainnet` off, `Node:EnableHtlcs` false (the `AssumeChannelValid` startup guard requires it),
  `Gossip:SyncPeers` = the number of peers (each peer runs a range sync, so every implementation's replies are
  checked).
- It listens on `127.0.0.1` only, connects outbound to the peers, opens no channel, sends no HTLC and announces
  nothing (it has no channel to announce).

Default peers (IPv4 clearnet, checked against mempool.space on 2026-09-26): ACINQ (Eclair), bfx-lnd0 and LNBiG Hub-1
(LND), Blockstream Store and noserver4u (Core Lightning). Pass `--peer <id>@<ip>:<port>` (repeatable) to use others.

## Run

```bash
dotnet build tools/NLightning.GossipProbe -c Release -f net10.0
# 60 min at least, then until the graph did not grow for 15 min, at most 120 min
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll run --label run1
# a second run reloads the stored graph (timed) and shows what the re-sync downloads
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll run --label restart \
    --min-minutes 10 --plateau-minutes 5 --max-minutes 20
# cross-check a random sample of the stored channels against mempool.space (at most 2 requests/s, stops on HTTP 429)
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll verify --sample 300
```

Copy the build output elsewhere before a long run if you keep working in the checkout. Ctrl+C (SIGINT) stops a run
gracefully: the graph is flushed and the summary written. Options: `--dir`, `--peer`, `--max-minutes`,
`--min-minutes`, `--plateau-minutes`, `--sample-seconds`, `--sync-peers`, `--tip`, `--listen-port` (19735),
`--log-level`, `--label`; for `verify`: `--sample`, `--rate`, `--esplora`.

## Output (`<dir>/runs/<UTC time>[-label]/`)

- `samples.csv`: one row per sample: graph channels, nodes (graph and announced), policies, channels without policy,
  disabled policies, assumed/unverified and spent channels, pending writes, the store's memory estimate, the
  `NLightning.Gossip` meter (received/accepted/rejected/orphaned/dropped, queue depths), connected peers, process RSS,
  managed heap, GC counts, CPU (% of one core over the interval), `probe.db` and WAL size, warnings/errors logged,
  funding lookups, write-behind flushes.
- `peers.csv`: per sample and peer: connected, connects, disconnects seen, the sync state (`GossipSyncManager`),
  both timestamp filters, the 256/257/258 it handed over and what the ingress refused at the door, range replies and
  the SCIDs they listed, `reply_short_channel_ids_end` count, its own queries, zlib-encoded replies.
- `range-replies.csv`: every `reply_channel_range` header (first block, number of blocks, end, `sync_complete`,
  encoding, SCIDs, first and last SCID block).
- `probe.log`: the node's log (Information and up by default; EF Core at Warning).
- `summary.json`: startup (migrate and graph load time, retained heap and RSS of the load), stop (final flush), the
  meter's counters and histograms by tag, warnings/errors by category, per-peer traffic, peer connection records,
  time to 50/90/95/99/100 % of the final channel count, and the graph's shape at the end (policies per channel, stale
  policies, capacity estimate).
- `verify.csv`, `verify-summary.json` (`verify`): per sampled channel the funding output's check (valid and unspent,
  valid and spent, script mismatch, no such transaction or output), the amount and the capacity estimate
  (`EstimatedCapacityMsat`, the larger `htlc_maximum_msat`), grouped by the freshness of the channel's policies.
