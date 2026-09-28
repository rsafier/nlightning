# NLightning.GossipProbe

A test harness, not product code: it runs NLightning's BOLT 7 gossip stack against real **mainnet** peers as a
gossip-only leech and samples it, to test the graph at mainnet scale, without a mainnet bitcoind
(`AssumeChannelValid`) or against a real one (`--chain rpc`, every funding output checked). Results and the
method are in `docs/agents/MAINNET_GOSSIP_PROBE.md`.

## What it runs

- The daemon's own composition (`AddNltgNodeServices`, the same the Docker test node uses) on mainnet, with a SQLite
  database `probe.db` and a throwaway node key `node-key.json` (generated at the first run, password
  `nltg-gossip-probe`, no funds, never used for a channel) in the probe directory (default `~/.nltg-gossip-probe/`,
  never `~/.nltg/mainnet`).
- By default (`--chain stub`) no bitcoind: `IBlockchainMonitor` is a stub that reports the mainnet tip read from
  the Esplora API at start (for `query_channel_range(0, tip + 1)`), raises no block and throws on any publish;
  `IFundingOutputLookup` is a stub that counts calls (it must stay at 0). `--chain rpc`: see below.
- Gossip settings: `Gossip:Enabled`, `SyncEnabled` and (stub mode only) `AssumeChannelValid` on (channel
  announcements accepted on their signatures), `RelayEnabled` off (we relay nothing to mainnet peers), `AcceptPublicChannels` and
  `AllowPublicChannelsOnMainnet` off, `Node:EnableHtlcs` false (the `AssumeChannelValid` startup guard requires it),
  `Gossip:SyncPeers` = the number of peers (each peer runs a range sync, so every implementation's replies are
  checked).
- It listens on `127.0.0.1` only, connects outbound to the peers, opens no channel, sends no HTLC and announces
  nothing (it has no channel to announce).

### RPC mode (`--chain rpc`): real funding-output verification

