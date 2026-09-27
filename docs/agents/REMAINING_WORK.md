# Remaining work

This is a high-level list of what NLightning still needs to be a full, real-funds BOLT node. It was written on 2026-09-26 at the end of the "safe channels + BOLT 5 + Mutinynet" goal (`wip/fafo`, ABCD waves 0–7), and updated after gossip waves G-A (`164289a`), G-B (`5bbfbb5`), G-C (`4dc0f77`), G-D (`48a8951`), anchors waves O7 (`897f032`) and O7b (`f9fad19`), and the mainnet gossip probe (`fbb113b`).
- Detailed status per item lives in [`ISSUES.md`](ISSUES.md) (NL IDs) and [`BOLT_COVERAGE.md`](BOLT_COVERAGE.md).
- Designs live in the plan files linked below.
- Update this file when a line item lands or a new one is found.

## Done (for orientation)

- **Channel lifecycle.**
  - BOLT 2 open (v1), normal operation, reestablish, `update_fee`, dust limits and HTLC deadlines.
  - Cooperative close: legacy and `option_simple_close`.
  - All proven against LND in Docker, with CLN interop 22/22.
  - **Anchor channels by default** (`option_anchors` Optional, wave O7b): CPFP with RBF, package relay, peer-anchor bumping, a per-channel on-chain reserve; LND, CLN, ABCD and gossip suites pass on anchors channels.
- **Multi-hop payments.** Forwarding, final hop, send, MPP both directions, retries with a fee limit, a persistent onion replay set, and error onions. The ABCD suite (LND → NLightning → NLightning → LND) passes 3 runs in a row.
- **BOLT 5 on-chain enforcement, O0–O6.**
  - Our force close and theirs, with HTLC resolution.
  - Penalties, including a real LND channel-database rollback breach.
  - On-chain preimage handling, including final-hop claims (NL-316).
  - RBF sweeps and reorgs.
  - O8 mempool reaction: preimages and penalties from unconfirmed transactions (NL-098), and the `chainstatus` halt gate (NL-216), gossip wave G-A.
- **BOLT 7 public channels, gossip and graph routing** (waves G-A..G-D): we pay and get paid over public channels without route hints against LND and CLN (goal proofs (a)-(e)); the whole mainnet graph synced and verified on chain against a real bitcoind ([`MAINNET_GOSSIP_PROBE.md`](MAINNET_GOSSIP_PROBE.md)); gossip stays off on mainnet by default (D12).
- **Mainnet HTLC gate open** (O6-T4, wave G-D): HTLCs on for every network by default after the full BOLT 5 proof set.
- **Live Mutinynet smoke test.** Open, pay, receive, restart, cooperative close, and a public channel with our node announced as `NLightningFAFO` and visible on mutinynet.com ([`MUTINYNET.md`](MUTINYNET.md)).
- **Platform.** net10.0 + net11.0; SQLite, Postgres and SQL Server schemas kept in sync.

## Before real funds (mainnet gate)

- ~~**Enable HTLCs on mainnet.**~~ **Done in gossip wave G-D** (BOLT5 plan O6-T4, NL-094 fixed): HTLCs are on for every network by default and `Node:EnableHtlcs=false` turns them off (6de56ad); NL-315 fixed on the way; the integrator kept the flip after on-chain 24/24, LND 58/58 incl. N9, CLN 22/22 and ABCD 3 x 10/10 (`BOLT5_ONCHAIN_PLAN.md` "O6-T4 decision"). Remaining BOLT 5 follow-ups (NL-307..NL-309, NL-312, NL-313, NL-318, NL-329, NL-330, NL-335, NL-336) are not fund-safety blockers.
- **Anchor channels.**
  - Done in wave O7 (BOLT5 plan O7-T1..T3): wallet signing and persisted fee-input reservations (NL-067), CPFP of our commitment with RBF, anchors HTLC txs funded by wallet inputs, anchors penalties (NL-314); Docker anchors proofs 12/12 against LND.
  - ~~Left before `option_anchors` can be enabled (O7-T4).~~ **Done in wave O7b**: on-chain wallet reserve per anchors channel (NL-379), package relay with `submitpackage` (NL-380), bumping the peer's commitment through our anchor (NL-381); `option_anchors` is advertised Optional by default (d4cc3f8). Operators need confirmed on-chain funds of 10,000 sat per anchors channel (up to 100,000; `Node:Anchors`), or set `Node:Features:OptionAnchors=No`.
  - Anchors follow-ups (none a fund-safety blocker): NL-384 (partial), NL-386, NL-387, NL-389..NL-393, NL-383.
