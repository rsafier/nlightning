# Remaining work

This is a high-level list of what NLightning still needs to be a full, real-funds BOLT node. It was written on 2026-09-26 at the end of the "safe channels + BOLT 5 + Mutinynet" goal (`wip/fafo`, ABCD waves 0–7), and updated after gossip wave G-A (`164289a`).
- Detailed status per item lives in [`ISSUES.md`](ISSUES.md) (NL IDs) and [`BOLT_COVERAGE.md`](BOLT_COVERAGE.md).
- Designs live in the plan files linked below.
- Update this file when a line item lands or a new one is found.

## Done (for orientation)

- **Channel lifecycle.**
  - BOLT 2 open (v1), normal operation, reestablish, `update_fee`, dust limits and HTLC deadlines.
  - Cooperative close: legacy and `option_simple_close`.
  - All proven against LND in Docker, with CLN interop 17/17.
- **Multi-hop payments.** Forwarding, final hop, send, MPP both directions, retries with a fee limit, a persistent onion replay set, and error onions. The ABCD suite (LND → NLightning → NLightning → LND) passes 3 runs in a row.
- **BOLT 5 on-chain enforcement, O0–O6.**
  - Our force close and theirs, with HTLC resolution.
  - Penalties, including a real LND channel-database rollback breach.
  - On-chain preimage handling, including final-hop claims (NL-316).
  - RBF sweeps and reorgs.
  - O8 mempool reaction: preimages and penalties from unconfirmed transactions (NL-098), and the `chainstatus` halt gate (NL-216), gossip wave G-A.
- **Live Mutinynet smoke test.** Open, pay, receive, restart, cooperative close ([`MUTINYNET.md`](MUTINYNET.md)).
- **Platform.** net10.0 + net11.0; SQLite, Postgres and SQL Server schemas kept in sync.

## Before real funds (mainnet gate)

- **Enable HTLCs on mainnet.** Re-evaluate BOLT5 plan O6-T4 now that NL-316 and NL-322 are fixed. `Node:EnableHtlcs` is still regtest/signet only (NL-094).
- **Anchor channels.**
  - CPFP of our commitment and fee inputs for HTLC txs (BOLT5 plan O7).
  - Wallet signing: `SignWalletTransaction` still throws (NL-067).
  - Until then anchors stay experimental and channels are `static_remotekey` only.
- **Signer and key persistence.** Done for channel signing data: the signer reloads it from the DB on first use (NL-067 first half, gossip wave G-A); `ChannelManager` still registers by hand (NL-343). Wallet signing remains (above).
- **Operational hardening.**
  - Watchtower-free safety review.
  - Backup and restore story for channel state.
  - Real mainnet soak.
- **Security review** of key-file handling and the IPC cookie (NL-148 remainder, NL-212 Windows ANSI key-file fallback).

## Protocol features

- **BOLT 7 gossip, graph and pathfinding** — plan: [`BOLT7_GOSSIP_PLAN.md`](BOLT7_GOSSIP_PLAN.md), four waves G-A..G-D, NL-099.
  - Public channels: `announce_channel`, `announcement_signatures`, our `channel_announcement` and `node_announcement`.
  - Validation and a graph store.
  - Gossip sync and relay.
  - Graph pathfinding in `PaymentService`: pay any node without route hints.
  - Wave G-A done (`164289a`): typed 256/257/259 with LND/CLN vectors, signature verifier, funding output lookup, graph schema, `SignChannelAnnouncement`, address descriptors (NL-008), Domain graph, validator and pathfinder; none wired yet.
  - Next, wave G-B: public channels end to end (NL-341 handlers, NL-342 handler), graph ingress/store/pruner and IPC, Docker proofs G0-G2.
  - Follow-ups: NL-343..NL-346.
- **Attribution data** (M3b). Wired end to end but kept experimental: LND 0.20 does not implement it, so it can't be proven against LND (NL-332). Follow-ups:
  - the retry policy ignores attribution blame (NL-333);
  - a fulfill reverted on disconnect loses its attribution (NL-334).
- **Route blinding** (ONION M5): blinded payment paths when receiving and forwarding (NL-079). Includes passing on a `fulfillment_payload` for blinded incoming adds.
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

- **More implementations.** Eclair and LDK interop suites (CLN done: 17/17). A CLN-funded ABCD-style multi-hop test.
- **CI.**
  - Run the Docker suites in CI. They are local only today, and NL-276 blocks the host process on macOS, so an in-container runner is needed.
  - Per-fixture container names, so suites can run in parallel.
- **Platform checks not yet done.** NativeAOT publish under SDK 11 and the Wasm/Blazor build on SDK 11 (NL-300).
- **Known flakes.** LNUnit fixture startup races (NL-263 family, NL-319 "server still starting").

## Tech debt worth scheduling

- `ChannelModel` legacy HTLC collections and the remaining clean-architecture violations (Application → Infrastructure) (NL-032, NL-157).
- The IPC surface: disconnect, peer management, richer channel and payment queries (NL-152 remainder).
- The binary naming: `nltg` in the usage text vs the `NLightning.Client` assembly (NL-185).

## Standard test cycle

Tests run on net10.0 only; net11.0 is build-only. Docker runs skip the SQL Server container tests (Postgres only). See `CLAUDE.md`.