With `--chain rpc` the probe talks to a real mainnet bitcoind instead of the stubs: `Gossip:AssumeChannelValid` is
off and every `channel_announcement` goes through the product's D3 path (`BitcoinChainService` +
`FundingOutputLookup`, BOLT 7 plan §3.4: `getblockhash`, `getblock <hash> 1`, `gettxout`), with its concurrency and
rate limits (`--chain-concurrency`, `--chain-rate` set `Gossip:ChainLookupConcurrency`/`ChainLookupsPerSecond`).
The probe only decorates those services to count and time the calls (`lookups.csv`, `summary.json` → `chain`) and
counts every HTTP request of the process (the RPCs; the peers are plain TCP). The chain monitor is a poller
(`RpcBlockFollower`): every `--block-poll-seconds` it reads the blocks connected since the start (at most
`--max-blocks-per-poll`, one `getblock` at a time, never history) and raises `OnBlockInputs` for the graph pruner's
spend detection; `LastProcessedBlockHeight` (the range sync's end) is bitcoind's header height by default
(`--sync-tip headers`), so a bitcoind still in IBD sees the whole graph and the channels above its blocks are the
lookup's transient `BlockNotFound`. Read-only RPCs only: `getblockchaininfo` (once per sample), `getblockcount`,
`getblockhash`, `getblock` (verbosity 1 for lookups, 0 for followed blocks), `gettxout`; nothing is published.

The RPC settings come from a `KEY=VALUE` file (`--rpc-env`, default `~/.nltg-gossip-probe/mainnet-rpc.env`, mode
600): `MAINNET_RPC_URL`, `MAINNET_RPC_USER`, `MAINNET_RPC_PASSWORD`. The password stays in the node's in-memory
configuration; it is never printed or written. `chaininfo` checks that the process reaches bitcoind (macOS Local
Network privacy can block a host `dotnet` from LAN addresses, NL-276; run it in an SDK container with
`--network host` then).

```bash
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll chaininfo
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll run --chain rpc \
    --dir ~/.nltg-gossip-probe/verified --chain-concurrency 3 --label verified-run1
```

Use a fresh `--dir` for a verified run: a database filled by an `AssumeChannelValid` run holds `Assumed` channels.

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
`--log-level`, `--label`, `--chain`, `--rpc-env`, `--chain-concurrency`, `--chain-rate`, `--sync-tip`,
`--block-poll-seconds`, `--max-blocks-per-poll`, `--relay-to`, `--bootstrap`; for `verify`: `--sample`, `--rate`, `--esplora`.

### Relay run (`--relay-to`, D12)

`--relay-to <node id|alias prefix>` (with `--chain rpc`) turns `Gossip:RelayEnabled` on, but the relay's peer
directory lists only that peer, so the other peers stay sync peers and never get our relayed gossip. Every relay send
is recorded per peer and type (`summary.json` → `relay`): sent or queued, refused by the outbox (`.refused`), and
`.echo_to_origin` for a message version (a 256's scid, a 257/258's signature) that the same peer had sent us. The
probe still has no channel and announces nothing of its own. The relay backlog is the `relay_pending` queue depth in
`samples.csv`.

```bash
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll run --chain rpc \
    --dir ~/.nltg-gossip-probe/verified --relay-to ACINQ --min-minutes 20 --max-minutes 25 --label relay
```

### BOLT 10 bootstrap run (`--bootstrap`, NL-113)

`--bootstrap` (a flag without value; no `--peer` or `--relay-to` with it) configures no peer: the probe starts the
product's `PeerBootstrapService` right after `PeerManager.StartAsync`, as `NltgDaemonService` does, with
`Node:Bootstrap` at its mainnet default (on since 2026-09-28; every other run sets `Node:Bootstrap:Enabled=false`),
so the peers come only from the DNS seeds and the sync runs from them (`Gossip:SyncPeers` at its product default, 3,
unless `--sync-peers` is given). The probe never dials. Use a fresh `--dir` so the node knows no peer and no graph
node (the bootstrap skips its run while the graph knows nodes with addresses, NL-543). After the run the probe asks
every mainnet seed once more through the product's seed client (the "seed census", not part of the discovery).

```bash
dotnet tools/NLightning.GossipProbe/bin/Release/net10.0/NLightning.GossipProbe.dll run --bootstrap --chain rpc \
    --dir ~/.nltg-gossip-probe/b10-bootstrap --min-minutes 40 --max-minutes 40 --sample-seconds 30 --label b10
```

Extra output: `bootstrap-seeds.csv` (every seed query: seed, outcome, candidates, rejected, whether the fallback
resolvers answered and what the system resolver said, seconds), `bootstrap-dials.csv` (every dial: seed, node id,
address, port, family, outcome `Connected`/`AlreadyConnected`/`Failed`/`TimedOut`, seconds, error),
`bootstrap-runs.csv` (every run: skip reason, candidates collected and selected, dials, connections, peers after),
`bootstrap-peers.csv` (every peer seen connected: address, first and last sample, alias and feature bits of its
node_announcement once gossip has it, init feature bits), six `bootstrap_*` columns in `samples.csv`, the discovered
peers in `peers.csv`, and `summary.json` → `bootstrap` (the status, dials by outcome, seed, family and port, minutes
to the first and third peer and to `sync_complete`, the seed census, the peers with an implementation guess: LND by
its node_announcement bit 2023 or its default alias, the first 20 hex digits of its node id).

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
- `lookups.csv` (RPC mode): every funding output lookup: time, `verify`/`lookup`, SCID, block, status,
  confirmations, amount, milliseconds (the wait for the lookup's limits included).
- `probe.log`: the node's log (Information and up by default; EF Core at Warning).
- `summary.json`: startup (migrate and graph load time, retained heap and RSS of the load), stop (final flush), the
  meter's counters and histograms by tag, warnings/errors by category, per-peer traffic, peer connection records,
  time to 50/90/95/99/100 % of the final channel count, and the graph's shape at the end (policies per channel, stale
  policies, capacity estimate).
- `verify.csv`, `verify-summary.json` (`verify`): per sampled channel the funding output's check (valid and unspent,
  valid and spent, script mismatch, no such transaction or output), the amount and the capacity estimate
  (`EstimatedCapacityMsat`, the larger `htlc_maximum_msat`), grouped by the freshness of the channel's policies.