- **Signer and key persistence.** Done for channel signing data: the signer reloads it from the DB on first use (NL-067 first half, gossip wave G-A); `ChannelManager` no longer registers by hand (NL-343, gossip wave G-B). Wallet signing landed in wave O7 (NL-067 fixed).
- **Operational hardening.**
  - Watchtower-free safety review.
  - Backup and restore story for channel state: **done in wave rf1** (static channel backup export/verify/restore, IPC 21-23, NL-426; BOLT 1 peer storage, NL-010). Left: rescan of an old funding spend (NL-430), graph addresses at restore (NL-431), height-0 proof (NL-435), peer-storage retrievals over IPC (NL-432).
  - Real mainnet soak.
- **Security review** of key-file handling and the IPC cookie: **done in wave rf1** (`docs/agents/SECURITY_REVIEW.md`; NL-148, NL-159, NL-212 fixed). Left: NL-224 (SR-14), NL-436 (SR-17), NL-437 (SR-09), NL-439 (Windows pipe ACL, database file mode).

## Protocol features

- **BOLT 7 gossip, graph and pathfinding** — plan: [`BOLT7_GOSSIP_PLAN.md`](BOLT7_GOSSIP_PLAN.md), four waves G-A..G-D, NL-099.
  - Public channels: `announce_channel`, `announcement_signatures`, our `channel_announcement` and `node_announcement`.
  - Validation and a graph store.
  - Gossip sync and relay.
  - Graph pathfinding in `PaymentService`: pay any node without route hints.
  - Wave G-A done (`164289a`): typed 256/257/259 with LND/CLN vectors, signature verifier, funding output lookup, graph schema, `SignChannelAnnouncement`, address descriptors (NL-008), Domain graph, validator and pathfinder; none wired yet.
  - Wave G-B done (`5bbfbb5`): public channels end to end (`openchannel --public`, NL-341, NL-342, NL-236), our channel_announcement/public channel_update/node_announcement relayed, graph ingress/store/pruner and `listnodes`/`listgraphchannels`, Docker Proofs G0, G1 (a)-(c), G2 (a)-(c) against LND.
  - Wave G-C done (`4dc0f77`): gossip queries answered from the graph and sync by queries (G3-T1/T2, `gossip_queries_ex` G3-T4), relay of others' gossip through the `PeerOutbox` (G3-T3, NL-351), graph routes with mission control in `PaymentService` and a refresh through gossip on failures (G4-T2/T3, G3-T5), `getroute` (IPC 19), invoices without route hints once an announced channel can receive (NL-245), NL-348..NL-356 fixed. The BOLT 7 goal proofs (a)-(e) are green: we pay and get paid over public channels without route hints against LND 0.20 and CLN v26.06.8.
  - Wave G-D done (`48a8951`): rate limits, misbehaviour ban, graph caps and the relay backlog bound (G5-T2), `Meter("NLightning.Gossip")` and `describegraph` (IPC 20) (G5-T4), batched graph store and streamed load, 200k channels loaded in 1.55 s on SQLite (G5-T3), the mainnet gossip gate written into the config template and the Mutinynet soak started (G5-T5 partial).
  - Mainnet gossip probe done (`bc3a2c3`, [`MAINNET_GOSSIP_PROBE.md`](MAINNET_GOSSIP_PROBE.md)): `tools/NLightning.GossipProbe` synced the whole mainnet graph (about 40,000 channels) from Eclair, LND and CLN peers, with `Gossip:AssumeChannelValid` (new; refused on mainnet with HTLCs or public channels) and verified against an unpruned mainnet bitcoind (about 18 min, 0 chain contradictions); interop fixes NL-400 (CLN range replies) and NL-401 (old LND updates), mainnet key files NL-403, sync efficiency NL-402, NL-404, NL-405, NL-410..NL-412.
  - Wave d12 done (`aa1cc10`, D12 decided): `Gossip:MaxMemoryMb` 1,024 MB enforced against the process RSS (NL-373), announcements without an update kept out of the graph until their first update (NL-406), scid-partitioned ingress (NL-408), verified-sync efficiency (NL-413, NL-414, NL-415: download multiple 3.6x to 2.3x, 99 % of the mainnet graph in 13 min), an Esplora funding-txid source for pruned nodes proven against our own headers, and **graph and gossip sync on by default on mainnet**; relay of other nodes' gossip stays off on mainnet (NL-417) and `AllowPublicChannelsOnMainnet` stays false.
  - Next (mainnet gossip): a relay run from a node with a public channel (NL-417), the relay pause on a full outbox so the outbox cap can ship on (NL-360), the 24 h Mutinynet and multi-day mainnet soaks (NL-376), NL-416, and the d12 follow-ups NL-418..NL-425 (sybil limit of pending candidates, re-query after a budget refusal, the ingress as a pending-channels source, Esplora live proof, speed and network check).
  - Follow-ups: NL-345, NL-346, NL-357, NL-360..NL-378 (incl. Docker Proof G4 (b)/(c), NL-367), NL-407, NL-417..NL-425, B7-CU-01b (accept the previous fee for a while).
