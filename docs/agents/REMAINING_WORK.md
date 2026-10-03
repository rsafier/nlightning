# Remaining work

This is a high-level list of what NLightning still needs to be a full, real-funds BOLT node. It was written on 2026-09-26 at the end of the "safe channels + BOLT 5 + Mutinynet" goal (`wip/fafo`, ABCD waves 0–7), and updated after gossip waves G-A (`164289a`), G-B (`5bbfbb5`), G-C (`4dc0f77`), G-D (`48a8951`), anchors waves O7 (`897f032`) and O7b (`f9fad19`), and the mainnet gossip probe (`fbb113b`), and wave M6 (onion messages and on-chain `withdraw`, `641a5fff`), and wave B12 (BOLT 12 offers, `a3445f3f`), and wave lh1 (close and wallet safety fixes, BOLT 12 closed, keysend, restore hardening, `a6c633f9`), and wave qit (quiescence and the interactive-tx layer, `b7d14056`), and lane nl559 (LDK's peer-storage size refusal answered with a fitting blob, NL-559), and lane nl330 (a preimage on a close a reorg replaced is reconciled with what was already told upstream, NL-330), and the batch3 wave (32 open lows fixed across 11 parallel lanes).
- Detailed status per item lives in [`ISSUES.md`](ISSUES.md) (NL IDs) and [`BOLT_COVERAGE.md`](BOLT_COVERAGE.md).
- Designs live in the plan files linked below.
- Update this file when a line item lands or a new one is found.
- The batch waves (batch3: 32, batch4: 43, batch5: 19, batch6: 8, batch7: 11 open-issue resolutions) each ran as parallel agent lanes; their ledger notes carry the per-item SHAs.

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
- **Onion messages** (wave M6, NL-080): forward, deliver and reply paths with per-peer and global rate limits, proven against CLN; `option_onion_messages` Optional by default. On-chain `withdraw` (NL-441) in the same wave.
- **Keysend and custom onion records** (wave lh1, NL-459): send and receive with custom records (>= 65536), IPC `keysend` (31), proven against LND 0.20 in both directions; single part only.
- **BOLT 12 offers** (wave B12, NL-447 closed in wave lh1 with the CLN-captured vectors and the invoice prune): `createoffer`/`listoffers`/`disableoffer`/`payoffer`/`fetchinvoice`, invoice_requests answered with node-signed invoices over blinded payment paths, offers paid over the invoice's blinded paths (MPP, introduction = us); both directions proven against CLN v26.06.8.
- **Mainnet HTLC gate open** (O6-T4, wave G-D): HTLCs on for every network by default after the full BOLT 5 proof set.
- **Live Mutinynet smoke test.** Open, pay, receive, restart, cooperative close, and a public channel with our node announced as `NLightningFAFO` and visible on mutinynet.com ([`MUTINYNET.md`](MUTINYNET.md)).
- **Platform.** net10.0 + net11.0; SQLite, Postgres and SQL Server schemas kept in sync.

## Before real funds (mainnet gate)

- ~~**Enable HTLCs on mainnet.**~~ **Done in gossip wave G-D** (BOLT5 plan O6-T4, NL-094 fixed): HTLCs are on for every network by default and `Node:EnableHtlcs=false` turns them off (6de56ad); NL-315 fixed on the way; the integrator kept the flip after on-chain 24/24, LND 58/58 incl. N9, CLN 22/22 and ABCD 3 x 10/10 (`BOLT5_ONCHAIN_PLAN.md` "O6-T4 decision"). Remaining BOLT 5 follow-up (NL-313) is not a fund-safety blocker (NL-309 wontfix). Fixed since: NL-330 (lane nl330, the replaced-close upstream reconciliation), NL-307, NL-312, NL-335, NL-336 (the batch3 wave) and NL-308, NL-318, NL-329, NL-461 (batch4).
- **Anchor channels.**
  - Done in wave O7 (BOLT5 plan O7-T1..T3): wallet signing and persisted fee-input reservations (NL-067), CPFP of our commitment with RBF, anchors HTLC txs funded by wallet inputs, anchors penalties (NL-314); Docker anchors proofs 12/12 against LND.
  - ~~Left before `option_anchors` can be enabled (O7-T4).~~ **Done in wave O7b**: on-chain wallet reserve per anchors channel (NL-379), package relay with `submitpackage` (NL-380), bumping the peer's commitment through our anchor (NL-381); `option_anchors` is advertised Optional by default (d4cc3f8). Operators need confirmed on-chain funds of 10,000 sat per anchors channel (up to 100,000; `Node:Anchors`), or set `Node:Features:OptionAnchors=No`.
  - Anchors follow-ups: none left (NL-387, NL-392, NL-393 fixed earlier; NL-383, NL-384, NL-386, NL-389..NL-391 in batch4).
- **Signer and key persistence.** Done for channel signing data: the signer reloads it from the DB on first use (NL-067 first half, gossip wave G-A); `ChannelManager` no longer registers by hand (NL-343, gossip wave G-B). Wallet signing landed in wave O7 (NL-067 fixed).
- **Operational hardening.**
  - Watchtower-free safety review.
  - Backup and restore story for channel state: **done in wave rf1** (static channel backup export/verify/restore, IPC 21-23, NL-426; BOLT 1 peer storage, NL-010). Wave lh1 added the background search for an old funding spend (NL-430), every known peer address at restore (NL-431) and persisted peer-storage retrievals with `listpeerstorage` (IPC 32, NL-432). Lane nl559 fixed NL-559 (LDK refuses our 65,531-byte blob for its 1,024-byte limit: the refusal's warning is answered with a 1,024-byte backup LDK keeps and hands back, proven by `LdkPeerStorageTests`). Left: height-0 proof (NL-435).
  - Real mainnet soak.
- **Security review** of key-file handling and the IPC cookie: **done in wave rf1** (`docs/agents/SECURITY_REVIEW.md`; NL-148, NL-159, NL-212 fixed). Left: NL-224 (SR-14), NL-436 (SR-17), NL-437 (SR-09); NL-439 closed in batch7 (the Windows pipe ACL review found no gap, and the daemon warns at start when the database file is group/world readable).

## Protocol features

- **Trampoline routing (BOLTs PR #836)** — plan: [`TRAMPOLINE_PLAN.md`](TRAMPOLINE_PLAN.md), NL-875, in progress on `wip/fafo` (draft PR on `wip/fafo`): client, relay and target in the spec format (56/57, TLV 20), blinded and BOLT 12 included; proofs are the spec vectors and in-process harness tests; interop with Eclair (after #2819) and LDK left as follow-ups.
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
  - Next (mainnet gossip): a relay run from a node with a public channel (NL-417) (the relay pause on a full outbox, NL-360, is done in lane nl360: the outbox cap ships on), the 24 h Mutinynet and multi-day mainnet soaks (NL-376), and the d12 follow-ups NL-422 (Esplora live proof; NL-418..NL-421 fixed in batch4, NL-424..NL-425 in batch5, NL-416's streaming getblock and NL-423's per-block Esplora batching in batch7).
  - Follow-ups: NL-357, NL-367 (Docker Proof G4 (b)/(c)), NL-377, NL-407, NL-422, B7-CU-01b (accept the previous fee for a while); batch4 fixed NL-345, NL-363, NL-365, NL-368, NL-371, NL-378, NL-418..NL-421, NL-549; batch5 fixed NL-346, NL-361, NL-364, NL-369, NL-424, NL-425; batch7 fixed NL-366, NL-374, NL-416, NL-423.
- **Attribution data** (M3b). Wired end to end but kept experimental: LND 0.20 and 0.21 do not implement it, so it can't be proven against LND (NL-332). Follow-ups NL-333 (the retry policy honors the attribution blame) and NL-334 (a reverted fulfill's replay goes up without attribution instead of a garbled block) fixed in batch4.
- **Route blinding** (ONION M5): **done in wave rf1** (NL-079, NL-339); MPP over blinded paths and introduction = us added in wave B12. BOLT 11 blinded paths (bLIP 39 draft, LND's field 20: decode, `payinvoice`, our own behind `Node:Invoices:BlindedPaths`) and dummy hops in our payment paths (`Node:Invoices:BlindedPathDummyHops`, default 1) done in wave nl440 (NL-440). Dummy hops in our onion-message paths landed in batch5 (NL-525, `OnionMessages:BlindedPathDummyHops`, default 1).
- **Onion messages** (ONION M6): **done in wave M6** (NL-080; proven against CLN v26.06.8, `option_onion_messages` Optional by default). NL-442 and NL-444 fixed in wave lh1. NL-446 (tuning, queue gauge) and NL-464 fixed in batch4.
- **BOLT 12 offers** (NL-447, wave B12 of [`BOLT12_PLAN.md`](BOLT12_PLAN.md)): **integrated in wave B12** (`a3445f3f`), **closed in wave lh1** (`a6c633f9`: prune timer NL-448, CLN-captured vectors NL-450, NL-453..NL-455). Left: reachability (NL-452); NL-451's rule-layer vector assertions landed in batch5. Out of scope: refunds, recurrence, payer proofs, blinded issuer ids (D2).
- **Dual funding** (v2 open, NL-037). **DF1/DF2 done in wave sp1** (lane SP1-F: `open_channel2`/`accept_channel2`, `openchannel --dual-fund`, RBF of an unconfirmed open off by default; Proof DF green against CLN v26.06.8). **DF3 done in wave d13** (D13: `option_dual_fund` Optional by default, out of the experimental set; `openchannel` stayed v1 unless `--dual-fund` until NL-551: since then it opens v2 whenever `option_dual_fund` is negotiated and no push is given, `--v1` forces v1). Lane dfrbf: RBF of a v2 open on by default, public opens included, `bumpopen` (IPC 38), the channel follows whichever signed attempt confirms (NL-527, NL-528). Lane accrbf: the accepter may start the RBF too (NL-530). Left: nothing — NL-529 fixed in batch5 (NL-473, NL-474 in batch4).
- **Splicing** (NL-021; user priority). Quiescence done in wave qit; waves SP1 and SP2 done in waves sp1/sp2; **wave SPR done in wave spr** (`a0800ac2`: splice RBF both directions, `bumpsplice` (IPC 37) and the optional auto-bump, Proof SPR green against CLN v26.06.8, `Day0FlowTests` RBF step; day-0 hardening NL-490, NL-492, NL-494, NL-495, NL-497 fixed; LND 0.20 observes a spliced channel). **D13 applied in wave d13** (`option_splice`/`option_quiesce` Optional by default on every network, mainnet included; splice RBF recency by blocks, NL-520). Follow-ups NL-477 (CLN-side), NL-480, NL-496, NL-509, NL-510, NL-515, NL-467, NL-468, NL-470; CLN-side NL-502, NL-511; batch5 fixed NL-483, NL-488, NL-507, NL-529; batch7 fixed NL-493, NL-508. From the day-0 work: NL-498, NL-514.
- **Simple taproot channels** (NL-877, plan [`TAPROOT_CHANNELS_PLAN.md`](TAPROOT_CHANNELS_PLAN.md)): not started. The spec is merged (BOLTs #995, 2026-05-04) and LND 0.21 and Eclair 0.14 run them as private channels; MuSig2, taproot scripts, nonce TLVs, BOLT 5 resolvers and interop proofs are needed (T0-T6). Public taproot channels wait for taproot gossip (BOLTs #1059, still a draft; NL-878).
- **Zero-conf and scid-alias channels** as first-class options (low priority per the user, 2026-09-27).
- ~~**Keysend / spontaneous payments, custom TLV records.**~~ **Done in wave lh1** (NL-459). Left: a dedicated custom-records column (NL-460).
- ~~**Peer storage** (`option_provide_storage`)~~ (done, NL-010) and ~~**DNS bootstrap** (BOLT 10)~~ (done in lane bolt10, NL-113: on by default on mainnet since 2026-09-28, with public fallback resolvers; left NL-541..NL-544; NL-545's testnet4 and signet seeds landed in batch5).

## Payments and wallet

- MPP send parts persisted across restarts (NL-321); hold times of parts are in memory only.
- A duplicate HTLC for a Settled invoice from before NL-323 (upgrade path).
- Wallet features: on-chain send **done in wave M6** (`withdraw`, ClientCommand 25, NL-441); coin control, consolidation, better fee estimation per target (partly done for sweeps) remain.

## Interop and testing

- **More implementations.** Eclair and LDK interop suites (CLN done: 22/22; mainnet gossip already exercised against Eclair, CLN and LND peers). Update (lane b10-eclair): the Eclair suite covers the day-0 shapes (splicing, dual-funded RBF, force and simple closes, public channels, gossip queries, offers); left against Eclair: attribution_data (NL-332) and HTLCs resolved on chain. A CLN-funded ABCD-style multi-hop test.
- **CI.**
  - Run the Docker-class suites in CI. Since the test harness (phases 3-6) and NL-820 the LND-based suites run on the Kubernetes harness only (`scripts/run-cluster.sh --matrix`, the full matrix in about 18 min at 6 namespaces) and since NL-866 the CLN, Eclair, LDK and Postgres suites too (their Docker backends retired), so CI needs a cluster (deferred, owner decision 2026-10-03); Tor interop stays Docker only (`scripts/run-interop.sh tor`).
  - Per-fixture container names, so suites can run in parallel.
- **Platform checks not yet done.** NativeAOT publish under SDK 11 and the Wasm/Blazor build on SDK 11 (NL-300).
- **Known flakes.** LND fixture startup races (NL-263 family, NL-319 "server still starting"; the LNUnit Docker fixture is gone since NL-820, the LND suites run on the cluster harness).

## Tech debt worth scheduling

- `ChannelModel` legacy HTLC collections and the remaining clean-architecture violations (Application → Infrastructure) (NL-032, NL-157).
- The IPC surface: `disconnect` done in wave rf1 (ClientCommand 24, NL-152 fixed), `withdraw` in wave M6 (25), the BOLT 12 commands in wave B12 (26-30), `keysend` (31) and `listpeerstorage` (32) in wave lh1, the splice/policy/bump commands (33-38) in the splicing waves, and `shutdown` (39) in NL-591/NL-592 (next free 40); richer channel and payment queries remain.
- ~~The binary naming: `nltg` in the usage text vs the `NLightning.Client` assembly~~ resolved by convention (NL-185, batch3: the docs state usage texts say `nltg` on purpose; assemblies keep their names).

## Standard test cycle

Tests run on net10.0 only; net11.0 is build-only. Integration runs skip the SQL Server container tests (not ported; Postgres runs on the cluster). See `CLAUDE.md`.