- **Attribution data** (M3b). Wired end to end but kept experimental: LND 0.20 does not implement it, so it can't be proven against LND (NL-332). Follow-ups:
  - the retry policy ignores attribution blame (NL-333);
  - a fulfill reverted on disconnect loses its attribution (NL-334).
- **Route blinding** (ONION M5): **done in wave rf1** (NL-079, NL-339). Left: MPP over blinded paths, BOLT 11 blinded paths, dummy hops (NL-440).
- **Onion messages** (ONION M6, optional).
- **BOLT 12 offers** (needs onion messages and blinded paths).
- **Dual funding / interactive-tx** (v2 open, NL-037). Messages and validators exist; no handlers.
- **Splicing and quiescence** (`stfu` is only answered with a warning).
- **Zero-conf and scid-alias channels** as first-class options.
- **Keysend / spontaneous payments, custom TLV records.**
- **Peer storage** (`option_provide_storage`) and **DNS bootstrap** (BOLT 10).

## Payments and wallet

- MPP send parts persisted across restarts (NL-321); hold times of parts are in memory only.
- A duplicate HTLC for a Settled invoice from before NL-323 (upgrade path).
- Wallet features: on-chain send, coin control, consolidation, better fee estimation per target (partly done for sweeps).

## Interop and testing

- **More implementations.** Eclair and LDK interop suites (CLN done: 22/22; mainnet gossip already exercised against Eclair, CLN and LND peers). A CLN-funded ABCD-style multi-hop test.
- **CI.**
  - Run the Docker suites in CI. They are local only today, and NL-276 blocks the host process on macOS, so an in-container runner is needed.
  - Per-fixture container names, so suites can run in parallel.
- **Platform checks not yet done.** NativeAOT publish under SDK 11 and the Wasm/Blazor build on SDK 11 (NL-300).
- **Known flakes.** LNUnit fixture startup races (NL-263 family, NL-319 "server still starting").

## Tech debt worth scheduling

- `ChannelModel` legacy HTLC collections and the remaining clean-architecture violations (Application → Infrastructure) (NL-032, NL-157).
- The IPC surface: `disconnect` done in wave rf1 (ClientCommand 24, NL-152 fixed; next free 25); richer channel and payment queries remain.
- The binary naming: `nltg` in the usage text vs the `NLightning.Client` assembly (NL-185).

## Standard test cycle

Tests run on net10.0 only; net11.0 is build-only. Docker runs skip the SQL Server container tests (Postgres only). See `CLAUDE.md`.
