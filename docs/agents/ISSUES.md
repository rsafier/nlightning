# NLightning Issue Ledger

The single durable issue ledger for this repo. GitHub issues are disabled on the fork, so this file replaces them. Every known bug, gap, spec violation, missing feature, test/CI hygiene problem and tech-debt item lives here, so nothing is lost between agent sessions.

Snapshot: 2026-09-25, `wip/fafo`. Sources: `docs/agents/{BOLT_COVERAGE,REPO_MAP,ONION_ROUTING_PLAN,LNBOLT_REVIEW}.md`, every `CLAUDE.md`, the onion M1/M2 workflow reports (open items, review fixes, final follow-ups), a `TODO`/`FIXME`/`NotImplementedException`/commented-out-file sweep, and a Release build. Bug claims were re-checked against the code at that snapshot; items still marked "unverified" in the evidence were not reproduced. Line numbers drift, so re-check the cited line before editing.

Updated 2026-09-25 after the fix swarm and its follow-ups were integrated into `wip/fafo` (at `1a38360`): statuses carry the `wip/fafo` SHAs (the swarm commits were cherry-picked with `-x`), and NL-203..NL-225 record the cross-batch review findings and the follow-ups the batches reported.

Updated 2026-09-25 after the four-lane work (l1 runtime, l2 BOLT 3/signer, l3 state machine, l4 onion M3) was integrated into `wip/fafo` (at `3c625e1`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `integrate:` commits have no lane counterpart), and NL-226..NL-238 record the lanes' new findings and open items.

Updated 2026-09-25 after ABCD wave 0 (W0-A engine seam + events, W0-B persistence, W0-C contracts, W0-D Bolt11, W0-E channel_update, W0-F multi-node harness) was integrated into `wip/fafo` (at `0b7e617`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`), and NL-239..NL-244 record the lanes' new findings (NL-239/NL-240 were already cited by ID in the W0-F harness).

Updated 2026-09-25 after ABCD wave 1 (W1-A channel wiring, W1-B payment core, W1-C payment schema, W1-D IPC/CLI, W1-E channel_update exchange and connect fixes) was integrated into `wip/fafo` (at `342d22e`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `4ae2eb3` and `342d22e` are `integrate:` commits), and NL-245..NL-255 record the lanes' new findings and seams.

Updated 2026-09-25 after ABCD wave 2 (W2-A reestablish, W2-B HTLC switch, W2-C send, W2-D N8 and ABCD Docker proofs) was integrated into `wip/fafo` (at `a5675cb`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `f2f1ef6` and `a5675cb` are `integrate:` commits), and NL-256..NL-270 record the lanes' and the integrator's new findings (NL-256 and NL-257 keep the IDs W2-B proposed; NL-257 was not reproduced and is wontfix).

Updated 2026-09-25 after ABCD wave 3 (W3-A N9 safety, W3-B N10 close (migration owner), W3-C N9 fees, W3-D replay and debt, W3-E CLN interop, W3-F BOLT 5 plan) was integrated into `wip/fafo` (at `c92d837`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `983b2b4` and `c92d837` are `integrate:` commits), and NL-271..NL-290 record the lanes' and the integrator's new findings (NL-278, NL-281 and NL-287 were found and fixed within the wave). The LND Docker suite could not run at integration (NL-276).

Updated 2026-09-26 after ABCD wave 4 (W4-A BOLT 5 plumbing (migration owner), W4-B BOLT 5 builders, W4-C net11, W4-D signet, W4-E interop follow-ups) was integrated into `wip/fafo` (at `6b5d50e`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `53accb1`, `960cf05`, `1161213`, `5cc32ba` and `6b5d50e` are `integrate:` commits), and NL-291..NL-300 record the lanes' and the integrator's new findings (NL-291 was cited by W4-D's commits; W4-A's proposed IDs were renumbered to NL-292..NL-294). The LND Docker suite ran from an SDK container (`--network host`, NL-276).

Updated 2026-09-26 after ABCD wave 5 (W5-A funding-spend watcher and resolution executor (migration owner), W5-B local commitment resolution, W5-C remote commitment resolution, W5-D revoked commitment penalties, W5-E Mutinynet live smoke) was integrated into `wip/fafo` (at `1a5ab49`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `dd2d64f`, `a5b24e3` and `1a5ab49` are `integrate:` commits), and NL-301..NL-320 record the lanes' and the integrator's new findings (NL-301..NL-306 keep the IDs W5-E cited in its commits and `MUTINYNET.md`; NL-305 is a duplicate of NL-280). The Docker suite ran from SDK containers on net10.0 and net11.0 (NL-276, NL-300).

Updated 2026-09-26 after ABCD wave 6 (W6-A persistent onion replay set (migration owner), W6-B basic_mpp receive, W6-C payment retries and MPP send, W6-D attribution_data library, W6-E option_simple_close, W6-F BOLT 5 O6) was integrated into `wip/fafo` (at `3ce3cad`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `9692ba4`, `e8841d6`, `a45d290` and `3ce3cad` are `integrate:` commits), and NL-321..NL-331 record the lanes' and the integrator's new findings (the lanes' proposed NL-321/NL-322 collided and were renumbered: W6-C's per-part persistence is NL-321, W6-B's on-chain gap NL-322, W6-D's serializer fix NL-324 and fulfillment_payload check NL-325). The Docker suite (106 tests) ran from SDK containers on net10.0 and net11.0.

Updated 2026-09-26 after ABCD wave 7 (W7-A attribution_data wiring (migration owner), W7-B final-hop on-chain claims and HTLC-set commitment, W7-C flakes and gates) was integrated into `wip/fafo` (at `4c37998`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `4751a26`, `672ed61` and `4c37998` are `integrate:` commits), and NL-332..NL-340 record the lanes' and the integrator's new findings (the lanes' proposed NL-331/NL-332 collided with existing or other lanes' IDs and were renumbered: W7-B's expiry gap is NL-335, its dust-exposure case NL-336, its `HtlcExpiryMonitor` item NL-337; W7-C's NativeAOT failure is NL-338). The Docker suite (114 tests) ran from SDK containers on net10.0 and net11.0.

Updated 2026-09-26 after gossip wave G-A (A1 wire, A2 schema and signer (migration owner, `AddGossipGraph`), A3 crypto and chain, A4 addresses, graph model and pathfinder, M1 BOLT 5 O8 mempool and the halt gate) was integrated into `wip/fafo` (at `164289a`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `b515155`, `2138eae` and `164289a` are `integrate:` commits), and NL-343..NL-347 record the lanes' and the integrator's new findings. The Docker suites ran from SDK containers on net10.0 only (SQL Server container tests skipped).

Updated 2026-09-26 after gossip wave G-B (B1 public channels and own announcements, B2 graph ingress/store/pruner and IPC, B3 Docker Proofs G0-G2, M2 BOLT 5 O6-T4 blockers; no migration-owner lane) was integrated into `wip/fafo` (at `5bbfbb5`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `00f6bcf`, `66e773c`, `717bd97` and `5bbfbb5` are `integrate:` commits), and NL-348..NL-356 record the lanes' new findings (NL-348 keeps the ID lane B1 proposed). The Docker suites ran from SDK containers on net10.0 only (SQL Server container tests skipped); net11.0 was not built (SDK 11 not installed on the integration machine).

Updated 2026-09-26 after gossip wave G-C (C1 sync and relay, C2 routing and `getroute`, C3 Docker goal proofs against LND and CLN, M3 G-B follow-ups (migration owner, `AddGraphFundingTxId`)) was integrated into `wip/fafo` (at `4dc0f77`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `b5de7be`, `0325ef3`, `b334442`, `485a9aa` and `4dc0f77` are `integrate:` commits), and NL-357..NL-369 record the lanes', the reviews' and the integrator's open items. The Docker suites ran from SDK containers on net10.0 (SQL Server container tests skipped); the build was also checked on net11.0 (SDK 11 rc.1).

Updated 2026-09-26 after gossip wave G-D (D1 limits, spam protection and metrics, D2 persistence performance and `describegraph`, D3 mainnet gossip gate, soak and container runner scripts, M4 BOLT 5 O6-T4 mainnet HTLC gate; no migration-owner lane shipped a migration) was integrated into `wip/fafo` (at `48a8951`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `d31cd2f` and `48a8951` are `integrate:` commits), and NL-370..NL-378 record the lanes' and the integrator's open items. The Docker suites ran from SDK containers on net10.0 (SQL Server container tests skipped); the build was also checked on net11.0 (SDK 11).

Updated 2026-09-26 after wave O7 (X1 wallet signing and fee-input reservations (migration owner, `AddFeeInputReservations`), X2 anchor CPFP, X3 anchors HTLC resolution and penalties, X4 anchors Docker proofs, X5 small fixes) was integrated into `wip/fafo` (at `8364a01`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `658e086`, `b76d661` and `8364a01` are `integrate:` commits), and NL-379..NL-387 record the lanes' and the integrator's open items (the integrator's NL-379..NL-383 keep their IDs; the lanes' unnumbered proposals follow; the lanes' proposals that their own review steps fixed (no Postgres round trip for the fee reservations, a reservation leaked by a failed executor save, anchors HTLC transactions never bumped) are not filed, and the change-address reuse is recorded under NL-280). `option_anchors` stays experimental (O7-T4 held on NL-379..NL-381). The Docker suites ran from SDK containers on net10.0 (SQL Server container tests skipped).

Updated 2026-09-26 after wave O7b (Y1 anchors on-chain reserve and funding selection, Y2 package relay and the peer-anchor bump, Y3 anchors gap Docker proofs; no migration) was integrated into `wip/fafo` (at `c16d6e1`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `d4cc3f8` and `c16d6e1` are `integrate:` commits), O7-T4 is done (`option_anchors` advertised Optional by default, d4cc3f8), and NL-388..NL-394 record the lanes' open items (NL-388 is lane Y2's proposal that its own review step fixed; the lanes' step-1 proposals that their review steps fixed (the reserve not counting in-flight opens or the funding fee) are not filed). The Docker suites ran from SDK containers on net10.0 (SQL Server container tests skipped).

Updated 2026-09-27 after the mainnet gossip probe (branches `wip/fafo-mainnet-gossip` and `wip/fafo-mainnet-gossip-verified`, `docs/agents/MAINNET_GOSSIP_PROBE.md`: `Gossip:AssumeChannelValid`, the harness `tools/NLightning.GossipProbe`, the whole mainnet graph synced assumed and verified against an unpruned mainnet bitcoind) was integrated into `wip/fafo` (at `bc3a2c3`, no conflicts, no `integrate:` commit needed): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`), and NL-400..NL-408 (first run) and NL-410..NL-416 (verified run) record the probe's findings; NL-409 was never assigned and is a wontfix placeholder. NL-395..NL-399 (reserved for the O7 waves) are still unassigned. Docker on net10.0 (in-container runner): gossip 24/24, CLN 22/22.

Updated 2026-09-27 after wave d12 (Z1 graph hygiene, Z2 memory budget, Z3 verified-sync efficiency, Z4 pruned funding lookup; no migration) was integrated into `wip/fafo` (at `aa1cc10`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `3736a39`, `4dc261f`, `8b97462` and `73a1acd` are `integrate:` commits), D12 is decided (graph and sync on by default on mainnet, relay of others' gossip still off there), and NL-417..NL-425 record the lanes' and the integrator's open items (NL-417 keeps the ID the integrator cited). Docker on net10.0 (in-container runner, SQL Server container tests skipped): gossip 24/24, CLN 22/22, LND 59/59, ABCD 3 x 10/10, on-chain legacy 24 (+2 Explicit), anchors 18/18.

Updated 2026-09-27 after wave rf1 (R1 static channel backup and restore, R2 peer storage (migration owner, `AddPeerStorage`), R3 key material and local attack surface security review (`docs/agents/SECURITY_REVIEW.md`), R4 operator IPC and open cleanup, M5 route blinding) was integrated into `wip/fafo` (at `be9fd000`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `aa9d67e0` and `be9fd000` are `integrate:` commits). NL-010, NL-026, NL-077, NL-079, NL-152, NL-159, NL-212, NL-339, NL-392 and NL-393 are fixed and NL-148 gained its last fix. Lane R1's commits cite NL-417, which is the D12 relay entry: static channel backup and restore is recorded as NL-426. NL-426..NL-440 record the lanes' and the integrator's new findings (the lanes' proposed IDs and the integrator's NL-430..NL-433 were renumbered from the next free ID); the security review's SR-## IDs stay local to `SECURITY_REVIEW.md`, and its open ones are NL-224 (SR-14), NL-436 (SR-17) and NL-437 (SR-09). Docker on net10.0 (in-container runner, SQL Server container tests skipped): LND 64/64, CLN 23/23, gossip 28/28, on-chain legacy 24 (+2 Explicit), anchors 18/18, ABCD 3 x 10/10.

Updated 2026-09-27 after wave M6 (onion messages: M6-A wire and codecs, M6-B crypto and vectors, M6-C transport and rate limiter, M6-D service and harness, M6-E Docker Proof M6 against CLN; plus lane W1 on-chain `withdraw`; no migration) was integrated into `wip/fafo` (at `641a5fff`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `fc686ff0`, `9639b7cf`, `5ae1701c` and `641a5fff` are `integrate:` commits). The M6 lanes cite NL-079 (the route blinding epic the BOLT 12 plan named as parent); the onion message work is recorded on NL-080, which is fixed, and `option_onion_messages` is advertised Optional by default (D9, 641a5fff). Lane W1 cites "NL-new": the withdraw command is NL-441. NL-441..NL-447 record the integrator's IDs (NL-441..NL-443 kept) and the lanes' open items, and NL-447 is the BOLT 12 offers epic the plan asked the ledger to file. Docker on net10.0 (in-container runner, SQL Server container tests skipped): CLN 33/33 (incl. `ClnOnionMessageTests` 10), LND 66/66, gossip 28/28, on-chain legacy 24 (+2 Explicit), anchors 18/18, ABCD 3 x 10/10.

Updated 2026-09-27 after wave B12 (BOLT 12 offers: B12-A codecs, string format, validators and Merkle tree, B12-B BIP-340 signer, B12-C schema (migration owner, `AddBolt12Offers`), B12-D receive, B12-E pay and blinded send; contracts B12-0 at `6f4bdaad`) was integrated into `wip/fafo` (at `a3445f3f`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `a3445f3f` is the `integrate:` commit, which also appended `ClientCommand` 26-30). NL-447 and NL-440 are partial; no new feature bit (offers ride on the advertised onion-message and route-blinding bits). NL-448..NL-456 record the integrator's and the lanes' open items (NL-448 keeps the ID the integrator cited; the lanes' "NEW" proposals were numbered from the next free ID; lane B12-E's result reached the ledger truncated, so its items are taken from its commits). Docker on net10.0 (in-container runner, SQL Server container tests skipped): CLN 39/39 (+3 Explicit; incl. `ClnOfferReceiveTests` 4 and `ClnOfferPayTests` 4), LND 62/62 (incl. `PostgresTests`), `MultiNodeHarnessTests` 5/5 facts, gossip 28/28, on-chain legacy + anchors 40/40 (+2 Explicit), ABCD 3 x 10/10.

Updated 2026-09-27 by ledger hygiene lane lh1-l5 (docs only, from `wip/fafo` at `d929b879`): every open critical/high/medium entry and the open epics were re-checked against the code, and open entries whose Location no longer resolves were corrected. Closed with evidence: NL-032 (the engine snapshot and `UpdateCommitments` replaced the missing mutators), NL-072 (attribution_data implemented end to end; only the experimental default remains, NL-332), NL-099 (BOLT 7 epic done; follow-ups carried as their own entries) and NL-137 (every named table exists; per-part MPP rows NL-321 and forward failure reasons NL-457 carried). Re-scoped: NL-178 (only `TcpService` is still untested; medium to low), NL-180 (CLN interop done; Eclair/LDK and Docker in CI remain), NL-276 (test infrastructure only; high to low), NL-012 (signet done, testnet4 remains), NL-171 (current 5 sites), and Locations of NL-113, NL-318, NL-330. New: NL-457, NL-458 (stale root `CLAUDE.md` and `OptionAttributionData` remark). Raised: NL-376 (low to medium: the unmet G5-T5 soak proof, carried from the closed NL-099). Verified still open as written: NL-037, NL-041, NL-042, NL-045, NL-259, NL-279, NL-280, NL-294, NL-309, NL-360, NL-430, NL-447, NL-448, NL-161, NL-162.

Updated 2026-09-27 after wave lh1 (l1 channel safety fixes (shipped migration `AddShutdownHtlcBoundaryAndAddressReservation`), l2 BOLT 12 and onion-message tidy, l3 keysend, l4 restore hardening (migration owner, `AddPeerStorageRetrievals`), l5 ledger hygiene) was integrated into `wip/fafo` (at `a6c633f9`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `a6c633f9` is the `integrate:` commit, which registered keysend (ClientCommand 31), `listpeerstorage` (32) and the BOLT 12 invoice pruner and synced the peer-storage migration Designers with lane l1's columns). Fixed: NL-045, NL-259, NL-279, NL-280, NL-294 (l1), NL-442, NL-444, NL-448, NL-450, NL-453, NL-454, NL-455 and the NL-449 follow-up (l2), NL-430, NL-431, NL-432 (l4); the BOLT 12 epic NL-447 is closed. Keysend (l3 cited no ID) is NL-459. NL-460..NL-466 record the lanes' and the integrator's open items (the integrator's proposed NL-459/NL-460 for two flakes and lane l3's proposed NL-457..NL-459 were renumbered from the next free ID: the flakes are NL-465 and NL-466). Docker on net10.0 (in-container runner, SQL Server container tests skipped): LND 66/66 (incl. `KeysendFlowTests` 2, `BackupRestoreFlowTests` 3), CLN 42/42 (+1 Explicit capture), `MultiNodeHarnessTests` 5/5 facts (server-database theory not run, NL-429), Docker.Utils 2/2, ABCD 3 x 10/10, on-chain legacy + anchors 42/42 (+2 Explicit), gossip 28/28.

Updated 2026-09-27 after wave qit (quiescence and interactive-tx, `SPLICING_PLAN.md` waves Q and IT run as one wave: Q-A wire and rules, Q-B service, gate and timeout, Q-C Docker Proof Q against CLN, IT-A engine, IT-B Bitcoin side, IT-C wire and schema (migration owner, `AddInteractiveTxSessions`), IT-D driver and handlers; contracts Q-0/IT-0 at `9355ad92`) was integrated into `wip/fafo` (at `b7d14056`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `ef806980`, `c502fdb6` and `b7d14056` are `integrate:` commits). Fixed: NL-219 (IT-A) and NL-041 (IT-A with IT-B's `PrevTxInspector`; the integrator reported it partial, the ledger closes it because its fix sketch is met); NL-019 gained the channel-message routing. Partial: NL-042 (quiescence done, `OptionQuiesce` stays experimental until splicing) and the epic NL-037 (interactive-tx layer done, dual funding itself remains). NL-467..NL-474 record the integrator's and the lanes' open items (the integrator's NL-467..NL-470 keep their IDs; the lanes' unnumbered flake reports and deviations follow). Docker on net10.0 (SQL Server container tests skipped): CLN 44/44 (incl. `ClnQuiescenceTests` 5, Proof Q), LND 73/74 in the full run (the `ReestablishFlowTests` miss is NL-469; the class alone 3/3), ABCD 3 x 10/10, gossip 28/28, on-chain legacy + anchors 40/40 (+2 Explicit).

Updated 2026-09-27 after wave sp2 (splicing completion, `SPLICING_PLAN.md` wave SP2: SP2-A reestablish across splices, SP2-B `splice_locked`, the retired SCID map and re-announcement, SP2-C BOLT 5 across fundings (migration owner; no migration needed), SP2-D `listchannels` fundings and Proof SP2 against CLN, SP2-E static channel backups and peer storage across splices and dual-funded opens, SP2-F the day-0 Docker proofs and `DAY0_RUNBOOK.md`; contracts SP2-0 at `81e337f8`) was integrated into `wip/fafo` (at `31950b81`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `a31c6c0d`, `990d3381`, `7abc96b2`, `db276852` and `31950b81` are `integrate:` commits). Fixed: NL-478 (SP2-B, SP2-E) and NL-479 (SP2-C); their restart-only remnants are NL-495 and NL-494. NL-021 and NL-037 stay partial (wave SPR and D13 remain; `OptionSplice`, `OptionQuiesce` and `OptionDualFund` stay experimental). NL-484..NL-501 record the integrator's and the lanes' new items: NL-484 (restart mid-splice, fixed in `990d3381`) and NL-486 keep the IDs the integrator cited; the integrator's NL-485 is the existing NL-472 and is filed as its duplicate; the lanes' unnumbered proposals follow from NL-487 (the lanes' proposals that their own review steps fixed, e.g. the stale `Pending` funding row after `tx_abort`, are not filed). Docker on net10.0 (in-container runner, SQL Server container tests skipped): CLN 65/68 (+1 Explicit; Proof SP2 `ClnSpliceReestablishTests` 11/11; `ClnQuiescenceTests.Given_OurHtlcInFlight_*` NL-477, two order-dependent fee cases NL-486), LND 69/72 in the full run (the 3 misses pass with their classes alone: NL-469 x2 and a `ChannelPolicyPublicFlowTests` fixture start), on-chain legacy 29/29 (incl. `OnchainSpliceTests` 5; +2 Explicit), anchors 18/18, `BackupRestoreFlowTests` 6/6, gossip 28/28, Day0 3/3, ABCD 3 x 10/10.

Updated 2026-09-28 by wave d13 (owner decision D13 of the splicing plan; branch `wip/fafo-d13` from `29ce3126`, code at `f30f3be3`, not merged into `wip/fafo`): `option_splice`, `option_quiesce` and `option_dual_fund` are Optional by default on every network and out of the experimental set, and the splice RBF recency rule is block-based. Fixed: NL-021, NL-042, NL-520 (new), and NL-477 (not reproduced since D13); NL-037 stays partial (DF3 done; RBF of v2 opens and NL-473, NL-474, NL-521 remain); NL-515 partial update. New open items: NL-521 (dual-funded RBF ignores a changed `tx_ack_rbf` contribution), NL-522 (order-dependent CLN full-run failures), NL-523 (LND observer flake), NL-524 (simultaneous-connect peer row race). Non-Docker on net10.0, Release: Domain 3524, Application 2943, Integration 933, Serialization 613, Infrastructure 455, Infrastructure.Bitcoin 1348, Bolt11 311, Daemon 762, all green apart from the known flake NL-466 (green alone). Docker on net10.0 (SQL Server tests skipped): CLN 64/70 in both full runs (the six failures, NL-521 and NL-522, pass with their classes alone 12/12; a full run of the pre-D13 build the same day failed 36/70 on CLN's fee floor), gossip 28/28, `Day0FlowTests` + `Day0UpgradeInPlaceTests` 3/3 (step 9 proves the block rule), `SpliceLndObserverTests` 1/3 (NL-523), LND suite 70/71 Postgres only (NL-524; the class 5/5 alone), on-chain legacy + anchors 47/47 (+2 Explicit not run), `BackupRestoreFlowTests` 6/6, ABCD 3 x 10/10.

Updated 2026-09-28 by lane rbf (branch `wip/fafo-rbf` from `wip/fafo` at `2b5dffd2`, code at `7b8dacd6` and `f1068fce`, not merged into `wip/fafo`): NL-521 fixed (either contribution may change in a dual-funded RBF), NL-522 fixed (our splice RBF fee rule was right; the CLN proofs no longer depend on the fee and graph state earlier classes leave), new NL-526 (`ClnOfferReceiveTests` exact-amount asserts vs NL-440's dummy hops) and NL-527 (a peer's `tx_abort` of our pending `tx_init_rbf` never reaches the dual-funding host). Non-Docker on net10.0, Release: Domain 3524, Application 2969, Integration 933, Serialization 613, Infrastructure 455, Infrastructure.Bitcoin 1348, Bolt11 327, Daemon 762, green apart from the known flakes NL-466 and NL-472 (green alone). Docker (CLN, host process): `ClnSpliceRbfTests` 5/5, `ClnDualFundTests` 3/3, `ClnOfferPayTests` 4/4 alone; one full CLN run 67/70 (+4 Explicit not run): the six d13 failures pass, the three misses are `ClnOfferReceiveTests` (NL-526, also 1/4 with the class alone on this base).

Updated 2026-09-28 by lane bolt10 (branch `wip/fafo-bolt10` from `978ad275`, code at `f09ff53d`, not merged into `wip/fafo`): BOLT 10 DNS seed bootstrap implemented and off by default. Fixed: NL-113. New: NL-541..NL-545 (NL-536..NL-540 left to the concurrent cli-lognoise lane). Targeted tests only (owner request), net10.0 Release: Domain `Node/Bootstrap` 91, Infrastructure 479, Infrastructure.Bitcoin `Bootstrap` 34, Application `PeerBootstrapServiceTests` 24, Daemon `NodeServiceExtensionsTests` 68; no Docker, no full matrix. Review fixes at `644bc5a8` (NL-113 entry): address filter ranges, SRV (target, port), the `n` condition, BOLT 10 example vectors, cancellable dials, gate retries, obsolete `Node:DnsSeedServers` ignored; no new IDs. Targeted, net10.0 Release: Domain 3660, Infrastructure.Bitcoin 1394, Application `Node` namespace 208 (3 runs), Daemon 799.

## How to use this file

- **Fixing something:** in the **same commit** as the fix, set `Status: fixed (<short SHA>)` (or `fixed (partial, <SHA>)` and say what remains in Evidence). Do not delete the entry.
- **Finding something new:** append a new entry in the right area with the next free ID (`NL-###`, one higher than the current maximum anywhere in the file). Never renumber. Never reuse an ID.
- **Never delete an entry.** Mark it `wontfix` (say why) or `duplicate of NL-###`.
- Keep entries tight: one line of evidence per claim, file:line where possible.
- Update the summary table below when you add an entry or change a status or severity.
- Plan milestones (e.g. `ONION M3`) refer to `docs/agents/ONION_ROUTING_PLAN.md`; `BOLT2 N#-T#` refers to `docs/agents/BOLT2_NORMAL_OPERATION_PLAN.md`. `BOLT_COVERAGE.md` remains the per-BOLT status matrix; this file is the status source for individual bugs.

### Status legend

| Status | Meaning |
|---|---|
| open | Known, not being worked on. |
| in-progress | Someone is actively on it (name the branch in Evidence). |
| fixed | Done; the commit SHA is recorded next to the status. |
| wontfix | Deliberately not fixing; reason recorded. |
| duplicate | Covered by another NL ID (named next to the status). |

### Severity legend

| Severity | Meaning |
|---|---|
| critical | Funds loss, security, or consensus failure. |
| high | Breaks interop with peers or kills connections/channels. |
| medium | Spec deviation or incorrect behaviour without immediate funds/interop impact. |
| low | Hygiene, tech debt, docs, tooling. |

Kinds: `bug`, `gap` (missing feature; `[EPIC]` in the title marks a large one), `spec-violation`, `test`, `tech-debt`.

## Summary

| Status | critical | high | medium | low | Total |
|---|---|---|---|---|---|
| open | 0 | 0 | 7 | 169 | 176 |
| in-progress | 0 | 0 | 0 | 0 | 0 |
| fixed | 14 | 61 | 147 | 130 | 352 |
| wontfix | 0 | 0 | 2 | 5 | 7 |
| duplicate | 0 | 0 | 1 | 1 | 2 |
| **Total** | **14** | **61** | **157** | **305** | **537** |

### Epics

- NL-426: Static channel backup and restore (fixed, high; wave rf1: encrypted SCB, export/verify/restore IPC 21-23, recovery channels and the data-loss reestablish, proven against LND; wave lh1: NL-430, NL-431 old spends and every peer address, NL-432 persisted peer-storage retrievals with `listpeerstorage` (32); follow-up NL-435)
- NL-031: HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee) (fixed, critical; N6 in wave 1, reestablish and switch in wave 2; the fail-the-channel broadcast is N9-T4 under NL-094)
- NL-034: Channel close (shutdown / closing_signed / option_simple_close) (fixed, critical; legacy close in wave 3 W3-B, Docker proof against LND and CLN close in wave 4, option_simple_close NL-020 in wave 6 (default No); NL-279 and NL-045 fixed in wave lh1; remaining: NL-285, NL-286)
- NL-035: channel_reestablish / option_data_loss_protect (fixed, critical; ABCD wave 2 W2-A; shutdown re-send is N10, Closing resumption NL-036)
- NL-037: Dual funding / interactive-tx (v2 open) (open (partial), medium; wave d13: DF3 done, `option_dual_fund` Optional by default (D13); wave qit: the interactive-tx layer (engine, Bitcoin side, wire and `AddInteractiveTxSessions`, driver and handlers; NL-041, NL-219 fixed) is done and registered; wave sp1 lane SP1-F: the v2 open (DF1, DF2) behind experimental `OptionDualFund` with Proof DF green against CLN v26.06.8, which also proves the interactive-tx layer on the wire; tx_init_rbf bugs NL-475, NL-476 fixed; wave sp2: the v2 open's `next_funding` goes through the shared reestablish planner, backups hold every signed dual-funded RBF candidate, day-0 Docker proof of a dual-funded public open between two nodes; remaining: DF3 (out of experimental, not scheduled), follow-ups NL-473, NL-474; lane rbf: NL-521 fixed, a changed contribution in either RBF message followed, follow-up NL-527; lane dfrbf: NL-527 and NL-528 fixed, the channel follows whichever signed attempt of an RBF confirms, `Node:DualFund:AllowRbf` true by default, `bumpopen` (IPC 38); follow-up NL-529; lane accrbf: NL-530 fixed, the accepter may bump too (CLN v26.06.8 refuses it with `tx_abort`))
- NL-021: Splicing (fixed, low; wave d13 at `f30f3be3`: D13 applied, `option_splice`/`option_quiesce` Optional by default, RBF recency by blocks NL-520; wave sp1 at `3660bff2`: wire, several-funding engine, per-funding signer, `AddSpliceFundings`, negotiation, `splicein`/`spliceout` (33/34), Proof SP1 green against CLN v26.06.8; wave sp2 at `31950b81`: reestablish across splices, full `splice_locked`, the 72-block retired SCID map, re-announcement, BOLT 5 across fundings, `listchannels` fundings, backups across splices, restart mid-splice (NL-484), Proof SP2 green against CLN v26.06.8 (11/11) and `OnchainSpliceTests` 5/5; NL-478, NL-479 fixed; `OptionSplice` experimental; wave spr at `a0800ac2`: splice RBF both ways (NL-489, NL-481 fixed), `bumpsplice` (37) and the optional auto-bump, Proof SPR 5/5 against CLN v26.06.8, day-0 hardening NL-490, NL-492, NL-494, NL-495, NL-497 fixed; wave d13 at `f30f3be3` (branch `wip/fafo-d13`): D13 applied, `OptionSplice`/`OptionQuiesce` Optional by default on every network, splice RBF recency by blocks (NL-520); follow-ups NL-477, NL-480, NL-483, NL-488, NL-493, NL-496, NL-502, NL-507..NL-511, NL-514, NL-515)
- NL-070: Error onions: failure messages, create / wrap / decrypt (ONION M3) (fixed, high; attribution_data NL-072 fixed: library done in wave 6, persisted and wired into the switch and send paths in wave 7 (NL-326); `OptionAttributionData` stays experimental (NL-332))
- NL-073: Onion integration with HTLC flow: peel after lock-in, forward, final hop, send (ONION M4) (fixed, high; `HtlcSwitch` W2-B and `PaymentService` W2-C; wave 6: persistent replay set NL-078, basic_mpp receive NL-081, retries and MPP send NL-270; wave 7: HTLC-set commitment NL-323, attribution_data NL-326, block-driven replay pruning NL-327)
- NL-079: Route blinding payload handling (ONION M5) (fixed, medium; wave rf1: send, receive and forward, advertised Optional, proven against LND 0.20; limits NL-440 fixed (MPP over blinded paths and own-introduction paths in wave B12, BOLT 11 blinded paths (bLIP 39) and dummy hops in wave nl440; message-path dummy hops NL-525); onion messages (M6) are NL-080, fixed in wave M6)
- NL-447: BOLT 12 offers (fixed, medium; wave B12 at `a3445f3f`: codecs, signer, schema, `createoffer`/`listoffers`/`disableoffer`/`payoffer`/`fetchinvoice` (IPC 26-30), both directions proven against CLN v26.06.8; closed in wave lh1 with the CLN-captured vectors NL-450, the invoice prune NL-448 and NL-442, NL-444, NL-453..NL-455; carried: NL-451, NL-452)
- NL-094: On-chain handling: unilateral close sweeps, HTLC resolution, penalty/justice (fixed, critical; fail-the-channel broadcast in wave 3; O0/O1 plumbing and the O2-O6 building blocks in wave 4; O2-O5 wired and proven against LND in wave 5; wave 6: O6-T1 `SweepScheduler` (NL-317, NL-296) and O6-T3 reorg re-resolution (NL-292, NL-293, NL-096) with Docker Proof O6; wave 7: final-hop HTLCs claimed on chain (NL-316, NL-322, Docker `OnchainFinalHopTests`); gossip wave G-A: O8 mempool (NL-098) and the halt gate (NL-216); gossip wave G-B: the O6-T4 blockers NL-311, NL-320, NL-337 fixed and Proofs O3-O6 green; gossip wave G-D: O6-T4 done, HTLCs on for every network by default (6de56ad, kept by the integrator in 48a8951), NL-315 fixed; wave O7: O7-T1..T3 done (NL-067, NL-314 fixed), anchors Docker proofs 12/12; wave O7b: NL-379, NL-380, NL-381, NL-385 fixed and O7-T4 done (`option_anchors` Optional by default, d4cc3f8), anchors Docker proofs 18/18; carried: follow-ups NL-307..NL-309, NL-312, NL-313, NL-318, NL-329, NL-330, NL-335, NL-336, NL-384 (partial), NL-386, NL-387, NL-389..NL-393)
- NL-099: BOLT 7 gossip: announcements, channel_update, queries, graph (fixed, high; closed in ledger hygiene lh1: G0-G4 done, G5-T1..T4 done, D12 decided at wave d12 (graph and sync on by default everywhere, relay of others' gossip off on mainnet), Docker gossip 28/28 and CLN gossip green; carried as their own entries: the unmet 24 h soak proof of G5-T5 NL-376 (medium; D12 opened mainnet sync without it), the outbox cap NL-360 (medium), mainnet relay NL-417, and the low follow-ups NL-345..NL-347, NL-357, NL-361..NL-372 (NL-369 is the value-object conversion trap the gossip code hit), NL-374, NL-375, NL-377, NL-378, NL-382, NL-394, NL-407, NL-416, NL-418..NL-425)
- NL-459: Keysend and custom onion records (fixed, medium; wave lh1 lane l3: send and receive, IPC `keysend` (31), proven against LND 0.20 both ways; follow-up NL-460)
- NL-114: Invoices not wired into the node: invoice store, create/pay commands, final-hop checks (fixed, high; receive, route hints and pay done in wave 2)
- NL-137: Payment/forwarding persistence: shared secrets, circuits, invoices, attempts, replay set, SCID map (fixed, high; closed in ledger hygiene lh1: shared secrets, circuits with replay, invoices, payments, HTLC origins, the persistent replay set (NL-078), the SCID/alias map (`Channels.ShortChannelId`, `ChannelLocalAliases`, `Channels.RemoteAlias`) and the graph tables exist; carried: per-part MPP send rows NL-321 and forward failure reasons NL-457)

---

## BOLT 1: Base protocol

### NL-001 Message TLV extensions accept unknown even TLV types
- **Status:** fixed (cd49ad9, 90a1905, 1d9bec5, 9e65cf8)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/*` (every serializer except `UpdateAddHtlcMessageSerializer.cs:74`)
- **Evidence:** Extensions are read with the open `TlvStreamSerializer.DeserializeAsync`, which cannot reject unknown even types. Only update_add_htlc uses `DeserializeStrictAsync`. Every message extension now uses `DeserializeStrictAsync` with its known-type set; a strict-TLV rejection answers with a connection `warning` (see NL-207).
- **Fix sketch:** Give each message serializer its known-type set and call `DeserializeStrictAsync`; add the BOLT 1 Appendix C init case (0xca) as a test.
- **Blocks/Blocked-by:** —
- **Plan ref:** ONION_ROUTING_PLAN §5 "M1/M2 as built"; BOLT_COVERAGE roadmap step 4; BOLT2 N0-T6 (touched messages)

### NL-002 init rejects a peer if any of its chains is unknown
- **Status:** fixed (e3d2ac3, 71d0944)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs:207-210`
- **Evidence:** `networkChainHashes.Any(h => !Features.ChainHashes.Contains(h))` disconnects; BOLT 1 only requires disconnecting when no chain is shared. Disconnects only when no chain is shared.
- **Fix sketch:** Disconnect only if the intersection is empty.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-003 init failures disconnect without sending error/warning
- **Status:** fixed (e3d2ac3, a6f1f9a)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (init validation)
- **Evidence:** Feature/network mismatches close the socket without an `error`/`warning`, so the peer gets no reason. Feature/chain failures send a `warning` after the peer's init; a first message that is not init disconnects silently (BOLT 1: send nothing before init).
- **Fix sketch:** Send `warning` (or `error` with all-zero channel id) before disconnecting.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-004 Pong is sent even when num_pong_bytes >= 65532
- **Status:** fixed (ad90605)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs:145-153`, `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs:221-234`
- **Evidence:** Every ping is answered with `new PongMessage(ping.Payload.NumPongBytes)`; BOLT 1 says do not respond when `num_pong_bytes >= 65532`.
- **Fix sketch:** Skip the pong for that range; add a test.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-005 No ping rate limiting
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs:221-224`
- **Evidence:** Every incoming ping triggers a pong with no rate check; BOLT 1 allows failing peers that ping too often.
- **Fix sketch:** Track the last ping time per peer and ignore or disconnect on floods.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-006 Pong-timeout disconnect path is dead code
- **Status:** fixed (1433ea7, 3226906)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/PingPongService.cs:60-67`
- **Evidence:** On timeout `Task.Delay` ends Canceled, the loop takes `IsCanceled -> continue` and re-pings; `DisconnectEvent` never fires for an unresponsive peer. Pinging starts only after both inits; disconnect is idempotent.
- **Fix sketch:** Distinguish timeout from shutdown cancellation (separate CTS) and raise `DisconnectEvent` on timeout; unit test it.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-007 Ping/Pong payload serializers don't consume the ignored bytes
- **Status:** fixed (8f048be)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/Payloads/PingPayloadSerializer.cs`, `PongPayloadSerializer.cs`
- **Evidence:** The `ignored` byte count is checked but the bytes are left in the stream.
- **Fix sketch:** Read (skip) exactly `byteslen` bytes.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-008 remote_addr / address descriptor conversion broken for Tor v3 and DNS
- **Status:** fixed (70744d6, caf8ee7)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs:51-54,95-103`, `src/NLightning.Domain/Protocol/Tlv/RemoteAddressTlv.cs:32`
- **Evidence:** Tor v3 decode reads `Value[1..37]` (36 bytes, spec 35). DNS (type 5): Domain length `3 + len` (spec `4 + len`) and encode overwrites `customAddressBytes[1]`. Only IPv4 is tested. Update (gossip wave G-A, `164289a`): fixed. `Domain/Gossip/Addresses/{AddressDescriptor,AddressDescriptorCodec}` decode types 1-5 with exact lengths (Tor v3 35 + 2, DNS 1 + len + 2), `EncodeList` enforces the sender rules and `DecodeList` the receiver rules (stop at the first unknown type, drop port 0 and Tor v2, keep the first DNS); DNS hostnames are LDH plus `.` and `_` (caf8ee7). `RemoteAddressTlvConverter` delegates to the codec and `RemoteAddressTlv.Value` now holds the wire bytes; Tor addresses read `<base32>.onion`. All five types tested (70744d6). The remote_addr handling in init is NL-344.
- **Fix sketch:** Fix offsets/lengths per BOLT 7 address descriptors; add tests for all 5 types.
- **Blocks/Blocked-by:** Blocks NL-099 (node_announcement addresses)
- **Plan ref:** —

### NL-009 Our init never sends remote_addr
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:241`
- **Evidence:** Commented out with `TODO: Review this when implementing BOLT7`. Update (gossip wave G-B, `5bbfbb5`): the peer's `remote_addr` is now kept as `IPeerService.ObservedAddress` (NL-344 fixed, 2635956), so the observed address is available; our init still sends no `remote_addr`, and `Gossip:AnnounceAddresses` is configured by hand.
- **Fix sketch:** Send the peer's observed address once NL-008 is fixed. Update (gossip wave G-A): NL-008 is fixed; the address we see for a peer is not kept separately yet (NL-344).
- **Blocks/Blocked-by:** Blocked-by NL-008
- **Plan ref:** —

### NL-344 The peer's init `remote_addr` is stored as that peer's address, and an undecodable one fails init
- **Status:** fixed (2635956, 00f6bcf)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (remote_addr handling, about lines 489-515), `src/NLightning.Application/Node/Managers/PeerManager.cs`, `src/NLightning.Infrastructure.Serialization/Messages/Types/InitMessageTypeSerializer.cs`
- **Evidence:** BOLT 1: `remote_addr` is the address the **sender** sees for **us**. `PeerService` stores it as the peer's `PreferredHost`/`PreferredPort`, and `PeerManager` then uses it as that peer's address. Since the strict codec (NL-008, 70744d6) a `remote_addr` the converter rejects (odd, advisory TLV) throws `InvalidCastException` and fails the whole init (malformed-message warning and close); whether `InitMessageTypeSerializer` wraps it for the NL-207 path is unverified. Found by lane A4's review (gossip wave G-A). Update (gossip wave G-B, `5bbfbb5`): `InitMessageTypeSerializer` catches the converter failure and fills `InitMessage.UndecodableRemoteAddress` instead of failing init; `PeerService` logs and drops it, and keeps a valid `remote_addr` only as `IPeerService.ObservedAddress` (2635956; tests in Serialization `InitMessageTests` and Infrastructure `PeerServiceGossipTests`). The integrator removed the never-set `PreferredHost`/`PreferredPort` and their dead branches in `PeerManager`, which keeps the address it connected to (00f6bcf).
- **Fix sketch:** Keep it only as an "our observed address" hint (NL-009, node_announcement addresses); log and drop an undecodable one instead of failing init.
- **Blocks/Blocked-by:** Related NL-008, NL-009
- **Plan ref:** BOLT7 G1-T6 (announced addresses)

### NL-010 peer_storage / peer_storage_retrieval messages missing
- **Status:** fixed (136a4cf5, 2769b615, 34d48a9d, b5dee649, 0b13c1b8, ae5d3389, aa9d67e0, be9fd000)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`
- **Evidence:** No message types, yet `option_provide_storage` is advertised Optional (see NL-109). Update: `option_provide_storage` now defaults to No and is in `FeatureOptions.ExperimentalFeatures` (not advertised without `AllowExperimentalFeatures`, e93eb41). Update (wave rf1, `wip/fafo` at `be9fd000`): BOLT 1 peer storage both ways (lane R2): `peer_storage` (7) / `peer_storage_retrieval` (9) with strict trailing TLVs, `PeerStorageBlobs` table (migration `AddPeerStorage`, all three providers), `Application/Node/PeerStorage/PeerStorageService` keeps the latest blob per peer with a channel (rate-limited writes, retrieval after every init) and sends our encrypted, 65531-byte padded channel list to storing peers; a retrieval naming unknown channels holds our backups (never overwrites the evidence); `StopAsync` flushes delayed writes. The integrator wired `AddPeerStorageServices` and flipped `option_provide_storage` to Optional (aa9d67e0) and kept a blob that arrives before the channel exists (be9fd000, NL-428). Docker `Interop/Cln/ClnPeerStorageTests` green against CLN v26.06.8. Follow-ups NL-432, NL-433.
- **Fix sketch:** Stop advertising, or implement types 7/9 with storage limits.
- **Blocks/Blocked-by:** Related NL-109
- **Plan ref:** —

### NL-011 DeserializeMessageAsync&lt;T&gt; ignores the wire type
- **Status:** fixed (0d80672)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs`
- **Evidence:** The generic overload reads the u16 type and then uses T's serializer regardless.
- **Fix sketch:** Assert the wire type matches T.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-012 No signet / testnet4 chain hashes
- **Status:** open (partial: e7d90370)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/ChainConstants.cs:20-40`
- **Evidence:** Only Main, Testnet, Regtest exist. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): signet (and custom signets such as Mutinynet) is added (`ChainConstants.cs:53`, e7d90370, NL-291); testnet4 is still missing.
- **Fix sketch:** Add Signet and Testnet4 genesis hashes and network mappings.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-013 TlvStream is a SortedDictionary that silently re-sorts records
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Models/TLVStream.cs:11`
- **Evidence:** Wire order is lost on construction; deserialization now enforces order (NL-017), so the remaining risk is only in hand-built streams.
- **Fix sketch:** Keep as is, or preserve insertion order and validate on write.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-014 BigSize decode throws ArgumentException and needs a seekable stream
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs`
- **Evidence:** Non-canonical input throws `ArgumentException` (kept for compat); short reads are detected via `stream.Position/Length`.
- **Fix sketch:** Map to a serialization exception; use `ReadExactlyAsync`.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T5 open item

### NL-015 EndianBitConverter trim/pad semantics are non-compliant
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Converters/EndianBitConverter.cs`
- **Evidence:** LE trim/pad helpers don't implement BOLT truncated ints; onion code already uses `TruncatedInt`.
- **Fix sketch:** Migrate callers to `BinaryPrimitives` / `TruncatedInt`, then delete the helpers.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-016 BigSize decoding accepted non-canonical encodings
- **Status:** fixed (cd2d906)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs`
- **Evidence:** Non-minimal encodings were accepted and 3 spec vectors in `Vectors/BigSize.txt` were commented out. Now rejected; all 18 vectors active.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T5

### NL-017 TlvStreamSerializer: closed type switch (RemoteAddressTlv missing), no ordering checks
- **Status:** fixed (dbd7438, 9b9fdbd)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs`
- **Evidence:** Serialize threw for unlisted TLV types; deserialize accepted out-of-order/duplicate types and read to end of stream. Now converter lookup by runtime type, raw `BaseTlv` passthrough, strictly-increasing check, `DeserializeStrictAsync`; BOLT 1 Appendix B vectors active (skip removed in 9b9fdbd).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T7

### NL-018 No strict tu16/tu32/tu64 codec
- **Status:** fixed (b7d0139, 5dd8370)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Converters/TruncatedInt.cs`
- **Evidence:** No minimal-encoding truncated ints existed; onion converters later switched from a private `OnionTruncatedInt` to the shared helper (5dd8370).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T6

### NL-203 Channel failures without a channel id went out as an all-zero `error`
- **Status:** fixed (699c67b, 00095cb, 4961ba5)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`HandleChannelMessageResponseAsync`), `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`default` branch), `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs` (`SendExceptionMessage`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 1. After NL-027, a missing channel_type threw `ChannelErrorException` with no id, sent as `ErrorPayload(null)`; the `default` branch did the same for every LND `channel_reestablish`, so an LND peer would fail all its channels with us on reconnect (BOLT 1). Now: ids attached per channel, unimplemented messages get a channel-scoped `warning`, unknown channels an `error` for that id; `Disconnect` disposes `MessageService` off the read loop (was a 5 s stall).
- **Fix sketch:** Done; the failed-channel state itself is NL-200.
- **Blocks/Blocked-by:** Related NL-200, NL-027, NL-023
- **Plan ref:** BOLT2 G1, G21

### NL-207 Malformed messages got a `warning` but the connection stayed open
- **Status:** fixed (963c04b, 00095cb)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs` (`ReceiveMessage`), `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs` (`RaiseException`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 5. The exception was wrapped in `ConnectionException`, and `RaiseException` only disconnected on `ErrorException`, so unknown-even TLVs, wire-type mismatches and malformed-without-BADONION only warned and dropped the message. Now warn and close; malformed-without-BADONION uses a channel-scoped warning + close.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-001, NL-011, NL-023, NL-024
- **Plan ref:** —

---

### NL-428 A peer_storage blob sent right after init, before the channel exists, is dropped
- **Status:** fixed (be9fd000)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/PeerStorage/PeerStorageService.cs`
- **Evidence:** CLN sends its `peer_storage` right after init, before our `open_channel`, so the blob was refused (no channel yet) and `ClnPeerStorageTests` failed in the full CLN suite but passed alone (found by the rf1 integrator). Fixed: such a blob is held in memory (at most 64 peers, 30 min) and kept at the next round once a channel with the peer exists; regression tests added.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-010
- **Plan ref:** —

### NL-432 Peer-storage data-loss retrievals live in memory only and have no IPC
- **Status:** fixed (bd054cae, a6c633f9)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Node/PeerStorage/PeerStorageService.cs` (`GetRetrievals`)
- **Evidence:** A retrieval that names channels we do not know is only logged and exposed through `IPeerStorageService.GetRetrievals()`/`GetStoredBlobAsync`; it is not persisted, there is no `ClientCommand` for it, and the hold on sending our backups lasts until the process restarts with no operator command to lift it (reported by lane R2). Update (wave lh1, `a6c633f9`): fixed. Every recorded `peer_storage_retrieval` is written to `PeerStorageRetrievals` (migration `AddPeerStorageRetrievals`, one row per peer: blob, arrival, whether it matched our last blob, channels unknown at receipt) before any backup goes back to that peer; a failed write is retried every round. `IPeerStorageService.ListRetrievalsAsync` re-checks every named channel against the database, and the daemon serves them as `listpeerstorage` (ClientCommand 32, `AddPeerStorageIpcServices`, registered in a6c633f9); the CLI prints the channels still to restore (bd054cae, lane l4). There is still no command to lift the send hold; restoring the named channels (`restorechanbackup`) is the operator's path.
- **Fix sketch:** Persist the retrieval, add an IPC command (next free ClientCommand) for the restore flow and one to lift the hold.
- **Blocks/Blocked-by:** Related NL-010, NL-426
- **Plan ref:** —

### NL-433 Our peer-storage blob is sent only when option_provide_storage is negotiated by both sides
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Node/PeerStorage/PeerStorageService.cs` (client side)
- **Evidence:** A node configured with `OptionProvideStorage=No` sends no backups even to peers that store them; BOLT 1 lets a node send `peer_storage` to any peer that offers the feature (reported by lane R2).
- **Fix sketch:** Check only the peer's bit for the client side.
- **Blocks/Blocked-by:** Related NL-010
- **Plan ref:** —

## BOLT 2: Wire layer

### NL-019 stfu is not a channel message and is silently dropped
- **Status:** fixed (76f8f8c, a6f1f9a, e93eb41, 3380b680, 3f230897)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Protocol/Messages/StfuMessage.cs:15`, `src/NLightning.Infrastructure/Node/Services/PeerService.cs:100-160`, `src/NLightning.Application/Channels/Handlers/StfuMessageHandler.cs`
- **Evidence:** `StfuMessage : BaseMessage`, and `PeerService.HandleMessage` only dispatches `IChannelMessage`/error/warning. `option_quiesce` is advertised Optional, so a peer that starts quiescence waits forever. stfu now gets a channel-scoped `warning` and the connection is closed (quiescence only ends on disconnect), and `option_quiesce` defaults to No and is experimental-gated. Real quiescence is NL-042. Wave qit (Q1-T1): `stfu` is a channel message routed by `ChannelManager` (`case MessageTypes.Stfu` after the unknown-channel and B2-RE-07 reestablish gates) to the single `StfuMessageHandler` under the channel lock, which delegates to `IQuiescenceService` (3380b680; reestablish gate and single-handler registration tests 3f230897); the `PeerService` warning arm is gone.
- **Fix sketch:** Make stfu channel-scoped (or add a dispatch branch), and stop advertising `option_quiesce` until NL-042 is done.
- **Blocks/Blocked-by:** Blocks NL-042
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T4 (stop advertising quiesce)

### NL-020 closing_complete / closing_sig (option_simple_close) missing
- **Status:** fixed (aa569c8, b67e065, a2331b8, 5b9ce68, d8dd76c, 7ebc93c)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`
- **Evidence:** Types 40/41 absent; `Feature.OptionSimpleClose` exists in the enum only. Update (ABCD wave 6, `3ce3cad`): `closing_complete` (40) / `closing_sig` (41) with strict `closing_tlvs` serializers (aa569c8); `Channels/Close/Simple/` closer-pays builder, closer/closee rules B2-SC-C01..C09, E01..E10, G01..G06, RBF through `closechannel` (b67e065); routed in `ChannelManager`, a simple-close spend accepted as a mutual close (a2331b8); Docker proof against LND 0.20 `--protocol.rbf-coop-close` (we close, LND closes, both RBF) and the feature taken out of `ExperimentalFeatures` (5b9ce68); review: every simple-close tx we signed is recognised after the peer changed its script, and an unsendable fee bump throws (7ebc93c). `OptionSimpleClose` still defaults to No (it needs `BeyondSegwitShutdown`), so the legacy close stays the default path.
- **Fix sketch:** Add messages and serializers per the recipe in root CLAUDE.md.
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N11-T1

### NL-021 Splicing and start_batch messages missing
- **Status:** fixed (f30f3be3; earlier: 52338a14, be14108b, 01b70bf1, e33ae3d2, 48b0339f, e9f17f6b, 15099a4c, acbab68b, c2ff9279, d37427ae, b50bdfbc, 1cd7515d, 29b4ae25, 7410e221, 4577c997, c7badd17, a00b8cbe, 957c1519, 11eeced9, df9a869a, ccb98862, 9cbd7bac, 03691f28, b22e204e, d7c77659, d778d100, 2ba4fe09, 81e337f8, 7bfeb108, 768f8642, 82b98af3, 2b6910ac, b9bd0183, c8d4257d, ba5bb413, d5e13781, 059383f4, a887c488, ff87bd93, 64374dd0, 505a3b90, 252063df, ba8ecfd6, 5f078964, 71df7272, d786e4f8, 1339ed01, c8d6ad6b, 3d0240ee, af6b1bcd, a31c6c0d, 990d3381, 7abc96b2, db276852, 2a386cb8, e86bb0c4, a3748f4d, 84f5696c, 1304b5f6, 900810a8, a9721c49, e0b8a7a9, 7a044d49, 9d13d0de, f3635ed7, c1e20033, c51fe99a, f9c6b7f0, de620e7b, 8566caf2, d34ab24b, a1e2b6c5, 9d017a7f, c5943068, e25249ea, b7b23099, 8a3ad0a3, 96d32442, a0800ac2)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`
- **Evidence:** No splice_init/ack/locked or start_batch types.
- **Update (wave sp1, `SPLICING_PLAN.md` "Wave SP1 record", integrated at `3660bff2`):** the splicing core is in, behind experimental `OptionSplice`/`OptionQuiesce` (default No). Wire 77/80/81/127, `channel_reestablish` TLV 5 and inbound `start_batch` grouping gated on negotiated `option_splice` (SP1-A: e9f17f6b, 15099a4c, acbab68b); several fundings in the commitment engine, batched `commitment_signed` with one `revoke_and_ack`, the splice commitment step, lock/discard, validation on every funding and the splicing simulator (500 seeds in CI, 10k `Category=Long`) (SP1-B: c2ff9279, d37427ae, b50bdfbc, 1cd7515d, 29b4ae25); per-funding signer keys `m/0'/i'`, the SP-I1 shared-input guard, per-funding S1, migration `AddSpliceFundings` on all three providers and the funding/policy repositories (SP1-C: be14108b, 01b70bf1, e33ae3d2, 48b0339f); `SpliceRules`, `SpliceService` with the `splice_init`/`splice_ack`/minimal `splice_locked` handlers, `SpliceDepthWatcher`, the splice harness on the real engine (SP-T-01/02) and the funding-spend routing of our own splice tx (SP1-D: 7410e221, 4577c997, c7badd17, a00b8cbe, 957c1519, 11eeced9, df9a869a, ccb98862, 9cbd7bac); `splicein` (33) / `spliceout` (34) and **Proof SP1 green against CLN v26.06.8** (`ClnSpliceTests` 5/5: CLN and we splice in and out, payments with batches while pending, the 2-of-2 spend and our fee; SP1-E: 03691f28, b22e204e, d7c77659); integration: registrations and startup catch-up (d778d100), the locked splice's SCID from the confirmation and anchors on the current funding keys, which CLN rejected as "Bad commit_sig" after a splice (2ba4fe09). Plan gaps: SG3, SG5, SG8 done; SG4 done in the engine, signer and persistence (gossip is SP2-B); SG6 partial (our own splice tx is left to the splice, 11eeced9; `FundingSpendClassifier` is SP2-C); SG9 partial (`listchannels` fundings is SP2-D); SG7 and SG10 open (SP2-A, SP2-B). Remaining: wave SP2 (reestablish SP-RE, lock/announcement/SCID map, BOLT 5 across fundings, Proof SP2), wave SPR, then D13; follow-ups NL-477..NL-481, NL-483.
- **Update (wave sp2, `SPLICING_PLAN.md` "Wave SP2 record", integrated at `31950b81`):** splicing is complete except RBF, still behind experimental `OptionSplice`/`OptionQuiesce`. SP2-A (059383f4, a887c488, ff87bd93): `ReestablishPlanner` fills and answers `next_funding` and `my_current_funding_locked` (SP-RE-01..06), the splice `commitment_signed` and `tx_signatures` are retransmitted byte-identical (from the stored rows after a restart), `my_current_funding_locked` is processed as `splice_locked`, an unknown `next_funding` gets the driver's `tx_abort`; the dual-funded open's `next_funding` uses the same path; `SpliceConformanceTests` SP-T-03..11 on the real engine. SP2-B (64374dd0, 505a3b90): the full `splice_locked` rules (SP-LK-01..04 send side, D11), `RetiredScidMap` (the old SCID forwards for 72 blocks, D12; never for an alias-only channel), `ChannelUpdateService` follows the new SCID, a spliced public channel is re-announced at 6 confirmations with the current funding keys (NL-478), the announcement halves reset in the lock's own save. SP2-C (7bfeb108, 768f8642, 82b98af3, 2b6910ac, c8d4257d, ba5bb413, d5e13781): `FundingSpendClassifier.ClassifyAny` over every funding (a splice is never a close), force close with a pending splice on either funding (SP-I4, `ISpliceCommitmentBroadcaster`), resolvers and the revocation log per funding (NL-479), a close reorged out while its discarded splice confirms retired in one save, the locked-splice reorg alert (partial, NL-493); Docker `OnchainSpliceTests` 5/5 (b9bd0183). SP2-D (71df7272, d786e4f8, 1339ed01): `listchannels` lists the fundings (IPC key 23) and retired SCIDs (24); **Proof SP2 green against CLN v26.06.8** (`ClnSpliceReestablishTests` 11/11: four cut points x {reconnect, restart}, CLN restarted mid-splice, the lock over a disconnect, re-announcement and forwards over the new and the retired SCID, `WIRE_UNKNOWN_NEXT_PEER` after 72 blocks; LND learning the spliced channel not covered, NL-496). SP2-E (252063df, ba8ecfd6, 5f078964): static channel backups and peer storage follow splices and dual-funded opens (NL-478). SP2-F (c8d6ad6b, 3d0240ee, af6b1bcd): the day-0 Docker proofs between two NLightning nodes (`Day0FlowTests`, `Day0UpgradeInPlaceTests`) and `DAY0_RUNBOOK.md`. Integration: the retired map loaded before the peers start and pruned at the tip (a31c6c0d, NL-487), pending splices restored into the engine on reload and a splice negotiation resumed from its rows on `channel_reestablish` (990d3381, NL-484), Proof SP2 (c) gossip flush and CLTV margin (7abc96b2), day-0 step 5 (a) expectation (db276852). Remaining: wave SPR (NL-481, NL-489), D13 (`OptionSplice` + `OptionQuiesce` Optional by default; Proof SP2 is green, so only the decision is left), follow-ups NL-477, NL-480, NL-483, NL-488, NL-490, NL-492..NL-496.
- **Update (wave spr, integrated at `a0800ac2`):** wave SPR is done (`SPLICING_PLAN.md` "Wave SPR record"): splice RBF in both directions under the BOLT 2 splice rules, RBF siblings in `FundingSet` with batches over every pending attempt, `bumpsplice` (IPC 37) and the optional auto-bump, Proof SPR 5/5 against CLN v26.06.8 and `Day0FlowTests` step 9 (NL-489, NL-481 fixed). Day-0 hardening: `SignedOnFundings` persisted and the current funding in the signing info (NL-494, NL-495), loopback inbound peers kept across restarts (NL-497), discarded splices' wallet inputs released at irrevocable depth (NL-492), a late half for a retired SCID ignored (NL-490), LND 0.20 observes a spliced channel (NL-496 partial). `OptionSplice`/`OptionQuiesce` stay No and experimental: D13 is the remaining decision. Follow-ups: NL-477, NL-480, NL-483, NL-488, NL-493, NL-496, NL-502, NL-507..NL-511, NL-515.
- **Update (wave d13, owner decision D13 of 2026-09-28, `f30f3be3` on `wip/fafo-d13`):** D13 applied: `FeatureOptions.OptionSplice` and `OptionQuiesce` (with `DualFund`, NL-037) default to Optional on every network, mainnet included, and left `ExperimentalFeatures` (attribution_data is the only experimental feature left). BOLT 9 (09-features.md, checked 2026-09-28) lists no dependency for 34/35, 28/29 or 62/63, so `FeatureSet` gains none; D14 (`SpliceRules.IsNegotiated`: 35 and 63 both negotiated) still gates every splice. The receiver's "another RBF attempt has been created recently" is now block-based (NL-520). Tests that prove legacy behaviour pin the features off (`OptionSplice = No` in the v1 `next_funding` and unbatched `commitment_signed` rows and in `ReestablishFundingLockedTests`); the day-0 proofs run on the defaults without `AllowExperimentalFeatures` (`Day0Harness.EnableDay0Features`). The epic is closed; its follow-ups stay open as their own entries (NL-477, NL-480, NL-483, NL-488, NL-493, NL-496, NL-502, NL-507..NL-511, NL-514, NL-515).
- **Fix sketch:** Later; after NL-042 and NL-037. Wave qit landed the quiescence and interactive-tx layers splicing builds on; the shared-input TLVs (`shared_input_txid`, `shared_input_signature`) are serialized (940baca2), but the session checks `shared_input_signature` for presence only (its ECDSA validity and low-S, SP-SIG-01, are the splice host's job).
- **Blocks/Blocked-by:** Blocked-by NL-042, NL-037
- **Plan ref:** `SPLICING_PLAN.md` waves SP1, SP2, SPR (gaps SG3..SG10 in §2.2)

### NL-022 update_fail_htlc / update_fulfill_htlc lack attribution_data and fulfillment TLVs
- **Status:** fixed (6d7e480, 3a54e11)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Messages/UpdateFailHtlcMessage.cs`, `UpdateFulfillHtlcMessage.cs`
- **Evidence:** No `attribution_data` (TLV 1) or fulfillment payload TLV (3) while `option_attribution_data` is advertised (NL-074). Update (ABCD wave 6, `3ce3cad`): `AttributionDataTlv` (TLV 1, exactly 920 bytes) and `FulfillmentPayloadTlv` (TLV 3, `IsTooLong` over 32768) with converters; both message serializers read and write them strictly (6d7e480). Remaining: `MessageFactory`/handlers never fill or consume them (the switch seam, NL-072, NL-326) and the fulfillment_payload size MUST (NL-325). Update (ABCD wave 7, `4c37998`): `MessageFactory` writes TLV 1 and TLV 3, the fail/fulfill handlers store them with the removal (`HtlcRemoval.AttributionData`/`FulfillmentPayload`, migration `AddAttributionData`), and retransmission re-sends them (3a54e11); the 32 KiB MUST is NL-325.
- **Fix sketch:** Add `AttributionDataTlv` (920 bytes) + converter; wire into NL-072.
- **Blocks/Blocked-by:** Blocks NL-072
- **Plan ref:** ONION M3b

### NL-023 update_fail_malformed_htlc failure_code has no BADONION check
- **Status:** fixed (5adb882, 90a1905, 963c04b, 00095cb)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Payloads/UpdateFailMalformedHtlcPayload.cs`
- **Evidence:** `FailureCode` is a raw ushort; BOLT 2 says the receiver MUST fail the channel if the BADONION bit is not set. A missing BADONION bit gets a channel-scoped `warning` and the connection is closed (`ChannelWarningException { CloseConnection = true }`), BOLT 2's alternative to failing the channel until NL-200.
- **Fix sketch:** Validate in the malformed handler; conversion lives in NL-071.
- **Blocks/Blocked-by:** Blocked-by NL-031
- **Plan ref:** ONION M3-T3; BOLT2 N4-T2

### NL-024 Witness deserializer max-length check is commented out
- **Status:** fixed (532d979)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/ValueObjects/WitnessTypeSerializer.cs:46-48`
- **Evidence:** The `length > MAX_SIGNATURE_SIZE` guard is commented out, so an attacker-sized length is trusted.
- **Fix sketch:** Restore a bound (and bound by remaining stream length).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-025 HTLC rows stored under the old optional-onion framing no longer deserialize
- **Status:** fixed (4472a8b, a8d1381)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/HtlcDbRepository.cs` (`AddMessageBytes`)
- **Evidence:** Since e7b21f3 the onion is mandatory; rows saved without it throw on reload. Only matters for pre-existing dev databases. Update (ABCD wave 0, `0b7e617`): migration `AddCommitmentState` cuts the onion out of `AddMessageBytes` so legacy rows keep their data; `ChannelStateDbRepository.LoadAsync` and `ChannelDbRepository.GetByIdAsync` refuse such a channel with `LegacyHtlcStateException` (names NL-025), and multi-channel loads (`GetAllAsync`, `GetReadyChannelsAsync`, `GetByPeerIdAsync`, startup) log and skip only that channel (`Given_LegacyHtlcRow_When_TheChannelIsLoaded_Then_ItIsRefused`, `Given_LegacyChannelAndGoodChannelOfOnePeer_...`).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T9 open item; BOLT2 N5-T1

### NL-026 MessageFactory.CreateUpdateAddHtlcMessage cannot attach a BlindedPathTlv
- **Status:** fixed (5d6e9770)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` (`CreateUpdateAddHtlcMessage`)
- **Evidence:** No path_key parameter. Update (wave rf1, `wip/fafo` at `be9fd000`): the switch offers blinded forwards with `BlindedPathTlv(NextPathKey)` built in `ChannelStateTransitionService`; `MessageFactory.CreateUpdateAddHtlcMessage` itself still has no path_key parameter, which no caller needs.
- **Fix sketch:** Add `BlindedPathTlv?` parameter.
- **Blocks/Blocked-by:** Part of NL-079
- **Plan ref:** ONION M5

### NL-027 open_channel deserializer requires the channel_type TLV
- **Status:** fixed (7d834c0, 9e65cf8, 3c4af42, 699c67b)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/OpenChannel1MessageTypeSerializer.cs`
- **Evidence:** A missing channel_type throws at deserialization instead of being handled as a negotiation failure. channel_type is nullable on the wire; `ChannelOpenValidator`/`AcceptChannel1MessageHandler` reject a missing one with a channel-scoped error (see NL-203).
- **Fix sketch:** Deserialize as optional; enforce the requirement in the handler with a proper error.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-028 update_add_htlc onion was optional; truncated messages accepted with a null onion
- **Status:** fixed (e7b21f3, ffebaa1)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Payloads/UpdateAddHtlcPayload.cs`, `src/NLightning.Infrastructure.Serialization/Payloads/UpdateAddHtlcPayloadSerializer.cs`
- **Evidence:** Onion is now a mandatory 1366-byte `ReadOnlyMemory<byte>` read with `ReadExactlyAsync`; the local length const was replaced by `OnionConstants.PacketLength` in ffebaa1.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T9

### NL-029 update_add_htlc blinded path looked up with TlvConstants.UpfrontShutdownScript
- **Status:** fixed (e7b21f3)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs`
- **Evidence:** Same numeric value (0), wrong constant. Now `TlvConstants.BlindedPath`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T9

### NL-030 update_add_htlc accepted unknown even TLVs; malformed blinded_path leaked ArgumentException
- **Status:** fixed (37f6c30)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs:74`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/BlindedPathTlvConverter.cs`
- **Evidence:** Now `DeserializeStrictAsync` with `{BlindedPath}`; converter requires exactly 33 bytes and errors map to `MessageSerializationException`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issues 1-2

### NL-197 channel_reestablish next_funding TLV has the wrong type and shape; TLV 5 missing
- **Status:** fixed (1d9bec5)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Constants/TlvConstants.cs:93`, `src/NLightning.Domain/Protocol/Tlv/NextFundingTlv.cs`
- **Evidence:** `NextFunding = 0` and the TLV holds only a 32-byte txid; bolts master defines type 1 `next_funding` = `next_funding_txid ‖ retransmit_flags` and type 5 `my_current_funding_locked`. The serializer test fixture encodes type 0. `NextFunding = 1`, 33-byte value with `retransmit_flags`. TLV 5 (odd) is ignored, which BOLT 1 allows; model it with splicing.
- **Fix sketch:** Type 1 with the flags byte, parse-and-ignore TLV 5, strict known set {1,5}; fix `TxChannelReestablishMessageTests`.
- **Blocks/Blocked-by:** Part of NL-035
- **Plan ref:** BOLT2 N0-T6

### NL-198 closing_signed deserializer requires the optional fee_range TLV
- **Status:** fixed (1b68d6c)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/ClosingSignedMessageTypeSerializer.cs:61-65`, `src/NLightning.Domain/Protocol/Messages/ClosingSignedMessage.cs`
- **Evidence:** Throws "Required extension is missing" when `fee_range` is absent; the spec makes it optional, so legacy peers' `closing_signed` would be rejected. Fixed: nullable `FeeRangeTlv`, strict known set {1}. N10 must negotiate with and without it (NL-034).
- **Fix sketch:** Nullable `FeeRangeTlv`, strict known set {1}; round-trip tests with and without it.
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N0-T6

### NL-199 commitment_signed has no funding_txid TLV
- **Status:** fixed (1b68d6c)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Messages/CommitmentSignedMessage.cs`, `src/NLightning.Infrastructure.Serialization/Messages/Types/CommitmentSignedMessageTypeSerializer.cs`
- **Evidence:** No TLV stream at all; bolts master says the sender MUST set TLV 1 `funding_txid` (receiver ignores a CS whose `funding_txid` does not match, outside splicing). Fixed: `FundingTxIdTlv` (type 1) + converter, strict known set {1}; `CreateCommitmentSignedMessage` always sets it. The receiver rule (ignore a CS whose funding_txid doesn't match, outside splicing) belongs to the N6 handler (NL-031). Update (ABCD wave 1, `342d22e`): the receiver needs no code: BOLT 2 applies the funding_txid ignore rule only inside `start_batch` (splicing); documented in `CommitmentSignedMessageHandler` (a604dff).
- **Fix sketch:** `FundingTxIdTlv` + converter, strict known set {1}, set it in `MessageFactory`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N0-T6

### NL-324 update_fail_htlc and update_fulfill_htlc ignored their TLV stream
- **Status:** fixed (6d7e480)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateFailHtlcMessageTypeSerializer.cs`, `UpdateFulfillHtlcMessageTypeSerializer.cs`
- **Evidence:** Both serializers never read the extension, so an unknown even TLV type was silently accepted (BOLT 1; missed by NL-001). They now use `DeserializeStrictAsync` with known types {1} and {1, 3}; a wrong-length attribution_data fails the stream (reported and fixed by W6-D).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-001, NL-022
- **Plan ref:** ONION M3b

### NL-325 An oversized fulfillment_payload does not fail the channel
- **Status:** fixed (3a54e11, e64da4e)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Handlers/UpdateFulfillHtlcMessageHandler.cs`, `Domain/Protocol/Tlv/FulfillmentPayloadTlv.cs`
- **Evidence:** BOLT 2: a receiver MUST send an error and fail the channel when update_fulfill_htlc's fulfillment_payload is longer than 32768 bytes. The serializer keeps such a TLV and sets `FulfillmentPayloadTlv.IsTooLong`, but the handler never checks it (reported by W6-D, review finding 1 skipped as out of lane). Update (ABCD wave 7, `4c37998`): the fulfill handler fails the channel when fulfillment_payload is over 32768 bytes (3a54e11); a valid id and preimage are applied and committed first (KnownPreimage kept, `OutgoingHtlcFulfilled` queued and drained by `ChannelManager`), so the switch still fulfills upstream; a wrong id or preimage persists nothing (e64da4e; `AttributionHandlerTests`). The upstream fulfill after such a failure is proven at handler/event level only, not in a three-node harness.
- **Fix sketch:** Throw `ChannelFailedException` in the fulfill handler when `message.FulfillmentPayloadTlv?.IsTooLong`.
- **Blocks/Blocked-by:** Related NL-022
- **Plan ref:** ONION M3b; BOLT2 N6

---

### NL-475 tx_init_rbf locktime and feerate were swapped on both ends
- **Status:** fixed (830610fc)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` (`CreateTxInitRbfMessage`), `src/NLightning.Infrastructure.Serialization/Payloads/TxInitRbfPayloadSerializer.cs` (`DeserializeAsync`)
- **Evidence:** Both passed (locktime, feerate) to the `TxInitRbfPayload(channelId, feerate, locktime)` constructor, so a received `tx_init_rbf` had its feerate and locktime exchanged and `InteractiveTxDriver`'s IT-RBF-01 minimum-feerate check used the peer's locktime; the old tests used 1 for both values and the in-memory qit harness never serializes (found by lane SP1-A, wave sp1; lane SHA 725e3811). Regression tests `MessageFactoryRbfTests.Given_LocktimeAndFeerate_When_CreatingTxInitRbf_Then_TheyAreNotSwapped`, `SpliceMessagesTests.Given_TxInitRbfWithDistinctLocktimeAndFeerate_When_Deserialized_Then_TheyAreNotSwapped`.
- **Fix sketch:** Pass the arguments in constructor order (done).
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** `SPLICING_PLAN.md` SP1-A-T1

### NL-476 funding_output_contribution was not an s64 and was read as msat
- **Status:** fixed (830610fc)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Tlv/FundingOutputContributionTlv.cs`, its converter, `MessageFactory.CreateTxInitRbf`/`CreateTxAckRbf`
- **Evidence:** BOLT 2 defines `tx_init_rbf`/`tx_ack_rbf` TLV 0 as `[s64:satoshis]`. The Domain TLV held a `LightningMoney` (never negative) and `MessageFactory` built it from a `long` through the implicit msat conversion, so 10 sat became 10 msat and was written as 0, and a negative (splice-out) contribution could not be expressed (lane SP1-A, wave sp1; lane SHA 725e3811). The TLV now has `long Satoshis` with the wire bytes in `Value`; the converter reads and writes signed big-endian; `MessageFactory` keeps a negative contribution. `FundingOutputContributionTlvConverterTests`, `MessageFactoryRbfTests`. The driver still builds only positive contributions (NL-481).
- **Fix sketch:** Signed satoshi TLV (done).
- **Blocks/Blocked-by:** Part of NL-037, NL-021
- **Plan ref:** `SPLICING_PLAN.md` SP1-A-T1

## BOLT 2: Behaviour layer

### NL-031 [EPIC] HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee)
- **Status:** fixed (a604dff, f5315c0, a02afa7, a388fc4, aac5f60, e5f7312, 4ec83d3, 22c29ae, ca87313)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:88-133`
- **Evidence:** The switch handles only OpenChannel/AcceptChannel/FundingCreated/ChannelReady/FundingSigned; `default` (L133) throws `ChannelErrorException`, so any update_add_htlc, commitment_signed, update_fee etc. **disconnects the peer**. No handlers exist. Update: `default` now throws a channel-scoped `ChannelWarningException` (the peer stays connected), an unknown channel_id gets an `error` for that id, and malformed-without-BADONION warns and closes (699c67b, 00095cb). Update (four-lane integration, `3c625e1`): the building blocks now exist but nothing is wired: ordered per-peer processing and per-channel lock (NL-033, NL-193), per-side params (NL-194), separate commitment numbers (NL-188), HTLC txs and signatures (NL-056, NL-057), revocation guard (NL-189), remote shachain storage (NL-136), and the pure commitment engine with its two-engine invariant simulator (`Channels/Commitments/`, N4). Still missing: persistence of the engine state (N5), the handlers and `ChannelManager` cases (N6), reestablish (N7). The engine/builder seam is NL-230. Update (ABCD wave 0, `0b7e617`): the engine is now connected to the real signer (NL-230, 2fa8cf4), uses one fee calculator (NL-231, 192e212), raises lock-in/fulfill/irrevocable-fail/settle events with `IHtlcSwitch` as the consumer port (N4-T4, b166ea0), and is persisted with one save per transition (`IChannelStateDbRepository`, N5-T1..T3, 4472a8b, bb2731a); `IChannelOperations`, `ChannelState.Failed` and `ChannelFailedException` contracts exist (2ede2ee). Still missing: the handlers, `ChannelOperationsService`/`CommitScheduler` and `ChannelManager` cases (N6, ABCD W1-A), reestablish (N7, W2-A). Update (ABCD wave 1, `342d22e`): N6 is done. The seven receive handlers (`Application/Channels/Handlers/`, all through the scoped `ChannelStateTransitionService`: `ApplyAsync` + one save, then `UpdateCommitments`, then send; the RAA secret is revealed only after the save) and their `ChannelManager` cases (a604dff); the send side `ChannelOperationsService : IChannelOperations`, the debounced `CommitScheduler` (never signs while `RemoteNextCommit` exists, persists `SentCommitDiff` first) and `LocalOnlyHtlcSwitch`, gated by `NodeOptions.EnableHtlcs`, plus startup replay of `DerivePending` (a02afa7); each channel's link is pinned to the connection it turned Open on and every send-side update and signature needs that link (e5f7312). Proofs: in-process `TwoNodeHarness` (30 HTLCs each way, fulfills, fails, fee round, anchors and not, txids identical at every step, I7; f5315c0) and Docker N6-T5 against LND 0.20 (`NormalOperationFlowTests.Given_LndPaysUs_When_LockedIn_Then_FailedBackAndChannelActive`: fail-back decoded by LND at source index 1, commitment numbers 2/2, channel Active; a388fc4). Remaining: forwarding and final-hop receive (NL-073, ABCD W2-B), reestablish (NL-035, W2-A; until then a channel loaded at startup never sends updates, NL-252), fail-the-channel broadcast (NL-200). Deviation: a normal-operation message on a channel that is not Open gets warning + close rather than an error. Update (ABCD wave 2, `a5675cb`): the remaining pieces landed: reestablish (NL-035, 4ec83d3, 22c29ae) and the forwarding/final-hop switch (NL-073, ca87313). Every update message is exercised end to end against LND 0.20 (Docker N6/N7/N8 proofs) and between two NLightning nodes (ABCD suite, green). The fail-the-channel broadcast is N9-T4 and is tracked in NL-094; close is NL-034.
- **Fix sketch:** Handlers + ChannelManager cases for all 7 messages, commitment dance state machine, per-channel locking, persistence of every state transition. Sub-issues: NL-032, NL-033, NL-057, NL-056, NL-125, NL-051, NL-187, NL-188, NL-190, NL-193, NL-194, NL-200.
- **Blocks/Blocked-by:** Blocks NL-073, NL-034, NL-035, NL-094
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT_COVERAGE roadmap step 5; BOLT2 N4-N6

### NL-032 ChannelModel has no HTLC/balance/next-id mutators; HtlcState has 4 values
- **Status:** fixed (c1f215f, 0edfa14, 4472a8b)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs:40-61`, `src/NLightning.Domain/Channels/Enums/HtlcState.cs:3-8`
- **Evidence:** HTLC collections, balances, next ids and revocation numbers are get-only. `HtlcState` = Offered/Fulfilled/Failed/Expired, no commitment-dance stages. Update: the pure engine `Channels/Commitments/ChannelCommitments` holds HTLC records, msat balances, next ids and commitment numbers, and `HtlcState` now has core-lightning's 20 `htlc_state` values (10-19/30-39; legacy 0-3 kept decodable and rejected by the machine) driven by `HtlcStateTable` (c1f215f, 0edfa14). `ChannelModel` itself still has no mutators; N5 persists the engine state and N6 wires it. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): fixed by design: the HTLC set, msat balances, next ids and commitment numbers live in the engine snapshot `ChannelModel.Commitments` (`ChannelModel.cs:52`), the balance/next-id/number getters read it (`ChannelModel.cs:132-175`), and the only mutator is `UpdateCommitments(next, extras)` (`ChannelModel.cs:304`, 4472a8b), called after `IChannelStateDbRepository.ApplyAsync` + one save per transition (N5/N6). Per-HTLC add/settle/fail stages are `HtlcState` 10-19/30-39 driven by `HtlcStateTable` (c1f215f, 0edfa14). Separate mutators on `ChannelModel` are deliberately not added (root `CLAUDE.md`: change the snapshot only through the engine).
- **Fix sketch:** Add add/settle/fail mutators and per-side commitment states (pending/committed/revoked, lock-in).
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT2 N4-T1

### NL-033 No per-channel ordering lock; PeerManager peer table not thread-safe; unobserved reply continuations
- **Status:** fixed (d60a891, 9c057f2)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:38,300,339,379`, `ChannelManager.cs`
- **Evidence:** Two messages for one channel can race on the shared `ChannelModel`; `_peers` is a plain `Dictionary`; replies attached with `ContinueWith` are never awaited, so exceptions are lost. Fixed: `IChannelLockProvider` per-channel lock around every channel mutation (peer messages, funding confirmation, stale/backfill, startup registration), `ConcurrentDictionary` of peer sessions, one ordered inbound loop per peer and a `PeerOutbox` as the single send path (d60a891); a closed connection's inbound loop stops, a reconnecting peer replaces its old session, and simultaneous connects keep the lower pubkey's connection (9c057f2).
- **Fix sketch:** Per-channel async lock/queue; `ConcurrentDictionary`; await or log continuations.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** ONION_ROUTING_PLAN §7 "Per-channel ordering"; BOLT2 N0-T3

### NL-034 [EPIC] Channel close (shutdown / closing_signed / option_simple_close)
- **Status:** fixed (34757a3, b38ce86, 6d81ecd, 9733937, 287a956, 5d0aafc, 8e0e154, d8680cd, 8096700, 8249044, b67e065, 5b9ce68)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Enums/ChannelState.cs` (Closing/Closed enum only), `src/NLightning.Infrastructure.Bitcoin/Transactions/ClosingTransaction.cs` (commented out)
- **Evidence:** Nothing moves a channel to Closing; an incoming shutdown disconnects the peer (see NL-031). Funds can only leave a channel via the peer's force close. Update (ABCD wave 3, `c92d837`): legacy cooperative close is implemented (N10-T1..T3): shutdown/closing_signed handlers, `ChannelCloseCoordinator`, pure `LegacyClosingNegotiator`, the BOLT 3 legacy closing tx, states ShuttingDown 23 / Negotiating 25 / Closing 30, migration `AddShutdownState` (3 providers), `closechannel` IPC (ClientCommand 13). Crash-safe: the closing watch is saved with Closing; a funding-spend watch records a mutual close the peer broadcast; startup and every block finish a confirmed close. Docker `CooperativeCloseFlowTests` passed 4/4 against LND at lane step 2 (287a956) but was **not re-run** after the step-3 fixes (8e0e154) nor at integration (Docker env, NL-276). Remaining, each with its own entry: `option_simple_close` (NL-020), the closing timeouts (NL-284), HTLCs added after our shutdown (NL-279), the R09 deviation (NL-285), Docker-only proof gaps (NL-286), local upfront script (NL-045). Close this epic once the Docker close proof passes on `wip/fafo`. Update (ABCD wave 4, `6b5d50e`): the Docker close proof `CooperativeCloseFlowTests` passed 4/4 against LND on `wip/fafo` at integration (in-container runner, NL-276), and `ClnCloseTests` closes against CLN in both roles with `fee_range` (8096700, a38c999). The closing timeouts are in (NL-284). The legacy close is done, so the epic is closed; what remains has its own entry: `option_simple_close` (NL-020), HTLCs added after our shutdown (NL-279), the R09 deviation (NL-285), the Docker restart-while-closing proof (NL-286), the local upfront script (NL-045). Update (wave lh1, `a6c633f9`): NL-279 (fail back HTLCs added after our shutdown) and NL-045 (our upfront shutdown script) are fixed (79ea3f92, 5aef7305); remaining follow-ups NL-285, NL-286.
- **Fix sketch:** shutdown/closing_signed handlers with fee_range, closing tx builder (NL-065), then option_simple_close (NL-020). Needs a close IPC command (NL-152).
- **Blocks/Blocked-by:** Blocked-by NL-031 (must wait for HTLCs to clear)
- **Plan ref:** BOLT_COVERAGE roadmap step 11; BOLT2 N10 (legacy), N11 (simple close)

### NL-035 [EPIC] channel_reestablish / option_data_loss_protect
- **Status:** fixed (4620895, 4ec83d3, 1ad14ce, a4e95d7, 82c4c37, 30c1bb8, 22c29ae, f2f1ef6)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:62`
- **Evidence:** TODO only; an incoming channel_reestablish disconnects the peer. `option_data_loss_protect` is advertised **Compulsory** (`FeatureOptions.cs:14`) with no implementation, and state needed for it (NL-136) is not persisted. Update: an incoming channel_reestablish now gets a channel-scoped `warning` instead of a disconnect (699c67b); `option_data_loss_protect` is advertised Optional (ASSUMED bit), not Compulsory. Update (ABCD wave 1, `342d22e`): W1-A left the reestablish hooks for N7 (W2-A): `ChannelStateTransitionService.LoadRemoteShachainAsync`, `SentCommitDiffCodec`, `CreateRevokeAndAck`, and `IPeerLivenessProbe.MarkLinkUp(channelId, peer)` plus the pending-event replay after reestablish (NL-252). `listchannels` reports `IsReestablished`, always false until N7 (e30a845). Update (ABCD wave 2, `a5675cb`): N7 is done. `Domain/Channels/Reestablish/ReestablishPlanner` is a pure implementation of the BOLT 2 rules, with named cases plus a table of about 4.7k cases checked against a spec-literal oracle (4620895). `IChannelManager.OnPeerConnectedAsync`/`OnPeerDisconnectedAsync`/`OnPeerConnectionChanged` are called by `PeerManager`: revert the peer's unsigned updates, send our `channel_reestablish` at connect for V1FundingSigned/ReadyForThem/ReadyForUs/Open, gate updates with `ReestablishGatedLivenessProbe` until the exchange completes, retransmit the stored `SentCommitDiff` byte-identical and a regenerated revoke_and_ack in `LastSent` order, then unsigned updates with their original ids and channel_ready. Data loss is detected: `DataLossDetected` is persisted, then the channel fails without broadcasting (4ec83d3). A peer's reestablish that arrives after channel_ready is answered, and `Publish` drops normal-operation messages of a channel not reestablished on the current connection (22c29ae). Proofs: I11 harness, with a drop at every message boundary, a crash at every save and an old-backup restore (1ad14ce); Docker `ReestablishFlowTests` (a) our restart, (b) an LND restart through `RestartByAlias("alice")` with address-hold containers (30c1bb8, NL-262), (c) a crash after our commitment_signed is persisted (a4e95d7); the ABCD reestablish test. `listchannels` `IsReestablished` reads `IReestablishTracker` (f2f1ef6). Deviation from plan §3.11 step 3: the secret is checked against our secret Y-1, as the spec says. Left for other milestones: shutdown re-send (N10, B2-RE-28), signer-level broadcast refusal after data loss (N9-T4), Closing/Negotiating resumption (NL-036).
- **Fix sketch:** Reestablish on reconnect with commitment/revocation number sync, retransmission, data-loss detection.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-136, NL-125, NL-126, NL-127
- **Plan ref:** BOLT_COVERAGE roadmap step 6; BOLT2 N7

### NL-036 Closing/Stale channels are not handled on startup
- **Status:** fixed (b38ce86, 8e0e154)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:70`
- **Evidence:** `TODO: Deal with channels that are Closing, Stale, or any other state`. Update (ABCD wave 2, `a5675cb`): `ChannelManager.ResumeStartupStateAsync` handles every stored state (82c4c37). Closed/Stale are skipped. None/V1Opening/V2Opening are never persisted, so they are logged and skipped. V1FundingCreated moves to V1FundingSigned when a watch for the funding txid exists; otherwise it is persisted Stale and not registered (BOLT 2: the funder SHOULD NOT remember). Explicit cases for V1FundingSigned, ReadyFor*, Closing and Failed; Failed re-sends its stored error. Remaining: Closing/Negotiating resumption and close logic (N10), and a Stale funder channel keeps its UTXO locks (NL-259). The funding-tx rebroadcast is NL-258. Update (ABCD wave 3, `c92d837`): ShuttingDown/Negotiating send channel_reestablish at connect, re-send our shutdown and restart negotiation (B2-RE-28/29, b38ce86); Closing channels take part in the reestablish, re-send the agreed closing_signed, and at startup close at once (watch completed), re-create a missing watch or rebroadcast the stored tx (8e0e154). Proven in-process (`CooperativeCloseHarnessTests`, `ClosingLifecycleTests`). Stale funder UTXO locks remain NL-259, funding rebroadcast NL-258.
- **Fix sketch:** Resume close / watch on-chain for these states.
- **Blocks/Blocked-by:** Part of NL-034, NL-094
- **Plan ref:** BOLT2 N7-T5, N10-T3 (partial)

### NL-037 [EPIC] Dual funding / interactive-tx (v2 open)
- **Status:** open (partial: 9355ad92, 081d599f, 8ee516c5, 21be3935, 8ed3aaa8, acd0f711, 940baca2, 99be2451, f513a369, 471d7e6b, 633980df, 65fb5b21, f03470fb, ef806980, c502fdb6, 830610fc, 01b70bf1, 966cad6e, c9603fb9, 27cca704, 7b325699, 5e49fdc3, d90dd68c, d618dbf3, e45b61b4, c6a4a779, 17066201, d778d100, 059383f4, 252063df, 5f078964, c8d6ad6b, af6b1bcd)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/InteractiveTx/`, `src/NLightning.Infrastructure.Bitcoin/InteractiveTx/`, `src/NLightning.Application/InteractiveTx/`, `src/NLightning.Application/Channels/Handlers/Tx*MessageHandler.cs` (the old `InteractiveTransactionService` and `Tx*Validator` are deleted)
- **Evidence:** Messages and serializers exist; service not in DI, no handlers. `option_dual_fund` is advertised Optional but `ChannelFactory.cs:52,147` only rejects a Compulsory DualFund, so peers may attempt v2 opens we disconnect on.
- **Fix sketch:** Stop advertising (NL-109) until implemented; then handlers + validators. Sub-issues: NL-038, NL-039, NL-040, NL-041.
- **Update (wave qit, `SPLICING_PLAN.md` wave IT):** the interactive-tx layer is done: the pure `InteractiveTxSession` with `InteractiveTxRules`, `TxSignaturesOrder`, `CollaborativeFeeCalculator` and `InteractiveTxRbfRules` (IT1, lane IT-A: 081d599f, 8ee516c5, 21be3935); `PrevTxInspector`, `InteractiveTxBuilder` (BOLT 3 Appendix G byte-exact) and `WalletInteractiveTxContributor` over fee-input reservations (IT2, IT-B: 8ed3aaa8, acd0f711); the shared-input TLVs and the `InteractiveTxSessions` table (migration `AddInteractiveTxSessions`, all three providers; IT3, IT-C: 940baca2, 99be2451, f513a369); `InteractiveTxDriver`, the nine `tx_*` handlers and their `ChannelManager` cases, `InteractiveTxHarnessTests` on the real engine (IT4, IT-D: 471d7e6b, 633980df, 65fb5b21, f03470fb); registered and orphaned reservations released at startup (ef806980), negotiations ended on disconnection (c502fdb6). Remaining for this epic: dual funding itself (`open_channel2`/`accept_channel2` still get the "not supported yet" warning; `OptionDualFund` stays No and experimental; plan wave DF) and an interop proof of the interactive-tx layer (Proof DF or Proof SP1). Follow-ups NL-473, NL-474.
- **Update (wave sp1, lane SP1-F, plan wave DF; integrated at `3660bff2`):** the dual-funded open is implemented behind experimental `OptionDualFund` (default No): `ChannelIdV2` and `DualFundingRules` (966cad6e), `open_channel2`/`accept_channel2` in satoshis with `second_per_commitment_point` (c9603fb9), `DualFundedOpenService`, `DualFundHost` over the interactive-tx driver, `openchannel --dual-fund` (27cca704, e45b61b4), the first commitment, `tx_signatures`, confirmation and `channel_ready` in process (7b325699), RBF of an unconfirmed open and `next_funding` retransmission after a restart (5e49fdc3), the refusals (d90dd68c), review fixes (keep the open once our `tx_signatures` went out, RBF off by default (`Node:DualFund:AllowRbf`) and refused for public or confirmed opens, the accepter's reserve from the announced share, accepter timeout, mismatched `next_funding` fails the channel; 17066201); the dual-funding columns ship in `AddSpliceFundings` (01b70bf1); `AddDualFundingServices()` registered and `Node:DualFund` bound by the integrator (d778d100, which also reads `tx_init_rbf`'s s64 contribution in the dual-funded RBF). **Proof DF green against CLN v26.06.8** (`ClnDualFundTests`: CLN opens v2 to us with our contribution, we open v2 and CLN matches, RBF of our unconfirmed open; d618dbf3, c6a4a779). This also proves the interactive-tx layer on the wire. The splice wire fixed two tx_init_rbf bugs (NL-475, NL-476). Remaining: DF3 (`option_dual_fund` out of the experimental set; not scheduled), a dual-fund RBF of a public channel (the signer keeps the first attempt's outpoint; SP1-C's per-funding registration could lift it), NL-473, NL-474.
- **Update (wave sp2, integrated at `31950b81`):** the v2 open's `next_funding` runs through the shared reestablish planner (`DualFundReestablish` keeps only the negotiation load and the commitment_signed rebuild; 059383f4); static channel backups and peer storage follow a dual-funded funding and back up every signed RBF candidate (252063df, 5f078964); the day-0 Docker proof opens a dual-funded public channel between two NLightning nodes, restarts mid-splice and upgrades a pre-sp1 database in place (c8d6ad6b, af6b1bcd). Remaining unchanged: DF3, NL-473, NL-474.
- **Update (wave d13, `f30f3be3` on `wip/fafo-d13`):** DF3 done by owner decision D13: `FeatureOptions.DualFund` (`option_dual_fund` 28/29, no BOLT 9 dependency) defaults to Optional on every network and left `ExperimentalFeatures`, so a peer's `open_channel2` is accepted (contributing `Node:DualFund:AcceptContributionSat`, 0 by default); `openchannel` still opens a v1 channel unless `--dual-fund`. Remaining: RBF of a v2 open is off by default (`Node:DualFund:AllowRbf`) and refused for a public channel (the signer keeps the first attempt's outpoint), NL-473, NL-474, NL-521.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-038 Interactive-tx serial_id parity check semantics unclear
- **Status:** fixed (59c6f6b, a404c37)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/Tx{AddInput,AddOutput,RemoveInput,RemoveOutput}Validator.cs:9-13`
- **Evidence:** Rejects odd ids only when `isInitiator`; whether that means the local node or the sender is undocumented (unverified against BOLT 2 semantics).
- **Fix sketch:** Define `isInitiator` as "sender is initiator", check parity for both sides, add tests.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-039 TxAddInputValidator.Validate is async void
- **Status:** fixed (41af05c, a404c37)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs:8`
- **Evidence:** Exceptions from the awaited prevTx check escape the caller and can crash the process.
- **Fix sketch:** Return `Task` and await it.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-040 InteractiveTransactionService checks output serial ids against inputs
- **Status:** fixed (0bb9f76)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs:54-65`
- **Evidence:** `IsSerialIdUnique`/`IsSerialIdPresent` only look in `_inputs`.
- **Fix sketch:** Check the union of inputs and outputs (serial ids are shared across both).
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-041 Interactive-tx prevTx and script validation are TODOs
- **Status:** fixed (081d599f, 8ed3aaa8, acd0f711)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs:49,57`, `InteractiveTransactionService.cs:48,69`
- **Evidence:** Output count and scriptPubKey of prevTx are not parsed. Wave qit: the session enforces the prevtx result through `IPrevTxInspector` (valid tx, vout in range, witness program checked by the inspector and again by the session) and output standardness and dust (081d599f, lane IT-A); `Infrastructure.Bitcoin/InteractiveTx/PrevTxInspector` parses prevtx strictly (one tx, no trailing bytes), accepts only witness programs (P2WPKH/P2WSH/P2TR and future versions; P2PKH, P2SH, P2SH-wrapped, bare and OP_RETURN refused) and checks confirmations through bitcoind, `gettxout` first so no `-txindex` is needed (8ed3aaa8, acd0f711, lane IT-B). The old validators are deleted. The integrator's report called this partial; the ledger closes it because the fix sketch is fully met (the remaining interactive-tx work is NL-037).
- **Fix sketch:** Parse prevTx with NBitcoin; require segwit spend.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-042 Quiescence (stfu) behaviour missing
- **Status:** fixed (f30f3be3; earlier: 9355ad92, 9303e6d4, 3380b680, 3f230897, 6534018d, 5614a07f, 5b9d079e, ac3a1aa8, 34664a14, 65a20108, ef806980, b7d14056)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Quiescence/`, `src/NLightning.Application/Channels/Quiescence/`
- **Evidence:** No handler or state; `option_quiesce` advertised Optional. Update: `option_quiesce` now defaults to No and is experimental-gated (e93eb41); stfu gets warning + disconnect (NL-019). Wave qit (`SPLICING_PLAN.md` wave Q, Q1-T1..T6 and Proof Q): the pure `QuiescenceRules` (9303e6d4), `stfu` routed as a channel message (3380b680, 3f230897), `QuiescenceService` with the owed-`stfu` release after each transition and the 60 s / idle `QuiescenceTimeoutMonitor` (6534018d, ac3a1aa8), the update gate `ChannelQuiescentException` (5614a07f), the harness proof on `TwoNodeHarness` and the production switch (5b9d079e), registration and `QuiescenceRules` in the service (ef806980), our probe ended with `tx_abort` through the interactive-tx driver and a peer `update_*` after its `stfu` answered with warning + close (Q-S-04 receive, b7d14056); Docker Proof Q against CLN v26.06.8, `ClnQuiescenceTests` 5/5 (34664a14, 65a20108, b7d14056). Remaining: `OptionQuiesce` stays No and experimental until a dependent protocol exists (splicing, plan D2/D13), proof (d) against LND 0.20 not attempted, and the seams in NL-470; CLN findings NL-467, NL-468. **Update (wave d13, `f30f3be3`):** splicing is the dependent protocol (plan D2), so D13 turned `OptionQuiesce` on by default (Optional, every network) together with `OptionSplice`, out of the experimental set; closed. Carried as their own entries: NL-467, NL-468, NL-470, NL-477 (LND 0.20 has no splicing, so a quiescence proof against it has no dependent protocol and stays unattempted).
- **Fix sketch:** Stop advertising until implemented; then stfu handling per BOLT 2.
- **Blocks/Blocked-by:** Blocked-by NL-019, NL-031; blocks NL-021
- **Plan ref:** BOLT2 N0-T4 (advertising only); `SPLICING_PLAN.md` wave Q

### NL-043 open_channel push_msat check is 1000x too lenient
- **Status:** fixed (f73a634, 1153f13)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:104`
- **Evidence:** `PushAmount > 1_000 * FundingAmount` where both are `LightningMoney` (msat); spec bound is `push_msat <= funding_satoshis * 1000`, i.e. `PushAmount > FundingAmount`. Also requires funding - push to cover the initial commitment fee (+ anchors); initiator rejects push > funding.
- **Fix sketch:** Compare `PushAmount > FundingAmount`; add a boundary test.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 3

### NL-044 Anchor/no-anchor commitment weight selection is inverted
- **Status:** fixed (f73a634, 1153f13)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:108-110`, `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:183-185`
- **Evidence:** `OptionAnchors > No ? ...WeightNoAnchor : ...WeightWithAnchor`. Fee check uses the peer's feerate_per_kw.
- **Fix sketch:** Swap the branches; test both.
- **Blocks/Blocked-by:** Related NL-061
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N2-T1

### NL-045 Local upfront_shutdown_script is never generated
- **Status:** fixed (79ea3f92, 5aef7305)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:101,235`
- **Evidence:** `TODO: Generate a script from the local key set`; the feature is advertised Optional. Update: `upfront_shutdown_script` now defaults to No (3c2b673). `ChannelFactory` still throws when the peer requires it; BOLT 2 allows sending a zero-length script instead. Update (ABCD wave 3, `c92d837`): the peer's upfront script is enforced (B2-SHUT-R05) and a script we sent upfront would be reused (B2-SHUT-S09), but we still never generate one. Update (wave lh1, `a6c633f9`): fixed. `UpfrontShutdownScriptSource` (Application `Channels/Close/`, registered by `AddChannelCloseServices`) reserves a fresh P2WPKH wallet address (`IBitcoinWalletService.ReserveUnusedAddressAsync`, `WalletAddresses.IsReserved`) when `option_upfront_shutdown_script` is negotiated and sets it through `ChannelModel.SetLocalUpfrontShutdownScript` (V1Opening only, once): the fundee in `OpenChannel1MessageHandler` after admission, the funder in the Daemon `OpenChannelClientHandler` after the funding UTXOs are locked. It is persisted in `ChannelConfig.LocalUpfrontShutdownScript`, sent in the open/accept TLV and reused at close (B2-SHUT-S09). `ChannelFactory` no longer refuses a peer that requires the feature (79ea3f92). Review fix: a fundee open abandoned before funding_created reuses its script, so repeated open_channel no longer grows the wallet (in memory only, NL-463) (5aef7305). The feature still defaults to No. Proofs: `OpenChannel1MessageHandlerTests`, `UpfrontShutdownScriptSourceTests`, `ChannelFactoryTests`, Integration `WalletAddressReservationTests` (lane l1).
- **Fix sketch:** Derive a wallet script (or send zero-length) and persist it for close.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T1

### NL-046 accept_channel rejected when channel_type present and upfront_shutdown_script absent
- **Status:** fixed (8ddae57, b08c494)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs:118-120`
- **Evidence:** Requirement triggers on `UpfrontShutdownScript > No || ChannelTypeTlv is not null`; only negotiation of `option_upfront_shutdown_script` should require it. Can reject valid peers. Incoming open half was NL-204.
- **Fix sketch:** Drop the channel_type condition.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-047 AcceptChannel1 error cleanup is inverted and leaks locked UTXOs
- **Status:** fixed (eb59385)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs:226-242`
- **Evidence:** Cleanup only runs when the channel id changed and then removes it from the temporary map; locked UTXOs are never released.
- **Fix sketch:** Remove from the correct map for both cases and unlock UTXOs.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-048 Initiator channel is not persisted before funding_signed
- **Status:** wontfix
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs`, `FundingSignedMessageHandler.cs`
- **Evidence:** A crash between funding_created and funding_signed loses the channel state (funding tx is not yet broadcast, so no direct loss). Spec-wrong: BOLT 2 "Message Retransmission" says a funder that has not broadcast the funding tx SHOULD NOT remember the channel on disconnect. Persisting was tried in eb59385 and reverted in d855f0f; `FundingSignedMessageHandler` persists before publishing. Update (ABCD wave 2, `a5675cb`): the wontfix is pinned by a test (82c4c37): `test/NLightning.Application.Tests/Channels/Reestablish/FunderRememberRuleTests.Given_FundingSigned_Then_PersistedBeforeBroadcast` checks the order add V1FundingCreated, save, broadcast, update V1FundingSigned, save; no store or broadcast after a bad signature. The startup rule forgets a V1FundingCreated channel without a watch (NL-036). A failed broadcast leaves V1FundingCreated only with a mocked monitor. The real `BlockchainMonitorService` saves the watch before publishing, so the channel is V1FundingSigned and the tx is never rebroadcast (NL-258).
- **Fix sketch:** Persist after funding_created is sent.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N7-T6

### NL-049 ForgetStaleChannels has no state filter and can mark open channels Stale
- **Status:** fixed (afbb108, ca64c66)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:189-205`, `src/NLightning.Domain/Channels/Models/ChannelModel.cs:20`
- **Evidence:** Selects `FundingCreatedAtBlockHeight <= height - 2016` for every channel. The field is 0 until confirmation, and old confirmed Open channels also match, so on any chain taller than 2016 blocks live channels are forgotten. Legacy fundee rows with height 0 are backfilled with the current height.
- **Fix sketch:** Filter to unconfirmed opening states and track the creation height explicitly.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N0-T7

### NL-050 ConfirmUnconfirmedChannels re-fires every block (commitment number drift, repeated channel_ready)
- **Status:** fixed (afbb108)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:231-267`, `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs:43-47`
- **Evidence:** A ReadyForUs channel stays ReadyForUs; the handler only logs the wrong state, so every block increments CommitmentNumber and re-sends channel_ready.
- **Fix sketch:** Return early on wrong state; make confirmation idempotent.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N0-T7, N1-T1

### NL-051 channel_ready never stores the peer's second per-commitment point
- **Status:** fixed (4568921)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs:62`
- **Evidence:** Guard `CurrentPerCommitmentIndex == 0`, but the index counts down from 2^48-1, so the point is likely never updated and the first commitment update would use the wrong point.
- **Fix sketch:** Compare against the initial index (2^48-1); test.
- **Blocks/Blocked-by:** Blocks NL-031
- **Plan ref:** BOLT2 N1-T2

### NL-052 Failed startup reconnect skips channel registration
- **Status:** fixed (753cbd9)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:69-72`
- **Evidence:** `TODO: Handle this case, maybe retry or log more details`; channels for that peer are never loaded/retried. Channels are registered before the connect attempt; registration is still fire-and-forget (NL-201).
- **Fix sketch:** Register channels regardless and retry connection with backoff.
- **Blocks/Blocked-by:** Related NL-035
- **Plan ref:** BOLT2 N1-T6

### NL-053 FundingCreatedMessageHandler flagged "REVIEW FULL FLOW"
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Handlers/FundingCreatedMessageHandler.cs:132`
- **Evidence:** Author TODO; no specific defect recorded.
- **Fix sketch:** Review the non-initiator flow against BOLT 2 and add tests.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-054 channel_ready: no application notification or routing-table update
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs:106-107`, `FundingConfirmedMessageHandler.cs:79-80`
- **Evidence:** TODOs only.
- **Fix sketch:** Raise a domain event; feed the graph once NL-099 exists.
- **Blocks/Blocked-by:** Blocked-by NL-099
- **Plan ref:** —

### NL-055 Handler discovery uses reflection, fragile under trimming/AOT
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/DependencyInjection.cs`
- **Evidence:** `Assembly.GetTypes()` scan registers `IChannelMessageHandler<>`; the Native/AOT configs may trim them.
- **Fix sketch:** Explicit registrations or a source generator.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-187 Second local per-commitment point is derived from index 1 instead of 2^48-2
- **Status:** fixed (b74e526)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs:52-54`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:127-139`
- **Evidence:** The handler passes `CommitmentNumber.Value` (1) to `GetPerCommitmentPoint`, whose `commitmentNumber` parameter is passed unchanged to `GeneratePerCommitmentSecret` as the BOLT 3 index. The first point uses `FirstPerCommitmentIndex`, so the second RAA would break the peer's shachain. Fixed: `PerCommitmentIndex.From(n) = 2^48-1-n`, every signer per-commitment API takes a commitment number, and channel_ready's second point is at index 2^48-2.
- **Fix sketch:** Signer APIs take commitment numbers and convert with `index = 2^48-1-n`; add a `PerCommitmentIndex` helper; test the point sent in channel_ready.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N1-T1

### NL-188 One CommitmentNumber is shared by the local and remote commitments
- **Status:** fixed (b74e526, 0b8dd33, 72a4ac6, 3c625e1)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (`CommitmentNumber`), `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:225`
- **Evidence:** The factory uses `channel.CommitmentNumber` for both sides; local and remote numbers diverge during the commitment dance, so one of the two commitments gets the wrong obscured number. No local/remote commitment numbers are persisted. Fixed: `ChannelModel.LocalCommitmentNumber`/`RemoteCommitmentNumber`, `CommitmentNumber` is an immutable opener/accepter obscuring helper, both numbers are persisted (migration `PersistCommitmentNumbers`), the factory refuses a stored remote point that belongs to another number (72a4ac6; the missing second remote point is NL-232), and listchannels reports both (3c625e1).
- **Fix sketch:** Separate `LocalCommitmentNumber`/`RemoteCommitmentNumber` (persisted); make `CommitmentNumber` an immutable obscuring helper.
- **Blocks/Blocked-by:** Part of NL-031; related NL-069, NL-127
- **Plan ref:** BOLT2 N1-T1

### NL-190 Next HTLC ids start at 1 instead of 0
- **Status:** fixed (efd8a2f, 2d1fca6)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:133,253`
- **Evidence:** `ChannelModel` is created with `localNextHtlcId = 1` and `remoteNextHtlcId = 1`; BOLT 2 requires the first id to be 0. `Bolt3IntegrationTests.GetTestChannelModel` also passes 1. Fixed: new channels start both ids at 0 (efd8a2f); the `StoreMsatBalancesAndShortChannelId` data step resets 1/1 to 0 on channels without HTLC rows (2d1fca6).
- **Fix sketch:** Start at 0; unit test.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N1-T3

### NL-193 Channel handlers return one message; out-of-band sends are unordered
- **Status:** fixed (d60a891, a38b571)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/Interfaces/IChannelMessageHandler.cs`, `src/NLightning.Application/Node/Managers/PeerManager.cs:299-301,364-384`
- **Evidence:** `HandleAsync` returns `Task<IChannelMessage?>`; receiving `commitment_signed` must emit `revoke_and_ack` then possibly `commitment_signed`, and reestablish needs several ordered messages. `OnResponseMessageReady` sends fire-and-forget, with no ordering relative to replies. Fixed: handlers return `IReadOnlyList<IChannelMessage>`, raised in order under the channel lock; every send goes through the per-peer `PeerOutbox` (d60a891); the IPC open-channel path raises open_channel through `IChannelManager.StartOpeningChannelAsync` under the temporary channel's lock (a38b571). Remaining hazard: NL-234.
- **Fix sketch:** Return a list; route replies and events through one per-peer ordered outbox.
- **Blocks/Blocked-by:** Part of NL-031; related NL-033
- **Plan ref:** BOLT2 N0-T3

### NL-194 ChannelConfig is one-sided: one set of limits and one to_self_delay for both directions
- **Status:** fixed (2aba47d, de4c93d, 90a83d0, 49db838)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/ValueObjects/ChannelConfig.cs`, `ChannelFactory.cs:115-120`, `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs:88-97`, `AcceptChannel1MessageHandler.cs:137-150`, `CommitmentTransactionModelFactory.cs:200`
- **Evidence:** Non-initiator copies the opener's reserve/htlc_minimum/max_accepted/max_in_flight/to_self_delay and echoes them back in accept_channel; initiator keeps its own limits but takes the peer's to_self_delay; the factory uses that one delay for both commitments. BOLT 2 add/fee limits can't be evaluated per direction. The Docker open test pushes 0, so the peer's commitment has no to_local output and the mismatch stays invisible (inferred). Fixed: `ChannelParams { Local, Remote }` of `ChannelParty`; accept_channel carries our values; the factory uses the counterparty's to_self_delay and the holder's dust per side; migration `SplitChannelParams` (+ `FlagInferredChannelParams`: rows from before the split carry `HasInferredParams` and must not be failed over those limits). Update (ABCD wave 0, `0b7e617`): the engine carries `HasInferredParams` as `CommitmentParams.HasInferredLimits`, and `UpdateValidator.ValidateReceiveAdd` then skips htlc_minimum, max_accepted/max_in_flight and the reserve part of B2-ADD-R02 (7ba115f).
- **Fix sketch:** Split into `ChannelParams { Local, Remote }` with documented direction rules; accept_channel sends our NodeOptions values; the factory uses the holder's delay per side; migration with a data step.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N1-T4

### NL-200 No failed-channel state; ChannelErrorException always just disconnects
- **Status:** fixed (699c67b, 00095cb, 4961ba5, 2ede2ee, 1390027, a604dff, a02afa7, 4ec83d3)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`HandleChannelMessageResponseAsync`), `src/NLightning.Domain/Channels/Enums/ChannelState.cs`
- **Evidence:** The spec's "send error and fail the channel" can't be expressed: nothing persists a failed state, refuses later updates or re-sends the error on reconnect. Partial: errors are now scoped to their channel id (699c67b); `ChannelWarningException.CloseConnection` gives "warning + close" where BOLT allows it (00095cb); `MessageService` dispose moved off the read loop (4961ba5). Remaining: no failed state; other `ChannelErrorException`s on Open channels (e.g. `ChannelReadyMessageHandler`) still make the peer force-close while we keep the channel (BOLT2 plan G21). Update (ABCD wave 0, `0b7e617`): contracts in place: `ChannelState.Failed = 35` (between Closing and Closed), `ChannelFailedException` (FailedChannelId, MustBroadcast, RequirementId; PeerMessage defaults to null so no local text leaks), `Channels.ErrorSent`/`DataLossDetected` columns (4472a8b). Persisting Failed + ErrorSent, sending the error, refusing updates and re-sending on reconnect remain (N6-T3, ABCD W1-A). Update (ABCD wave 1, `342d22e`): a handler's `ChannelFailedException` makes `ChannelManager` persist `ChannelState.Failed` and the serialized error (`MarkErrorSent`) under the lock before `PeerManager` sends it and disconnects; every later message on the channel is answered with the error again, Failed channels stay in memory at startup (a604dff), and every `IChannelOperations` call on a Failed channel is refused with nothing persisted (a02afa7). Remaining: re-send `ErrorSent` when the peer reconnects (B2-RE-05) and error without disconnect (PeerManager, W2-A); the broadcast / fail-the-channel service (N9-T4). Update (ABCD wave 2, `a5675cb`): a Failed channel's stored error is re-sent on every connection, and any message on it gets the error again without a second persist. `PeerManager` turns every `ChannelFailedException` into an error that keeps the connection, through `PeerOutbox.TryEnqueueError` and `IPeerService.SendErrorAsync` (4ec83d3). Other `ChannelErrorException`s still disconnect. The fail-the-channel broadcast (N9-T4) is tracked in NL-094. `SendErrorAsync` has no unit test (NL-261).
- **Fix sketch:** `ChannelFailedException`, `ChannelState.Failed = 35`, persisted error bytes, error retransmission; broadcast later via a single ChannelFailureService.
- **Blocks/Blocked-by:** Part of NL-031; related NL-094
- **Plan ref:** BOLT2 N6-T3, N9-T4

### NL-201 Startup connects before registering channels, and registration is fire-and-forget
- **Status:** fixed (424ae84, d39ca18, 9c057f2)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:63-84`
- **Evidence:** `StartAsync` connects to each peer first and only then calls `_ = _channelManager.RegisterExistingChannelAsync(channel)` without awaiting; a peer's immediate `channel_reestablish` (LND sends it after init) can arrive for a channel that isn't registered yet. Update: since NL-052 (753cbd9) registration happens before the connect attempt, but `RegisterExistingChannelAsync` is still not awaited. Fixed: startup awaits registration (memory + signer) of every non-Closed/Stale channel before connecting; unreachable peers with active channels are retried with backoff (5 s doubling to 10 min) (424ae84), and so are peers with active channels that drop at runtime (9c057f2). Marking channels as awaiting reestablish is N7 (NL-035).
- **Fix sketch:** Load and await registration (incl. signer) for every non-Closed channel before connecting.
- **Blocks/Blocked-by:** Related NL-052, NL-035
- **Plan ref:** BOLT2 N1-T6

### NL-204 Incoming open_channel still required upfront_shutdown_script whenever channel_type was present
- **Status:** fixed (b08c494)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs` (`CreateChannelV1AsNonInitiatorAsync`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 2. NL-046 fixed only the accept_channel side; a peer sending channel_type without the TLV (allowed when the option is not negotiated) was rejected with an all-zero error. LND always sends the TLV, so LND opens were unaffected.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-046
- **Plan ref:** —

### NL-217 Outgoing open_channel sets announce_channel when scid_alias is negotiated Compulsory
- **Status:** fixed (2aba47d)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs:127-128`
- **Evidence:** `if (peer.NegotiatedFeatures.ScidAlias == FeatureSupport.Compulsory) channelFlags = AnnounceChannel`; BOLT 2 requires announce_channel = 0 when option_scid_alias is in channel_type. Unreachable by default (ScidAlias defaults to No). Reported by the features batch, verified in code. Fixed: `OpenChannelClientHandler` never sets announce_channel together with option_scid_alias. Public-channel policy: NL-236.
- **Fix sketch:** Never announce when scid_alias is in channel_type; decide announce from config.
- **Blocks/Blocked-by:** Related NL-103
- **Plan ref:** —

### NL-218 accept_channel builds its own channel_type instead of echoing the opener's
- **Status:** fixed (2aba47d)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs:70-84`
- **Evidence:** Uses `FeatureSet.NewBasicChannelType()`; BOLT 2 says an acceptor that sets channel_type MUST set it to the open_channel value (or fail). Harmless while we only accept the basic type. Reported by the features batch, verified in code. Fixed: accept_channel echoes the opener's channel_type bytes; `ChannelOpenValidator` refuses bits other than 12/22/46/50 and scid_alias when not negotiated; `AcceptChannel1MessageHandler` fails a channel_type different from ours.
- **Fix sketch:** Validate the opener's channel_type against what we support and echo it.
- **Blocks/Blocked-by:** Related NL-112
- **Plan ref:** BOLT2 N11 (anchors)

### NL-219 Interactive-tx input/output caps can be bypassed; input uniqueness compares raw prevtx bytes
- **Status:** fixed (081d599f)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs`, `TxAddOutputValidator.cs`, `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs` (`IsUniqueInput`)
- **Evidence:** BOLT 2 caps tx_add_input/tx_add_output messages received per negotiation (4096), but the validators count the inputs/outputs currently held, so remove + re-add gets around it; uniqueness compares prevtx bytes, not txid. Reported by the interactive-tx batch (unverified). Fixed in wave qit (lane IT-A): `InteractiveTxSession` counts received `tx_add_input`/`tx_add_output` messages per negotiation (removals do not lower the count) and compares inputs by outpoint; the old validators and service are deleted.
- **Fix sketch:** Count received messages per negotiation; compare by (txid, vout).
- **Blocks/Blocked-by:** Part of NL-037; related NL-041
- **Plan ref:** —

### NL-220 open_channel receiver has no rule for "both initial outputs <= channel_reserve"
- **Status:** fixed (e053fb8)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs`
- **Evidence:** BOLT 2 open_channel receiver MUST fail if both to_local and to_remote of the initial commitment are <= channel_reserve_satoshis. Today it is only rejected by accident, through the commitment factory throw that NL-196 removes. Reported by the open-validation batch. Fixed: `ChannelOpenValidator` rejects an open_channel whose initial to_local and to_remote are both <= channel_reserve_satoshis.
- **Fix sketch:** Add the check to the validator before fixing NL-196.
- **Blocks/Blocked-by:** Related NL-196
- **Plan ref:** —

### NL-221 A failed accept_channel leaves the channel registered with the signer
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs` (catch block), `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs`
- **Evidence:** The NL-047 cleanup removes the channel and releases UTXOs but not `RegisterChannel` state (pre-existing; noted by the channel-lifecycle batch).
- **Fix sketch:** Unregister from the signer in the cleanup path.
- **Blocks/Blocked-by:** Related NL-047, NL-067
- **Plan ref:** —

### NL-227 open_channel carried funding minus push as funding_satoshis
- **Status:** fixed (0958f18)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`
- **Evidence:** `funding_satoshis` was set from the opener's balance (funding − push), so for any channel with `push_msat` LND built a smaller funding output and rejected our commitment signature ("counterparty's commitment signature is invalid"). Found by the Proof-N1 Docker test. Fixed: open_channel sends `request.FundingAmount`; Daemon regression test `Given_PushAmount_When_HandleAsync_Then_OpenChannelCarriesTheWholeFundingAmount`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 Proof N1

### NL-234 IChannelManager.HandleChannelMessageAsync both sends and returns the replies
- **Status:** fixed (4ec83d3)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Interfaces/IChannelManager.cs`, `src/NLightning.Application/Channels/Managers/ChannelManager.cs`
- **Evidence:** It raises every reply through `OnResponseMessageReady` under the channel lock and also returns them as `Task<IReadOnlyList<IChannelMessage>>`. Documented, but a new caller that sends the returned list would send each reply twice (reported by the N0-T3 lane). Update (ABCD wave 1, `342d22e`): not changed. Replies reach the peer only through `OnResponseMessageReady`; `PeerManager.cs:670` ignores the returned list, so nothing is sent twice today. The signature change belongs to the owner of `IChannelManager` (W2-A); `PeerManager.cs` was owned by W1-A in wave 1, not W1-E. Update (ABCD wave 2, `a5675cb`): `HandleChannelMessageAsync` returns `Task`; replies go out only through `OnResponseMessageReady` (4ec83d3).
- **Fix sketch:** Return only a status/count, or stop raising and let the single caller enqueue.
- **Blocks/Blocked-by:** Related NL-193
- **Plan ref:** BOLT2 N6-T1

### NL-235 Block events keyed by the real channel id are not serialized against the handler that creates the channel
- **Status:** fixed (a604dff)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs`
- **Evidence:** A channel is locked by its temporary id until funding_created/funding_signed and by its real id afterwards, so a block event for the real id can run while the handler that is creating the channel still holds only the temporary-id lock. Harmless today because the channel is not in memory until that handler adds it (reported by the N0-T3 lane). Update (ABCD wave 1, `342d22e`): funding_created now runs under both the temporary-id lock and the real channel id lock (computed with `IChannelIdFactory`, temporary first); this is the only place two channel locks are held, and it is documented (`ChannelManagerNormalOperationTests.Given_FundingCreated_When_Handled_Then_TheRealChannelIdIsLockedToo`).
- **Fix sketch:** Take both locks in a fixed order during the id switch, or add the channel to memory only after releasing under the real-id lock.
- **Blocks/Blocked-by:** Related NL-033
- **Plan ref:** BOLT2 N5-T2, N6-T1

### NL-236 No public-channel policy: scid_alias is put in channel_type whenever negotiated and announce_channel is never set
- **Status:** fixed (4115b34)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/ValueObjects/ChannelParams.cs` (`ToChannelType`), `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`
- **Evidence:** Since NL-217/NL-218 the initiator uses option_scid_alias in the channel_type whenever the peer negotiated it (`UseScidAlias` Compulsory) and never announces. There is no way to request a public (announced) channel without scid_alias (reported by the N1-T4 lane). Update (gossip wave G-B, `5bbfbb5`): `openchannel <node> <sats> [push_sats] --public` (IPC key 4) opens a public channel: `announce_channel` set and option_scid_alias left out of its channel_type (`UseScidAlias` Optional when negotiated), public + zero-conf refused, refused on mainnet unless `Gossip:AllowPublicChannelsOnMainnet` (D12) (4115b34). Routing through the real SCID of such a channel is NL-348.
- **Fix sketch:** Add an open-channel option (public/private) that drops scid_alias from the type and sets announce_channel; design with BOLT 7 announcements.
- **Blocks/Blocked-by:** Related NL-099, NL-217
- **Plan ref:** —

### NL-246 Channels that got channel_ready before ABCD wave 1 have no commitment snapshot and can never carry HTLCs
- **Status:** wontfix
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`, `Channels/Managers/ChannelManager.cs` (`RegisterExistingChannelAsync`)
- **Evidence:** The first snapshot is built at the first channel_ready (NL-232, a604dff). Channels that received channel_ready earlier had the peer's commitment-0 per-commitment point overwritten, so no snapshot can be built: they are logged at startup and HTLC messages on them get a warning (reported by W1-A). Update (ABCD wave 3, `c92d837`): cannot be fixed (813fc85): the first snapshot needs the peer's commitment-0 per-commitment point, which that channel_ready replaced, and BOLT 2 makes the receiver ignore `my_current_per_commitment_point` in channel_reestablish, so nothing recovers it. Such channels keep working without HTLCs; close them with `closechannel` (N10) and reopen. Never build a snapshot from an unverified point.
- **Fix sketch:** Close and reopen such channels (no automated path); or recover commitment 0's point via reestablish (`my_current_per_commitment_point` is not sent for commitment 0), so closing is the practical answer once N10 exists.
- **Blocks/Blocked-by:** Related NL-232, NL-034
- **Plan ref:** BOLT2 N6-T1

### NL-251 Ping-before-commit only checks that the peer is connected
- **Status:** fixed (d7f09a9, c84f81a)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ConnectedPeerLivenessProbe.cs`, `CommitScheduler.cs`
- **Evidence:** BOLT 2 says to send a ping before commitment_signed when the peer has been quiet (B2-CS-S05). `IPeerLivenessProbe`'s default implementation only checks that the channel's pinned connection is still the peer's current one (a02afa7, e5f7312); there is no last-message timestamp or ping API on `IPeerService` (reported by W1-A). Update (ABCD wave 3, `c92d837`): `IPeerService` exposes `LastMessageReceivedAt` and `PingAsync`; the commit scheduler asks `IPingBeforeCommit` outside the lock and pings a quiet peer, awaiting its pong before signing (B2-CS-S05). The ping handler is subscribed before the ping loop is marked started (c84f81a).
- **Fix sketch:** Expose `IPeerService.LastMessageReceivedAt` (or a ping-and-wait API) and ping when it is older than a threshold; swap the probe with `services.Replace` or register it before `AddApplicationServices` (TryAdd).
- **Blocks/Blocked-by:** Related NL-031
- **Plan ref:** BOLT2 N6-T2

### NL-252 Reestablish seam: channel links are marked up only at Open, so channels loaded at startup never send updates and pending events are not replayed after reconnect
- **Status:** fixed (4ec83d3, d1476a4, 22c29ae)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ConnectedPeerLivenessProbe.cs` (`MarkLinkUp`), `Channels/Managers/ChannelManager.cs` (`QueuePendingDomainEventsAsync`, `RaiseDomainEventsAsync`), `ChannelOperationsService.cs`
- **Evidence:** Since e5f7312 a channel's link is pinned to the connection it turned Open on, and every `IChannelOperations` call and every commitment_signed needs that link, so no signature can cover an update the peer never received. A channel loaded at startup or after a reconnect is never marked, so its startup replay is refused (nothing persisted) and locked-in HTLCs stay unresolved until N7. An unsigned update enqueued just as its connection closes stays `SentRemoveHtlc` (the link stays down for good). A `ReadyForThem` channel loaded from the DB that turns Open on funding confirmation after a reconnect is pinned without reestablish (reported by W1-A). Update (ABCD wave 2, `a5675cb`): after each reestablish, `ChannelManager` calls `MarkLinkUp`, queues the pending events for the switch after the lock, and schedules a commit (4ec83d3). `LinkUpReplayingPeerLivenessProbe` also replays a channel's pending events on every `MarkLinkUp` (d1476a4). Both paths run, which is harmless but redundant (NL-264). Channels past funding_signed send their reestablish at connect (22c29ae).
- **Fix sketch:** In N7: after channel_reestablish call `IPeerLivenessProbe.MarkLinkUp(channelId, peer)`, retransmit or forget our unsigned updates and the stored `SentCommitDiff` per BOLT 2, then replay pending domain events (`QueuePendingDomainEventsAsync` + `RaiseDomainEventsAsync`).
- **Blocks/Blocked-by:** Part of NL-035; related NL-031
- **Plan ref:** BOLT2 N7-T3; ABCD W2-A

### NL-254 No node option for the dust-exposure policy
- **Status:** fixed (1dbbc1f, 525973a)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/NodeOptions.cs`, `CommitmentParams.FromChannel` callers
- **Evidence:** The snapshot stores and reloads `MaxDustHtlcExposureMsat` (NL-242), but no option sets it, so the first snapshot is created with none and the dust-exposure check (BOLT 2 `max_dust_htlc_exposure_msat`) never runs (reported by W1-A). Update (ABCD wave 3, `c92d837`): `NodeOptions.MaxDustHtlcExposureMsat` is stored with the first snapshot (`CreateInitialCommitments`); `DustExposureHtlcSwitch` fails incoming dust HTLCs over the limit before forward or preimage (B2-DUST-01/02); `FeeUpdatePolicy` checks the dust limit on a non-anchor fee increase (B2-DUST-05). Snapshots created earlier have no stored limit: NL-290.
- **Fix sketch:** Add a node option (e.g. `Node:MaxDustHtlcExposureMsat`, or feerate-scaled like LND) and pass it to `CommitmentParams.FromChannel` when creating the first snapshot.
- **Blocks/Blocked-by:** Related NL-242
- **Plan ref:** BOLT2 N9-T3

### NL-256 The engine raised no event when an incoming HTLC's removal became irrevocable
- **Status:** fixed (d1476a4)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Commitments/` (`ReceiveCommit`, `ChannelDomainEvents.DerivePending`), `Payments/Switch/HtlcSwitch.cs`
- **Evidence:** Without it, invoices were marked Settled when the fulfill was persisted rather than when it was irrevocable, and archived incoming rows could not be pruned (reported by W2-B). Fixed: the new Domain event `IncomingHtlcSettled` is raised when our removal is final (state 39) and derived by `DerivePending`. The switch prunes the row; simulator invariants (500 seeds, 10k Long) are green (d1476a4).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-243, NL-253
- **Plan ref:** ONION M4-T7; BOLT2 N4-T4

### NL-258 The funder's funding transaction is never rebroadcast
- **Status:** fixed (f251dde, 5bedf44, a9e33a7)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`PublishAndWatchTransactionAsync`), `Application/Channels/Handlers/FundingSignedMessageHandler.cs`
- **Evidence:** The watch is saved before `SendTransactionAsync`, so a crash or a failed publish leaves the channel V1FundingSigned, waiting for a tx that never went out. Startup (NL-036) treats the watch as proof of broadcast (reported by W2-A, review F3). Update (ABCD wave 4, `6b5d50e`): `FundingSignedMessageHandler` saves V1FundingSigned, the funding watch, the signed funding tx (a `BroadcastTransactions` row, purpose Funding) and the funding-output watch in one save, then publishes; every Pending row is sent again after each processing round and at start, also while processing is halted, until a block holds it (IT `ChainMonitorPersistenceTests`: rebroadcast until mined, published at startup). A tx the node refuses for good is retried forever: NL-294.
- **Fix sketch:** Add a publish-only `IBlockchainMonitor` method and rebroadcast unconfirmed funder V1FundingSigned channels at startup (rebuild from the locked UTXOs as `FundingSignedMessageHandler` does), or store the signed raw tx with the watch (migration).
- **Blocks/Blocked-by:** Related NL-048, NL-036
- **Plan ref:** BOLT2 N7-T5

### NL-259 A funder channel forgotten at startup keeps its UTXO locks
- **Status:** fixed (f365bc13)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`ResumeStartupStateAsync`), `UtxoModel.LockedToChannelId`
- **Evidence:** A V1FundingCreated channel with no watch is persisted Stale and not registered (82c4c37), but nothing calls `ReturnUtxosNotSpentOnChannel` or persists the unlock, so those wallet UTXOs stay locked (reported by W2-A). Update (ABCD wave 4, `6b5d50e`): still open. A funding tx the node refuses for good is now retried every block with a periodic Warning, but never abandoned, so its UTXOs stay locked too (NL-294). Update (wave lh1, `a6c633f9`): fixed. The UTXO channel locks live only in memory (NL-462); they are released on three paths: a failed `funding_signed` (bad signature, rebuilt funding mismatch, signing error) calls `ReturnUtxosNotSpentOnChannel` before rethrowing; `ChannelManager.ResumeInterruptedFundingAsync` marks the channel's pending Funding/Unspecified `BroadcastTransactions` row Abandoned in the Stale save and then releases the locks; the chain monitor releases a funding's locks when it abandons it (NL-294). Proofs: `StartupStateTests`, `FunderRememberRuleTests` (lane l1). Follow-up: a funder channel whose funding the monitor abandoned stays V1FundingSigned (NL-461).
- **Fix sketch:** Release the channel's UTXO locks in the same save that marks it Stale.
- **Blocks/Blocked-by:** Related NL-036
- **Plan ref:** BOLT2 N7-T5

### NL-260 channel_ready retransmitted at reestablish carries only the first local alias
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Reestablish/` (retransmission), `FundingConfirmedMessageHandler.cs`
- **Evidence:** `FundingConfirmedMessageHandler` sends one channel_ready per alias; the reestablish retransmission sends one with the first local alias (or the real scid) (reported by W2-A). Only matters with option_scid_alias, which defaults to No.
- **Fix sketch:** Retransmit one channel_ready per local alias.
- **Blocks/Blocked-by:** Related NL-103
- **Plan ref:** BOLT2 N7-T3

### NL-264 Pending HTLC events are replayed twice after a reestablish
- **Status:** fixed (82b7dcc)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`CompleteReestablishAsync`), `Payments/Switch/LinkUpReplayingPeerLivenessProbe.cs`
- **Evidence:** `CompleteReestablishAsync` queues the pending events itself and also calls `MarkLinkUp`. The W2-B probe decorator turns that call into a second replay. The switch is idempotent, so nothing breaks, but every reconnect does the work twice (reported by the integrator). Update (ABCD wave 3, `c92d837`): the link-up probe leaves the replay to the channel manager when the tracker says the channel was just reestablished, so pending events are replayed once.
- **Fix sketch:** Keep one path (the probe replay) and drop the manager's own queueing, or have the probe skip channels the manager just replayed.
- **Blocks/Blocked-by:** Related NL-252
- **Plan ref:** BOLT2 N7-T2; ABCD W2-A/W2-B

### NL-269 A channel that turned Open on this connection may send an update before LND finishes its reestablish sync (unverified)
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs`, `Reestablish/ReestablishTracker`
- **Evidence:** A channel opened on this connection counts as reestablished, so an update can go out before LND's link has processed its own channel_reestablish; LND would then see update_add_htlc as its first sync message. Not reproduced; no Docker proof covers a reconnect before channel_ready (reported by W2-A).
- **Fix sketch:** Add a Docker proof with a reconnect before channel_ready; if LND objects, hold updates until the peer's reestablish arrives.
- **Blocks/Blocked-by:** Related NL-035
- **Plan ref:** BOLT2 N7-T2

### NL-273 PeerChannelErrorSender bypasses the PeerOutbox ordering
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Safety/` (`PeerChannelErrorSender`), `IPeerService.SendErrorAsync`
- **Evidence:** The fail-the-channel `error` is sent through `IPeerService.SendErrorAsync`, not enqueued on the peer's `PeerOutbox`, so it can overtake queued replies. Harmless for a Failed channel (nothing else is sent for it) (reported by W3-A).
- **Fix sketch:** Add a `PeerManager` API that enqueues a channel error on the outbox and use it here.
- **Blocks/Blocked-by:** Related NL-033, NL-261
- **Plan ref:** BOLT2 N9-T4

### NL-274 HtlcExpiryMonitor treats an invoice settled by another HTLC as preimage-known
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Safety/HtlcExpiryMonitor.cs` (incoming resolution)
- **Evidence:** The monitor derives "preimage known" from the invoice status. An invoice settled by another HTLC of the same payment hash makes an unrelated incoming HTLC look fulfillable, so the channel is failed at cltv_expiry - 18 instead of the HTLC being failed back. A residual race with a switch forward exists only if the switch read a height more than cltv_expiry_delta + ExpiryTooSoonBlocks blocks old (reported by W3-A).
- **Fix sketch:** Resolve per HTLC (stored preimage or origin), not per invoice.
- **Blocks/Blocked-by:** Related NL-094
- **Plan ref:** BOLT2 N9-T2

### NL-277 MessageFactory.CreateClosingSignedMessage uses msat for fee_satoshis and always adds fee_range
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` (`CreateClosingSignedMessage`)
- **Evidence:** The fee is passed as a `ulong` that converts implicitly to msat, so it is 1000x too small, and a `fee_range` is always added. Unused today: `ChannelCloseCoordinator` builds closing_signed directly (reported by W3-B).
- **Fix sketch:** Take a `LightningMoney` (satoshis) and an optional range, or delete the method.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T3

### NL-278 A mutual close the peer broadcast before our final closing_signed was not detected
- **Status:** fixed (8e0e154)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`HandleFundingSpentAsync`), `IBlockchainMonitor.WatchOutpointSpend`
- **Evidence:** If the peer broadcast a closing tx we signed and the link dropped before our final closing_signed, we stayed Negotiating forever (reported by W3-B). Fixed: from the first shutdown the funding outpoint is watched for a spend; a spend shaped like a mutual close to the two shutdown scripts becomes the closing tx (Closing + watch in one save, then Closed at depth).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N10-T3

### NL-279 HTLCs added to us after our shutdown are not failed back (B2-SHUT-S08)
- **Status:** fixed (79ea3f92, 5aef7305)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`
- **Evidence:** BOLT 2: after sending shutdown a node MUST fail to route any HTLC added after it. The switch forwards or accepts such an HTLC like any other; only our own adds are refused after shutdown (B2-ADD-S13) (reported by W3-B). Update (wave lh1, `a6c633f9`): fixed. `ChannelCloseCoordinator.SendShutdownAsync` records the peer's next HTLC id as `ChannelModel.FirstRemoteHtlcIdAfterLocalShutdown` in the shutdown's save (column `Channels.FirstRemoteHtlcIdAfterLocalShutdown`, migration `AddShutdownHtlcBoundaryAndAddressReservation`); `HtlcSwitch.HandleLockedInAsync` fails a locked-in incoming HTLC at or past that id back with `temporary_node_failure` after the peel (secret recorded first, blinded rules through `FailBackAsync`), so it is neither forwarded nor accepted (79ea3f92). Review fixes (5aef7305): no fail-back on a Failed/OnchainResolving channel, where a final-hop HTLC is claimed on chain (NL-316); `ChannelManager.RevertUncommittedAsync` lowers and saves the boundary when a reconnection drops the peer's uncommitted adds. A shutdown saved before the migration (null boundary) keeps the old behaviour. Proofs: `Payments/Switch/ShutdownFailBackTests` (ThreeNodeHarness, SQLite restart), `Channels/Harness/ShutdownBoundaryRevertTests` (lane l1).
- **Fix sketch:** Fail an incoming HTLC on a ShuttingDown channel whose add came after our shutdown, before forwarding or final-hop accept.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T3, B2-SHUT-S08

### NL-282 ChannelCloseCoordinator mutates the in-memory ChannelModel before its save
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Close/ChannelCloseCoordinator.cs`
- **Evidence:** Scripts and state are set on the in-memory model before `SaveChangesAsync`; a failed save leaves memory ahead of the database until a restart (reported by W3-B). Update (ABCD wave 5, `1a5ab49`): the same class exists in `OnchainChannelWatcher.PersistAsync` (NL-307); `OnchainResolutionExecutor` stages Closed on the database copy and changes the shared model only after the save (cbd99c6).
- **Fix sketch:** Apply to a copy and swap after the save, as the commitment engine does.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T3

### NL-284 closing_signed reply timeout and no-overlap fee_range timeout are missing (B2-CLS-03, B2-CLS-R04)
- **Status:** fixed (d8680cd, 8249044)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Close/`
- **Evidence:** BOLT 2: the funder MUST fail the channel if it gets no closing_signed reply in time, and a node that sees no fee_range overlap MUST fail the channel if the peer does not send a new range after a reasonable time; we only warn. Now possible through `IChannelFailureService` (N9-T4) (reported by W3-B). Update (ABCD wave 4, `6b5d50e`): `ClosingTimeoutMonitor` fails the channel through `IChannelFailureService` when our `closing_signed` goes unanswered (`Node:Close:ClosingSignedReplyTimeout`, 5 min, counted only while the peer is on the pinned link) or no overlapping `fee_range` follows (`FeeRangeTimeout`, 10 min), with a `StillApplies` precondition checked under the lock. Memory only: a restart restarts the negotiation.
- **Fix sketch:** Timer per negotiation; on expiry call `IChannelFailureService.FailChannelAsync`.
- **Blocks/Blocked-by:** Related NL-034, NL-094
- **Plan ref:** BOLT2 N10-T3

### NL-285 Closing negotiation holds our fee at our limit instead of proposing strictly between (B2-CLS-R09 deviation)
- **Status:** open (partial: 8096700, b67e065)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Closing/LegacyClosingNegotiator.cs`
- **Evidence:** LND 0.20 sends no fee_range and lowers its fee 10 % per closing_signed from its own estimate (4225 sat vs our funder limit 513 sat), so the old R09 rule failed the channel on LND's second message. We now re-send our limit, but only to a peer that already moved strictly towards us (R07); otherwise warning + close, and after `MaxRounds` (100) warning + close (9733937, 5d0aafc). Takes about 19 rounds against LND. A strict peer (CLN/Eclair legacy) may fail over a repeated fee (reported by W3-B). Update (ABCD wave 4, `6b5d50e`): the `fee_range` receive path is proven against CLN in both roles (`ClnCloseTests`, R03/R05/R06), so the repeated-fee path only runs with a peer that sends no `fee_range` (LND 0.20). The integrator listed this as fixed; the ledger keeps it open because the R09 deviation itself is unchanged (option_simple_close, NL-020, is the real fix). Update (ABCD wave 6, `3ce3cad`): `option_simple_close` (NL-020) removes the negotiation with peers that support it, but `OptionSimpleClose` defaults to No, so the legacy close (and this deviation) is still the default path.
- **Fix sketch:** A better funder limit (e.g. the peer's first offer capped by the commitment fee), or option_simple_close (NL-020).
- **Blocks/Blocked-by:** Related NL-034, NL-020
- **Plan ref:** BOLT2 N10-T3, B2-CLS-R09

### NL-287 A retransmitted channel_ready in ShuttingDown/Negotiating/Closing disconnected the peer
- **Status:** fixed (8e0e154)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`, `ChannelReestablishMessageHandler`
- **Evidence:** `ChannelReadyMessageHandler` threw `ChannelErrorException` ('Unexpected ChannelReady') when the peer retransmitted channel_ready in a closing state, which LND does per spec (B2-RE-15); and we did not retransmit ours in those states (reported by W3-B). Fixed: ignored in those states, and the reestablish retransmits ours; harness test `Given_IdleChannelShuttingDown_When_Reconnect_Then_ChannelReadyAndShutdownRetransmitted`.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-036
- **Plan ref:** BOLT2 N10-T3, B2-RE-15

### NL-288 Fee estimate multiplier is sat/kvB, not sat/kw: our feerates are 4x too high
- **Status:** fixed (3350020, 803df11, 1036dfe, 960cf05)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Options/FeeEstimationOptions.cs` (`RateMultiplier` "1000"), `Services/FeeService.cs:162`, `NodeConfigurationExtensions.cs:239`
- **Evidence:** The estimate in sat/vB is multiplied by 1000 (sat/kvB) and used as sat/kw, so 10 sat/vB becomes 10,000 sat/kw (should be 2,500). Our default open to CLN is refused above its 2530 sat/kw limit (Explicit reproducer `ClnInteropTests.Given_OurDefaultFeerate_When_OpeningToCln_Then_ClnAccepts`), and as fundee we refuse LND's open_channel ('Fee rate per kw is too small: 6250, currentFee 10000'; `FeeUpdateFlowTests` LND-funded case skipped, 533330f) (reported by W3-C, W3-E). Update (ABCD wave 4, `6b5d50e`): `FeeRateConverter` converts sat/vB × 250 to sat/kw with a 253 sat/kw floor (`RateMultiplier` ignored with a warning); one shared started `FeeService` for every consumer (`AddFeeServices`, 803df11, registered in 960cf05) that never reports 0 (`FallbackFeeRatePerKw`); the fee source is configurable (`Http`/`Bitcoind`/`Fixed`). The CLN reproducer is a regular test and the LND-funded `FeeUpdateFlowTests` case runs (1036dfe).
- **Fix sketch:** Convert sat/vB to sat/kw (x 250) in one place; drop the Explicit/Skip markers and the explicit test feerates.
- **Blocks/Blocked-by:** Related NL-289
- **Plan ref:** BOLT2 N9-T1

### NL-289 As fundee we refuse a feerate below 80 % of our own estimate
- **Status:** fixed (1036dfe)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:99`, `IFeeService`
- **Evidence:** A CLN-funded channel at CLN's own estimate (253 sat/kw on an idle regtest) is refused (Explicit reproducer `ClnInteropTests.Given_ClnFundsAtItsOwnEstimate_When_Opening_Then_WeAccept`). BOLT 2 only asks to fail an unreasonably low feerate; LND and CLN accept down to the relay floor. Made worse by NL-288 (reported by W3-E). Update (ABCD wave 4, `6b5d50e`): as fundee we accept any `open_channel` feerate from the 253 sat/kw floor; `ClnInteropTests.Given_ClnFundsAtItsOwnEstimate_When_Opening_Then_WeAccept` is a regular test.
- **Fix sketch:** Accept anything from the 253 sat/kw floor (or a configurable lower bound) upwards.
- **Blocks/Blocked-by:** Related NL-288
- **Plan ref:** BOLT2 N9-T1, B2-FEE-R01

### NL-290 Snapshots without a stored dust limit offer unlimited dust on the send side
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Fees/DustExposurePolicy.cs`, engine `UpdateValidator`
- **Evidence:** The engine's send rules B2-DUST-03/04 read `CommitmentParams.MaxDustHtlcExposureMsat` from the snapshot; channels whose first snapshot predates wave 3 have none, and the `NodeOptions` fallback covers only the receive and fee checks (reported by W3-C, 9bfa09d).
- **Fix sketch:** Backfill the stored limit from `NodeOptions` at load (a migration or a one-time save).
- **Blocks/Blocked-by:** Related NL-254
- **Plan ref:** BOLT2 N9-T3

---

### NL-392 Temporary channels of failed or abandoned opens are never removed from ChannelMemoryRepository
- **Status:** fixed (bdfc90c0, c74d3173)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Memory/ChannelMemoryRepository.cs`, `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`, the accept path
- **Evidence:** A temporary channel stays in memory after its open fails or the peer abandons it (predates wave O7b; a slow leak). Since NL-379 the anchors reserve counts an admitted open while its temporary channel is stored, capped by `Node:Anchors:PendingOpenTimeout` (10 min), so the leak cannot hold the reserve forever (reported by lane Y1). Update (wave rf1, `wip/fafo` at `be9fd000`): temporary channels of failed, timed-out and disconnected opens are forgotten, the failed-open cleanup runs under the temporary channel's lock (lane R4).
- **Fix sketch:** Remove the temporary channel when an open fails, is refused, or times out (funding_created/funding_signed never arrives), and on disconnect before funding.
- **Blocks/Blocked-by:** Related NL-379
- **Plan ref:** BOLT2 N0

### NL-393 A funding lock that fails for too few funds (not the reserve) is not answered as not_enough_balance
- **Status:** fixed (bdfc90c0, c74d3173)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Memory/UtxoMemoryRepository.cs` (`LockUtxosToSpendOnChannel`), `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`
- **Evidence:** When the funding selection itself finds too few UTXOs it throws `InvalidOperationException`, which `OpenChannelClientHandler` does not map to `not_enough_balance` (only `AnchorReserveException`/the pre-check are); the pre-check usually catches the case first, so it shows only in races (predates wave O7b; reported by lane Y1). Update (wave rf1, `wip/fafo` at `be9fd000`): a funding lock that fails for too few funds is answered as `not_enough_balance`; only the lock's own failure is mapped (lane R4).
- **Fix sketch:** Throw `InsufficientFundsException` from the selection and map it to `not_enough_balance`.
- **Blocks/Blocked-by:** Related NL-379
- **Plan ref:** —

### NL-426 [EPIC] Static channel backup and restore
- **Status:** fixed (781fe96a, 38742d78, 6fb5326c, 958304ad, aa9d67e0)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Backup/`, `src/NLightning.Daemon/Handlers/{ExportChanBackup,VerifyChanBackup,RestoreChanBackup}ClientHandler.cs`
- **Evidence:** Wave rf1, lane R1 (commits cite NL-417, which is the D12 relay entry; this is the backup/restore ID). Versioned, XChaCha20-Poly1305 encrypted SCB (key from HKDF of the node key, no private keys stored), atomic `channel.backup` written by `ChannelBackupMonitor` (a file with channels missing from the database is moved aside as `.superseded`, the directory is fsynced), IPC `exportchanbackup` (21), `verifychanbackup` (22), `restorechanbackup` (23). Restore writes recovery channels (Failed + DataLossDetected, no snapshot), reserves the restored key indexes, sends the data-loss `channel_reestablish` (next_commitment_number 0) so the peer force-closes, never signs or broadcasts our commitment, finds a funding spend mined before the restore within `Node:Backup:RestoreSpendSearchDepth` (4032), and `RemoteCommitResolver` sweeps to_remote. The integrator wired `AddChannelBackupNodeServices`/`AddChannelBackupFile` and fixed the reserver loop (NL-427). Docker `BackupRestoreFlowTests` (anchors and legacy, LND force close after or before the restore) 4/4. A restore disconnects an already connected peer first, which interrupts its other live channels. Follow-ups NL-430, NL-431, NL-435. Update (wave lh1, `a6c633f9`): follow-ups NL-430 (old funding spends searched in the background and resumed at start) and NL-431 (every known peer address tried) fixed (098eb58b, cfd71631), peer-storage retrievals persisted and listed (NL-432, bd054cae); NL-435 remains.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-010, NL-035
- **Plan ref:** —

### NL-430 Restore only reports a funding spend older than RestoreSpendSearchDepth or on a backup without a SCID
- **Status:** fixed (098eb58b, cfd71631)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Backup/ChannelRestoreService.cs`, `ChainFundingSpendLocator`
- **Evidence:** A peer force close mined more than `Node:Backup:RestoreSpendSearchDepth` (4032) blocks before the restore, or a backup entry without a SCID, yields `FundingAlreadySpent: ... rescan from height N` and an error log; our to_remote is not swept automatically (reported by lane R1 review). Update (wave lh1, `a6c633f9`): fixed. The spend locator reads blocks in batches (`Node:Backup:RestoreSpendSearchBatchSize`) and uses the backup's funding height as the floor for an entry without a SCID; a spend older than `RestoreSpendSearchDepth` is searched in the background (`IFundingSpendLocator.RescanAsync`) down to the funding block and handed to the on-chain watcher; re-running `restorechanbackup` re-checks waiting recovery channels (098eb58b). Review fixes (cfd71631): `IChannelRestoreService.ResumeSpendSearches` (called by the daemon's `ChannelBackupHostedService`) resumes the search at every start; a block bitcoind pruned ends it as `BlocksPruned` with a sweep-by-hand detail. Docker `BackupRestoreFlowTests` gains the old-spend case (lane l4).
- **Fix sketch:** Add an operator rescan-from-height (or search from `ChannelBackupEntry.FundingHeight`) and resolve the spend through `IOnchainChannelWatcher`.
- **Blocks/Blocked-by:** Part of NL-426
- **Plan ref:** —

### NL-431 Restore reconnects only through the backup's single peer address
- **Status:** fixed (098eb58b, cfd71631)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Backup/ChannelBackupCodec.cs`, `ChannelRestoreService.cs`
- **Evidence:** The backup holds one host:port per peer from the peers table; node_announcement addresses from the gossip graph are neither backed up nor tried at restore time, so a peer whose address changed reconnects only if it connects to us (reported by lane R1). Update (wave lh1, `a6c633f9`): fixed. Backups carry the peer's announced addresses from the gossip graph; the restore tries the graph addresses, the peer row and every backup address, then keeps retrying with backoff in the background (098eb58b); only the first address is tried synchronously and the next ones within `ConnectBudget` (20 s), the rest go to the background loop (cfd71631) (lane l4).
- **Fix sketch:** At restore, also try the peer's addresses from the graph; optionally store several addresses in the backup.
- **Blocks/Blocked-by:** Part of NL-426; related NL-099
- **Plan ref:** —

### NL-435 The data-loss reestablish of a restored channel is unproven for a channel at commitment height 0
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/BackupRestoreFlowTests.cs`
- **Evidence:** LND force-closes on `next_commitment_number = 0` at commitment height 4 (proven); a channel that never carried a payment is not covered, and LND 0.20 does not force close on a remote `error`, so if LND reads 0 as "retransmit" there the restore does not get the channel closed (reported by lane R1).
- **Fix sketch:** Add a Docker case that restores a channel with no updates and check LND's reaction; fall back to asking the operator to close from the peer.
- **Blocks/Blocked-by:** Part of NL-426
- **Plan ref:** —

### NL-461 A funder channel whose funding the monitor abandoned stays V1FundingSigned
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (NL-294 abandonment), `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`ForgetStaleChannels`)
- **Evidence:** Since NL-294 (f365bc13) the chain monitor abandons a funding bitcoind refuses for good and releases its UTXO locks, but the channel stays V1FundingSigned in memory and in the database: funder channels never time out in `ForgetStaleChannels`, so the dead channel is listed and resumed at every start (reported by lane l1, wave lh1).
- **Fix sketch:** Mark the channel Stale (or surface it for the operator) in the abandonment's save.
- **Blocks/Blocked-by:** Related NL-259, NL-294
- **Plan ref:** —

### NL-462 UTXO channel locks are never persisted
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `UtxoModel.LockedToChannelId`, `src/NLightning.Infrastructure.Repositories/Database/Bitcoin/UtxoDbRepository.cs` (`Update` has no caller)
- **Evidence:** Funding inputs are locked to a channel only in memory, so after a restart the inputs of a V1FundingSigned funder channel are protected only by the pending-broadcast exclusion (NL-385), not by a lock; the NL-259 release paths therefore only touch memory (reported by lane l1, wave lh1).
- **Fix sketch:** Persist the lock with the funding save, or restore it at startup for V1FundingSigned funder channels.
- **Blocks/Blocked-by:** Related NL-259, NL-385
- **Plan ref:** —

### NL-467 CLN v26.06.8 holds back a fulfill queued while quiescent until the next reestablish
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnQuiescenceTests.cs` (Proof Q (c))
- **Evidence:** CLN restarts channeld in place after our `tx_abort` and marks an HTLC it had queued a fulfill for while quiescent as already fulfilled (`SENT_REMOVE_HTLC`), so it never sends `update_fulfill` until the next `channel_reestablish`; Proof Q (c) reconnects when the payment is still in flight 20 s after `tx_abort` (b7d14056, reported by the wave qit integrator). The HTLC is not lost, but it stays pending until a reconnection, or until its deadline forces a close.
- **Fix sketch:** Ask CLN's splicing lead (`SPLICING_PLAN.md` §10) whether this is a CLN bug; until then consider a reconnection when a peer that ended a quiescence leaves an HTLC it can settle unanswered.
- **Blocks/Blocked-by:** Related NL-042, NL-468
- **Plan ref:** `SPLICING_PLAN.md` Proof Q, §7, §10

### NL-468 CLN v26.06.8 quiescence RPCs need option_splice; CLN's tx_abort and our echo are unproven on the wire
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnQuiescenceTests.cs`
- **Evidence:** `stfu_channels` and `abort_channels` return error 354 ("Peer does not support splicing") because we advertise no splice bit, so Proof Q (a) starts CLN's quiescence with `dev-quiesce` and ends it with our own `tx_abort`, which CLN acks and resumes (b7d14056). The path where CLN sends `tx_abort` and we echo it is proven only in-process (IT4-T2 handler and driver tests); plan Proof Q (a) as written is not run (reported by the wave qit integrator).
- **Fix sketch:** Re-run Proof Q (a) with `abort_channels` once `OptionSplice` is advertised (SP1/SP2 integration, D13).
- **Blocks/Blocked-by:** Related NL-042, NL-021
- **Plan ref:** `SPLICING_PLAN.md` Proof Q (a), §7

### NL-470 Quiescence and interactive-tx seams left open by wave qit
- **Status:** open (partial: 940da49c, df9a869a, d778d100)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Quiescence/QuiescenceService.cs` (`OnPeerDisconnected`), `test/NLightning.Application.Tests/Channels/Quiescence/QuiescenceTestPair.cs:43`, `src/NLightning.Infrastructure.Repositories/Database/Channel/InteractiveTxSessionDbRepository.cs:112` (`DeleteByChannelIdAsync`)
- **Evidence:** (1) `IQuiescenceService.OnPeerDisconnected` has no caller in `PeerManager`/`ChannelManager`; the service ends each quiescence through its own connection binding (`IPeerService.OnDisconnect`, replacement check in `GetState`), so behavior is correct but the explicit Q-R-04 hook is dead. (2) `QuiescenceTestPair` still routes `stfu` by reflection over `HarnessNode._outbox` straight into `OnStfuReceived`, not through `ChannelManager`/`StfuMessageHandler`, although the manager case exists now (lane Q-B F6). (3) Not covered on the wire by Proof Q: a CLN add not yet revoked at our request, the Q-R-03 60 s timeout with HTLCs pending, and a CLN fulfill queued while quiescent (in-process only). (4) A released owed `stfu` goes through `ChannelManager.Publish`, which drops normal-operation messages of a channel not yet reestablished on the connection; that `stfu` is regenerated by the next `TryReleaseStfu`, not retransmitted. (5) `InteractiveTxSessions` has no FK to `Channels` and `DeleteByChannelIdAsync` has no caller, so rows of a forgotten or closed channel stay until something deletes them (lane IT-C; harmless while nothing dependent opens negotiations) (reported by lanes Q-A, Q-B, Q-C, IT-C and the integrator).
- **Update (wave sp1):** (1) fixed: `ChannelManager.OnPeerDisconnectedAsync` calls `IQuiescenceService.OnPeerDisconnected` (Q-R-04), and `QuiescenceService` raises `QuiescenceEnded` for its dependent protocol (940da49c). (4) fixed: `CompleteReestablishAsync` calls `IStfuReleaseScheduler.ScheduleRelease`, so an owed `stfu` goes out after the reestablish (940da49c). The splice's `SpliceDepthWatcher` is started with the splice service and caught up at host startup (df9a869a, d778d100). Still open: (2) `QuiescenceTestPair` reflection routing, (3) the uncovered Proof Q cases (the in-flight case now fails against CLN, NL-477), (5) the `InteractiveTxSessions` cleanup.
- **Fix sketch:** Call `OnPeerDisconnected` from `ChannelManager.OnPeerDisconnectedAsync` (or delete it from the contract); deliver `stfu` through `ChannelManager` in `QuiescenceTestPair` and re-run the quiescence harness with both ends quiescent; record the uncovered cases in the Proof Q record; call `DeleteByChannelIdAsync` where channels are forgotten or reach Closed.
- **Blocks/Blocked-by:** Part of NL-042, NL-037
- **Plan ref:** `SPLICING_PLAN.md` Q1-T3, Q1-T6, IT3-T2, "Seams to reconcile"

### NL-473 Interactive-tx dust check uses Bitcoin Core's thresholds, not a negotiated dust_limit
- **Status:** open
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/InteractiveTx/InteractiveTxRules.cs:50,198,604` (`GetDustThreshold`)
- **Evidence:** BOLT 2 says the receiver of `tx_add_output` fails the negotiation when `sats` is below the `dust_limit`; `InteractiveTxSessionParameters` carries no dust limit, so the rules use Bitcoin Core's per-script dust threshold at 3000 sat/kvB (294 sat P2WPKH, 330 P2WSH/P2TR, 546 P2PKH, 540 P2SH, 0 OP_RETURN) (reported by lane IT-A, wave qit).
- **Fix sketch:** Add the channel's dust limit to the session parameters (a contract change for the DF/SP1 contracts) and check against the larger of the two.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** `SPLICING_PLAN.md` IT1-T1

### NL-474 InteractiveTransactionConstants is mostly dead code
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Constants/InteractiveTransactionConstants.cs`
- **Evidence:** Since the old validators were deleted (081d599f) only `MaxSequence` is used (`TxAddInputPayload`); `MaxInputsAllowed`, `MaxOutputsAllowed`, `MaxMoney` and `MaxStandardTxWeight` duplicate the limits `InteractiveTxRules` now owns (reported by lane IT-A, wave qit).
- **Fix sketch:** Delete the unused constants or point `InteractiveTxRules` at them.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —


### NL-478 Several readers still use the original funding keys after a splice
- **Status:** fixed (64374dd0, 505a3b90, 252063df, ba8ecfd6, 5f078964)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Backup/ChannelBackupService.cs`, `src/NLightning.Application/Gossip/Announcements/ChannelAnnouncementBuilder.cs`, `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (`GetSigningInfo`)
- **Evidence:** A splice rotates our funding key (`m/0'/i'`, SP1-C). 2ba4fe09 moved `ChannelModel.LocalFundingPubKey`/`RemoteFundingPubKey` to the current funding (the anchors were built on the old keys and CLN rejected our first post-lock `commitment_signed`), but static channel backups still record the key sets' original funding keys and carry neither the funding key index nor the current funding outpoint, the announcement builder would announce a spliced public channel with the old keys (SP2-B scope), and `GetSigningInfo` reports `LocalKeySet.FundingCompactPubKey` (safe today only because the signer treats a re-registration of a known splice funding as a refresh) (reported by lane SP1-C and the wave sp1 integrator).
- **Fix sketch:** Read the current funding everywhere (`ChannelModel.LocalFundingPubKey`, `FundingOutput`); add the funding key index and current outpoint to the SCB entry and rewrite the backup at the lock (`LocalLightningSigner.GetFundingPubKey(channelKeyIndex, i)` needs only the index).
- **Update (wave sp2):** SP2-B: `ChannelAnnouncementBuilder.BuildUnsigned`/`GetRemoteSignatureChecks` read `ChannelModel.LocalFundingPubKey`/`RemoteFundingPubKey`, and a spliced public channel is re-announced with the rotated keys (proven in-process by `SpliceAnnouncementHarnessTests` and against CLN in Proof SP2 (c); 64374dd0, 505a3b90). SP2-E: backup entries carry the current funding (outpoint, capacity, both keys), our funding key index and the pending splices (codec version 1); verify and restore re-derive the rotated key and follow the funding through splice spends; `channel.backup` is rewritten when a splice locks; peer-storage blob version 2 carries each channel's outpoint and key index (252063df, ba8ecfd6, 5f078964; CLN Docker `ClnSpliceBackupRestoreTests`). `ChannelModel.GetSigningInfo` still reports the key set's original funding key (harmless while the signer refreshes a known splice funding); carried as NL-495.
- **Blocks/Blocked-by:** Part of NL-021; related NL-426, NL-495
- **Plan ref:** `SPLICING_PLAN.md` D5, SP2-B-T3

### NL-479 The revocation log is not written or read per funding for splices (SP-I5)
- **Status:** fixed (768f8642, 82b98af3, 2b6910ac, c8d4257d, d5e13781)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs:469` (`MapRevokedAsync`), `src/NLightning.Application/Onchain/Resolvers/Revoked/RevokedCommitDataSource.cs:110`, `IChannelFundingDbRepository.StageRevokedCommitmentAsync`
- **Evidence:** `ChannelStateDbRepository.ApplyAsync` logs a revoked commitment only for the current funding; `StageRevokedCommitmentAsync` (per pending funding) exists but has no caller in the `revoke_and_ack` save. The on-chain callers use the unscoped `GetAsync(channelId, n)` and `channel.FundingOutput`, so a breach of a pending or retired splice funding would find the wrong row and capacity; the repository has the scoped `GetAsync(channelId, fundingTxId, n)` since 48b0339f (reported by lane SP1-C, wave sp1). Harmless while `OptionSplice` is experimental.
- **Fix sketch:** In the `revoke_and_ack` save stage a revocation row per active funding (the engine lists them in `RevokedRemoteCommitFundings`); in the watcher and the revoked data source pass the spent outpoint's txid and take that funding's capacity; a resolver test that punishes a revoked commitment of a discarded funding.
- **Update (wave sp2, lane SP2-C):** `ChannelStateDbRepository.ApplyAsync` writes a revocation row per entry of `RevokedRemoteCommitFundings` and keeps every pending funding's slots in step (768f8642); `RevokedCommitmentDbRepository.GetByFundingAsync` and `FundingTxId`; the watcher, `RevokedCommitDataSource` and the mempool reactor read the spent funding's entry and capacity (82b98af3, 2b6910ac, c8d4257d); without an entry on the spent funding, another funding's entry for that number is rebased on the spent funding's deltas, so HTLC outputs are still penalized (d5e13781). A revoked commitment of a discarded funding is punished (watcher test) and Docker `OnchainSpliceTests` (c) penalizes a revoked commitment of the pre-splice funding after the locked splice is reorged out. `RemoteCommit.SignedOnFundings` is not persisted, so after a restart the revocation is logged on the current funding only until the next splice (the rebase above covers penalties); carried as NL-494.
- **Blocks/Blocked-by:** Part of NL-021, NL-094; related NL-494
- **Plan ref:** `SPLICING_PLAN.md` SP2-C-T3

### NL-480 The receive reserve on a spliced funding (D9/Q3) is not confirmed against the peers
- **Status:** open
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Commitments/CommitmentParams.cs` (`RemoteReceiveReserveMsatOn`, `LocalReserveMsatOn`), `src/NLightning.Domain/Channels/Splicing/SpliceRules.cs` (`GetReserveSatoshis`)
- **Evidence:** We send with reserve = max(announced, 1 % of the new capacity) on every non-initial funding and accept the peer's adds down to min(announced, 1 % of capacity) on a spliced funding (SP1-B review F4, 29b4ae25); the reading of BOLT 2 is plan question Q3 and was not checked against the CLN, LND or Eclair splice code; Proof SP1 did not hit the boundary (reported by lane SP1-B).
- **Fix sketch:** Read CLN's and Eclair's splice reserve code (or ask CLN's splicing lead, plan §10) and add a Docker case that adds to the reserve boundary on a spliced-out channel.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` D9, §10 Q3

### NL-481 InteractiveTxDriver can only send positive funding contributions
- **Status:** fixed (9d13d0de, 96d32442)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/InteractiveTx/InteractiveTxDriver.cs:1048` (`CreateContributionTlv(LightningMoney)`)
- **Evidence:** It emits `funding_output_contribution` only when the amount is above zero, from a `LightningMoney`, so the `tx_init_rbf`/`tx_ack_rbf` of a splice-out RBF cannot carry its negative contribution although the TLV is an s64 since NL-476 (reported by lanes SP1-A and SP1-B, wave sp1).
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-A: `InteractiveTxDriver.CreateContributionTlv(long)` (0 = no TLV; the `LightningMoney` overload delegates) and `SpliceService` rewrites the driver's `tx_init_rbf`/`tx_ack_rbf` with our signed contribution, negative for a splice-out RBF (9d13d0de, `SpliceRbfHarnessTests` case 5). Since 96d32442 a splice RBF always carries the TLV, 0 included (NL-503).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SPR-T1

### NL-483 commitment_signed and revoke_and_ack handlers defer their follow-up signature while a splice is pending
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Handlers/CommitmentSignedMessageHandler.cs`, `RevokeAndAckMessageHandler.cs`
- **Evidence:** Both still call the single-message `SignIfPendingAsync`, which with a pending splice signs nothing and schedules `ICommitScheduler`, so their follow-up batch goes out after the lock is released instead of in the handler's reply (one extra round trip; correct but slower). `ChannelCloseCoordinator` was switched to the list form `SignPendingAsync` in 29b4ae25 (reported by lane SP1-B, wave sp1).
- **Fix sketch:** Switch both handlers to `SignPendingAsync` and return the whole batch in wire order.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SP1-B-T2

### NL-484 A restart in the middle of a splice broke the splice: pending fundings not restored and the negotiation forgotten
- **Status:** fixed (990d3381; partial fixes ff87bd93)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs` (`LoadAsync`), `src/NLightning.Application/Channels/Splicing/SpliceService.cs` (`EnsureLoadedAsync`), `ChannelReestablishMessageHandler.cs`
- **Evidence:** Lane SP2-A found that `SpliceService` kept negotiations in memory only and never resumed the interactive-tx driver, so after a restart between our splice `commitment_signed` and both `tx_signatures` the peer's retransmitted splice `commitment_signed` was not recognised and its `tx_signatures` got `tx_abort`; it fixed the retransmission of our own stored `tx_signatures` (ff87bd93) and blocked the SP-T-04/05 restart variants on the rest. At integration Proof SP2's restart cases failed against CLN: `ChannelStateDbRepository.LoadAsync` passed no pending fundings to the engine (so CLN's `splice_locked` named an unknown splice), and CLN's retransmitted splice `commitment_signed` was verified as a commitment update ("invalid signature"). 990d3381: pending splices whose local slot carries the peer's signatures are restored on load, and `SpliceService.EnsureLoadedAsync`, called from the `channel_reestablish` handler, rebuilds the negotiation from the `InteractiveTxSessions` and `ChannelFundings` rows and resumes it in the driver. Tests: 2 in `SpliceFundingsPersistenceTests`, `SpliceConformanceTests.Given_BothCommitSigsLostAndTheInitiatorRestarted_*` (fails without the fix); Proof SP2 11/11 against CLN v26.06.8 (reported by lanes SP2-A, SP2-C and the integrator).
- **Fix sketch:** Done. The remaining restart variants are test coverage (NL-496).
- **Blocks/Blocked-by:** Part of NL-021; related NL-494, NL-496
- **Plan ref:** `SPLICING_PLAN.md` SP2-A-T3, Proof SP2 (a)

### NL-487 The retired SCID map was never loaded at startup
- **Status:** fixed (a31c6c0d)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Services/NltgDaemonService.cs`, `test/NLightning.Integration.Tests/Docker/Utils/NLightningTestNode.cs`
- **Evidence:** `IRetiredScidMap.LoadAsync` had no caller in the hosts, so after a restart within 72 blocks of a splice lock a forward over the old SCID failed with `unknown_next_peer` (lane SP2-B review finding 1; the lane proved `LoadAsync` itself in `RetiredScidForwardTests.Given_BobRestartsAfterTheLock_*`). a31c6c0d loads the map before `PeerManager.StartAsync` and prunes it at the chain tip once the chain monitor has started, in the daemon and the Docker test node (the lane asked for "after the chain monitor, before PeerManager", but both hosts start PeerManager first).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SP2-B-T2, D12

### NL-488 A locked splice's announcement_signatures flag is never stored, and SP2-A's contract fallbacks remain
- **Status:** open (partial: 9d13d0de)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Reestablish/ReestablishService.cs` (`GetSpliceStateAsync`, `IsReadyForAnnouncementSignatures`), `src/NLightning.Application/Channels/Handlers/ChannelReestablishMessageHandler.cs:369`, `src/NLightning.Domain/Channels/Splicing/ChannelFunding.cs` (`AnnouncementSignaturesReceived`)
- **Evidence:** Lane SP2-A reads `ChannelFunding.AnnouncementSignaturesReceived` for a locked splice's bit 0 of `my_current_funding_locked`, but nothing sets it (no writer in `src/`), so after a public splice locks we ask for `announcement_signatures` again on every reconnection (safe over-asking; the peer retransmits). The handler also keeps a `catch (NotImplementedException)` fallback to `SpliceService.HandleSpliceLockedAsync` for `HandlePeerFundingLockedAsync` and `ReestablishService` a fallback to `CanSendAnnouncementSignatures`; both contracts are implemented since SP2-B, so the fallbacks are dead (reported by lanes SP2-A and SP2-B).
- **Update (wave spr, integrated at `a0800ac2`):** lane SPR-A removed the dead `catch (NotImplementedException)` fallback of `ChannelReestablishMessageHandler` and the dead catches in `SpliceDepthWatcher` (9d13d0de). Still open: nothing writes `ChannelFunding.AnnouncementSignaturesReceived` for a locked splice (the writer belongs in `AnnouncementSignaturesMessageHandler`/`ChannelAnnouncementService`), and `ReestablishService` keeps its `CanSendAnnouncementSignatures` fallback.
- **Fix sketch:** Set the funding row flag where the peer's half for a locked splice is stored, then drop `ReestablishService`'s fallback and rerun `SpliceConformanceTests`.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SP2-A-T2, SP2-B-T3

### NL-489 Splice RBF is not implemented (wave SPR), including SP-LK-04's receive rule
- **Status:** fixed (7a044d49, 9d13d0de, f3635ed7, c1e20033, d34ab24b, a1e2b6c5, 9d017a7f, 8a3ad0a3, fd704b26, c5943068, e25249ea, b7b23099, 96d32442)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Splicing/SpliceNegotiationHost.cs` (RBF rejected), `SpliceService.cs`
- **Evidence:** `tx_init_rbf` on a splice is rejected by the host, so neither side can bump a splice (the day-0 runbook lists it as an accepted gap). When RBF lands, the receiver must also fail a peer's `tx_init_rbf` sent after its own `splice_locked` (SP-LK-04 receive side; lane SP2-B implemented only the send side).
- **Update (wave spr, integrated at `a0800ac2`):** done. SPR-T1/T2 (lane SPR-A): `SpliceRules.CheckSendRbf`/`CheckReceiveRbf`/`CheckReceiveAckRbf`/`CheckRbfDoubleSpends` (a sender violation is refused; the receiver's MUSTs are warning and close, including a `tx_init_rbf` after the sender's own `splice_locked` (SP-LK-04 receive side); the feerate floor max(floor(25/24 x prev), prev + 25), `Splice:MinRbfInterval`, more than 10 attempts below the quick estimate and a batch above 20 get `tx_abort`), `FundingSet.AddRbfSibling`/`Siblings` (lock discards every sibling), `SpliceService.BumpAsync` (either quiescence initiator may bump; our contribution rebuilt from the latest attempt, negative for a splice-out RBF, NL-481), every sibling checked and saved before our `commitment_signed`, `start_batch` over every pending attempt (7a044d49, 9d13d0de, f3635ed7). Review (c1e20033): a peer's RBF feerate above `Splice:MaxFeeratePerKw` gets `tx_abort`, our share of a peer's RBF is capped (`Splice:MaxRbfFeeShareSatoshis`, 50,000, and at most half of what our contribution moves; otherwise we contribute 0), the bumped attempt stays Pending and is rebroadcast until the lock (the lock abandons the losers), the RBF host is restored when an attempt ends. SPR-T3 (lane SPR-B): `bumpsplice` (ClientCommand 37, d34ab24b) and the optional `SpliceAutoBumper` (`Splice:AutoBumpAfterBlocks`, off by default, with its own `AutoBumpMaxFeeratePerKw` 25,000, `AutoBumpMaxFeeSat` 100,000 and `AutoBumpMaxWait` 120 s; a1e2b6c5, 9d017a7f), registered and started after the chain monitor and the peers in both hosts (8a3ad0a3, fd704b26). SPR-T4 (lane SPR-C): Proof SPR `Docker/Interop/Cln/ClnSpliceRbfTests` 5/5 against CLN v26.06.8 and `Day0FlowTests` step 9 green (c5943068, e25249ea, b7b23099). The integration found that CLN fails an RBF whose `tx_init_rbf`/`tx_ack_rbf` omits a zero `funding_output_contribution` (NL-503, 96d32442). Proofs: `SpliceRbfRulesTests`, `FundingSetRbfTests`, `SpliceRbfHarnessTests`, `SpliceRbfContributionTests`, `BumpSpliceIpcHandlerTests`, `SpliceAutoBumperTests`. Residual behaviour: while several attempts are pending each is rebroadcast every block and the losing one is refused as a mempool conflict (logged, temporary). Follow-ups: NL-507, NL-508, NL-509, NL-510, NL-515; CLN deviations NL-502, NL-511.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021; related NL-481
- **Plan ref:** `SPLICING_PLAN.md` wave SPR, SP-LK-04

### NL-492 A splice's wallet reservation is not released when a commitment confirmation discards it, nor rolled back on a reorg
- **Status:** fixed (f9c6b7f0, 8566caf2)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs` (discard in the close's save), `src/NLightning.Application/Onchain/Reorg/SpliceReorgMonitor.cs`
- **Evidence:** When our or the peer's commitment on the current funding confirms, pending splices are set `Discarded` and their broadcast abandoned, but the interactive-tx fee-input reservation stays until one of its inputs is spent; the plan wants it released once the conflict is irrevocable (§3.6). A reorg does not roll the reservation back either (SP2-C-T4) (reported by lane SP2-C).
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-E: `Onchain/Reorg/DiscardedSpliceReservations` (first in the executor's block round) settles the unresolved `InteractiveTxSessions` row of a Discarded funding and calls `WalletInteractiveTxContributor.ReleaseDiscardedAsync` only once the transaction that spent the funding is 100 blocks deep, so a reorg has nothing to roll back; a reservation that also holds another spend's or a kept outpoint is kept and retried every block (8566caf2). This also covers the discarded RBF siblings of wave spr (lane SPR-A review finding 2). Proof: `OnchainSpliceDay0Tests`, `WalletInteractiveTxContributorTests`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` §3.6, SP2-C-T4

### NL-493 Splice reorg handling is partial (SP2-C-T4)
- **Status:** open (partial: f9c6b7f0, 8566caf2)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Reorg/SpliceReorgMonitor.cs`, `src/NLightning.Application/Channels/Splicing/SpliceDepthWatcher.cs`, `OnchainResolutionExecutor`
- **Evidence:** The locked-splice reorg alert (CRITICAL `[SP2-C-T4]`) is kept in memory only, so it repeats after a restart during the reorg; `SpliceDepthWatcher` does not react to a pending splice reorged out before its lock (only idempotency protects it); after a close is retired because its discarded splice confirmed, the executor warns "resolving on chain without a recorded funding spend" every block until our commitment on the splice confirms; and a crash after the chain monitor recorded the splice spend but before the watcher's save relies on the spend being handed over again after the restart (`CheckConfirmedSplicesAsync` covers only the state after the save). Docker `OnchainSpliceTests` (d) and (e) prove the pending and locked reorg cases end to end (reported by lane SP2-C).
- **Update (wave spr, integrated at `a0800ac2`):** lane SPR-E: `Onchain/Reorg/RecordedFundingSpendReplay` hands a funding spend the chain monitor recorded but the watcher never handled (crash between the saves) to the watcher again, once per process, retrying a failed hand-over or an unreadable block (CRITICAL after 144 rounds); the executor's 'resolving on chain without a recorded funding spend' warning is logged once per channel (f9c6b7f0, 8566caf2; `OnchainSpliceDay0Tests`). Still open: the locked-splice reorg alert is in memory only, and `SpliceDepthWatcher` does not move a pending splice reorged out before its lock back to waiting.
- **Fix sketch:** Persist the alert state; have `SpliceDepthWatcher` move a reorged-out splice back to waiting.
- **Blocks/Blocked-by:** Part of NL-021, NL-094
- **Plan ref:** `SPLICING_PLAN.md` SP2-C-T4

### NL-494 RemoteCommit.SignedOnFundings is not persisted
- **Status:** fixed (e86bb0c4, a9721c49, e0b8a7a9)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs` (`MapToDomain`), `src/NLightning.Domain/Channels/Commitments/` (`RemoteCommit.SignedOnFundings`)
- **Evidence:** The engine records which fundings each remote commitment was signed on; the snapshot does not store it, so after a restart `ReceiveRevoke` falls back to the pending fundings and a revocation of a commitment signed on a funding that has since been discarded or retired is logged on the current funding only. The on-chain side rebases another funding's entry (NL-479, d5e13781), so penalties still work; since 990d3381 pending fundings are restored, which closes most of the window lane SP2-C reported (`BuildOnFundingAsync` "No signed local commitment n on splice") (reported by lane SP2-C).
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-D (migration owner): migration `AddSpliceHardening` (all three providers) adds `Commitments.SignedOnFundings` (per funding the txid plus the engine's rebased local/remote deltas, since the rows' own deltas are relative to the funding current when each was written), written by `ChannelStateDbRepository.ApplyAsync` for the state machine's current and unacked remote slots and restored at load (e86bb0c4); a damaged record keeps the resolvable and pending fundings and never fails a channel load (a9721c49); the migration's `Down` refuses while a signed-on record exists (e0b8a7a9). Proof: `SignedOnFundingsReloadTests` (fails with the restore disabled), `SpliceHardeningSchemaRoundTrip`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021; follows NL-479
- **Plan ref:** `SPLICING_PLAN.md` SP-I5

### NL-495 ChannelModel.GetSigningInfo reports the original funding key after a splice
- **Status:** fixed (a3748f4d, 1304b5f6)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs:489` (`GetSigningInfo`)
- **Evidence:** It still passes `LocalKeySet.FundingCompactPubKey`/`RemoteKeySet.FundingCompactPubKey`, not the current funding's keys; safe today only because the signer treats the re-registration of a known splice funding as a refresh (remnant of NL-478; reported by lane SP2-B).
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-D: `GetSigningInfo` reports the current funding's outpoint, capacity, keys and `LocalFundingKeyIndex`, set at reload, at the lock and in `ChannelRestoreService` (a3748f4d); the change exposed `ThreeNodeHarness` building the funding output as funder/fundee instead of local/remote (1304b5f6). Proof: `SplicedSigningInfoReloadTests`, `ChannelRoundTripTests`, `ChannelModelSigningInfoTests`. `LocalLightningSigner.RegisterChannel` still keeps its own key index on a refresh (both values agree now).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021; remnant of NL-478
- **Plan ref:** `SPLICING_PLAN.md` D5

### NL-497 An inbound peer from a loopback address is not saved, so its channels are forgotten at a restart
- **Status:** fixed (84f5696c, 900810a8, e0b8a7a9, a0800ac2)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:623` (`args.Host != "127.0.0.1"`)
- **Evidence:** `PeerManager` stores an inbound peer only when its host is not loopback, and a restarted node loads its channels peer by peer from that table, so a channel whose peer connected from `127.0.0.1` (a local tunnel, Tor on the same host) is forgotten at the restart and the peer's `channel_reestablish` gets `error` "unknown channel" (found by `Day0UpgradeInPlaceTests`, lane SP2-F; the Day-0 proofs dial from both sides to avoid it and `DAY0_RUNBOOK.md` warns operators). An inbound peer from another address is saved with port 9735, not the port it dialed from.
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-D: every inbound peer is saved; a loopback one (127/8, ::1, IPv4-mapped, localhost) as `Peers.IsInboundOnly` (migration `AddSpliceHardening`) with no address, never overwriting a saved dialable row; `StartAsync` registers every peer's channels but never dials an inbound-only peer, and `UnitOfWork.GetPeersForStartupAsync` also returns an inbound-only stub for a non-Closed/Stale channel with no peer row (84f5696c). Review (900810a8): inbound-only rows stay out of static channel backups and restores; a loopback session reconnects at its saved dialable row. The migration's rollback drops inbound-only peers (e0b8a7a9), so `Day0UpgradeInPlaceTests` counts only dialable rows (a0800ac2). Proof: `PeerManagerInboundRestartTests` (SQLite), `ChannelRestoreServiceInboundOnlyTests`. The non-loopback port-9735 residue is NL-514.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-201
- **Plan ref:** —

### NL-502 CLN v26.06.8 fails the channel when its splicein RBF is refused
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnSpliceRbfTests.cs` (proof (c))
- **Evidence:** CLN's `splicein` on a channel with a pending splice sends `tx_init_rbf` at its unchanged estimate, i.e. the same feerate; we answer with `tx_abort` (IT-RBF-01, correct), CLN aborts the splice although its first attempt already holds signatures and then fails the channel ("I needed to abort a splice where I have already sent my signatures"). Proof SPR (c) now drives the same-feerate RBF through CLN's low-level `splice_init` instead; our side is unchanged (reported by the integrator, 96d32442).
- **Fix sketch:** CLN-side. Keep the runbook rule (bump only your own splice, never through CLN's `splicein`), and report upstream; recheck with the next CLN release.
- **Blocks/Blocked-by:** Related NL-489, NL-511
- **Plan ref:** `SPLICING_PLAN.md` SPR-T4

### NL-520 Splice RBF "created recently" was a one-minute wall clock
- **Status:** fixed (f30f3be3)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Splicing/SpliceOptions.cs` (`MinRbfBlocks`, `MinRbfInterval`), `SpliceService.Rbf.cs` (`IsLastAttemptRecentAsync`), `src/NLightning.Domain/Channels/Splicing/SpliceRules.cs` (`IsLastAttemptRecent`)
- **Evidence:** BOLT 2 `tx_init_rbf` receiver: "If another RBF attempt has been created recently: SHOULD send `tx_abort` to reject this RBF attempt and wait for the previous RBF attempt to confirm" (no number). Wave spr judged it by the session row's `CreatedAt` against `Splice:MinRbfInterval`, default 1 minute: several blocks on Mutinynet (so the day-0 rehearsal had to set 5 s) and a tenth of a block on mainnet, where a peer could replace a splice every minute without any attempt having had a chance to confirm.
- **Fix sketch:** Done: an attempt is recent until at least `Splice:MinRbfBlocks` (default 1) new blocks were processed since it was created, network-agnostic ("wait for it to confirm": one block is the least that gives it a chance). The creation height is derived, not stored: the latest attempt's broadcast row (`BroadcastTransactions.FirstBroadcastHeight`), saved in the same save as its signed interactive-tx row, so the rule survives a restart without a migration; an unknown height (no chain monitor) never makes an attempt recent. `Splice:MinRbfInterval` stays a supported key (owner update 2026-09-28: the live Mutinynet nodes keep their 5 s override): null by default, and when set it replaces the block rule (TimeSpan.Zero turns recency off); the daemon logs it at start. `MinRbfBlocks` 0 turns the block rule off. The dual-funded RBF has no BOLT 2 recency rule and is off by default (`Node:DualFund:AllowRbf`), so it is unchanged. Proofs: `SpliceRbfRulesTests` recency rows (14), `SpliceRbfHarnessTests.Given_TheDefaultBlockRule_*` (same block: `tx_abort`; one block later: accepted), `SpliceConfigTemplateTests` (template and an old `MinRbfInterval` binding), `Day0FlowTests` step 9 (a bump in the first attempt's block refused by B, the same bump after one empty block accepted).
- **Blocks/Blocked-by:** Related NL-489, NL-515
- **Plan ref:** `SPLICING_PLAN.md` SPR-T1, "Wave d13 record"; `DAY0_RUNBOOK.md` §1

### NL-521 A dual-funded RBF ignores a changed contribution in the peer's tx_ack_rbf
- **Status:** fixed (7b8dacd6, proof f1068fce)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/InteractiveTx/InteractiveTxDriver.cs` (`ReceiveAckRbfAsync`), `src/NLightning.Application/Channels/DualFunding/DualFundedOpenService.cs` (`BumpAsync`, `DecideRbfAsync`)
- **Evidence:** Wave d13 full CLN runs (2/2): `ClnDualFundTests.Given_OurUnconfirmedDualFundedOpen_When_WeBumpIt_*` failed with CLN's `tx_abort` "Insufficiently funded funding tx, total desired funding 600000sat != funding output 300000sat": CLN's funder did not match our first open (its feerate bounds follow its estimate, which the earlier classes move, as in NL-486) and matched in our RBF, but our RBF builds the funding output from the first attempt's shares and never reads `tx_ack_rbf.funding_output_contribution` (a received `tx_init_rbf` with a changed contribution is refused, `DecideRbfAsync`). The class passes alone (3/3). BOLT 2 lets either side change its contribution in an RBF. RBF of a v2 open is off by default (`Node:DualFund:AllowRbf`).
- **Fix sketch:** Read the accepter's contribution from `tx_ack_rbf` and rebuild the shared funding (or `tx_abort` with a clear reason when we do not support the change); pin CLN's funder feerate bounds in the proof.
- **Update (lane rbf, `7b8dacd6` on `wip/fafo-rbf`):** Fixed per BOLT 2 (`tx_init_rbf`/`tx_ack_rbf` Rationale: "it may be different from the contribution made in the previously completed transaction"; "Fee bumping": "Peers can use different values in `tx_init_rbf.funding_output_contribution` and `tx_ack_rbf.funding_output_contribution` from the amounts transmitted in `open_channel2` and `accept_channel2`"). The driver hands `tx_ack_rbf` to the host before the attempt is built (`IInteractiveTxHost.OnRbfAcknowledgedAsync`, default no-op, so the splice path, which reads `tx_ack_rbf` itself in `SpliceService.HandleTxAckRbfAsync` before `TryPrepareSharedFunding`, is unchanged: it had no such bug, `SpliceRbfHarnessTests.Given_OurFeeShareAboveTheCap_*` covers an ack whose contribution changed from -80,000 to 0). `DualFundedOpenService` checks a new contribution (`GetRbfShareViolation`: not negative, not above 21M BTC, a funding output above the dust limit, the opener's share paying the first commitment's fee and anchors, `option_support_large_channel` for a large one; else `tx_abort` naming the reason), follows it as opener (`tx_ack_rbf`) and as accepter (`tx_init_rbf`, which used to be refused), and `BumpAsync(channelId, feerate, localContribution)` changes ours from the same inputs. An accepter that funded nothing at the open (empty wallet, as CLN's funder outside its bounds) contributes in a peer's RBF when `AcceptContributionSat` asks for a share (a fresh wallet contribution: nothing of ours is in the earlier attempts). At the commitment step the channel's capacity, balances, reserve (1% of the new capacity, both sides) and our in-flight limit follow the attempt (`ChannelModel.ReplaceUnconfirmedFunding`, before the first snapshot only) and come back with the old outpoint and signatures when it ends unsigned. The signer signs an RBF attempt as a pending funding (`RegisterFunding`, per-funding `SignChannelTransaction`/`ValidateSignature`: the BIP 143 sighash commits to the funding amount, so the first attempt's registration could not sign a changed capacity) and `LockFunding`s it at completion; the `Initial` `ChannelFundings` row, which kept the first attempt's txid and capacity after any dual-funded RBF, now follows the channel (`ChannelDbRepository.EnsureInitialFundingAsync`). Proofs: `DualFundRbfContributionTests` (5: the accepter's new 400,000 sat in `tx_ack_rbf` and the opener's 650,000 in `tx_init_rbf`, each followed end to end with capacity, balances, reserve, the funding row and payments both ways; a negative `tx_ack_rbf` contribution and an opener share below the commitment fee refused with `tx_abort`; an attempt with a new contribution aborted after both `commitment_signed` restored to the old capacity), `ClnDualFundTests` (c) now picks both feerates inside CLN's `min_acceptable`..`max_acceptable` (the funder plugin's `feerate_our_min`/`feerate_our_max`; CLN has no option to pin them, `--ignore-fee-limits` does not reach `feerate_min`/`feerate_max`) and checks the channel against what CLN reports it put in. Follow-up: NL-527.
- **Blocks/Blocked-by:** Part of NL-037; related NL-486, NL-527
- **Plan ref:** `SPLICING_PLAN.md` wave DF

### NL-522 Order-dependent CLN failures in the wave d13 full runs: CLN's splice RBF fee and offer fetches
- **Status:** fixed (f1068fce)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnSpliceRbfTests.cs` (`Given_ClnsPendingSplice_When_ClnBumpsIt_*`), `ClnOfferPayTests.cs` (all four)
- **Evidence:** Both full CLN runs of wave d13 (64/70 each, the other failure NL-521) and green alone (the three classes together 12/12). (b): CLN's first splice paid 3178 sat at its moved estimate and its 4x `feerate_per_kw` RBF only 1202 sat, so we refused the RBF at our commitment step (SP-TX-05, BOLT 2: an RBF must pay at least the previous fee) after CLN had sent its `commitment_signed`, and CLN failed the channel ("tx_abort is not allowed after I have sent my signature"). `ClnOfferPayTests` ran next: CLN never answered our `invoice_request` ("No reply from offer path 0 within 30 s", then NoPath/Unreachable). A full CLN run of the pre-D13 build `29ce3126` on the same machine the same day failed 36/70 on CLN's fee floor ("feerate_per_kw 2500 below minimum 2501"), so the fee state of the shared CLN fixture decides these; no D13 cause was found.
- **Fix sketch:** Derive the RBF feerate in proof (b) from CLN's first attempt (at least its fee plus IT-RBF-01); give `ClnOfferPayTests` a fresh CLN peer state or wait for CLN's offers plugin; recheck in the next full run.
- **Update (lane rbf, `f1068fce` on `wip/fafo-rbf`):** (b) Our numbers are right. Both fees are read the same way, every input's amount minus every output's (`SpliceService.GetTotalFee`, now shared by `GetFee` for the stored attempt and `GetFacts` for the new one): the shared input counts at the current capacity in both, the new funding output and each side's change are outputs, and the previous fee is taken from the latest attempt's stored row (`GetLatestAttemptSessionAsync`), so neither the funding amount nor the contributions leak in (`SpliceRbfFeeTests` with the d13 numbers). Since our `tx_ack_rbf` passed IT-RBF-01, CLN's `tx_init_rbf` (1,000 sat/kw) was at least 25/24 of its first `splice_init` feerate, which was therefore at most 960 sat/kw, yet the first transaction paid 3,178 sat for about 1,040 WU (the full run of this lane measured the same shape at 1,039 WU): about 3,000 sat/kw. CLN's `splicein` funds its wallet part at its own opening estimate, which the earlier classes had moved, not at the feerate it names in `splice_init`. So CLN's RBF was genuinely cheaper in absolute fee, BOLT 2 splice `tx_complete` makes the receiver `tx_abort` ("This is an RBF attempt and the transaction's total fees is less than the last successfully negotiated splice transaction's fees"), and bitcoind would have refused the replacement anyway (BIP 125 rules 3 and 4). CLN then failing the channel ("tx_abort is not allowed after I have sent my signature") deviates from BOLT 2: `tx_abort`'s sender "MUST NOT have already transmitted `tx_signatures`", and neither side had; a `commitment_signed` does not forbid it (recorded on NL-511). Proof (b) now derives CLN's bump feerate from the first attempt (`GetClnBumpFeerate`: the IT-RBF-01 floor over its `splice_init` feerate, 1,000 sat/kw, and 25 % above the first fee plus BIP 125's 1 sat/vB of the replacement at the first attempt's weight; helper tests). (c) `ClnOfferPayTests`: CLN fronts an offer with the peer of its largest enabled incoming public channel as soon as it has one (plugins/offers.c `find_best_peer`; with no public channel it issues a pathless offer), and the earlier classes of a full run leave public channels to nodes they disposed of, so the offer's only path started at a node nobody could reach ("No reply from offer path 0"). CLN's offers now carry a path introduced by CLN itself (`dev_paths`, the fixture runs `--developer`), and each test logs the front CLN would have chosen. Results: the three classes alone 5/5, 3/3, 4/4; a full CLN run 67/70 with all six d13 failures passing (the misses are NL-526, not order-dependent).
- **Blocks/Blocked-by:** Related NL-486, NL-502, NL-511
- **Plan ref:** `SPLICING_PLAN.md` Proof SPR

### NL-523 SpliceLndObserverTests: LND bob sometimes keeps the pre-splice SCID for good
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/SpliceLndObserverTests.cs`, `Docker/Day0/Day0Harness.cs` (`WaitLndForgotChannelAsync`)
- **Evidence:** Wave d13: failed 2 of 3 runs of the class alone ("bob still has 273x1x1 73 blocks later"), green on the third; the pre-D13 build `29ce3126` passed 2 of 2 the same day. In every run alice (our peer) forgot the old SCID at the spending block and bob (which learned both SCIDs through alice's relay) listed the new SCID and routed; in the failures bob never pruned the old edge. Nothing in D13 changes what the two nodes announce in this test (both ran with splice, quiesce and dual_fund Optional before too, then with `AllowExperimentalFeatures`), so this looks like LND 0.20's graph pruning of a relayed edge; not proven either way.
- **Update (merge of d13 + nl440 on `wip/fafo` at `e784c9c0`, 2026-09-28):** 2 of 3 class runs green; the miss is the same ("bob still has 273x1x1 73 blocks later"), so 4 misses in 6 runs since D13. Only bob, the LND node that learned the channel by relay, keeps the old SCID; our peer alice forgets it. The day-0 script is NLightning to NLightning and does not depend on it.
- **Fix sketch:** Capture bob's graph and chain-view state when it happens (`describegraph`, LND logs); if LND never prunes a relayed edge whose outpoint it did not watch in time, assert only alice's forgetting and bob's new SCID.
- **Blocks/Blocked-by:** Related NL-496
- **Plan ref:** `SPLICING_PLAN.md` SPR-E

### NL-524 A simultaneous inbound and outbound connection can both insert the peer row
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`ConnectToPeerAsync`, the save at about line 353)
- **Evidence:** Wave d13 LND suite (70/71): `MultiNodeHarnessTests.Given_BobAndCarol_When_ConnectingToEachOtherAtTheSameTime_*` failed once in the full run with `SqliteException: UNIQUE constraint failed: Peers.NodeId` thrown by bob's `ConnectToPeerAsync` save: the inbound connection from carol saved carol's row while bob's own connect to carol was about to insert it too. The class passed alone 5/5 right after; not a D13 change (nothing in the peer path moved). The connection kept by the LND tie-break survives, but the `connect` call that lost the race fails with a database error instead of reporting the kept connection.
- **Fix sketch:** Upsert the peer row (or re-read and update on a unique-key conflict) in `ConnectToPeerAsync` and the inbound path, under one per-peer lock.
- **Blocks/Blocked-by:** Related NL-239, NL-240, NL-497
- **Plan ref:** —

### NL-503 Splice RBF omitted a zero funding_output_contribution, which CLN rejects
- **Status:** fixed (96d32442)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Splicing/SpliceService.Rbf.cs`, `src/NLightning.Application/InteractiveTx/InteractiveTxDriver.cs` (`CreateContributionTlv`)
- **Evidence:** Our `tx_init_rbf`/`tx_ack_rbf` for a splice left out `funding_output_contribution` when our contribution was 0 (BOLT 2 allows the omission), and CLN v26.06.8 fails the RBF without it; found by Proof SPR at integration. 96d32442: a splice RBF always sends the TLV, 0 included; the three harness tests expect an explicit 0. The dual-funded RBF still omits a zero contribution (untested against CLN in that shape) (reported by the integrator).
- **Fix sketch:** Done for splices. If a dual-funded RBF with a zero contribution fails against CLN, send the TLV there too.
- **Blocks/Blocked-by:** Part of NL-021; related NL-481, NL-037
- **Plan ref:** `SPLICING_PLAN.md` SPR-T1

### NL-507 The splice auto-bumper keeps its state in memory and uses the node-wide fee estimate
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Fees/SpliceAutoBumper.cs`
- **Evidence:** The last try per attempt and the first-seen heights are in memory only, so after a restart the interval counts again from the broadcast row's `FirstBroadcastHeight` and a splice the peer kept refusing is retried at once, once; it uses the node-wide fee estimate rather than a deadline target and counts only `Pending.Count - 1` against `MaxRbfAttempts`, not failed tries. Its 'ours' rule (non-zero `LocalBalanceDeltaMsat`, or our last negotiation named an attempt) is narrower than `SpliceRules.CheckSendRbf`, which lets any quiescence initiator bump (reported by lane SPR-B).
- **Fix sketch:** Persist the last try per attempt (or derive it from the broadcast rows); pick a confirmation target; align the 'ours' rule with `CheckSendRbf`.
- **Blocks/Blocked-by:** Part of NL-021; related NL-489
- **Plan ref:** `SPLICING_PLAN.md` SPR-T3

### NL-509 InteractiveTxDriver.RejectRbf and its tx_abort echo never end the quiescence
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/InteractiveTx/InteractiveTxDriver.cs` (`RejectRbf`)
- **Evidence:** `SpliceService` ends the quiescence itself when it refuses a splice RBF, but any other RBF host running behind quiescence would stay quiescent until the quiescence timeout after a rejected `tx_init_rbf` (reported by lane SPR-A).
- **Fix sketch:** End the quiescence from the driver's rejection and echo paths (or give the host a callback), with a test for a non-splice host.
- **Blocks/Blocked-by:** Related NL-470, NL-489
- **Plan ref:** —

### NL-517 After a restart on a locked splice, the engine's current funding is the initial one (kind, key index 0)
- **Status:** fixed (d5be5b73)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs` (`MapWithStateAsync`), `src/NLightning.Domain/Channels/Commitments/CommitmentParams.cs` (`FromChannel`)
- **Evidence:** Mutinynet day-0 rehearsal (2026-09-28, build `d5d8b184`): NLightningFAFO2 restarted while its own splice-out `2cb71d6b...c693` was the current funding (key index 2); at the next lock the row was rewritten as `Kind` Initial, `LocalFundingKeyIndex` 0 with the rotated pubkey (the node's other side kept Splice/2). The reload built the engine's `CommitmentParams.Funding` with `ChannelFunding.FromFundingOutput` (Initial, index 0), the lock retired that copy through `ApplyLockAsync`, and at the next start `ChannelSigningInfoDbRepository` hands the row to the signer, whose `RegisterFundingLocked` throws `SignerException` (index 0 does not derive that pubkey). Until the next lock the engine also applied the initial funding's reserve rule (`CommitmentParams.LocalReserveMsatOn`) and the reestablish treated the current funding as not a splice (`ReestablishService.GetSpliceState`).
- **Fix sketch:** Done: the reload puts the stored current funding row into the engine's params when it is a splice. Proof: `SpliceFundingsPersistenceTests.Given_ALockedSplice_When_ReloadedAndTheNextSpliceLocks_Then_TheRetiredRowKeepsItsKindAndKeyIndex` (fails without the fix). The one corrupted row (FAFO2) was repaired by hand with the node stopped (`Kind` 1, `LocalFundingKeyIndex` 2, checked against the peer's row).
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` D5; `DAY0_RUNBOOK.md` §5

### NL-518 MempoolReactor logs every splice of ours as a warning
- **Status:** fixed (a981bd45)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Onchain/Mempool/MempoolReactor.cs` (the `Unconfirmed {Kind} ... waiting for it to confirm` line)
- **Evidence:** Mutinynet day-0 rehearsal (2026-09-28): each splice and splice RBF logged `WRN [MempoolReactor] Unconfirmed Splice <txid> (commitment null) spends the funding output of channel ...` on both nodes; a cooperative close (`Mutual`) takes the same path. An operator watching warnings (the soak sampler counts them) sees one per splice.
- **Fix sketch:** Log `Splice` and `Mutual` at Information (no commitment number), keep Warning for commitments, with a test on a capturing logger.
- **Fix:** Lane cli535. `MempoolReactor.GetFundingSpendLogLevel`: an unconfirmed `Splice` or `Mutual` funding spend is logged at Information (without the "(commitment null)" part when there is no number); commitments (local, remote, next, revoked, future) and unknown spends stay Warning. Tests: `MempoolReactorTests` (a capturing logger: our commitment at Warning, a mutual close paying our shutdown script at Information with no Warning; the level table).
- **Blocks/Blocked-by:** Related NL-021, NL-098
- **Plan ref:** `DAY0_RUNBOOK.md` §5

### NL-510 Splice RBF cannot add fresh wallet inputs to our contribution
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Splicing/SpliceService.Rbf.cs` (`PlanRbfContribution`, `GetRbfFeeShareRefusal`)
- **Evidence:** Our RBF contribution is rebuilt from the latest attempt (same wallet inputs and reservation, or the same splice-out output), so a `SpliceBumpRequest` with a positive `ContributionSatoshis` when the pending attempt had no wallet inputs is refused, and a peer RBF we cannot pay into (no inputs to reuse, or over `Splice:MaxRbfFeeShareSatoshis` / half of what we move) makes us contribute 0, which drops our splice-in or splice-out from that sibling (reported by lane SPR-A).
- **Fix sketch:** Reserve fresh wallet inputs for an RBF attempt (released with the losing siblings, NL-492's path), then allow a positive contribution without earlier inputs.
- **Blocks/Blocked-by:** Part of NL-021; related NL-489
- **Plan ref:** `SPLICING_PLAN.md` SPR-T1

### NL-511 CLN v26.06.8 splice RBF deviations (informational)
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnSpliceRbfTests.cs` (class remarks)
- **Evidence:** Checked CLN to CLN by lane SPR-C: CLN as RBF acceptor acks a `tx_init_rbf` at the same feerate as the attempt it replaces (no BOLT 2 25/24 rule), so bitcoind rejects the replacement while CLN keeps the unbroadcast attempt inflight; `splicein` has no feerate option and RBFs at its unchanged estimate; `splice_init`'s `feerate_per_kw` is read per kvB (4000 gives 1000 perkw); CLN contributes 0 as acceptor of an RBF it did not start, so an RBF by the non-initiator drops the initiator's contribution (reported by lane SPR-C).
- **Update (lane rbf, NL-522):** two more: CLN fails the channel when we answer its splice RBF with `tx_abort` after both `commitment_signed` but before any `tx_signatures` ("tx_abort is not allowed after I have sent my signature"; BOLT 2 forbids `tx_abort` only after `tx_signatures`), and CLN's `splicein` pays its wallet part at its own estimate rather than at the feerate its `splice_init` names, so a later RBF at a higher named feerate can pay less in total (refused under BOLT 2's RBF fee rule). CLN also fronts its offers with a public peer that may be long gone (`find_best_peer` only checks the peer's channel update is enabled).
- **Fix sketch:** None on our side (the runbook says to bump only your own splice); recheck with the next CLN release.
- **Blocks/Blocked-by:** Related NL-502
- **Plan ref:** `SPLICING_PLAN.md` SPR-T4

## BOLT 3: Transactions and scripts

### NL-056 HTLC-success / HTLC-timeout second-stage transactions not implemented
- **Status:** fixed (dfe8866, b222896)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/HtlcTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/HtlcTransactionBuilder.cs`
- **Evidence:** No builder, no Appendix C HTLC-tx vector tests. Fixed: Domain `HtlcTransactionModelFactory` + `HtlcTransactionBuilder` (unsigned tx, witness script, amount; `AddWitness`); all 33 Appendix C and 15 Appendix F HTLC txs byte-exact (`Bolt3HtlcTxVectorTests`, `Bolt3AnchorVectorTests`); the commented-out classes are deleted and the NL-056 test skip is gone.
- **Fix sketch:** Builders + Appendix C/F HTLC vectors.
- **Blocks/Blocked-by:** Part of NL-031; blocks NL-094
- **Plan ref:** BOLT_COVERAGE roadmap step 5; BOLT2 N2-T4

### NL-057 ILightningSigner has no HTLC-signature API; channel signing hardcodes input 0 + SIGHASH_ALL
- **Status:** fixed (615395c, 908bd32)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs`
- **Evidence:** `htlc_signatures` in commitment_signed cannot be produced or verified; anchors need SIGHASH_SINGLE|ANYONECANPAY. Fixed: `SignRemoteHtlcTransactions`, `ValidateLocalHtlcSignatures` and `SignLocalHtlcTransaction` over `HtlcSigningContext` (remote sighash SINGLE|ANYONECANPAY with anchors, else ALL); `CommitmentSigningService` signs/verifies a commitment with its HTLC signatures in output order; Appendix C/F signer vectors byte-exact. `SignChannelTransaction` still assumes input 0 + ALL, which is right for commitment and closing txs.
- **Fix sketch:** Add HTLC tx sign/verify with sighash parameter.
- **Blocks/Blocked-by:** Part of NL-031; blocked-by NL-056
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT2 N3-T1

### NL-058 HtlcResolutionOutput swaps revocation and delayed keys
- **Status:** fixed (ddf5e8e)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Outputs/HtlcResolutionOutput.cs:14-16` vs `:23`
- **Evidence:** Ctor passes `(revocationPubKey, localDelayedPubKey)` into `GenerateHtlcOutputScript(localDelayedPubKey, revocationPubKey, …)`. Not used yet, but any second-stage output built with it would pay the wrong keys.
- **Fix sketch:** Fix argument order; add Appendix C script test.
- **Blocks/Blocked-by:** Blocks NL-056
- **Plan ref:** BOLT2 N2-T4

### NL-059 BaseOutput.Amount setter is a no-op
- **Status:** fixed (5582ca1)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Outputs/BaseOutput.cs:24`
- **Evidence:** `set => Money.Satoshis(value.Satoshi);` discards the result.
- **Fix sketch:** Assign the backing field (or remove the setter).
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N2-T4

### NL-060 BaseOutput ctor calls virtual ScriptType before subclass init
- **Status:** fixed (8995085, 017050a)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Outputs/BaseOutput.cs`, `ToRemoteOutput.cs`, `OfferedHtlcOutput.cs`
- **Evidence:** `ToRemoteOutput._hasAnchorOutputs` is still false when read; harmless today only because P2WPKH/P2WSH share a branch.
- **Fix sketch:** Pass script type into the base ctor.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-061 option_anchors commitment fee: weight 1116 vs 1124, only one anchor deducted
- **Status:** fixed (4f9f550, b222896)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:173,243-255`, `src/NLightning.Domain/Bitcoin/Transactions/Constants/TransactionConstants.cs:21-27`
- **Evidence:** `AdjustForAnchorOutputs` subtracts `AnchorOutputAmount` once; spec deducts two 330-sat anchors from the funder and uses base weight 1124. Appendix F vectors (`test/NLightning.Tests.Utils/Vectors/Bolt3AppendixFVectors.cs`) are unused. Anchors default No. Byte-exact against all 9 Appendix F vectors. Appendix F HTLC txs and every Appendix F commitment are also checked from the verbatim spec vectors (b222896).
- **Fix sketch:** Fix weights/deduction; wire Appendix F tests.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N2-T1, N2-T5

### NL-062 Commitment tx: suspect HTLC subtraction and to_remote dust limit
- **Status:** fixed (4f9f550, c0113c9)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs`
- **Evidence:** Every HTLC is subtracted from to_local; to_remote trimming uses the remote dust limit (spec: the commitment holder's). Unverified; Appendix C only has no-HTLC-from-remote cases. Balances are gross (include the owner's pending offered HTLCs); documented on `ChannelModel`.
- **Fix sketch:** Verify against BOLT 3 and Appendix C; fix and add vectors.
- **Blocks/Blocked-by:** Blocks NL-031
- **Plan ref:** BOLT2 N2-T1

### NL-063 Funding tx: no change-dust check; insufficient inputs throw ArithmeticException
- **Status:** fixed (7a1a728, 830fc92)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/FundingTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs`
- **Evidence:** Dust change outputs can be created; failure surfaces as a generic arithmetic error.
- **Fix sketch:** Drop dust change into fee; throw a typed error.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-064 FundingTransactionBuilder mutates the model; funding output fixed at index 0
- **Status:** fixed (7a1a728, e89957c, 47e6a56)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs`
- **Evidence:** Sets `FundingOutput.TransactionId` and `Index = 0` on the input model. Inputs and outputs BIP 69-sorted; builder returns the funding output index.
- **Fix sketch:** Return the result; compute the real output index (BIP69 ordering with change).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-065 Closing transaction builder not implemented
- **Status:** fixed (34757a3)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Transactions/ClosingTransaction.cs` (commented out, `TODO: Find out correct lockTime`)
- **Evidence:** No closing tx; `BaseTransaction.cs`/`FundingTransaction.cs` in the same folder are also dead commented code. Update (ABCD wave 3, `c92d837`): Domain `ClosingTransactionModel` + `LegacyClosingTransactionFactory`, `ClosingFeeCalculator`, Infra.Bitcoin `ClosingTransactionBuilder` (v2, locktime 0, BIP69, `0 sig1 sig2 script` in funding-key order); the dead `Transactions/{Closing,Base,Funding}Transaction.cs` are deleted. BOLT 3 has no closing vector: signed txs are verified with NBitcoin's interpreter.
- **Fix sketch:** New builder for closing_signed and closing_complete; delete the dead files.
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N10-T2

### NL-066 SecretStorageService: GetBasepointPrivateKey and LoadFromIndex throw NotImplementedException
- **Status:** fixed (e3145a5)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs:160,166`
- **Evidence:** The shachain can't be reloaded or used to derive basepoint secrets. Fixed: `Export()`/`Load(entries)` replace `LoadFromIndex` (Load validates bucket placement and the cross-bucket derivations), `GetBasepointPrivateKey` returns the stored key, and out-of-order secrets are rejected.
- **Fix sketch:** Implement both, backed by NL-136.
- **Blocks/Blocked-by:** Blocks NL-035, NL-094
- **Plan ref:** BOLT2 N3-T4

### NL-067 LocalLightningSigner: channel info memory-only; SignWalletTransaction not implemented
- **Status:** fixed (a49e166, 709030c, 910d085, 5c9a8c9, 9e75a2b, a56a013, 658e086)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:52,189`
- **Evidence:** `TODO: Load channel key data from database`; after a restart channels must be re-registered by hand. `SignWalletTransaction` throws. Update: registration now carries `LocalCommitmentNumber` and `RemoteHtlcBasepoint` (615395c), and startup awaits it for every active channel before connecting (NL-201). Key data is still memory-only and `SignWalletTransaction` still throws. Update (gossip wave G-A, `164289a`): first half done. `ChannelSigningInfoDbRepository` reads every signing field from the `Channels`/`ChannelKeySets`/`ChannelConfigs`/`BroadcastTransactions` rows (no private key stored; Closed and Stale channels are not loaded) and `LocalLightningSigner` loads and registers an unknown channel on first use through `IChannelSigningInfoSource` (one scope per lookup, read outside the commitment lock, no negative cache), with the revocation guard, data-loss flag and S1 broadcast mark restored (a49e166, 709030c; proof `SignerStateReloadTests`); a re-registration with other keys or another funding outpoint throws `SignerException` before any guard moves (910d085). The redundant hand registration in `ChannelManager` is NL-343. Remaining: `SignWalletTransaction` (BOLT5 O7-T1). Update (wave O7, `8364a01`): second half done (lane X1). `LocalLightningSigner.SignWalletTransaction` signs P2WPKH and P2TR key-path wallet inputs with `SIGHASH_ALL`, only for UTXOs held by a fee-input reservation (the overload with a reservation id refuses inputs of another reservation), checks the derived key's script against the UTXO's recorded address and script-verifies every signature before writing it; P2TR needs the other spent outputs (BIP 341). `IFeeInputSelector` → `FeeInputSelector` with persisted, outpoint-keyed reservations (migration `AddFeeInputReservations`, all three providers), restored at startup, never selecting outputs of pending broadcasts, confirmed only after the chain monitor saw the spend (5c9a8c9, 9e75a2b, a56a013). The integrator wired it to the CPFP and anchors HTLC ports and passes the P2TR prevouts (658e086). Tests: `LocalLightningSignerWalletTests`, `FeeInputSelectorTests`, SQLite `FeeInputReservationPersistenceTests`, Postgres `FeeInputReservationSchemaRoundTrip`, Docker anchors proofs (12/12).
- **Fix sketch:** Load channel key data from the DB on demand; implement wallet signing.
- **Blocks/Blocked-by:** Blocks NL-035, NL-094
- **Plan ref:** BOLT2 N1-T6 (re-registration only)

### NL-343 ChannelManager still registers every channel with the signer by hand
- **Status:** fixed (2555443)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`RegisterExistingChannelLockedAsync`, the `GetBroadcastSignedCommitmentNumberAsync` lookup, `ConfirmFundingAsync`)
- **Evidence:** Since NL-067's first half (709030c) the signer loads an unknown channel from the DB on first use, so the startup registration and the one at funding confirmation are redundant (harmless: registration is idempotent and a mismatch throws before any guard moves, 910d085). A signer without an `IChannelSigningInfoSource` (tests only) would still need a re-registration after a reorg moves the SCID. Also: the lazy load has no negative cache (each lookup of an unknown id is a synchronous DB read, deliberately), and a DB failure surfaces as "not registered" (logged as an error by `ChannelSigningInfoSource`); `SignChannelTransaction` on an unregistered channel throws `InvalidOperationException`, not `SignerException`. Update (gossip wave G-B, `5bbfbb5`): `ChannelManager` registers a channel with the signer (at startup and at a funding confirmation of a channel not in memory) only when no `IChannelSigningInfoSource` is registered; startup then also skips the broadcast-row read. A source-less signer (in-process harnesses) is still registered with the S1 mark and re-registered after the funding confirmation so it knows the real SCID (needed by `SignChannelAnnouncement`). Regression tests in `StartupStateTests` and `ChannelManagerTests` (2555443). The reorg case of a source-less signer is NL-350, fixed in gossip wave G-C: `FundingReconfirmationHandler` re-registers it when the SCID moves (7a4ef7f).
- **Fix sketch:** A lane that owns `ChannelManager` drops the hand registration (keep the SCID refresh if a source-less signer must know it).
- **Blocks/Blocked-by:** Related NL-067
- **Plan ref:** —

### NL-068 DustService is not registered in DI
- **Status:** fixed (3805db9)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/DustService.cs`
- **Evidence:** Implemented and unused.
- **Fix sketch:** Register in `AddBitcoinInfrastructure` when HTLC trimming needs it.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N10-T1

### NL-069 CommitmentNumber ctor names (local, remote) but needs (opener, accepter); Increment mutates
- **Status:** fixed (b3d2881)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Models/CommitmentNumber.cs`
- **Evidence:** Misleading names caused NL-127.
- **Fix sketch:** Rename parameters to opener/accepter; consider immutability.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N1-T1

### NL-189 Local commitment key derivation computes the current per-commitment secret; no revocation guard
- **Status:** fixed (615395c)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/CommitmentKeyDerivationService.cs:27`, `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs`
- **Evidence:** `DeriveLocalCommitmentKeys` calls `ReleasePerCommitmentSecret` for the current commitment and returns it in `CommitmentKeys.PerCommitmentSecret`. It is not sent today, but nothing stops a caller from revealing the secret of an unrevoked commitment, which would let the peer take all channel funds. Fixed: `RevealPerCommitmentSecret(channelId, n)` throws unless n < the signer's local commitment number, which only `AdvanceLocalCommitment` (after persistence) moves; local keys are derived from the point and `CommitmentKeys` carries no secret.
- **Fix sketch:** Derive local keys from the point; make secret release internal behind a guard `n < LocalCommitted` advanced only after persistence; remove the secret from `CommitmentKeys`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N3-T2

### NL-195 option_anchors HTLC trim fee uses 666/706 weights instead of zero
- **Status:** fixed (e053fb8)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:105-115`, `src/NLightning.Domain/Bitcoin/Transactions/Constants/WeightConstants.cs:30-33`
- **Evidence:** With anchors the HTLC-timeout/success fee is 0 (zero-fee HTLC txs), so trimming uses the dust limit alone; the factory uses weights 666/706. Anchors default No. Fixed: `CommitmentFeeCalculator` uses HTLC tx fee 0 with anchors; Appendix F byte-exact. The duplicate fee code is NL-231.
- **Fix sketch:** Fee 0 with anchors in a `CommitmentFeeCalculator`; Appendix F vectors.
- **Blocks/Blocked-by:** Related NL-061
- **Plan ref:** BOLT2 N2-T1

### NL-196 Commitment factory throws when both outputs are below the channel reserve
- **Status:** fixed (e053fb8)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:189-193`
- **Evidence:** The reserve is an update-validation rule, not a tx-building rule; Appendix C's "fee greater than funder amount" case needs the tx to build. Fixed: the factory no longer checks the reserve and the Appendix C 'fee greater than funder amount' case builds; the reserve is enforced by the N4 `UpdateValidator` and at open by `ChannelOpenValidator` (NL-220).
- **Fix sketch:** Remove the check from the factory; enforce the reserve in the update validator.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N2-T1

### NL-202 LightningMoney is a mutable reference type
- **Status:** open (partial: c1f215f, 0edfa14)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Money/LightningMoney.cs:7,19-25`
- **Evidence:** A class with a public `MilliSatoshi` setter; shared instances in commitment math can be changed through aliasing. Update: the N4 engine does its arithmetic in checked `ulong` msat and never uses `LightningMoney`; the type itself is still mutable.
- **Fix sketch:** Keep commitment-engine arithmetic in `ulong` msat with `checked`; consider making `LightningMoney` immutable.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N4-T1

### NL-230 Engine and builder use separate spec, signature and signer-port types; nothing adapts them
- **Status:** fixed (2fa8cf4)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Commitments/{CommitmentSpec,CommitmentSignatures,Interfaces/ICommitmentSigner,Interfaces/ICommitmentVerifier}.cs`, `src/NLightning.Domain/Channels/{Commitments/CommitmentTxSpec,Interfaces/ICommitmentSigner,Interfaces/ICommitmentVerifier}.cs`, `src/NLightning.Application/Channels/Services/CommitmentSigningService.cs`
- **Evidence:** The N4 engine's ports take a channel id + `CommitmentSpec` (holder view, `SpecHtlc`) and return `CommitmentSignatures`; `CommitmentSigningService` implements the other pair (channel + `CommitmentTxSpec`, returns `CommitmentTxSignatures`). The lanes were integrated by renaming (f4bf350); no code converts a `CommitmentSpec` into a `CommitmentTxSpec` and nothing implements the engine ports, so the engine cannot sign or verify a real commitment yet. Update (ABCD wave 0, `0b7e617`): one port family: the duplicates in `Domain/Channels/Interfaces/ICommitment{Signer,Verifier}.cs` are deleted; `EngineCommitmentSignerPort`/`EngineCommitmentVerifierPort`/`EngineRevocationVerifierPort` (Application, registered by `AddCommitmentEngineServices`) adapt via `CommitmentTxSpec.FromCommitmentSpec`, `CommitmentParams.FromChannel` and `CommitmentTxSignatures.ToCommitmentSignatures` over `CommitmentSigningService` (now a concrete class) and `IPerCommitmentSecretVerifier`. Proof: `EngineCommitmentPortsTwoNodeTests` (real `LocalLightningSigner` on both sides, add/CS/RAA/fulfill/fail/update_fee with and without anchors, txid signed == txid verified at every commitment; tampered/swapped sigs fail B2-CS-R01). Follow-up: NL-244.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Blocks NL-031 (N5/N6)
- **Plan ref:** BOLT2 N5 prerequisite, N6-T1

### NL-231 Two BOLT 3 commitment fee calculators
- **Status:** fixed (192e212, c68a34d)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Commitments/CommitmentFees.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentFeeCalculator.cs`
- **Evidence:** The N4 engine (`CommitmentFees`) and the N2 factory (`CommitmentFeeCalculator`) implement the same weights and trimming separately; both are vector-tested today, but a fix to one can silently diverge from the other, and a divergence means our signature over the peer's commitment is invalid. Update (ABCD wave 0, `0b7e617`): `CommitmentFees` is deleted; `CommitmentFeeCalculator` holds the formulas once (`…Satoshis` members) and the engine, `UpdateValidator`, the test kits and the simulator call it; `CommitmentFeeCalculatorSpecTests` has a parity theory between the factory and spec overloads.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-230
- **Plan ref:** BOLT2 N5/N6 seam

### NL-244 CommitmentTxSpec.FromCommitmentSpec builds Htlc values with a null AddMessage
- **Status:** fixed (1de15f9)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Commitments/CommitmentTxSpec.cs:90`, `src/NLightning.Domain/Channels/Models/Htlc.cs`
- **Evidence:** The engine-to-builder adapter passes `null!` for the non-nullable `Htlc.AddMessage` because no builder reads it (documented in `src/NLightning.Domain/CLAUDE.md`); any future reader gets a NullReferenceException (reported by the W0-A lane, finding 3). Update (ABCD wave 1, `342d22e`): `Htlc.AddMessage` is nullable and `CommitmentTxSpec.FromCommitmentSpec` passes null without suppression; regression test in `EnginePortTests`.
- **Fix sketch:** Give the builders a slim HTLC input type or make `AddMessage` nullable.
- **Blocks/Blocked-by:** Related NL-230
- **Plan ref:** —

---

## BOLT 4: Onion routing

### NL-070 [EPIC] Error onions: failure messages, create / wrap / decrypt (ONION M3)
- **Status:** fixed (ded60a1, ce3cfeb, 9b2e294, 37df603, a657719, 3c1d68a, a42c33b)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Onion/{Models/FailureMessage,Interfaces/IFailureOnionService,Interpreters/FailureInterpreter}.cs`, `src/NLightning.Infrastructure.Bitcoin/Onion/FailureOnionService.cs`, `src/NLightning.Infrastructure.Serialization/Onion/FailureMessageSerializer.cs`
- **Evidence:** Only failure codes exist; `onion-error-test.json` packets are unused (only per-hop keys are checked). `um`/`ammag` keys are derivable via `SphinxKeyGenerator`. `ammagext` label unconfirmed. Fixed (ONION M3): `FailureMessage` + `FailureMessageSerializer` for every BOLT 4 code (with legacy 17 and PERM|16 recognised on receipt), `IFailureOnionService`/`FailureOnionService` create/wrap/decrypt (constant max(27, hops) iterations, constant-time HMAC) byte-exact against onion-error-test.json and the inline Returning Errors trace at every hop, malformed conversion (NL-071), `FailureChannelUpdateFactory` and the origin-side `FailureInterpreter`. attribution_data stays open (NL-072, M3b); nothing calls the error onion until N6/N8.
- **Fix sketch:** M3-T1..T3 per plan: model + serializer, create/wrap/constant-27-iteration decrypt, byte-exact against `onion-error-test.json` and the inline BOLT 4 trace. Sub-issues: NL-071, NL-072.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M3

### NL-071 update_fail_malformed_htlc → update_fail_htlc conversion missing
- **Status:** fixed (9b2e294, 3c1d68a)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Onion/Validators/MalformedHtlcValidator.cs`, `src/NLightning.Infrastructure.Bitcoin/Onion/FailureOnionService.cs`
- **Evidence:** No code path. Fixed: `FailureMessage.FromMalformed` (BADONION required), `MalformedHtlcValidator` (an all-zero sha256_of_onion is valid, e.g. `invalid_onion_blinding`) and `IFailureOnionService.CreateErrorPacketFromMalformed`; `MalformedFailureConversionTests` against BOLT 4 vectors.
- **Fix sketch:** M3-T3; reject non-BADONION codes (NL-023).
- **Blocks/Blocked-by:** Part of NL-070
- **Plan ref:** ONION M3-T3

### NL-072 attribution_data (error attribution) not implemented
- **Status:** fixed (d5866a6, 9692ba4, 3a54e11, 06b906c, e64da4e, 672ed61, 4c37998)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:69`
- **Evidence:** Advertised Optional; no TLV, no HMAC chain, no hold times. Update (ABCD wave 6, `3ce3cad`): the attribution_data library is done: `IAttributionDataService` (Domain) → `AttributionDataService` (Infrastructure.Bitcoin, `AddOnionAttributionServices`, registered by `AddBitcoinInfrastructure` in 9692ba4): create/wrap/origin-verify for failures (per-hop truncated HMACs, hold times, blamed hop) and fulfills (hold times, `fulfillment_payload`), byte-exact at every hop against the BOLT 4 Returning Errors and Returning success traces (d5866a6; the traces put hold time 1 on the erring/final node, recorded in `BOLT4/Vectors/README.md`). Not used by `HtlcSwitch`/`PaymentService`; `OptionAttributionData` stays experimental and No. Remaining: the switch/send seam and the persistence it needs (NL-326). Update (ABCD wave 7, `4c37998`): wired end to end: the channel layer persists and sends attribution_data (NL-326), `PaymentService` verifies it at the origin (blamed hop, hold times on `PaymentHops.HoldTimeMs`), and `HtlcSwitch` creates/wraps it when we advertise `OptionAttributionData` and the incoming add had no `path_key` (672ed61). Proofs: `AttributionHarnessTests`, Docker `AttributionFlowTests` 5/5 (LND 0.20 accepts our TLV 1 but does not advertise bits 36/37 or send attribution; three NLightning nodes with the feature on record both hops' hold times through the production switch, 4c37998). Remaining: `OptionAttributionData` stays in `ExperimentalFeatures` because no LND interop proof is possible yet (NL-332); follow-ups NL-333, NL-334, NL-339. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): closed: attribution_data is implemented end to end (library `AttributionDataService`, persistence, `HtlcSwitch.cs:112,155` and `PaymentService.cs:127,157` take `IAttributionDataService`); the entry's "not implemented" no longer holds. What is left is only the default: `OptionAttributionData` is in `FeatureOptions.ExperimentalFeatures` (`FeatureOptions.cs:29`) and defaults to No (`FeatureOptions.cs:150`) until an interop proof exists, tracked by NL-332; follow-ups NL-333, NL-334.
- **Fix sketch:** M3b after M3; until then default to No (NL-074).
- **Blocks/Blocked-by:** Blocked-by NL-070, NL-022
- **Plan ref:** ONION M3b

### NL-073 [EPIC] Onion integration with HTLC flow: peel after lock-in, forward, final hop, send (ONION M4)
- **Status:** fixed (6156173, 234607e, a02afa7, ca87313, c4ad8e9, d1476a4, 6cb279f, 083a726, f2f1ef6)
- **Severity:** high
- **Kind:** gap
- **Location:** planned `src/NLightning.Application/Payments/` (`HtlcSwitch`, `HtlcForwardingPolicy`, `FinalHopProcessor`, `PaymentManager`)
- **Evidence:** Nothing calls peel → replay → deserialize → validate. No forwarding, no final-hop checks, no sending. Update (ABCD wave 0, `0b7e617`): the engine-side gates exist: `IncomingHtlcLockedIn` fires once at lock-in, `OutgoingHtlcFailed` only when the removal is irrevocable, `OutgoingHtlcFulfilled` at once, all re-derivable at startup with `ChannelDomainEvents.DerivePending` (b166ea0); `IForwardingPolicy`/`ForwardingFee` (BOLT 7 fee formula), `ForwardCircuitModel` and `HtlcOrigin` contracts are in Domain (2ede2ee, 1390027). No processor, policy implementation or switch yet (ABCD W1-B, W2-B). Update (ABCD wave 1, `342d22e`): the payment core exists in `Application/Payments/` (6156173, 234607e): `IncomingOnionProcessor` (peel, replay record after a good peel, payload parse/validate, forward/final/malformed/failed results; route blinding refused), `FinalHopProcessor` (0x0013/0x0012 before the 0x400F invoice checks, read-only), `HtlcForwardingPolicy : IForwardingPolicy` (reads `RoutingOptions` on every call), `HintRouteBuilder` (mandatory fee limit) and `PaymentOnionFactory`. `LocalOnlyHtlcSwitch` peels every locked-in HTLC and fails it back (a02afa7). Nothing in `src/` calls the processor, policy or route builder yet: the forwarding switch (M4-T2 wiring, T4, T5, replay) is W2-B and send is W2-C. The final-hop accept must be atomic (NL-253) and the offered HTLC's origin persisted (NL-250). Update (ABCD wave 2, `a5675cb`): M4 is wired. `Application/Payments/Switch/HtlcSwitch` (registered by `AddHtlcSwitchServices`, called from `AddApplicationServices`) handles several cases. It peels locked-in HTLCs, storing the shared secret first. It runs the final hop under a per-payment-hash lock, with the invoice settled in the fulfill's own save (NL-253). It forwards by scid (real, or alias per `option_scid_alias`) with `HtlcForwardingPolicy`, saves the circuit Pending before the offer and marks it Offered after. It fulfills upstream immediately and fails upstream only when the failure is irrevocable, wrapping with the incoming secret and converting malformed. It replays after restart or link-up (`LinkUpEventReplayer`) (ca87313, d1476a4). The send side `Payments/Send/PaymentService` decodes the invoice, routes directly or through the first usable hint and persists the per-hop secrets before `OfferHtlcAsync(HtlcOrigin.Local)`. It decrypts failures with `FailureInterpreter` (6cb279f, 083a726) and is wired to the switch through `PaymentOutcomeSwitchHandler` (f2f1ef6). Proofs: `ThreeNodeSwitchTests` on SQLite with restarts (c4ad8e9), `PaymentHarnessTests`, and the ABCD Docker suite (happy path with exact BOLT 7 fees, variant a decoded at index 3, b1/b2 restarts, c send/receive). Remaining, tracked elsewhere: persistent replay set (NL-078, deferred by roadmap decision 5), attribution_data (NL-072), route blinding (NL-079), alias channel_update in failures (NL-266), forward checks before the first block (NL-267).
- **Fix sketch:** M4-T1..T7 per plan. Prereqs: NL-031, NL-075, NL-078, NL-101, NL-102, NL-167, NL-137, NL-114.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-070
- **Plan ref:** ONION M4; BOLT2 N6-T1, N8 (direct-channel slice)

### NL-074 route_blinding and attribution_data advertised Optional without implementation
- **Status:** fixed (0dd030e)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:55,69`
- **Evidence:** Peers may send blinded HTLCs or attribution TLVs we cannot process.
- **Fix sketch:** Default both to No until M5/M3b.
- **Blocks/Blocked-by:** Related NL-109
- **Plan ref:** ONION_ROUTING_PLAN §9 risk 7; BOLT2 N0-T4

### NL-075 IHopPayloadSerializer declared in Infrastructure.Serialization (Application can't use it)
- **Status:** fixed (2abecac)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/Interfaces/IHopPayloadSerializer.cs`
- **Evidence:** Application must not reference Serialization; M4 HtlcSwitch needs the interface.
- **Fix sketch:** Move to `src/NLightning.Domain/Serialization/Interfaces/`.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M4 prerequisites; BOLT2 N8-T1

### NL-076 IHopPayloadSerializer resolves only if AddBitcoinInfrastructure registered ITlvConverterFactory
- **Status:** fixed (2abecac, d36bc41)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/DependencyInjection.cs`, `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs`
- **Evidence:** Hidden cross-layer DI dependency (same for `TlvStreamSerializer`).
- **Fix sketch:** Register `ITlvConverterFactory` in the Infrastructure layer that owns it.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 payload open item

### NL-077 current_path_key (TLV 12) is not curve-validated
- **Status:** fixed (6b2c3d61)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Tlv/Converters/Onion/CurrentPathKeyTlvConverter.cs`
- **Evidence:** Length/prefix only; `0x02||ff×32` passes (test documents it). The update_add path_key is validated by the peeler. Update (wave rf1, `wip/fafo` at `be9fd000`): `RouteBlindingService` curve-checks every path_key, including a payload's `current_path_key`, and the processor maps a failure to `invalid_onion_blinding` (`RouteBlindingServiceTests`).
- **Fix sketch:** Validate via `ISecp256K1Math` in M5 and map to `invalid_onion_blinding`.
- **Blocks/Blocked-by:** Part of NL-079
- **Plan ref:** ONION M5

### NL-078 Onion replay cache is in-memory FIFO, not cltv-keyed or persistent
- **Status:** fixed (a2b57ff, 53c038c)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Onion/PersistentOnionReplayStore.cs`, `Application/Payments/IncomingOnionProcessor.cs`
- **Evidence:** 100k-entry FIFO; replays older than capacity or across restarts are accepted. Update (ABCD wave 2, `a5675cb`): still in memory, by ABCD roadmap decision 5 (regtest only). The switch skips the replay check (`checkReplay: false`) for an HTLC whose shared secret is already stored, so a restart replays persisted HTLCs without tripping the cache (ca87313). A restart still forgets the replay entries of onions that are not stored. Update (ABCD wave 3, `c92d837`): `IOnionReplayStore` (Domain) with `InMemoryOnionReplayStore` (Infrastructure, `AddOnionReplayStore()`): entries are owned by the incoming HTLC and expire at its cltv_expiry, ready to be backed by a table. Not yet wired into the switch in place of `OnionReplayCache`, and not persisted. Update (ABCD wave 6, `3ce3cad`): `PersistentOnionReplayStore : IOnionReplayStore` over the new `OnionReplayEntries` table (migration `AddOnionReplaySet`, all three providers, PK HMAC, owner channel + HTLC id, `ExpiryHeight` indexed); each add commits its own save before the HTLC is forwarded or fulfilled; the same HTLC is not a replay, any other is; pruned below the chain tip at most once per height inside `TryAddAsync` or by `PruneAsync` (a2b57ff). `IOnionReplayCache`/`OnionReplayCache` removed. The owner is required (`ProcessAsync(packet, hash, OnionReplayOwner? replayOwner, pathKey)`, null = no replay check, replacing `checkReplay: false`), and a crash between the HMAC save and the secret save is proven not to become a replay (53c038c, `ThreeNodeSwitchTests`). Proofs: SQLite restart replay with a real onion, pruning, seeded-upgrade round trips on SQLite/Postgres/SQL Server. Limits: one node process per database (check-and-insert serialized in process only); a replayed onion always fails (the BOLT 4 MAY-redeem is not implemented); no Docker replay proof (LND cannot be told to re-send an onion); pruning only when an onion arrives (NL-327).
- **Fix sketch:** Key by HMAC with cltv_expiry eviction; persist (see NL-137).
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M4-T2/T7; M2 review issue 9 (partial)

### NL-079 [EPIC] Route blinding payload handling (ONION M5)
- **Status:** fixed (6b2c3d61, 5d6e9770, f696e3bd, 01aff502, 8675ca37, 2be510fc, eaeb2797)
- **Severity:** medium
- **Kind:** gap
- **Location:** planned `src/NLightning.Infrastructure.Bitcoin/Onion/RouteBlinding/`
- **Evidence:** Peel applies the path_key tweak and exposes `PathKeySharedSecret`, but no `encrypted_recipient_data` decrypt, no blinded path builder, no `invalid_onion_blinding` remap in the validator. `route-blinding-test.json` is only loaded. Update (wave rf1, `wip/fafo` at `be9fd000`): M5 done (lane rf1-m5): route-blinding crypto and `encrypted_data_tlv` codec byte-exact against `route-blinding-test.json` and `blinded-payment-onion-test.json`; `IncomingOnionProcessor`/`HtlcSwitch` forward and receive blinded payments with the `invalid_onion_blinding` rules (introduction-node errors delayed, malformed inside the path); `PaymentService.PayBlindedAsync` sends to blinded paths and `BlindedPathBuilder` builds ours; `option_route_blinding` advertised Optional (2be510fc); Docker `Docker/Gossip/RouteBlindingFlowTests` against LND 0.20 (4/4). Limits: NL-440. Note (wave M6, `641a5fff`): the onion message commits cite NL-079 as the plan's parent epic; that work is recorded on NL-080.
- **Fix sketch:** M5 per plan. Sub-issues: NL-077, NL-026.
- **Blocks/Blocked-by:** Blocked-by NL-073
- **Plan ref:** ONION M5

### NL-080 Onion messages (type 513) not implemented (ONION M6)
- **Status:** fixed (a16baab2, 6aea59de, a99af0d2, db29eccd, 47adc130, 3fdf4b00, abd81802, 7f0afe02, 413d420b, 3149da79, 76ee51af, e4c10d9c, b81a1fd9, dcd9d5d8, 365e3128, fc686ff0, 9639b7cf, 641a5fff)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Serialization/Payloads/OnionMessagePayloadSerializer.cs`, `src/NLightning.Domain/Protocol/OnionMessages/`, `src/NLightning.Infrastructure.Bitcoin/Onion/OnionMessages/`, `src/NLightning.Infrastructure/Node/Services/PeerService.cs`, `src/NLightning.Application/Node/Services/PeerOutbox.cs`, `src/NLightning.Application/OnionMessages/`
- **Evidence:** No type; `PeerService.HandleMessage` drops non-channel messages; `IPeerService.SendMessageAsync` only accepts `IChannelMessage`. Sphinx core already supports `OnionPacketKind.OnionMessage`. Update (wave M6, `wip/fafo` at `641a5fff`; the commits cite NL-079): done per `BOLT12_PLAN.md` OM0-OM3. OM0 (M6-A): the 513 serializer in both factory dictionaries, the strict `OnionMessageTlvsCodec`, `BlindedPathCodec`/`SciddirOrPubkeyCodec` (byte-exact against `blinded-onion-message-onion-test.json` and the `bolt12/offers-test.json` offer_paths) and `MessagePathRecipientDataRules` (a16baab2..3fdf4b00). OM1 (M6-B): `BlindedMessagePathBuilder`, `OnionMessagePacketBuilder`, `OnionMessageUnwrapper`, byte-exact for generate, route, packet and every decrypt hop; the vector's `generate.session_key` is the Sphinx session key (abd81802, 7f0afe02). OM2-T1/T2 (M6-C): the `PeerService` 513 arm and gated `SendOnionMessageAsync`, the capped low-priority onion class in `PeerOutbox` (interleaved 1 per 8 gossip sends, never ahead of channel/warning/error/disconnect), the Domain port `IPeerOnionMessageOutbox` on `PeerManager`, `OnionMessageRateLimiter` per peer and node-wide (413d420b, 3149da79, 76ee51af). OM2-T3..T5/OM3 (M6-D): `OnionMessageService` (rate limit, queue, peel, strict decode, unblind, reader rules, forward by node id or SCID through the capped outbox, deliver by type, `PendingReplyRegistry`), `OnionMessagePathFinder`, `ReplyPathFactory`, `OnionMessageOptions` (`OnionMessages` section; invalid options keep the service off), `Meter("NLightning.OnionMessages")`, the three-node harness and the BOLT 4 vector through four service nodes (e4c10d9c, b81a1fd9). Integration: `IPeerOnionMessageOutbox`, the outbox cap from `OnionMessages:MaxOutboxPerPeer`, the rate limits from `OnionMessageOptions`, the service on lane A's Domain codec (fc686ff0, 9639b7cf). Proof M6 (M6-E): `Docker/Interop/Cln/ClnOnionMessageTests` 10/10 against CLN v26.06.8 (CLN as our prefix hop and introduction node, `injectonionmessage`, `fetchinvoice` answered with `invoice_error` through CLN's reply path and through an offer path we forward, our reply path through CLN, a direct burst dropped by the rate limit, a corrupt HMAC ignored, bit 39 both ways; dcd9d5d8, 365e3128). D9: `OptionOnionMessages` Optional by default and out of `ExperimentalFeatures` (641a5fff). Follow-ups: NL-442, NL-444, NL-446; BOLT 12 offers NL-447.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Blocked-by NL-079; blocks NL-447
- **Plan ref:** ONION M6; `BOLT12_PLAN.md` OM0-OM3, Proof M6

### NL-081 Basic MPP (final-hop HTLC sets) missing while basic_mpp is advertised
- **Status:** fixed (ba3db36, 451aa79, 78a3beb, 9692ba4)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`, `Switch/HtlcSet.cs`, `FinalHop/FinalHopProcessor.cs`, `src/NLightning.Domain/Node/Options/FeatureOptions.cs`
- **Evidence:** `BasicMpp` default Optional; no HTLC set handling, no `total_msat` / MPP timeout (0x0017). Update: `basic_mpp` now defaults to No and is experimental-gated (e93eb41). Update (ABCD wave 6, `3ce3cad`): `basic_mpp` receive: the switch holds an in-memory `HtlcSet` per payment hash (rebuilt from persisted incoming HTLCs by the startup and link-up replays, no schema change) until the parts reach `total_msat`, then fulfills every part with the invoice settled in the first fulfill's save; a `total_msat` mismatch fails the whole set (incorrect_or_unknown_payment_details); an incomplete set gets `mpp_timeout` after `HtlcSwitchOptions.MppTimeout` (60 s, `Node:Switch`, bound in 9692ba4); `BasicMpp` defaults to Optional, leaves `ExperimentalFeatures` and is set (bit 17 optional) in our invoices (ba3db36). Docker `MppFlowTests`: LND pays our 450k sat invoice in 2 parts over two channels (451aa79). Review: every part of a settled set is fulfilled at any replay height (and height 0), a part resolved while waiting for the hash lock is skipped, AmountReceived counts only the held parts, the container disposes the switch behind `DustExposureHtlcSwitch` (78a3beb). Follow-ups: NL-322, NL-323.
- **Fix sketch:** Implement in FinalHopProcessor (M4-T3); stop advertising until then (NL-109).
- **Blocks/Blocked-by:** Blocked-by NL-073
- **Plan ref:** ONION M4-T3; BOLT2 N0-T4 (stop advertising)

### NL-082 PeelAsLocalNode does a node-key EC multiplication per call and copies the key
- **Status:** fixed (896e3e6, 0b96267)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxService.cs`, `src/NLightning.Domain/Protocol/Interfaces/ISecureKeyManager.cs`
- **Evidence:** Copy is now zeroed (ca141a6), but each peel still materializes the private key.
- **Fix sketch:** Add an ECDH-with-node-key method to the key manager (touches daemon + Docker DI).
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issue 16 (partial)

### NL-083 Sphinx hot path allocates per hop (SphinxKeyGenerator / Sha256)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxKeyGenerator.cs`, `src/NLightning.Infrastructure/Crypto/Hashes/Sha256.cs:21`
- **Evidence:** One generator fewer per build after M2 review; no pooling, no benchmark. Partial (cfc9219, 831712e): node ECDH now 376 B/call instead of 952 (allocation test <= 512). Remaining: no BenchmarkDotNet benchmark, no `SphinxKeyGenerator` pooling, one managed key copy per call.
- **Fix sketch:** Benchmark, then pool generators.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issue 17 (partial); ONION_ROUTING_PLAN §9 risk 6

### NL-084 Truncated-int encoder duplicated in Domain and Infrastructure
- **Status:** fixed (8ba8530, d36bc41)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Onion/Tlv/TruncatedIntEncoder.cs`, `src/NLightning.Infrastructure/Converters/TruncatedInt.cs`
- **Evidence:** Domain can't reference Infrastructure, so an encode-only copy exists.
- **Fix sketch:** Keep one BCL-only implementation in Domain and use it from both.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T8 open item

### NL-085 Missing onion crypto primitives: HMAC (any key), raw ChaCha20 keystream, public EC tweak math
- **Status:** fixed (af0e16f, 6562629, 45df180)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs`, `ICryptoProvider.StreamChaCha20IetfXor` (3 providers), `src/NLightning.Domain/Crypto/Interfaces/ISecp256K1Math.cs`
- **Evidence:** Previously only the private 32-byte-key `Hkdf.HmacHash`, AEAD-only ChaCha20, and private EC helpers in `KeyDerivationService`.
- **Fix sketch:** Done (RFC 4231 / RFC 8439 vectors, BOLT 4 hop-0 ECDH).
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T1..T4

### NL-086 No Sphinx construct/peel, onion packet or hop payload model
- **Status:** fixed (65d6c02, 737df73, a30aa49, 224e543, c0e1cb0, 2638a1d, 2f73f45)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Onion/`, `src/NLightning.Infrastructure.Bitcoin/Onion/`, `src/NLightning.Infrastructure.Serialization/Onion/HopPayloadSerializer.cs`
- **Evidence:** Re-implemented from spec (LNBolt not ported, see `LNBOLT_REVIEW.md`); byte-exact against `onion-test.json`, peel chain against `blinded-payment-onion-test.json` and `blinded-onion-message-onion-test.json`.
- **Fix sketch:** Done. Follow-ups tracked in NL-070, NL-073, NL-079.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T8, M2

### NL-087 default(OnionPacket) threw NullReferenceException
- **Status:** fixed (dc287a6)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Protocol/Onion/ValueObjects/OnionPacket.cs`
- **Evidence:** Accessors now throw `InvalidOperationException`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issue 7

### NL-088 JS ChaCha20 provider and KeyDerivationService left secrets on the heap
- **Status:** fixed (8683169)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs`, `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs`
- **Evidence:** Buffers now zeroed in `finally`; the JS change was not compiled locally (Wasm is linux-only, see NL-169).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issues 5-6

### NL-089 FailureCode UPDATE-flag docs outdated; path keys undocumented as unvalidated
- **Status:** fixed (8b541ca)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Onion/Enums/FailureCodeFlags.cs`, `FailureCode.cs`, `CurrentPathKeyTlv.cs`, `BlindedPathTlv.cs`
- **Evidence:** Docs now match BOLT 4 (channel_update may be empty).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issues 3-4

### NL-090 Sphinx peel/construct review defects (blinded failure codes, framing secret, onion-message lengths, lost secrets)
- **Status:** fixed (ca141a6)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/{OnionPeeler,OnionBuilder,SphinxService}.cs`, `src/NLightning.Domain/Protocol/Onion/Models/{ConstructedOnion,PeeledOnion}.cs`
- **Evidence:** With a path_key failures now map to `invalid_onion_blinding`; framing failures carry `OnionException.SharedSecret`; `OnionPacketKind` allows 0/1-byte onion-message payloads; `ConstructWithSharedSecrets` and `PathKeySharedSecret` keep secrets; blinded vector construct/peel tests added.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issues 3, 5, 12-15

### NL-091 HopPayloadValidator: unknown odd TLVs accepted in blinded hops; scid rejected at non-blinded final hop
- **Status:** fixed (993f24e)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Onion/Validators/HopPayloadValidator.cs`
- **Evidence:** Blinded hops now use a strict allowlist; the final-hop scid rule is writer-only, so the reader ignores it.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issues 2, 8

### NL-092 invalid_onion_payload offsets differed between the two deserialize paths
- **Status:** fixed (896b531)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Onion/HopPayloadSerializer.cs`
- **Evidence:** Offsets now count the stripped bigsize length prefix in both paths.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issue 4

### NL-093 Plan/ISphinxService said to peel with the payload's current_path_key; no DI resolution test
- **Status:** fixed (fba7dfe)
- **Severity:** medium
- **Kind:** bug
- **Location:** `docs/agents/ONION_ROUTING_PLAN.md`, `src/NLightning.Domain/Protocol/Onion/Interfaces/ISphinxService.cs`, `test/NLightning.Integration.Tests/BOLT4/OnionServiceRegistrationTests.cs`
- **Evidence:** Only the path_key received with the onion is used; replay recording moved after HMAC verification; DI resolution test added.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issues 1, 9, 19

### NL-250 OfferHtlcAsync does not persist the HtlcOrigin with the add
- **Status:** fixed (4ca9b56)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ChannelOperationsService.cs` (`OfferHtlcAsync`)
- **Evidence:** The `IChannelOperations` contract says the origin (Local payment hash or Forwarded incoming HTLC) is saved atomically with the add, so a restart can tie the outgoing HTLC back to its payment or circuit. `OfferHtlcAsync` validates the origin but does not store it (a02afa7); `IChannelStateDbRepository.SetHtlcOriginAsync` exists since 899e36b (reported by W1-A, integrator). Update (ABCD wave 2, `a5675cb`): `OfferHtlcAsync` stages `SetHtlcOriginAsync` after `ApplyAsync` in the same save, through the new optional `stageWithTransition` callback of `ChannelStateTransitionService.CommitAsync` (4ca9b56). Regression tests are in `ChannelOperationsServiceTests`, and `ThreeNodeHarness` covers it on real SQLite.
- **Fix sketch:** Call `SetHtlcOriginAsync(channelId, key, origin)` after `ApplyAsync` in the same unit of work, together with the payment/circuit rows (W2-B/W2-C).
- **Blocks/Blocked-by:** Blocks NL-073 (restart mid-forward); related NL-137
- **Plan ref:** ONION M4-T7; ABCD W2-B, W2-C

### NL-253 Final-hop invoice accept must be an atomic check-and-mark
- **Status:** fixed (ca87313, d1476a4)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs` (read-only by design), future `Application/Payments/Switch/HtlcSwitch`
- **Evidence:** `FinalHopProcessor` never mutates the invoice, so two concurrent HTLCs for the same payment hash can both pass `Evaluate` and both be fulfilled; a second run for an already Accepted invoice fails with 0x400F, so a restart must act on the HTLC's persisted state instead of re-running the final hop (`IncomingOnionProcessor.ProcessAsync(checkReplay: false)` exists for that path) (reported by W1-B). Update (ABCD wave 2, `a5675cb`): under a per-payment-hash lock, the switch re-reads the invoice and runs `FinalHopProcessor.Evaluate`. It stages Accept, then Settle, then `UpdateAsync` on the fulfill's own unit of work, through the new `IChannelOperations.FulfillHtlcAsync(..., stageWithFulfill, ct)` overload, so the invoice and the fulfill commit in one save. A second HTLC for the hash gets PERM|15 (ca87313, d1476a4). The interleaving is proven deterministically (`HookedUnitOfWork.AfterInvoiceRead`; the test fails when the lock is removed). Update (ABCD wave 6, `3ce3cad`): `checkReplay: false` is now `replayOwner: null` (53c038c). With `basic_mpp` on, a second HTLC with the right secret for a Settled invoice is fulfilled (NL-323), so the concurrency test is pinned to `BasicMpp=No` (78a3beb).
- **Fix sketch:** In the switch, under a per-payment-hash lock and in the same unit of work as the staged fulfill: re-read the invoice, `Evaluate`, `Accept` + `UpdateAsync`, save (or compare-and-set the status); fail the loser with PERM|15.
- **Blocks/Blocked-by:** Part of NL-073; related NL-114
- **Plan ref:** ONION M4-T3; ABCD W2-B

### NL-257 Forwarded onions that name a local alias fail after a restart (not reproduced)
- **Status:** wontfix
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (scid resolution)
- **Evidence:** W2-B reported that `LocalAliases` are not persisted, so the switch would answer unknown_next_peer after a restart. Not reproduced at `a5675cb`: aliases are persisted in `ChannelLocalAliases` (NL-103) and reloaded (`ChannelDbRepository.cs:426`), and the switch matches `LocalAliases` (`HtlcSwitch.cs:847`). Wontfix: not a bug. Reopen only with a failing test.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-103
- **Plan ref:** ONION M4-T4

### NL-265 An outgoing HTLC with no stored origin never reaches the payment handlers
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (origin dispatch)
- **Evidence:** With no stored origin the switch logs a warning and does not call `ILocalPaymentHtlcHandler`; W2-C asked for "Local or no circuit". Since NL-250 every new add stores its origin, so only HTLCs offered by older builds are affected, and `PaymentService.ReconcileInFlightPaymentsAsync` (startup and re-pay) still resolves them (reported by the integrator).
- **Fix sketch:** Call the local payment handlers for an outgoing HTLC with neither an origin nor a circuit.
- **Blocks/Blocked-by:** Related NL-250
- **Plan ref:** ONION M4-T5

### NL-266 UPDATE-class failures embed our channel_update only when its scid equals the onion's
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`, `Application/Gossip/ChannelUpdateService.cs`
- **Evidence:** For option_scid_alias channels `ChannelUpdateService` signs with `RemoteAlias`, so an onion that names one of our `LocalAliases` gets `len=0` (allowed by BOLT 4). Needs an alias-policy decision; moot while ScidAlias=No (reported by W2-B).
- **Fix sketch:** Sign an update for the scid the onion used (alias or real), or keep one update per alias.
- **Blocks/Blocked-by:** Related NL-099, NL-236
- **Plan ref:** ABCD W3-C

### NL-267 Forwarding checks before the first processed block answer temporary_node_failure
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`, `IBlockchainMonitor.LastProcessedBlockHeight`
- **Evidence:** Forward CLTV and amount checks read the monitor's height; right after startup it is 0, so an HTLC replayed or received then is failed with temporary_node_failure instead of waiting (reported by W2-B).
- **Fix sketch:** Defer the switch's lock-in handling until the monitor has a height (the replay reruns), or read the stored tip.
- **Blocks/Blocked-by:** Related NL-073
- **Plan ref:** ONION M4-T4

### NL-268 The forward liquidity check ignores commitment fees
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (usable-channel / `AvailableToSend` estimate)
- **Evidence:** The per-channel usable check and the `AvailableToSend` estimate leave out commitment fees, so the engine's re-check at offer time can still refuse. The forward then fails with temporary_channel_failure after the circuit was saved (handled, but the pre-check is imprecise) (reported by W2-B).
- **Fix sketch:** Compute spendable balance with `CommitmentFeeCalculator` (fee for one more HTLC plus reserve) before choosing the channel.
- **Blocks/Blocked-by:** Related NL-073
- **Plan ref:** ONION M4-T4

### NL-270 Payments have no per-call fee limit and no retries
- **Status:** fixed (7024f57, 703d83b, 74c9014)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Send/PaymentService.cs`, `Send/PaymentRetryPolicy.cs`, `Routing/PaymentRoutePlanner.cs`, `Domain/Payments/Models/PayInvoiceOptions.cs`
- **Evidence:** `IPaymentService.PayInvoiceAsync` takes no fee limit, so every payment uses `PaymentSendOptions.GetMaxFee` = max(0.5 %, 5000 msat) (CLN defaults, `Node:Payments`). There are no automatic retries: a Failed hash can be paid again and the new attempt replaces the old one (W0-C policy) (reported by W2-C). Update (ABCD wave 6, `3ce3cad`): per-call limits `IPaymentService.PayInvoiceAsync(bolt11, amount, PayInvoiceOptions{Timeout, MaxFee, MaxParts})` → `PayInvoiceResult(Payment, Attempts, Parts)`; retryable failures plan a new round with `PaymentRoutePlanner` over `RouteConstraints` within the fee limit (all parts together), MaxParts, MaxAttempts (32) and the timeout; MPP split over direct channels and route hints when the invoice offers basic_mpp and no single route fits (liquidity from the engine's own dry run); IPC `payinvoice --max-fee-msat/--max-parts/--timeout` as optional keys of the existing command (7024f57). Docker `PaymentRetryFlowTests` 3/3: MPP to david, an incorrect_cltv_expiry retry, the fee limit boundary (703d83b). Review: the row carries the settled parts' fee and a live part's route and HTLC id, only liquidity refusals bound a channel, in-flight parts count against hint bounds (74c9014). Limits: B2-ADD-S08 (HTLC count) excludes a channel for the rest of the payment; parts added while others are in flight are memory-only (NL-321).
- **Fix sketch:** Add an optional `maxFeeMsat` to the pay request/IPC (new `ClientCommand` or field) and, later, retries over other hints.
- **Blocks/Blocked-by:** Related NL-114, NL-152
- **Plan ref:** ONION M4-T6

### NL-321 MPP send parts added while others are in flight are not persisted
- **Status:** open (partial: 74c9014)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Send/PaymentService.cs` (`PaymentSession`), `Payments`/`PaymentHops` tables
- **Evidence:** The payment row holds one route and HTLC. Parts added while other parts are in flight live only in the in-memory session, so after a restart their error onions cannot be decrypted (the payment fails without a failure code). The row's fee and route are corrected on success and when the recorded part fails (74c9014) (reported by W6-C). Update (ABCD wave 7, `4c37998`): hold times verified at the origin are recorded only for the persisted part; in-memory parts lose them (same cause).
- **Fix sketch:** A `PaymentParts` table (route, shared secrets, HTLC id per part) from a migration-owner lane, reconciled at startup.
- **Blocks/Blocked-by:** Related NL-137, NL-270
- **Plan ref:** ONION M4-T6

### NL-323 A late or duplicate HTLC for a Settled invoice with the right secret is fulfilled
- **Status:** fixed (7ca5c60, a3cf0ce, 672ed61)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs`, `Switch/HtlcSwitch.cs`
- **Evidence:** With `basic_mpp` on (the default), acceptance of a Settled invoice depends only on the payment_secret and `total_msat <= AmountReceived`, because HTLC sets are not persisted and a replayed held part cannot be told from a new HTLC. A duplicate single-part HTLC or a late part is fulfilled (BOLT 4 MAY accept an already paid hash); LND fails it. Only the payer loses (it overpays) (reported by W6-B). Update (ABCD wave 7, `4c37998`): the invoice's Settled save is the commit point: set membership is `HtlcRecord.KnownPreimage` (no migration); `FinalHopProcessor` accepts a Settled invoice only for a committed member, and any other HTLC gets 0x400F like LND (7ca5c60, a3cf0ce; `MppReceiveTests`, `FinalHopProcessorTests`); guides updated (672ed61). Accepted limit: pre-NL-323 settled sets whose held parts carry no mark are failed with 0x400F on replay after an upgrade (HTLCs are regtest-only, schema-reset policy).
- **Fix sketch:** Persist set membership (the incoming HTLCs a settle covered) and fail HTLCs outside it with 0x400F.
- **Blocks/Blocked-by:** Related NL-081, NL-253
- **Plan ref:** ONION M4-T3

### NL-326 attribution_data needs persistence and a hold-time source before the switch can use it
- **Status:** fixed (3a54e11, 06b906c, e64da4e, 4751a26, 672ed61, 4c37998)
- **Severity:** medium
- **Kind:** gap
- **Location:** `IChannelOperations.FailHtlcAsync`/`FulfillHtlcAsync`, engine `HtlcRemoval`, `Htlcs` table, `HtlcSwitch`, `PaymentService`
- **Evidence:** The switch seam (erring node create, intermediate wrap, final-hop and forwarded fulfill, origin decrypt/verify with `BlamedHopIndex`) is mapped out, but retransmission at reestablish and wrapping upstream after a restart need the 920-byte attribution_data (and an optional fulfillment_payload) stored with the HTLC removal (a migration on all three providers), the message factory must send them, and no add-received timestamp exists for hold times (0 is allowed meanwhile) (reported by W6-D and the W6 integrator). Update (ABCD wave 7, `4c37998`): W7-A (migration owner): migration `AddAttributionData` on all three providers (`Htlcs.AttributionData`, `FulfillmentPayload`, `AddedAt` in UTC ticks, `PaymentHops.HoldTimeMs`; no data step, no pending model changes), the engine and `IChannelOperations` carry the bytes (`FailHtlcAsync(AttributedErrorPacket)`, `FulfillHtlcAsync(AttributedFulfillment, ...)`, `GetHoldTimeAsync` in 100 ms units), `PaymentService` verifies and records hold times only on the fulfilled part's route (3a54e11, 06b906c, e64da4e); the hold time test uses a manual clock (4751a26). Integration: `HtlcSwitch` uses the seam (erring/final failures, final-hop and set fulfills, wrapped downstream failures incl. malformed and on-chain timeout, forwarded fulfills) gated on the feature and no `path_key` (672ed61); Docker three-node proof (4c37998). Feature gating is NL-332.
- **Fix sketch:** Migration-owner lane: columns next to `HtlcRemoval.Reason`; pass through `IChannelOperations` and `MessageFactory`; wire `IAttributionDataService` into `HtlcSwitch`/`PaymentService`; then take `OptionAttributionData` out of `ExperimentalFeatures`.
- **Blocks/Blocked-by:** Blocks NL-072; related NL-022
- **Plan ref:** ONION M3b

### NL-327 The onion replay table is pruned only when an onion arrives
- **Status:** fixed (aa41f2b, 368a057, 672ed61)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Onion/PersistentOnionReplayStore.cs`
- **Evidence:** Pruning runs lazily inside `TryAddAsync` (at most once per height) or on an explicit `PruneAsync`; nothing calls it on new blocks, so a node that receives no HTLCs keeps expired rows until the next one (reported by W6-A; the integrator kept lazy pruning). Update (ABCD wave 7, `4c37998`): `OnionReplayBlockPruner` (Infrastructure.Bitcoin, `AddOnionReplayBlockPruner()`) prunes on every `OnNewBlockDetected`, coalesced to the highest height, retrying after a failed prune (aa41f2b, 368a057); registered in `AddNltgNodeServices` and started/stopped with the chain monitor by the daemon and `NLightningTestNode` (672ed61). The lazy prune stays as a fallback. The `IOnionReplayStore` XML remark is now stale (NL-340).
- **Fix sketch:** Call `IOnionReplayStore.PruneAsync(height)` from an existing `OnNewBlockDetected` handler (e.g. `HtlcExpiryMonitor`).
- **Blocks/Blocked-by:** Related NL-078
- **Plan ref:** ONION M4-T7

### NL-328 PaymentModel doc comment still says single HTLC, no MPP
- **Status:** fixed (ef03b12)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Payments/Models/PaymentModel.cs`
- **Evidence:** The XML doc says the payment is a "single HTLC (no MPP)"; since W6-C a payment can be split and the row records one live part (reported by W6-C, out of its lane). Update (ABCD wave 7, `4c37998`): the doc comment describes the multi-part row and links NL-321 (ef03b12).
- **Fix sketch:** Rewrite the comment (and link NL-321).
- **Blocks/Blocked-by:** Related NL-270, NL-321
- **Plan ref:** —

### NL-332 OptionAttributionData cannot be un-gated on an LND interop proof
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs` (`ExperimentalFeatures`)
- **Evidence:** LND 0.20 does not implement option_attribution_data (no bits 36/37, no TLV 1 sent; it accepts ours and still reads the failure), so the LND interop gate for taking `OptionAttributionData` out of `ExperimentalFeatures` cannot pass. Attribution is proven only between NLightning nodes (Docker `AttributionFlowTests`) (reported by W7-A and the W7 integrator).
- **Fix sketch:** Decide: un-gate on the NLightning-to-NLightning proof plus a CLN/Eclair check, or wait for an LND release with the feature.
- **Blocks/Blocked-by:** Related NL-072, NL-326
- **Plan ref:** ONION M3b

### NL-333 PaymentRetryPolicy ignores the attribution_data blame
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Send/PaymentRetryPolicy.cs`, `PaymentService.cs`
- **Evidence:** When no hop authenticates the return packet but attribution_data blames hop i, the payment records `FailureSourceIndex = i`, but the retry policy still avoids our own first channel instead of the blamed hop's channel (reported by W7-A).
- **Fix sketch:** Feed the blamed index into the retry decision (exclude the channel after hop i).
- **Blocks/Blocked-by:** Related NL-072, NL-270
- **Plan ref:** ONION M3b, M4-T6

### NL-334 A fulfill reverted by a disconnect loses its attribution_data on replay
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Commitments/` (`RevertUncommitted`, `DerivePending`), `Payments/Switch/HtlcSwitch.cs`
- **Evidence:** A fulfill that a disconnect reverts keeps only `KnownPreimage`; the replayed `OutgoingHtlcFulfilled` then carries no attribution, so a forwarding node wraps an all-zero block and the origin sees an invalid HMAC at the downstream hop (hold times lost, no funds impact) (reported by W7-A).
- **Fix sketch:** Keep the attribution bytes with the preimage when a fulfill is reverted, or treat a missing block as "no attribution" instead of wrapping zeros.
- **Blocks/Blocked-by:** Related NL-326
- **Plan ref:** ONION M3b

### NL-339 A blinded forward drops the downstream fulfillment_payload
- **Status:** fixed (eaeb2797)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (forwarded fulfill)
- **Evidence:** BOLT 4: a forwarding node passes the downstream fulfillment_payload on (obfuscated) even when it adds no attribution. When the incoming add had a `path_key` the switch adds no attribution and drops the payload. Unreachable until route blinding is enabled (M5) (reported by the W7 integrator). Update (wave rf1, `wip/fafo` at `be9fd000`): blinded forwards never relay a downstream error or payload and fail upstream with `invalid_onion_blinding` per BOLT 4; the blinded failure rules also apply in the dust and expiry fail-backs.
- **Fix sketch:** Pass the payload through the blinded branch as BOLT 4 describes when M5 lands.
- **Blocks/Blocked-by:** Related NL-079, NL-326
- **Plan ref:** ONION M3b, M5

### NL-340 IOnionReplayStore doc remark says nothing prunes on blocks
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Onion/Interfaces/IOnionReplayStore.cs:30`
- **Evidence:** The XML remark says nothing in the node has to call `PruneAsync` and a node without HTLCs does not prune; since NL-327 `OnionReplayBlockPruner` prunes on every block (reported by W7-C).
- **Fix sketch:** Reword the remark to name `OnionReplayBlockPruner` and keep the lazy prune as the fallback.
- **Blocks/Blocked-by:** Related NL-327
- **Plan ref:** —

### NL-348 The switch refuses the real SCID of a public channel that negotiated option_scid_alias
- **Status:** fixed (bca66aa)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (`ResolveOutgoingChannel`, the `UseScidAlias == FeatureSupport.No` test, about line 1586)
- **Evidence:** The onion's SCID matches the real SCID only when `UseScidAlias` is `No`. A public channel opened with `openchannel --public`, or opened to us by a peer, with option_scid_alias negotiated but not in its channel_type has `UseScidAlias` Optional, while its `channel_announcement` and public `channel_update` name the real SCID; a payment routed through that SCID is failed with `unknown_next_peer`. BOLT 2 forbids forwarding by the real SCID only when option_scid_alias is in the channel_type (Compulsory here). LND negotiates option_scid_alias by default, so this blocks routing through our public channels (reported by lane B1, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): `HtlcSwitch.ResolveOutgoingChannel` accepts the real SCID unless `UseScidAlias == FeatureSupport.Compulsory` (BOLT 2: only option_scid_alias in the channel_type forbids it); `ThreeNodeSwitchTests` `Given_ScidAlias*` cover No/Optional/Compulsory with the real SCID and the alias (bca66aa).
- **Fix sketch:** Accept the real SCID unless `UseScidAlias == FeatureSupport.Compulsory`; switch test: a public channel with Optional forwards by its real SCID, an alias-typed (Compulsory) channel still refuses it.
- **Update (wave sp2):** the retired SCID map of a splice (`RetiredScidMap`, 505a3b90) never retires the real SCID of a channel whose `option_scid_alias` is Compulsory, at the lock or in `LoadAsync`, so an alias-only channel's old real SCID does not become routable after a splice. `HtlcSwitch.ResolveRetiredChannel` has no alias check of its own (the guard sits where entries are written).
- **Blocks/Blocked-by:** Part of NL-099 (G4 routing through us); related NL-236, NL-103
- **Plan ref:** BOLT7 G1-T5, G4

---

### NL-440 Blinded payments: no MPP, no own-introduction paths, no BOLT 11 blinded paths, no dummy hops
- **Status:** fixed (9e963b82, ba314f36, 48952ee5, 7272d43b, c5018e32)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Send/PaymentService.cs` (`PayBlindedAsync`), `Invoices/BlindedPathBuilder.cs`
- **Evidence:** M5 sends one part per blinded payment, cannot pay a path whose introduction node is us, does not decode BOLT 11 blinded paths (LND's field 20; the Docker proof reads them through `DecodePayReq`), and our own paths have no dummy hops (reported by lane M5). Update (wave B12, `a3445f3f`): lane B12-E (B4-T1) added MPP over blinded paths (one part per path when no single path carries the amount, planned parts counted against liquidity) and paying a path whose introduction node is us (`Payments/Send/Blinded/SelfIntroducedBlindedPath`, B12-PAY-02; `PaymentRoute.FirstHopPathKey`; a malformed answer excludes the path only when we introduced it, and a self-introduced path whose relay leaves the payee short is skipped) (9e963b82, ba314f36). B12-PAY-02 is proven in-process only (`OfferHarnessTests`): CLN introduces its invoice paths itself even when we are its only peer. Left: BOLT 11 blinded paths (field 20) and dummy hops. Update (wave nl440, branch `wip/fafo-nl440` from `29ce3126`): fixed. (1) BOLT 11 blinded paths: no BOLT defines tag 20 and no bolts PR proposes one; the standard is bLIP 39 "BOLT 11 Invoice Blinded Path Tagged Field" (lightning/blips#39, merged 2024-08-14 with status **Draft**), which LND 0.18+ writes (`zpay32/blinded_path.go`); CLN, Eclair and LDK neither write nor read it. `NLightning.Bolt11` decodes and encodes it as documented draft support (`BlindedPaymentPathTaggedField`, repeatable; strict reader: valid points, `num_hops` >= 1, canonical BigSize, no trailing byte; `s` not required with `b`; `b` + `r` refused as the bLIP writer rule and LND's reader do; the bLIP's feature bit 262 known); the bLIP appendix vector decodes field by field and its `b` fields re-encode byte-exact (48952ee5). `payinvoice` pays an invoice with `b` fields through `PayBlindedAsync` (MPP across paths only with `basic_mpp`, the invoice's `c` and ephemeral signing key ignored), and `Node:Invoices:BlindedPaths` (off by default, draft) makes our BOLT 11 invoices carry our paths instead of `r` hints and `s`, signed by an ephemeral key (7272d43b). (2) Own-introduction paths: done in B12 (`SelfIntroducedBlindedPath`); now also proven from a bLIP 39 invoice (`Bolt11BlindedInvoiceTests`) and with dummy hops after the payer; the degenerate path that ends at the payer (paying ourselves) is refused. (3) Dummy hops (BOLT 4 writer MAY): `BlindedPathBuilder` ends each path with `Node:Invoices:BlindedPathDummyHops` hops (default 1: LND's default shape, `blinding.num-hops` 2 with one real hop; LDK also uses realistic relay values; at most 4) of our own node (`next_node_id` = us, the introduction node's `payment_relay` as LND takes the real hops' average, chained `payment_constraints`, every hop padded to one length, BOLT 4 SHOULD), and `IncomingOnionProcessor` peels them on receipt (7272d43b). No spec vector covers dummy hops; the layout is checked by decrypting every hop (`BlindedSendThreeNodeTests`). Onion-message paths use a separate builder and have none: NL-525. Proofs: `Bolt11.Tests/Models/Blip39BlindedPathInvoiceTests`, `Application.Tests/Payments/Onion/DummyHopOnionProcessorTests`, `Payments/Send/BlindedSendThreeNodeTests` (0-2 dummy hops), `Payments/Send/Bolt11BlindedInvoiceTests`; Docker against LND 0.20: `Docker/Gossip/Bolt11BlindedPathFlowTests` (our field 20 decode equals LND's `DecodePayReq` field by field and pays david's invoice with his dummy hops; bob pays our blinded BOLT 11 invoice through alice and our dummy hop) and `RouteBlindingFlowTests` 4/4 with our dummy hops on; the whole `Docker.Gossip` namespace 30/30 on net10.0 (in-container runner).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Follow-up of NL-079
- **Plan ref:** ONION M5

### NL-442 Onion message reader and path code exists twice
- **Status:** fixed (e1693d44)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/OnionMessages/{OnionMessagePayloadCodec,IOnionMessageUnwrapper,IBlindedMessagePathBuilder,OnionMessageUnwrapResult}.cs`, `src/NLightning.Application/OnionMessages/{OnionMessageService,MessagePathFactory}.cs`, `test/NLightning.Application.Tests/OnionMessages/` (`HarnessOnionMessagePacketBuilder`)
- **Evidence:** Lanes M6-A, M6-B and M6-D were written in parallel. The Bitcoin-side `OnionMessagePayloadCodec` duplicates the Domain `OnionMessageTlvsCodec`/`BlindedPathCodec` (lane D's Application copy was deleted at integration, fc686ff0); `OnionMessageService` does its own peel, unblind and ignore rules instead of calling `IOnionMessageUnwrapper`; `MessagePathFactory` overlaps `BlindedMessagePathBuilder`; the Application harness uses its own packet builder, so the send-side encoding (reply_path, sciddir) is proven only by the vector and the CLN proof. The public `IOnionMessageUnwrapper`, `IBlindedMessagePathBuilder` and the unwrap result types live in Infrastructure.Bitcoin rather than Domain, so Application depends on Infrastructure.Bitcoin for them (reported by lanes M6-B, M6-D and the integrator). Update (wave lh1, `a6c633f9`): fixed. `IOnionMessageUnwrapper`, `IBlindedMessagePathBuilder` and `OnionMessageUnwrapResult` (with `OnionMessageUnwrapStatus`/`OnionMessageIgnoreReason`) moved to `Domain/Protocol/OnionMessages`; `OnionMessagePayloadCodec` and Application's `MessagePathFactory` are deleted; the packet builder and unwrapper use only the Domain `OnionMessageTlvsCodec`/`BlindedPathCodec`; `OnionMessageService` reads through `IOnionMessageUnwrapper` (metric tags unchanged); `OfferService` takes `IBlindedMessagePathBuilder`. The Application harness runs on the production packet builder, path builder and unwrapper (`RawOnionMessageWriter` only for reader-rule tests). Proofs: onion-message vectors, Bitcoin 97 and Application 110 onion-message tests, CLN `ClnOnionMessageTests`, `ClnOfferReceiveTests`, `ClnOfferPayTests` 18/18 (lane l2).
- **Fix sketch:** Move the interfaces and result types to `Domain/Protocol/OnionMessages/Interfaces`, keep one codec (Domain) and one path builder, make the service call `IOnionMessageUnwrapper`, and run the harness through `OnionMessagePacketBuilder`, `OnionMessageRateLimiter` and the real `PeerOutbox`.
- **Blocks/Blocked-by:** Follow-up of NL-080; related NL-157
- **Plan ref:** `BOLT12_PLAN.md` OM1, OM2-T3, OM2-T5

### NL-443 The CLN onion message proof sent an empty path_id where it meant none
- **Status:** fixed (5ae1701c)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnOnionMessageTests.cs` (`CreateMessagePath`)
- **Evidence:** A null `byte[]` converted to an empty, non-null `ReadOnlyMemory<byte>?`, so the prefix-hop path carried an empty `path_id` and `Assert.Null(delivered.PathId)` failed; a conditional `x is null ? null : x` has the same problem. Fixed by leaving the field unset when there is no path id (reported by the integrator).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** `BOLT12_PLAN.md` Proof M6

### NL-444 A malformed onion_message gets a warning and a closed connection
- **Status:** fixed (8035c63b)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Payloads/OnionMessagePayloadSerializer.cs`, `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs`
- **Evidence:** A 513 whose `len` is below 66, whose key prefix is bad or that is truncated throws `PayloadSerializationException`, which takes the NL-207 path (warning and close). BOLT 4 asks only that an invalid onion message be ignored; the plan sanctioned the NL-207 behavior for OM0-T1. Since 641a5fff bits 38/39 are advertised by default, so any peer can trigger the close of its own connection (no channel impact beyond the reconnect) (reported by lane M6-A). Update (wave lh1, `a6c633f9`): fixed. `MessageService.HandleMalformedOnionMessage` ignores a 513 that fails to deserialize (BOLT 4 ignore rule; 513 is odd), with no warning and no close, whether or not the feature was negotiated, and counts it as `nlightning.onion_messages.dropped{reason=malformed}` (Information log on the first and every 1,000th). The change is in `Infrastructure/Protocol/Services/MessageService.cs`: such a 513 never reaches `PeerService`. The count is published on a second static `Meter("NLightning.OnionMessages")`, so `OnionMessageMetrics`' in-memory counts miss it (NL-464). Proof: `MessageServiceTests.Given_MalformedOnionMessage_*` (lane l2).
- **Fix sketch:** Drop a malformed 513 with a `dropped{reason=malformed}` count and keep the connection, or record the NL-207 policy as deliberate.
- **Blocks/Blocked-by:** Follow-up of NL-080; related NL-207
- **Plan ref:** `BOLT12_PLAN.md` OM0-T1

### NL-446 Onion message rate-limit defaults are untuned and the meter has no queue gauge
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/OnionMessages/{OnionMessageRateLimiter,OnionMessageMetrics,OnionMessageOptions}.cs`, `src/NLightning.Application/Node/Managers/PeerManager.cs`
- **Evidence:** The global burst (2,560 KiB), global message cap (200/s, burst 200) and per-peer message burst (20) were chosen by lane M6-C; plan §3.4 gives only 640 KiB/s global and marks every value "to be tuned". `PeerManager.QueuedOutboxOnionMessageCount` exists but no gauge publishes it in `Meter("NLightning.OnionMessages")` (plan §3.4 asks for the queue depth); outbox refusals are counted by the service as `dropped{reason=outbox_full}` (reported by lane M6-C).
- **Fix sketch:** Add an observable gauge for the outbox and handler queues; tune the defaults from a Mutinynet or mainnet run with LND 0.21/CLN peers.
- **Blocks/Blocked-by:** Follow-up of NL-080
- **Plan ref:** `BOLT12_PLAN.md` §3.4, OM2-T2, OM3-T3

### NL-447 [EPIC] BOLT 12 offers not implemented
- **Status:** fixed (95066ae5, b7ac3543, 3acf5ca6, 80bd155c, 03c2bfde, ffc5c62b, 61ea7b02, 883a883f, 16019fb9, bf8da622, 5ec25ce9, f01081a8, 59a0072d, c7f0586d, 1e43d24a, 9e963b82, bc59fa90, 5a99843b, 4fd7d44e, e6248970, 33e49e03, 8ae4c1b8, b0d3f056, 64f2b1db, 52494429, ba314f36, a3445f3f, e767fd30, 8035c63b, b9faac3d, c0587be9, 844318cc, e1693d44, 3295f675, 84eef9ed)
- **Severity:** medium
- **Kind:** gap
- **Location:** planned `src/NLightning.Domain/Offers/`, `src/NLightning.Infrastructure.Bitcoin/Offers/`, `src/NLightning.Application/Offers/`, migration `AddBolt12Offers`
- **Evidence:** No BOLT 12 codecs, bech32 without checksum, Merkle tree or BIP-340 message signatures (plan gap OG5); `InvoiceEntity.Bolt11` is required and `PaymentEntity` has no BOLT 12 fields (OG7); no IPC for offers (OG9). Onion messages (NL-080) and route blinding with blinded send (NL-079, OG6) are done; `MinimalOfferEncoder` in `test/NLightning.Tests.Utils/Bolt12/` is test-only (filed by the ledger agent after wave M6 as the plan asked). Update (wave B12, `wip/fafo` at `a3445f3f`; contracts B12-0 `6f4bdaad`): B0 (B12-A): `Domain/Offers/` `Bolt12Bech32` (all 12 `format-string-test.json` cases), `Bolt12TlvStream` and typed views (every valid `offers-test.json` offer byte-exact, all 33 invalid ones rejected with the requirement id asserted), `OfferValidator`/`InvoiceRequestValidator`/`InvoiceValidator` (fail closed on an overflowing or unconvertible expected amount, expected node id required for paths-only offers, odd 241-1000 elements ignored per BOLT 1), `Bolt12MerkleTree` (every `signature-test.json` leaf, branch and root) (95066ae5, b7ac3543). B1 (B12-B): `IBolt12Signer` (`Infrastructure.Bitcoin/Offers/`), `ILightningSigner.GetBolt12PayerId`/`SignBolt12` with the exact tag bound to each key kind; the vector signature reproduced byte for byte (deterministic, zero aux); `SignAsBlindedRecipient` built, not wired (D2) (3acf5ca6, 80bd155c). B2 (B12-C): migration `AddBolt12Offers` on all three providers (`Offers` table, `Invoices.Kind/OfferId/...`, nullable `Invoices.Bolt11`, BOLT 12 payment columns), `OfferDbRepository` with SQL-side unpaid counts, `PruneExpiredBolt12InvoicesAsync` (no caller yet, NL-448) (03c2bfde, ffc5c62b). B3 (B12-D): `OfferService`, the type-64 `InvoiceRequestHandler` (signature before any answer, unknown offers ignored silently, node-wide token before parsing, per-offer and global limits, D11 caps), `OfferInvoiceFactory` + `BlindedPaymentPathFactory`, the BOLT 12 final-hop rule, `createoffer`/`listoffers`/`disableoffer` (61ea7b02, 883a883f, 16019fb9, bf8da622, f01081a8, 1e43d24a); Docker `ClnOfferReceiveTests` 4/4 (5ec25ce9, 59a0072d, c7f0586d). B4 (B12-E): blinded send with MPP and introduction = us (NL-440 partial), `InvoiceRequestFactory`, `InvoiceVerifier`, `OfferPaymentService`, `payoffer`/`fetchinvoice`, `OfferHarnessTests` with the production issuer and signer (9e963b82, bc59fa90, 5a99843b, 4fd7d44e, 33e49e03, 8ae4c1b8, b0d3f056, 52494429, ba314f36); Docker `ClnOfferPayTests` 4/4 (e6248970, 64f2b1db). Integration (a3445f3f): `ClientCommand` 26-30, `AddOffersServices`/`AddOfferSendServices`/`AddOfferIpcServices`/`AddOfferSendIpcServices`, `OfferOptions` bound from `Offers`, both `Bolt12Wire` seams delegate to lane A's codecs, the BOLT 11 fallback removed; no feature bit (plan F-04). CLN suite 39/39 incl. both offer proofs. Left before the epic closes: CLN-captured vectors (B0-T4, NL-450), the invoice prune timer (NL-448), reachability limits (NL-452); follow-ups NL-451, NL-453, NL-454, NL-455. Update (wave lh1, `a6c633f9`): closed as the fix sketch planned: the CLN-captured vectors (NL-450, 3295f675) and the expired invoice prune (NL-448, b9faac3d, 84eef9ed) landed, with NL-442, NL-444, NL-453, NL-454 and NL-455 (lane l2). Carried as their own entries: NL-451 (malformed-case layer), NL-452 (reachability).
- **Fix sketch:** Remaining: NL-450 (B0-T4 captures, needed to close B4-T2 as planned) and NL-448; then close the epic and carry NL-451..NL-455 as their own entries.
- **Blocks/Blocked-by:** Blocked-by NL-080, NL-079
- **Plan ref:** `BOLT12_PLAN.md` wave B12

### NL-448 Expired unpaid BOLT 12 invoice rows are never deleted
- **Status:** fixed (b9faac3d, 84eef9ed, a6c633f9)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Payment/InvoiceDbRepository.cs` (`PruneExpiredBolt12InvoicesAsync`), `src/NLightning.Application/Offers/Receive/`
- **Evidence:** Lane B12-C added `IInvoiceDbRepository.PruneExpiredBolt12InvoicesAsync(now, max)` (ffc5c62b), but nothing calls it. Every answered invoice_request writes an invoice row; the D11 caps count only open unexpired (and Accepted) rows, so expired rows accumulate without bound, at up to the node-wide invoice_request rate (20/s) from any onion-message peer (reported by the integrator and lanes B12-C, B12-D). Update (wave lh1, `a6c633f9`): fixed. `Application/Offers/Receive/ExpiredBolt12InvoicePruner` (singleton, `AddOffersServices`) runs at start and every `Offers:ExpiredInvoicePruneInterval` (10 min; 0 disables), deleting Open expired BOLT 12 rows in batches of `Offers:ExpiredInvoicePruneBatchSize` (500), one save per batch, at most 100 batches a round (b9faac3d). Review fix (84eef9ed): only rows expired by at least `Offers:ExpiredInvoicePruneGrace` (1 h, never less than `Node:Switch:MppTimeout`) are pruned, so an HTLC set held across the expiry still settles. The daemon runs it through `ExpiredBolt12InvoicePruneHostedService` (`AddExpiredBolt12InvoicePruning()` in `ConfigureNltgServices`, a6c633f9); the Docker `NLightningTestNode` does not start it. Proof: `ExpiredBolt12InvoicePrunerTests` incl. a pruned invoice's late HTLC failing like an expired one (lane l2).
- **Fix sketch:** A timer (or the block tick) in `Offers/Receive` calls the prune with a batch size and saves; test that a pruned invoice's late HTLC fails like an expired one.
- **Blocks/Blocked-by:** Follow-up of NL-447
- **Plan ref:** `BOLT12_PLAN.md` D11, B2-T2

### NL-450 No CLN-captured BOLT 12 vectors (B0-T4)
- **Status:** fixed (3295f675)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/Vectors/Bolt12Vectors.cs`, `test/NLightning.Integration.Tests/BOLT12/Bolt12VectorTests.cs` (TODO B0-T4)
- **Evidence:** The plan asks for a CLN v26.06.8 offer, invoice_request, invoice and invoice_error, each parsed, re-encoded byte-exact, validated and signature-verified, and uses the captured invoice as the positive case of `InvoiceVerifierTests` (B4-T2). Lane B12-A could not capture them; lane B12-E's `ClnOfferPayTests` prints CLN's invoice bytes as `VECTOR` lines, but they were not added (reported by lanes B12-A, B12-E). Update (wave lh1, `a6c633f9`): fixed. The Explicit Docker `Interop/Cln/ClnBolt12CaptureTests` recorded CLN v26.06.8's offer, our invoice_request, CLN's invoice and invoice_error, our offer, CLN's invoice_request and our invoice into `Bolt12ClnVectors` (`test/NLightning.Tests.Utils/Vectors/Bolt12Vectors.cs`); Integration `BOLT12/Bolt12ClnVectorTests` (9) checks parse, byte-exact re-encode, validators, expiry and BIP-340 signatures; `InvoiceVerifierTests` uses CLN's invoice as the B4-T2 positive case. Re-capturing produces new bytes: replace the class as a whole (lane l2).
- **Fix sketch:** Take the `VECTOR` lines (and an invoice_request/invoice_error through `RawOnionMessageRecorder`) into `Bolt12Vectors.cs` and assert parse, re-encode, validate and verify.
- **Blocks/Blocked-by:** Part of NL-447
- **Plan ref:** `BOLT12_PLAN.md` B0-T4, B4-T2

### NL-451 offers-test.json malformed cases fail at the TLV layer, not at the rule they name
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/BOLT12/Vectors/offers-test.json` (bolts 1aadb719), `test/NLightning.Domain.Tests/Offers/`
- **Evidence:** The six "Malformed ... blinded_path" cases encode `offer_paths` with a length of 2 or 3, and "Contains type > 1999999999" and "unknown even type (1000000002)" encode their type as a 3-byte `fd` BigSize (30517, 15258) with a length past the end, so all eight are rejected as B12-ENC-03 before the rule they name is reached. Our unit tests cover num_hops 0, off-curve points, the range above 1999999999 and 1000000002 with well-formed streams (reported by lane B12-A).
- **Fix sketch:** Report the vectors upstream (lightning/bolts); update the vector file when fixed.
- **Blocks/Blocked-by:** Related NL-447
- **Plan ref:** `BOLT12_PLAN.md` B0-T3

### NL-452 BOLT 12 reachability: private-channel payment paths, fragile offer paths, blinded issuer not wired
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Offers/Receive/{OfferService,BlindedPaymentPathFactory}.cs`
- **Evidence:** `BlindedPaymentPathFactory` builds payment paths over private channels too, so such a path only works when that channel's peer is the payer (as CLN is in the proof); a third-party payer cannot use it. When no connected onion-message peer has an Open channel, offer paths are introduced by peers without a channel, which we never reconnect to, so the offer dies with the connection (only logged; `createoffer` does not surface the warning). Offers with `IssuerKind.BlindedPaths` (signed by `SignAsBlindedRecipient`) are not wired (plan D2). B12-PAY-02 (introduction = us) is proven only in-process: CLN introduces its own invoice paths (reported by lanes B12-D, B12-E).
- **Fix sketch:** Prefer announced channels (or the graph) for payment-path introduction nodes, return the fallback warning in the `createoffer` response, and decide D2 when an operator wants a hidden node id.
- **Blocks/Blocked-by:** Follow-up of NL-447; related NL-440
- **Plan ref:** `BOLT12_PLAN.md` §3.7, D2, Proof B12 pay (c)

### NL-453 Two Bolt12Wire seams remain over the Domain codecs
- **Status:** fixed (844318cc)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Offers/Receive/Bolt12Wire.cs`, `src/NLightning.Application/Offers/Send/Bolt12Wire.cs`
- **Evidence:** Lanes B12-D and B12-E each wrote a stand-in for lane B12-A's codecs; at integration both became thin delegates to `Bolt12TlvStream`, `Bolt12MerkleTree`, `Bolt12Bech32`, `Bolt12FieldCodec` and `Bolt12TlvRanges` (8ae4c1b8, a3445f3f) instead of being deleted, so the lanes' tests stayed unchanged (reported by the integrator and lane B12-D; like NL-442 for onion messages). Update (wave lh1, `a6c633f9`): fixed. Both `Bolt12Wire` classes are deleted and every call site uses the Domain codecs; `Bolt12Bech32.Decode(text, expectedHrp)` was added for the payer's prefix-checked decode; the tests became `Bolt12CodecTests` (lane l2).
- **Fix sketch:** Inline the call sites onto the Domain codecs and delete both files.
- **Blocks/Blocked-by:** Follow-up of NL-447; related NL-442
- **Plan ref:** `BOLT12_PLAN.md` B0

### NL-454 Offers operator surface: no template keys, listinvoices without kind or offer
- **Status:** fixed (c0587be9, a6c633f9)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`, `src/NLightning.Transport.Ipc/Responses/InvoiceInfoIpcResponse.cs`, `src/NLightning.Client/Printers/`
- **Evidence:** The `Offers` section (`OfferOptions`, bound since a3445f3f) is not in the daemon's `appsettings.json` template, so the defaults apply silently. `InvoiceInfo` client/IPC responses carry no `Kind`/`OfferId`; `listinvoices` prints `- (BOLT 12)` for a BOLT 12 invoice's `Bolt11` (reported by the integrator and lane B12-C). Update (wave lh1, `a6c633f9`): fixed. The daemon template has an `Offers` section at the code defaults (checked by `OfferConfigTemplateTests` for 4 networks); `InvoiceInfoClientResponse`/`InvoiceInfoIpcResponse` carry `Kind` (key 10) and `OfferId` (key 11; keysend's `CustomRecords` is key 12); `listinvoices` prints the kind (BOLT 11, BOLT 12 or keysend) and `Offer Id:` for BOLT 12 (c0587be9 lane l2; key layout and kind printing merged with keysend in a6c633f9).
- **Fix sketch:** Add the `Offers` keys to the template; append `Kind` and `OfferId` keys to `InvoiceInfoIpcResponse` and print them.
- **Blocks/Blocked-by:** Follow-up of NL-447
- **Plan ref:** `BOLT12_PLAN.md` §3.11

### NL-455 BOLT 12 BIP-340 signatures use zero auxiliary randomness
- **Status:** fixed (e767fd30)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Offers/Bolt12TaggedHash.cs` (`SignBip340`)
- **Evidence:** Signing is deterministic (aux = 32 zero bytes) to match CLN and reproduce `signature-test.json`; BIP-340 recommends fresh aux randomness as side-channel hardening. Acceptable for a software signer with the key in process (reported by lane B12-B). Update (wave lh1, `a6c633f9`): fixed. `Bolt12TaggedHash.SignBip340` signs with 32 fresh aux bytes from the crypto provider (zeroed after use); an internal overload takes explicit aux, and the `signature-test.json` vector passes 32 zero bytes, so it stays byte-exact (lane l2).
- **Fix sketch:** Use random aux in production and keep the zero-aux path for the vector test; revisit with a hardware/VLS signer.
- **Blocks/Blocked-by:** Follow-up of NL-447
- **Plan ref:** `BOLT12_PLAN.md` B1-T1

### NL-459 Keysend (spontaneous payments) and custom onion records not implemented
- **Status:** fixed (f55366ed, 90aea9f9, a6c633f9)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Keysend/`, `src/NLightning.Domain/Payments/Keysend/`, `Payments/Send/PaymentService.cs` (`PayKeysendAsync`), `Payments/Switch/HtlcSwitch.cs`, `Payments/FinalHop/FinalHopProcessor.cs`
- **Evidence:** Listed in `REMAINING_WORK.md` ("Keysend / spontaneous payments, custom TLV records"); filed at wave lh1 for lane l3, which carried no NL ID. Fixed: send (`PaymentService.PayKeysendAsync`: our preimage in `keysend_preimage` (5482373484) and the custom records in the payee's payload instead of payment_data, single part, graph or direct routing, final CLTV delta `Node:Keysend:FinalCltvExpiryDelta` 40) and receive (`KeysendReceiver` builds an `InvoiceKind.Keysend` record from the onion's preimage when no invoice exists; `FinalHopProcessor` checks it, single part, any amount; saved only once accepted; `Node:Keysend:Accept` on by default, as CLN) (f55366ed). Custom records (>= 65536) are kept by the hop payload parser whatever their parity; `HopPayloadValidator` accepts an even one only at the final hop (LND behaviour, a deliberate interop deviation from BOLT 1 limited to the final hop). IPC `keysend` (ClientCommand 31), `listinvoices`/`listpayments` show the records (`InvoiceInfoIpcResponse` key 12, `PaymentInfoIpcResponse` keys 14/15). Review fixes (90aea9f9): `FinalHopClaims` decides a locked-in HTLC with no invoice, so a keysend the switch never accepted is claimed on chain after a force close; tolerant record reads; onion size checks. Integration (a6c633f9): `AddKeysendIpcServices()`, `Node:Keysend` template section. Proofs: `Payments/Keysend/KeysendHarnessTests` (both ways, multi-hop, restart), Docker `KeysendFlowTests` 2/2 against LND 0.20 both directions with custom records (alice runs `--accept-keysend`). No keysend MPP (no standard form; LND refuses it). Records are stored in the BOLT 12 bytes column (NL-460).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-114, NL-460
- **Plan ref:** `REMAINING_WORK.md`

### NL-464 Malformed onion_message drops are counted on a second static Meter
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs` (`HandleMalformedOnionMessage`), `src/NLightning.Application/OnionMessages/OnionMessageMetrics.cs`
- **Evidence:** The NL-444 fix counts a malformed 513 on a static `Meter("NLightning.OnionMessages")` in Infrastructure, beside the service's own meter of the same name, so the exported metric is right but `OnionMessageMetrics`' in-memory counts (and anything reading them) miss malformed drops (reported by lane l2, wave lh1).
- **Fix sketch:** Route the malformed drop through one onion-message metrics port that Infrastructure can call, or document the split.
- **Blocks/Blocked-by:** Follow-up of NL-444
- **Plan ref:** `BOLT12_PLAN.md` OM0-T1

## BOLT 5: On-chain handling

### NL-094 [EPIC] On-chain handling: unilateral close sweeps, HTLC resolution, penalty/justice
- **Status:** fixed (36d2270, 06da54b, 983b2b4, 7b4a173, f251dde, 4fd3617, dbe4cc8, 5926d0c, 263ab8f, d7a4c73, 369314d, 7394f19, 0df889a, 36e8797, ff7f6cc, 0761773, 960cf05, 152144c, d040654, 41f5fc2, c2ae40a, 567a3c1, 7f6ebd9, cbd99c6, 5af263f, 7d3a6b3, cc207a8, 037c04b, 3794c0d, 6bd3645, 202341b, e5a556d, bbc51b4, f4b83ff, 7189b71, dd2d64f, a5b24e3, 6a4eb7d, 7c437b3, e8bb45b, b330d8c, d3dfff8, 7dcf472, cde5ebb, 8fcea62, 30584cf, 70cbd33, 6d4b625, e8841d6, a140940, 04aab92, 6de56ad, 44767d3, 48a8951)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`, `src/NLightning.Domain/Onchain/`, `src/NLightning.Infrastructure.Bitcoin/Onchain/`, `src/NLightning.Infrastructure.Bitcoin/Builders/{Sweep,Penalty}TransactionBuilder.cs`
- **Evidence:** No detection of commitment broadcasts, no sweeps, no HTLC on-chain resolution, no penalty tx. A revoked-state broadcast by a peer goes unpunished. Update (ABCD wave 2, `a5675cb`): this entry also tracks the fail-the-channel broadcast service (BOLT2 N9-T4). NL-200 and NL-035 are closed without it: a Failed channel is persisted and its error re-sent but our commitment is not broadcast, and the signer does not yet refuse broadcast signing after `DataLossDetected` (no broadcast path exists). Update (ABCD wave 3, `c92d837`): N9-T4 done: `ChannelFailureService` is the only broadcast path; under the lock it persists Failed + the error, signs the latest local commitment with the stored remote signature (`ILightningSigner.SignLocalCommitmentForBroadcast`, refuses a revoked number), saves the watch and publishes after the lock, retries a refused publish every block and resumes interrupted broadcasts at start (06da54b); the signer refuses every signature after `MarkDataLoss` (also set at registration from `DataLossDetected`). `ChannelManager` hands a `MustBroadcast` failure to it after the lock, and the daemon starts it (983b2b4). Docker `ChannelSafetyFlowTests` (02b12f7, a681dad) passed in the lane, not re-run at integration (NL-276). The design for the rest is `docs/agents/BOLT5_ONCHAIN_PLAN.md` (3b02972, 6290443). Still open: sweeps (to_local after the delay, HTLC outputs), HTLC-timeout/success broadcasts, detecting the peer's commitment on chain (NL-272), a persisted broadcast intent (NL-271), penalty (NL-095). An upstream HTLC forwarded onto a force-closed channel stays AwaitingDownstream until BOLT 5 resolves the downstream HTLC. Update (ABCD wave 4, `6b5d50e`): `BOLT5_ONCHAIN_PLAN.md` O0 and O1 are done and wired: persisted broadcasts with per-block rebroadcast (`IChainBroadcaster`), persisted outpoint watches with every channel's funding output watched (`IOutpointWatcher`), one unit of work per block, the reorg header ring and rewind (7b4a173, f251dde, 5bedf44, a9e33a7; Docker `Onchain/OnchainSmokeTests`); the revocation log written in the revoke_and_ack save, the `ChannelCloses`/`OutputResolutions` tables, `ChannelState.OnchainResolving = 37`, migration `AddOnchainResolution` (4fd3617). The O2-O6 building blocks exist but are **not wired**: signer invariant S1 (dbe4cc8, 0761773; not restored after a restart, NL-297), `FundingSpendClassifier` + `CommitmentNumber.Decode` (5926d0c), `CommitmentOutputMapper` (263ab8f), preimage extraction (d7a4c73), `SignSweepInput` + `SweepTransactionBuilder` (7394f19, ff7f6cc), `PenaltyTransactionBuilder` (0df889a), `OutputResolutionPlanner` (36e8797), `SweepFeePolicy` (369314d), all against the Appendix C/F vectors; `AddOnchainBitcoinServices()` is registered (960cf05). Still open: the watcher that classifies funding spends and drives the planner (O2-T5, NL-272), the sweep scheduler (O6-T1), HTLC resolution into the switch (O3-T3/T4), penalty execution (O5-T2/T3), anchors CPFP (O7), reorg rollback of completed watches (O6-T3, NL-292), a confirmation-target fee estimate (NL-296). Update (ABCD wave 5, `1a5ab49`): `BOLT5_ONCHAIN_PLAN.md` O2-O5 are done, wired and proven against LND. O2 (W5-A): the `IOutputResolver` port and action model (152144c); the commitment `BroadcastTransactions` row with its commitment number in the Failed save and S1 restored at registration (d040654, 41f5fc2; NL-271, NL-297); `OnchainChannelWatcher` classifies every non-mutual funding spend, persists `ChannelCloses`/`OutputResolutions` + watches and moves the channel to `OnchainResolving` (41f5fc2; NL-272); `OnchainResolutionExecutor` runs the resolvers every block in one save, marks outputs Irrevocable at 100 blocks and closes the channel (O6-T2), and catches up spends mined before a watch was tracked (cbd99c6); `forceclosechannel` (ClientCommand 14) and `pendingsweeps` (15) IPC (c2ae40a); a Closing channel is never force-failed (7f6ebd9). O3 (W5-B): `LocalCommitResolver` (to_local after the CSV, HTLC-timeout/success, second-level sweeps; 7d3a6b3, 037c04b) and `HtlcRemovalKind.OnchainTimeout = 4` failing upstream with our own `permanent_channel_failure` (5af263f). O4 (W5-C): `RemoteCommitResolver` (to_remote, timeout and preimage claims at the peer's point incl. a forward's downstream preimage, remote-next and future commitments; 3794c0d, 202341b). O5 (W5-D): `RevokedCommitResolver` + `PenaltyTransactionComposer` (batched, single and split penalties, second-level penalties; e5a556d, f4b83ff, 7189b71). Integration dd2d64f registers the three resolvers in `AddApplicationServices` and binds their options from `Node:Onchain`. Docker `Docker/Onchain/` O2 (2), O3 (4), O4 (5), O5 (2 end to end incl. an LND channel.db rollback, + 2 `Explicit` by-hand variants) green on net10.0 and net11.0 (567a3c1, cc207a8, 6bd3645, bbc51b4, a5b24e3). Still open: O6-T1 sweep scheduler and fee bumping (NL-317, NL-296), O6-T3 reorg re-resolution (NL-292, NL-293), O6-T4 mainnet gate, O7 anchors (NL-314), O8 mempool (NL-098), and the wave 5 follow-ups NL-307..NL-309, NL-311..NL-313, NL-315, NL-316, NL-318, NL-320. Update (ABCD wave 6, `3ce3cad`): `BOLT5_ONCHAIN_PLAN.md` O6-T1 and O6-T3 are done with Docker Proof O6: per-target fee estimates (6a4eb7d, NL-296), `SweepScheduler` RBF-bumps unconfirmed sweeps, claims and penalties every block and retires broadcasts that can no longer confirm (7c437b3, cde5ebb; NL-317, NL-294 partial); the chain-monitor rewind rolls back completed watches and wallet UTXOs (e8bb45b, 30584cf, 7dcf472; NL-293); the executor re-resolves after a reorg (pauses a channel whose funding spend left the chain, unresolves rolled-back spends, rebroadcasts our commitment, broadcasts it after `ReorgGraceBlocks` when the peer's is gone, moves a reconfirmed funding tx's SCID and sends a new channel_update, retires a replaced close; b330d8c, 8fcea62, 70cbd33, 6d4b625; NL-292); Docker `OnchainO6Tests` (a) reorged sweep rebroadcast, (b) penalty rebroadcast after restart, (c) RBF-bumped sweep, and the stale-SCID reproducer is a regular test (d3dfff8). The Onchain suite (18 + 2 Explicit) is green on net10.0 and net11.0. O6-T4 mainnet gate: opened (09052d0) and reverted (0c0d5c8): HTLCs stay regtest-only until NL-316 (and NL-311, NL-320, NL-322) are fixed. Still open: O6-T4, O7 anchors (NL-314), O8 mempool (NL-098), follow-ups NL-307..NL-309, NL-311..NL-316, NL-318, NL-320, NL-322, NL-329, NL-330. Update (ABCD wave 7, `4c37998`): NL-316 and NL-322 are fixed (W7-B; Docker `OnchainFinalHopTests`), so O6-T4 is no longer blocked by them; the gate stays closed (HTLCs regtest-only) pending NL-311, NL-320 and the new NL-337. New follow-ups NL-335, NL-336. Update (gossip wave G-A, `164289a`): O8 done (NL-098 fixed: ZMQ `rawtx`, `MempoolReactor` preimage and penalty reaction, Docker `OnchainMempoolTests`) and the chain-processing halt is surfaced and gates new HTLCs and channels (NL-216 fixed). Signer channel data now reloads from the DB (NL-067 partial). Still open: O6-T4 (NL-311, NL-320, NL-337), O7 anchors (NL-314, `SignWalletTransaction` NL-067). Update (gossip wave G-B, `5bbfbb5`): the three O6-T4 blockers are fixed by lane M2: NL-311 (startup catch-up of saved resolution watches, Docker `OnchainWatchCatchUpTests`), NL-320 (upstream fails on future/unknown closes before Closed) and NL-337 (final-hop preimage-known test). The whole on-chain Docker suite is green (22 + 2 Explicit), so Proofs O3-O6 pass; the evidence and remaining risks are in `BOLT5_ONCHAIN_PLAN.md` "O6-T4 evaluation evidence". The gate itself (`Node:EnableHtlcs` default regtest-only) is unchanged: the decision is left to the G-D integrator, after a re-run of N9 `ChannelSafetyFlowTests`, ABCD and the LND/CLN normal-operation suites. Still open: O6-T4 decision, O7 anchors (NL-314), follow-ups NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330, NL-335, NL-336. Update (gossip wave G-D, `48a8951`): O6-T4 done, the mainnet gate is open (lane M4): `NodeOptions.HtlcsEnabled` = `Node:EnableHtlcs ?? true`, so HTLCs are on for every network including mainnet and `Node:EnableHtlcs=false` still turns them off (6de56ad); the daemon template writes `"EnableHtlcs": null` for mainnet/testnet, which binds as unset (aadb9d4). NL-315, the only follow-up judged a fund-safety blocker, is fixed (a140940, 44767d3); the Explicit O5 cheater variant runs its victim without the mempool reaction since O8 (04aab92, test only). The G-D integrator kept the flip after the full matrix on the integrated branch: on-chain 24/24 incl. both Explicit O5 variants, LND suite 58/58 incl. N9 `ChannelSafetyFlowTests`, CLN 22/22, ABCD 3 x 10/10, gossip 3 x 24/24 (48a8951; `BOLT5_ONCHAIN_PLAN.md` "O6-T4 decision" and "O6-T4 integration record"). The epic's scope (sweeps, HTLC resolution, penalties) is done on non-anchor channels; closed here. Carried as their own entries: O7 anchors (NL-314, `SignWalletTransaction` NL-067), follow-ups NL-307..NL-309, NL-312, NL-313, NL-318, NL-329, NL-330, NL-335, NL-336, none a mainnet fund-safety blocker. Update (wave O7, `8364a01`): O7-T1..T3 done (NL-067, NL-314 fixed), anchors proofs 12/12; O7-T4 (`option_anchors`) held on NL-379, NL-380, NL-381 (`BOLT5_ONCHAIN_PLAN.md` "O7 wave record and O7-T4 decision"). Update (wave O7b, `c16d6e1`): the three gaps are fixed (NL-379: 41ce320, 083424e, db1afdb; NL-380: 4219c6c, 50f005a; NL-381: 8e75a13, 50f005a) and O7-T4 is done: `option_anchors` is advertised Optional by default (d4cc3f8; `BOLT5_ONCHAIN_PLAN.md` "O7b wave record and O7-T4 decision").
- **Fix sketch:** Watch funding outpoints, classify spends, sweep to_local/to_remote/HTLC outputs, justice txs. Sub-issues: NL-095, NL-096, NL-097, NL-098.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-056, NL-066, NL-136, NL-067
- **Plan ref:** BOLT_COVERAGE roadmap step 11; BOLT2 N9-T4 (done); `BOLT5_ONCHAIN_PLAN.md` O0-O8

### NL-095 Revocation watch / penalty is a stub
- **Status:** fixed (4fd3617, 0df889a)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/IRevocationWatchDbRepository.cs` (empty), `src/NLightning.Infrastructure.Persistence/Entities/Bitcoin/RevocationWatchEntity.cs` (not mapped), `BlockchainMonitorService.cs:189-198,400-411` (commented out), `src/NLightning.Infrastructure.Bitcoin/Transactions/PenaltyTransaction.cs`
- **Evidence:** Nothing is watched for revoked commitments. Update (ABCD wave 4, `6b5d50e`): the stubs `RevocationWatchEntity`, `RevocationWatchDbRepository`, `IRevocationWatchDbRepository`, `PenaltyTransactionModel` and `PenaltyTransaction` are deleted (4fd3617). They are replaced by the revocation log (`RevokedCommitments`, written in the revoke_and_ack save when the revoked commitment has HTLCs, plan D2) and the `OutputResolutions` table (D3), and by `PenaltyTransactionBuilder` (batched, single and split; script-executed against every Appendix C commitment and HTLC tx, 0df889a). Executing penalties on chain is part of NL-094 (O5-T2/T3) and NL-272.
- **Fix sketch:** Map the entity (key + config + 3 migrations), implement the repo and the watcher.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** BOLT2 N5-T1 (entity mapping)

### NL-096 BlockchainMonitorService has no reorg handling
- **Status:** fixed (f251dde, 5bedf44, a9e33a7, 53accb1, e8bb45b, b330d8c, 30584cf, 7dcf472)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`
- **Evidence:** ZMQ rawblock only; confirmations and SCIDs are never rolled back. Update (ABCD wave 4, `6b5d50e`): a 100-block header ring (`BlockHeaders`) drives a rewind to the fork point with one rollback save (pending first-seen heights, outpoint spends, broadcast confirmations, headers, state), then `OnBlockDisconnected` per block and the new branch; a deeper reorg halts; a late orphan notification is dropped without a rewind and the fork is searched from our own tip (a9e33a7). Not rolled back: a watch completed in a disconnected block (funding confirmation, channel SCID; NL-292, explicit Docker reproducer `OnchainSmokeTests.Given_FundingBlockReorged_When_CompetingBranchIsActive_Then_ScidFollowsTheFundingTransaction`) and wallet UTXOs (NL-293). Update (ABCD wave 6, `3ce3cad`): the rewind now also rolls back watches completed in disconnected blocks and wallet UTXOs (e8bb45b, 30584cf, 7dcf472), and the on-chain executor re-resolves channels after a reorg, including the SCID of a reconfirmed funding tx (b330d8c, 70cbd33); the stale-SCID reproducer is a regular Docker test (d3dfff8). A reorg deeper than the 100-header ring still halts chain processing by design. Residue: NL-329, NL-330.
- **Fix sketch:** Track block hashes; roll back watched-tx heights on disconnect.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** —

### NL-097 A block whose processing throws is never removed from the queue
- **Status:** fixed (cbf3184, a56185c)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`
- **Evidence:** The failing block is retried forever, stalling all later chain processing. Replayed blocks skip known deposits; the queue is capped at 144 and refilled from bitcoind. Follow-ups: NL-214, NL-215, NL-216.
- **Fix sketch:** Dequeue with bounded retry and alerting.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** —

### NL-098 No mempool (rawtx) monitoring
- **Status:** fixed (567197f, 7fde9bf, d51f6ec, 99ba3ac)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs:248,400`
- **Evidence:** `TODO: Check for new transactions`, `TODO: Check for revocation transactions in mempool`. Update (ABCD wave 5, `1a5ab49`): still open; the BOLT 5 resolvers act on confirmed spends only (plan O8). Update (gossip wave G-A, `164289a`): fixed (BOLT5 O8). The chain monitor subscribes to ZMQ `rawtx` (`Bitcoin:WatchMempool`, default on) and raises `OnWatchedOutpointSpentInMempool` for spends of watched outputs (567197f); `Application/Onchain/Mempool/MempoolReactor` stages a preimage found in the mempool on the HTLC record and fulfills upstream at once, and broadcasts the penalty behind a revoked commitment before it confirms (`RevokedCommitResolver.PrepareUnconfirmedPenaltiesAsync`); the watcher links a prepared penalty to the close when the block arrives, abandons it after `Node:Onchain:Mempool:EvictionGraceBlocks` (3) blocks without its commitment and revives it when the commitment comes back (7fde9bf, 99ba3ac). A mempool tx is never treated as a confirmation. Docker `Onchain/OnchainMempoolTests` (2) against bitcoind and LND (d51f6ec).
- **Fix sketch:** Subscribe to ZMQ rawtx.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** —

### NL-214 A block that fails part-way can be partly persisted
- **Status:** fixed (f251dde)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`ProcessBlock`)
- **Evidence:** Exceptions are caught per block but `SaveChangesAsync` still runs for the whole scope afterwards. Reported by the persist-misc batch (unverified). Update (ABCD wave 4, `6b5d50e`): each block is staged in one unit of work; memory and events change only after its save (IT `ChainMonitorPersistenceTests.Given_BlockFailsMidway_When_Processed_Then_NothingPersistedAndLaterRoundProcessesItOnce`, real SQLite).
- **Fix sketch:** One unit of work per block; save only when the block fully succeeds, otherwise discard the scope.
- **Blocks/Blocked-by:** Related NL-097, NL-133
- **Plan ref:** —

### NL-215 The tip block is not processed at startup
- **Status:** fixed (f251dde)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`StartAsync`, `AddMissingBlocksToProcessAsync`)
- **Evidence:** Catch-up fetches only below the current height, so the tip waits for the next ZMQ block. Reported by the scid-chain batch (unverified). Update (ABCD wave 4, `6b5d50e`): the tip is processed at start.
- **Fix sketch:** Include the current height in the catch-up range.
- **Blocks/Blocked-by:** Related NL-097
- **Plan ref:** —

### NL-216 Halted chain processing is only logged
- **Status:** fixed (f251dde, a9e33a7, 567197f)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`IsChainProcessingHalted`), `IBlockchainMonitor`
- **Evidence:** After NL-097 a poisoned block sets `IsChainProcessingHalted` and logs Critical; `IBlockchainMonitor` does not expose it and nothing fails the node or stops channel operations. Update (ABCD wave 4, `6b5d50e`): `IsChainProcessingHalted` is on `IBlockchainMonitor` and is also set by a reorg deeper than the header ring; pending broadcasts are still sent while halted (a9e33a7). Not done: an IPC surface and refusing channel operations while halted. Update (gossip wave G-A, `164289a`): fixed. `chainstatus` (`ClientCommand` 16) reports the halt with its `ChainProcessingHaltReason`, the last processed block and bitcoind's tip; while halted the node refuses `openchannel` and `payinvoice` over IPC, a peer's `open_channel` (error), every HTLC offer (payments and forwards fail back with `temporary_channel_failure`) and new final-hop acceptances (`temporary_node_failure`); fulfills, fails, fee updates, closes and broadcasts go on (567197f).
- **Fix sketch:** Expose the flag, surface it over IPC and refuse new channel operations (or stop the node) while halted.
- **Blocks/Blocked-by:** Related NL-097, NL-094
- **Plan ref:** —

### NL-271 Fail-the-channel has no persisted broadcast intent
- **Status:** fixed (d040654, 41f5fc2)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Safety/ChannelFailureService.cs`
- **Evidence:** Failed is saved under the lock and the commitment watch only after it. A stop between the two saves leaves no record that a broadcast was wanted, so the start-up resume (06da54b, which covers Failed channels with an unconfirmed commitment watch) skips it, unless an HTLC past its deadline makes the monitor ask again (reported by W3-A). The BOLT 5 plan's invariant S1 wants Failed + the broadcast row in one save. Update (ABCD wave 4, `6b5d50e`): `ChannelFailureService` now saves the commitment's watch in the same save as Failed + the error and publishes after the lock (eb38c26); a failure request can carry a precondition checked under the lock. W4-A added the persisted broadcast row and its staging API (`IBroadcastTransactionDbRepository.Add` + `IChainBroadcaster.PublishAsync`), and the signer can restore S1 from `ChannelSigningInfo.BroadcastSignedCommitmentNumber` (0761773). Remaining: a `MustBroadcast` `ChannelFailedException` from a handler is still persisted Failed by `ChannelManager` before it reaches the service (hub file); the failure service stores a watch, not a `BroadcastTransactions` row; nothing fills the S1 field at registration (NL-297). Update (ABCD wave 5, `1a5ab49`): fixed by W5-A: `ChannelFailureService` writes the `LocalCommitment` `BroadcastTransactions` row, with its `CommitmentNumber` (migration `AddBroadcastCommitmentNumber`, all three providers, d040654), in the same save as Failed + the error; a handler's `MustBroadcast` failure goes through `PrepareFailureUnderLockAsync` under `ChannelManager`'s lock and `CompleteFailureAsync` (publish) after it; channels failed by an older build get their row at start (41f5fc2). Tests: `ChannelFailureServiceTests`, `ChannelManagerNormalOperationTests`, IT `BroadcastCommitmentNumberTests`. The Postgres/SqlServer container round trips of the new column ran in the integration's LND suite (48/48).
- **Fix sketch:** Save the watch (or a 'broadcast requested' marker) in the same save as Failed (migration owner).
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** BOLT2 N9-T4; `BOLT5_ONCHAIN_PLAN.md`

### NL-272 No detection of the peer's commitment (or any non-close tx) spending our funding output
- **Status:** fixed (41f5fc2, 567a3c1, cbd99c6)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`HandleFundingSpentAsync`), `BlockchainMonitorService`
- **Evidence:** A funding spend that is not a mutual close is only logged at critical level. A Failed channel whose peer's commitment confirmed stays Failed and our conflicting publish is retried (PublishFailed) every block; the funding spend watch exists only from the first shutdown on (reported by W3-A and W3-B). Update (ABCD wave 4, `6b5d50e`): every channel's funding output is a persisted watch (both roles, from funding_signed, the fundee's first sighting and a startup backfill) and every spend is raised through `IOutpointWatcher.OnWatchedOutpointSpent` (f251dde). `FundingSpendClassifier` (5926d0c) and `CommitmentOutputMapper` (263ab8f) exist, but nothing calls them: `ChannelManager.HandleFundingSpentAsync` now sees every funding spend and still only logs a non-mutual one as Critical until O2-T5 (`OnchainChannelWatcher`). Update (ABCD wave 5, `1a5ab49`): fixed by W5-A: `OnchainChannelWatcher` classifies every funding spend (`FundingSpendClassifier`), maps the outputs (`ICommitmentOutputMapper`) and saves the close, the output rows, their watches, our abandoned commitment broadcast, the error and `OnchainResolving` in one save; mutual closes stay on `ChannelManager`'s close path (41f5fc2). Review fixes: spends mined before a watch was tracked are caught up by a block scan, a reorged funding spend's height and block are recorded, unmapped vouts raise a B5-GEN-06 alert (cbd99c6). Docker O2 `Given_LndForceCloses_Then_WeDetectRemoteCommit` green (567a3c1). Follow-ups: NL-307, NL-308, NL-311, NL-312.
- **Fix sketch:** Watch every channel's funding outpoint and classify spends (ours, the peer's current/previous, revoked) per the BOLT 5 plan.
- **Blocks/Blocked-by:** Part of NL-094; related NL-095
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md`

### NL-275 ChannelFailureService logs TxIds in internal byte order
- **Status:** fixed (eb38c26, 8249044)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Safety/ChannelFailureService.cs`
- **Evidence:** Log lines print the txid bytes as stored, not in the reversed display order that bitcoind and LND show (reported by W3-A). Update (ABCD wave 4, `6b5d50e`): `ChannelFailureService` logs txids in display order; regression test in 8249044.
- **Fix sketch:** Format through the display-order helper.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-280 The wallet hands out spent and shared addresses
- **Status:** fixed (10b39e7, 8249044, f365bc13, 5aef7305)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` (`GetUnusedAddressAsync`), `UtxoDbRepository.Spend`
- **Evidence:** An address with no UTXO rows counts as unused, and spending deletes the row, so spent addresses come back. Concurrent closes get the same shutdown address, which links the channels on chain (reported by W3-B). Close re-watches its address, so the output is credited (9733937). Update (ABCD wave 4, `6b5d50e`): a close skips a shutdown address that another unconfirmed close already pays to (10b39e7) and reservations are atomic across concurrent closes (`ClosingNegotiationRegistry.TryReserveShutdownScript`, 8249044), swapping to the first unused change address. A third concurrent close can still collide (logged), and the wallet still infers use from UTXO rows, so spent addresses come back. Update (ABCD wave 5, `1a5ab49`): the BOLT 5 sweep destinations (`WalletSweepDestinationProvider`, `WalletRemoteSweepDestination`) take the first unused wallet address too, so sweeps decided close together can share an address (W5-B, W5-C); on Mutinynet the cooperative close paid to the deposit address again after its UTXO funded the channel (NL-305, duplicate). Update (wave O7, `8364a01`): `FeeInputSelector` takes its change address from `GetUnusedAddressAsync` too, so concurrent fee-input reservations (CPFP children, anchors HTLC txs) can share a change script until a UTXO lands on it (privacy only; reported by X1). Update (wave lh1, `a6c633f9`): fixed. `GetUnusedAddressAsync` now reserves and saves every address it hands out, like `ReserveUnusedAddressAsync` (under the process-wide address lock; it saves the scope's unit of work, so stage nothing before calling it), and `WalletAddressesDbRepository.GetUnusedAddressAsync` never goes below the highest reserved or funded address of its type and chain. Shutdown, sweep, penalty, funding-change, `FeeInputSelector` change and `getaddress` addresses are therefore unique (f365bc13). Review fixes (5aef7305): one penalty destination per channel (the stored Penalty row's output, else one reserved address cached per process), so a rebuilt penalty keeps its txid; a funding without change reserves no change address. Residual: a pre-fix spent address that was the highest used one can be handed out once more. Address growth and the missing restore gap limit are NL-463. Proofs: `WalletAddressReservationTests`, the shared `WalletIssuanceSchemaRoundTrip` (SQLite and Docker Postgres), `RevokedCommitDataSourceTests` (lane l1).
- **Fix sketch:** Record handed-out addresses (reserve per use) instead of inferring use from UTXO rows.
- **Blocks/Blocked-by:** Related NL-281, NL-283
- **Plan ref:** —

### NL-281 BlockchainMonitorService stopped watching a wallet address after its first deposit
- **Status:** fixed (9733937, 8e0e154)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`CheckBlockForWalletMovement`)
- **Evidence:** An address was removed from `_watchedAddresses` after its first deposit (a restart reloaded it), so a second deposit to a reused address, such as a closing output, was never credited (reported by W3-B). Workaround 9733937 (close re-watches its address), fix 8e0e154 (addresses stay watched; regression test with two deposits).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-280
- **Plan ref:** BOLT2 N10-T3

### NL-283 Wallet address generation re-adds index 9 in the second batch
- **Status:** fixed (3350020, 803df11)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` (`GetUnusedAddressAsync`)
- **Evidence:** New addresses start at `GetLastUsedAddressIndex` (the highest existing index) instead of that + 1, so the second batch of 10 re-adds index 9 and `SaveChanges` hits the (Index, IsChange, AddressType) key; after 10 used addresses, getaddress, funding change and shutdown addresses fail (found by W3-B, not reproduced in Docker). Update (ABCD wave 4, `6b5d50e`): a new batch starts one past the highest stored index of its type and chain; lookup and generation run under a process-wide semaphore, so two scopes never generate the same indexes.
- **Fix sketch:** Start at max + 1; add a test that generates two batches.
- **Blocks/Blocked-by:** Related NL-280
- **Plan ref:** —

### NL-292 A watch completed in a disconnected block is not rolled back (funding confirmation, SCID)
- **Status:** fixed (e8bb45b, b330d8c, 8fcea62, 70cbd33, 6d4b625, 1530dfb, d3dfff8)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`TryRewindAsync`), `ChannelManager` funding confirmation
- **Evidence:** After a reorg the rewind resets pending watches only; a funding confirmation completed in a disconnected block is logged Critical and the channel keeps its `ShortChannelId` and stays Open, even when the funding tx confirms at another position on the new branch (reported by W4-A, review finding 1). Explicit Docker reproducer `Onchain/OnchainSmokeTests.Given_FundingBlockReorged_When_CompetingBranchIsActive_Then_ScidFollowsTheFundingTransaction` (the smoke logs the funding tx at 256x1 while the channel keeps 255x1x1). Update (ABCD wave 5, `1a5ab49`): still open. `OnchainChannelWatcher` records the new height and block of a funding spend re-confirmed after a reorg (cbd99c6), but a different recorded spend overwrites the old one and its output rows stay; `Resolved` rows whose spend was reorged out are still aged to Irrevocable; the remote resolver does not clear a disconnected spend. The Explicit stale-SCID reproducer still fails (SCID 256 expected, 255 actual). Update (ABCD wave 6, `3ce3cad`): the rewind rolls back watches completed in disconnected blocks (e8bb45b); the executor unresolves rolled-back spends, pauses a channel whose funding spend left the chain (never back to Open), rebroadcasts our commitment, broadcasts it after `ReorgGraceBlocks` when the peer's is gone, ignores the rows of a replaced close (b330d8c), publishes a revived resolving tx right after its save (8fcea62); `FundingReconfirmationHandler` moves the SCID of a reconfirmed funding tx and a new channel_update is sent (70cbd33); a critical alert names HTLC rows of a replaced close that were already resolved (6d4b625). The stale-SCID reproducer is a regular test and passes (d3dfff8). Residue: NL-329, NL-330.
- **Fix sketch:** Raise the disconnect to the watch consumers and re-derive the confirmation and SCID on the new branch (BOLT 5 plan O6-T3); drop the reproducer's `Explicit`.
- **Blocks/Blocked-by:** Part of NL-096, NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T3

### NL-293 Wallet effects of disconnected blocks are not rolled back
- **Status:** fixed (e8bb45b, 30584cf, 7dcf472)
- **Severity:** medium
- **Kind:** bug
- **Location:** `BlockchainMonitorService` (reorg path), `src/NLightning.Infrastructure.Repositories/Database/Bitcoin/UtxoDbRepository.cs` (`Spend`)
- **Evidence:** Deposits added in a disconnected block stay spendable in `IUtxoMemoryRepository` and the database, and UTXOs spent in one stay deleted, because `Spend` deletes the row (reported by W4-A, review finding 1). Update (ABCD wave 6, `3ce3cad`): no migration needed: the rewind re-reads each affected wallet output from bitcoind (`GetUnspentOutputAsync`, `gettxout` incl. the mempool) and restores or drops it (e8bb45b); an output whose spend is back in the mempool is never restored (30584cf); a failed lookup never fails the rewind (7dcf472).
- **Fix sketch:** Record a spent-at height on UTXO rows instead of deleting them (migration owner), then undo deposits and spends above the fork point in the rollback save.
- **Blocks/Blocked-by:** Part of NL-096; related NL-280
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T3

### NL-294 No abandonment rule for a broadcast the node refuses for good
- **Status:** fixed (41f5fc2, 7c437b3, 7dcf472, f365bc13)
- **Severity:** medium
- **Kind:** gap
- **Location:** `BlockchainMonitorService` (`RebroadcastPendingAsync`, `TrySendAsync`), `BroadcastTransactions` (state `Abandoned` unused)
- **Evidence:** A Pending broadcast is re-sent after every block until a block holds it. A tx bitcoind will never accept (e.g. a funding tx with missing or double-spent inputs) is retried forever and its UTXOs stay locked. Visibility is fixed: Warning on the first refusal and then every 6 in a row (a9e33a7) (reported by W4-A, review finding 5). Update (ABCD wave 5, `1a5ab49`): our pending `LocalCommitment` row is abandoned (state `Abandoned`, `MarkAbandonedAsync`) in the watcher's save when another tx spends the funding output, so it is no longer rebroadcast forever (41f5fc2). Still open: other refused rows (funding txs, NL-259) and the broadcast row of a penalty batch replaced by its split (W5-D), which is retried and refused every block. Update (ABCD wave 6, `3ce3cad`): the `SweepScheduler` abandons a pending sweep/claim/penalty whose input was spent by another transaction and marks a split penalty batch `Replaced` (7c437b3); rows `Replaced`/`Abandoned` are no longer rebroadcast (7dcf472). Still open: a refused funding tx (NL-259) and other rows bitcoind refuses for good without a conflicting spend. Update (wave lh1, `a6c633f9`): fixed. `Infrastructure.Bitcoin/Wallet/BroadcastRefusalRules` classifies bitcoind's reject reasons (permanent: missing/spent inputs, `bad-txns-*` except premature-coinbase and nonfinal, script failures, deserialization; temporary reasons reset the count). `BlockchainMonitorService.TrySendAsync` abandons a row after `AbandonAfterPermanentRefusals` (12) consecutive permanent refusals only for rows that spend wallet outputs alone (Funding, Unspecified, WalletSend), and for missing inputs only when an input is no longer a confirmed unspent output; it saves the row Abandoned, stops resending it, releases a funding's UTXO locks and logs an Error. Channel-output spends (LocalCommitment, Penalty, HtlcTransaction, Sweep, HtlcClaim, AnchorCpfp, MutualClose) are never abandoned for refusals (an unconfirmed parent is refused as missing too); they get one Error at the threshold. `pendingsweeps` lists abandoned broadcasts (`IBroadcastTransactionDbRepository.GetAbandonedAsync`, IPC `PendingSweepsIpcResponse` key 1). Proofs: `BroadcastRefusalRulesTests`, `BlockchainMonitorServiceTests`, `OnchainClientHandlerTests`, `PendingSweepsPrinterTests` (lane l1).
- **Fix sketch:** Mark a row Abandoned after N refusals with a permanent reject reason or when its inputs are spent elsewhere, and release the channel's UTXO locks with it.
- **Blocks/Blocked-by:** Related NL-259, NL-258
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O0-T1

### NL-296 IFeeService has no confirmation-target fee estimate
- **Status:** fixed (6a4eb7d)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/IFeeService.cs`, `src/NLightning.Domain/Onchain/Fees/SweepFeePolicy.cs`
- **Evidence:** `SweepFeePolicy.GetConfirmationTarget` computes the target from the deadline (clamp(deadline - tip - 3, 1, 144)), but `IFeeService` returns one node-wide rate, so the caller must pass an estimate for that target in (plan gap OG12; reported by W4-B). Update (ABCD wave 5, `1a5ab49`): still open; the three resolvers and `PenaltyTransactionComposer` pass the node-wide `IFeeService` estimate to `SweepFeePolicy.Decide`. Update (ABCD wave 6, `3ce3cad`): `IFeeService.GetFeeRatePerKwAsync(uint confirmationTarget, ct)` (default interface method answering the node-wide rate): `Bitcoind` asks `estimatesmartfee` per target, `Http` picks the mempool.space bucket, `Fixed` the fixed rate; `SweepFeePolicy.DecideReplacement` adds the BIP 125 replacement rule (6a4eb7d). The `SweepScheduler` and the resolvers use it.
- **Fix sketch:** Add `GetFeeRatePerKwAsync(confirmationTarget)` (bitcoind `estimatesmartfee` / mempool.space buckets) for the sweep scheduler.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` OG12, O6-T1

### NL-297 Signer invariant S1 is not restored after a restart
- **Status:** fixed (41f5fc2)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs` (`RegisterChannel`), `ChannelSigningInfo.BroadcastSignedCommitmentNumber`, channel registration at startup
- **Evidence:** S1 (never reveal the secret of, or sign past, a commitment signed for broadcast) is kept in memory; the signer restores it at `RegisterChannel` from `ChannelSigningInfo.BroadcastSignedCommitmentNumber` (0761773), but nothing fills that field from the persisted broadcast, so after a restart a racing revoke_and_ack is not blocked by the signer. The tx is already published, and a Failed channel refuses normal-operation messages, so this is defense in depth (reported by W4-B, O2-T1 partial). Update (ABCD wave 5, `1a5ab49`): fixed by W5-A: `ChannelManager.RegisterExistingChannelLockedAsync` sets `ChannelSigningInfo.BroadcastSignedCommitmentNumber` from the lowest `LocalCommitment` row's `CommitmentNumber`; real-signer test `ChannelManagerOnchainTests`: after registration `AdvanceLocalCommitment(L+1)` and `RevealPerCommitmentSecret(L)` are refused, the control without a row can advance.
- **Fix sketch:** Fill the field at registration from the persisted commitment broadcast (`BroadcastTransactions` row or the Failed channel's commitment watch); add a restart test through `PeerManager.StartAsync`.
- **Blocks/Blocked-by:** Part of NL-094; related NL-271
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O2-T1, O2-T2

### NL-299 BOLT 5 penalty witness weights: we estimate the real witness, below the spec's upper bounds
- **Status:** wontfix (deliberate: the real witness weight is exact and never below the signed weight; the spec numbers are upper bounds)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Onchain/Fees/SweepWeights.cs`
- **Evidence:** BOLT 5 gives 160 (to_local penalty) and 249 (accepted HTLC penalty) as witness weights; they assume an 8-byte to_self_delay push and a 3-byte cltv push and count the `1` element as one byte. Our estimator computes the actual witness: to_local 155-156, accepted HTLC 249 with a 3-byte cltv push and 248 with Appendix C's 2-byte expiries, offered HTLC exactly 243; the estimate is never below the signed weight (reported by W4-B).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O5-T1

### NL-302 UTXOs loaded at startup have no wallet address, so a funding tx cannot be signed after a restart
- **Status:** fixed (9a8a0f0)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (startup UTXO load), `UtxoDbRepository`
- **Evidence:** Found on Mutinynet (W5-E): coins received before a daemon restart came back without their `WalletAddress`; the signer skipped the input and the open failed with a NullReferenceException after funding_signed (nothing broadcast, the UTXO was released). The startup load now includes the address (9a8a0f0); tests `BlockchainMonitorServiceTests`, `UtxoDbRepositoryTests`, IT `FundingSigningAfterReloadTests` (ce091a1: saved to SQLite, reloaded, locked and signed, verified by NBitcoin's interpreter).
- **Fix sketch:** Load the wallet address with the UTXO set.
- **Blocks/Blocked-by:** Related NL-304
- **Plan ref:** `MUTINYNET.md` live smoke

### NL-304 The signer skips a funding input it cannot sign and fails later with a NullReferenceException
- **Status:** fixed (ce091a1)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs` (`SignFundingTransaction`)
- **Evidence:** An input with no locked UTXO or no wallet address was logged as a warning and left unsigned; the partly signed funding tx then failed with a wrapped NullReferenceException (W5-E, Mutinynet). It now throws `SignerException` naming the input and its outpoint (ce091a1; IT `FundingSigningAfterReloadTests` no-address and not-locked cases).
- **Fix sketch:** Throw a `SignerException` naming the input.
- **Blocks/Blocked-by:** Related NL-302
- **Plan ref:** `MUTINYNET.md` live smoke

### NL-305 A deposit address is handed out again after its UTXO is spent
- **Status:** duplicate of NL-280
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` (`GetUnusedAddressAsync`)
- **Evidence:** On Mutinynet the deposit address `tb1qf2fz...5xt` counted as unused again once its UTXO funded the channel, so the cooperative close paid our output to it (address reuse); `getaddress` moved on only after the close output arrived (W5-E). This is NL-280's "spent addresses come back".
- **Fix sketch:** See NL-280.
- **Blocks/Blocked-by:** Duplicate of NL-280
- **Plan ref:** `MUTINYNET.md` live smoke

### NL-307 OnchainChannelWatcher mutates the in-memory ChannelModel before its save
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs` (`PersistAsync`)
- **Evidence:** The state (`OnchainResolving`) and the stored error are set on the shared model before `SaveChangesAsync`; a failed save leaves the in-memory channel at 37 with its error marked, so the error goes out only on reconnection (the replayed block records the close). Same class as NL-282 (reported by W5-A; the executor's Closed transition was fixed the right way in cbd99c6).
- **Fix sketch:** Stage on the database copy and apply to the shared model after the save, as `OnchainResolutionExecutor` does.
- **Blocks/Blocked-by:** Related NL-282, NL-272
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O2-T5

### NL-308 An Unknown funding spend moves the channel to OnchainResolving instead of Failed
- **Status:** open
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs`
- **Evidence:** A funding spend the classifier cannot identify goes to `OnchainResolving` with no outputs and a B5-GEN-06 critical alert, and closes at the irrevocable depth; plan §3.3 says Failed (deviation reported by W5-A). Nothing of ours is recoverable from such a tx, and the alert is raised either way.
- **Fix sketch:** Decide and document: keep the deviation (record it in the plan) or persist Failed.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` §3.3, O2-T5

### NL-309 A revoked commitment without a revocation-log entry: its HTLC outputs are not penalized
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/Revoked/RevokedCommitDataSource.cs`, `OnchainChannelWatcher` (revoked mapping)
- **Evidence:** Without a log entry (a commitment revoked before the O1 log existed, or an HTLC-less spec), the revoked commitment is mapped from a stand-in spec (half/half balances, no HTLCs) by script, which finds only to_local and to_remote. The other vouts are alerted (B5-GEN-06, "predates the revocation log", cbd99c6) and watched; a preimage in their spends fulfills upstream and our offered HTLCs fail only once those outputs are spent 6 deep without it (7189b71), but the HTLC outputs themselves are never penalized (reported by W5-A, W5-D). Channels opened after wave 4 always have the log.
- **Fix sketch:** Try every HTLC script shape the channel could have had (the HTLC set is lost, but the revocation key is known), or accept it for pre-O1 channels only and document it.
- **Blocks/Blocked-by:** Part of NL-094, NL-095
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` §8 risk 5, O5-T2

### NL-311 Resolution watches saved but not tracked before a crash are never caught up at startup
- **Status:** fixed (eff0196, 1902bb5, e0b3421, e3023b8)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs`, `OnchainResolutionExecutor` (`CatchUpSpendsAsync`), startup of `OnchainResolving` channels
- **Evidence:** Between the watcher's or executor's save and `TrackWatchedOutpoint` a crash leaves persisted watches the monitor tracks again after a restart, but blocks it already processed in between are never rescanned for them, so a spend mined there is missed; there is no startup catch-up for `OnchainResolving` channels (reported by W5-A review). Update (gossip wave G-B, `5bbfbb5`): fixed by lane M2: before a channel's first block round in a process `OnchainResolutionExecutor.CatchUpSavedWatchesAsync` scans bitcoind for spends of the saved watch of every Pending/Waiting/Broadcast row, from the row's parent height or the spend recorded on the watch (eff0196); since e3023b8 the scan runs after every channel's time-critical round, as one block scan shared by all resolving channels, and resumes at a block it could not read. Proofs: `OnchainResolutionExecutorTests`, `OnchainRestartCatchUpTests` (real SQLite, bitcoind unreachable in the first round) and Docker `Onchain/OnchainWatchCatchUpTests` against LND david (1902bb5). The lower bound is still the row's parent height (no persisted per-watch scan height; performance only, NL-313).
- **Fix sketch:** At startup, run `CatchUpSpendsAsync` for every pending resolution watch of an `OnchainResolving` channel from its parent height.
- **Blocks/Blocked-by:** Part of NL-094; related NL-272
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O2-T5, O6-T2

### NL-312 A Failed channel whose signed mutual close confirms stays Failed
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs` (Mutual left alone), `ChannelManager.RecordMutualCloseSpendAsync`
- **Evidence:** The watcher classifies the spend as Mutual and leaves it to `ChannelManager`, which records a mutual close only for ShuttingDown, Negotiating or Closing; a channel failed after it signed a closing tx (e.g. Negotiating → Failed) therefore stays Failed for good (reported by W5-A review; the residue of 7f6ebd9 for non-Closing states).
- **Fix sketch:** Record the mutual close for Failed channels too (move to Closed at the irrevocable depth).
- **Blocks/Blocked-by:** Related NL-272, NL-034
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` §1.3

### NL-313 The resolution catch-up scan fetches whole blocks from the parent height to the tip
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Onchain/OnchainResolutionExecutor.cs` (`CatchUpSpendsAsync`)
- **Evidence:** Each new watch is caught up by fetching every block over RPC from its parent height (commitment height, a spend's height, a broadcast's confirmation) to bitcoind's tip. Cheap on regtest; on mainnet a peer's second-level tx without a broadcast row falls back to the commitment's height and can mean a long scan (reported by W5-A review).
- **Fix sketch:** A monitor-side back-scan in `Infrastructure.Bitcoin` (or `gettxout`/`gettxspendingprevout` first).
- **Blocks/Blocked-by:** Related NL-311
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6

### NL-314 Anchor channels: HTLC outputs of our commitment are not resolved on chain
- **Status:** fixed (7b6b703, 91e35d2, 7947b63, 8363358, d399ecb, 2ab70dc, d7d9efc, 658e086, b76d661)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/LocalCommitResolver.cs`
- **Evidence:** The HTLC-timeout/success txs of an anchor channel carry zero fee and need fee inputs (`SINGLE|ANYONECANPAY`, O7-T3); `LocalCommitResolver` only logs such outputs (reported by W5-B). `OptionAnchors` is not negotiated, so no live channel has them. Update (wave O7, `8364a01`): O7-T3 (X3): `HtlcTransactionBuilder.AddFeeInputs` combines the zero-fee HTLC tx with wallet fee inputs and change (Appendix F byte-exact alone, script-valid combined), `LocalCommitResolver` resolves the HTLC outputs through `IAnchorFeeInputProvider` (owner-keyed persistent reservations, rebuild after a lost wallet input, RBF, one shortage warning per output), and a revoked HTLC we offered on an anchors channel is penalized alone from the first round (7b6b703, 91e35d2, 7947b63, 8363358). O7-T2 (X2): `AnchorCpfpService` CPFPs our commitment through `to_local_anchor`, RBFs the child, releases inputs and sweeps anchors after 16 blocks (d399ecb, 2ab70dc, d7d9efc). Integration: wallet adapters, the signer seam for a combined anchors HTLC tx, deadline RBF of the child and the mempool penalty without the CSV-1 `to_remote` (658e086, b76d661). Docker `Docker/Onchain/Anchors/` 12/12 against LND 0.20. `option_anchors` stays experimental: NL-379, NL-380, NL-381 block O7-T4. Update (wave O7b, `c16d6e1`): NL-379, NL-380, NL-381 fixed and O7-T4 done: `option_anchors` is advertised Optional by default (d4cc3f8); anchors Docker suite 18/18, LND/CLN/ABCD/gossip suites green on anchors channels.
- **Fix sketch:** Plan O7 (wallet fee inputs, CPFP, HTLC txs with fee inputs) before enabling `OptionAnchors`.
- **Blocks/Blocked-by:** Part of NL-094; anchors enabled in d4cc3f8 after NL-379, NL-380, NL-381
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7

### NL-315 Reading a peer's HTLC spend needs the tx from bitcoind; the fallback alert is raised only at one depth
- **Status:** fixed (a140940, 44767d3)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/LocalCommitResolver.cs` (`ResolveOutputAsync`)
- **Evidence:** When our offered HTLC output is spent by someone else and no preimage was staged, the resolver fetches the spender (`IBitcoinChainService`) to read its witness; without txindex or the block the upstream HTLC stays unresolved (never failed wrongly), the resolver logs an error every round and raises one B5-LCL-LO-03 alert only on the round at exactly the reasonable depth, so a node offline over that block gets only the logs (reported by W5-B review). Update (gossip wave G-D, lane M4): fixed. `LocalCommitResolver.ReadSpendingWitnessAsync` reads the spender from the block at the watch's recorded spend height first (no txindex needed), then `getrawtransaction`; a block fetch that throws (pruned block, reorg, RPC error) falls back to `getrawtransaction` (`TryGetBlockAsync`, 44767d3). The B5-LCL-LO-03 alert is raised once per process and HTLC from the first round at or past the reasonable depth, and marked sent only after the executor logged it following a successful save (`AlertAction.Emitted`, 44767d3). Regression tests in `LocalCommitResolutionTests` and the executor tests. Residue: a pruned node without the block still gets only the alert (never a wrong fail).
- **Fix sketch:** Read the spender from the block at the recorded spend height (as `RevokedCommitDataSource` does) and raise the alert once when first due.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O3-T3

### NL-316 An HTLC for our invoice not yet fulfilled when the peer force-closes is never claimed on chain
- **Status:** fixed (7ca5c60, db00321, 72e7f49, a3cf0ce)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/RemoteCommitResolver.cs`, `HtlcSwitch` final hop
- **Evidence:** The preimage claim on the peer's commitment uses only a preimage we learnt for that HTLC (our persisted fulfill, or a forward's downstream preimage, 202341b), never the invoice table alone (B5-LCL-RO-02). An HTLC locked in for our invoice whose fulfill was refused (e.g. the channel failed first) leaves the invoice Open and nothing ties it to the HTLC, so the peer times it out (reported by W5-C). Update (ABCD wave 6, `3ce3cad`): still open; it keeps the O6-T4 mainnet gate closed (0c0d5c8). The held parts of a settled MPP set have the same on-chain gap by another path (NL-322). Update (ABCD wave 7, `4c37998`): on a Failed/OnchainResolving incoming channel the switch acts only on final-hop onions and commits an accepted part by persisting the preimage on the incoming `HtlcRecord.KnownPreimage` in the invoice's settle save (7ca5c60); `FinalHopClaims.GetAcceptedPreimageAsync` lets `LocalCommitResolver`/`RemoteCommitResolver` claim with it only when the invoice is Settled with that preimage and the record has no fail removal, and the resolvers ask the switch to decide for unprocessed HTLCs of an Open invoice (db00321, a3cf0ce). Docker `Onchain/OnchainFinalHopTests` (72e7f49). O6-T4 is no longer blocked by NL-316/NL-322. Residual: the engine keeps KnownPreimage through a fail removal (harmless: the resolvers check the removal); NL-335, NL-336.
- **Fix sketch:** A switch-side hook: accept the HTLC as final hop on chain (checks of `FinalHopProcessor`) and persist the fulfill/preimage for it, then the resolver claims it.
- **Blocks/Blocked-by:** Part of NL-094; related NL-114
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O4-T2, §3.4

### NL-317 No sweep scheduler: sweeps, claims and penalties are never fee-bumped
- **Status:** fixed (7c437b3, cde5ebb, d3dfff8)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/`, `Revoked/PenaltyTransactionComposer.cs`, `SweepFeePolicy`
- **Evidence:** Every resolver builds one tx per output (no batching of to_remote with preimage claims) at the fee decided when it is first built and republishes the same tx every block; there is no RBF of a sweep, claim or single penalty (only the one-time split of a penalty batch at deadline - 18), so a low estimate can miss a deadline (reported by W5-C, W5-D). Update (ABCD wave 6, `3ce3cad`): `Onchain/Fees/SweepScheduler : ISweepScheduler` runs in every block round of the executor: a pending `Sweep`/`HtlcClaim`/`Penalty` broadcast due per `SweepFeePolicy.ShouldBump` is re-signed with the same inputs at `DecideReplacement` (BIP 125 minimum or the per-target estimate, NL-296), stored as a replacement row in the round's save and published after it; penalties re-signed with the revocation key (cde5ebb); pre-signed HTLC txs and our commitment are never bumped (no anchors). Docker Proof O6 (c) RBF-bumped sweep (d3dfff8). No batching of to_remote with preimage claims.
- **Fix sketch:** O6-T1 `SweepScheduler`: bump per block by `SweepFeePolicy` with a per-target estimate (NL-296).
- **Blocks/Blocked-by:** Part of NL-094; related NL-296, NL-294
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T1

### NL-318 The revoked-commitment resolver does not persist an on-chain preimage
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/RevokedCommitResolver.cs`
- **Evidence:** A preimage in the cheater's HTLC-success fulfills upstream at once, but it is not staged into `HtlcRecord.KnownPreimage` (the remote resolver does, 202341b); after a restart the fulfill is rebuilt from the recorded spend, which needs the spending tx to be readable again, and the switch's `DerivePending` replay does not see it (reported by W5-D). Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): the resolver moved to `Onchain/Resolvers/RevokedCommitResolver.cs` (its data source stays in `Resolvers/Revoked/`); still open: it reads `KnownPreimage` (`RevokedCommitResolver.cs:341,443,498`) and raises the fulfill from the witness (`:271-276`), but never stages it as `RemoteCommitResolver.StageKnownPreimageAsync` does (`RemoteCommitResolver.cs:1015`).
- **Fix sketch:** Stage the preimage with a `StageWriteAction` in the round's save, as `RemoteCommitResolver` does.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O5-T2

### NL-320 Upstream HTLCs of a channel closed by a future or unknown commitment are failed only after Closed
- **Status:** fixed (6ea8b1c, 7e96e28, 9b4e4c7)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/RemoteCommitResolver.cs` (future commitment), `OnchainChannelWatcher` (unknown), `HtlcSwitch.ResumeCircuitAsync`
- **Evidence:** A future (data loss) or unknown commitment has no HTLC set the resolvers can fail at reasonable depth; a preimage in a spend still fulfills, but an upstream HTLC forwarded onto such a channel is failed only by the switch once the channel is Closed (100 blocks, or, for a future commitment, once its HTLCs expired more than 100 blocks ago), which can be past the upstream deadline and force-close the upstream channel too (reported by W5-A review F2, W5-C). Update (gossip wave G-B, `5bbfbb5`): fixed by lane M2: `RemoteCommitResolver` also resolves `Unknown` closes (as data loss: every output watched, to_remote swept, B5-GEN-06 alert) and, for data-loss and `Unknown` closes, fails each open offered forward upstream (`OnchainTimeout`) once the tip reaches `cltv_expiry + ReasonableDepth` and the close is that deep, or fulfills it with a known preimage; `HtlcSwitch.ResumeCircuitAsync` fails an Offered circuit whose outgoing channel is Closed with no preimage (6ea8b1c). Our own payment on such a close stays in flight until `cltv_expiry + IrrevocableDepth`, so a late on-chain preimage still settles it (7e96e28). `HtlcExpiryMonitor` fails back a forward whose outgoing HTLCs all sit on a Closed, unloaded channel at its fail-back deadline, and the switch fallback reads `Node:Onchain:ReasonableDepth` (9b4e4c7; the circuit stays `Offered`, no loss). Proofs: `RemoteCommitResolverTests`, `OnchainEventsTests`, `HtlcExpiryMonitorTests`.
- **Fix sketch:** Fail such upstream HTLCs (permanent_channel_failure) once their outgoing `cltv_expiry` plus reasonable depth has passed without a preimage on chain.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O4-T3, §3.4

### NL-322 Held parts of a settled MPP set are not claimed on chain after a force close
- **Status:** fixed (7ca5c60, db00321, 72e7f49, a3cf0ce)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Resolvers/LocalCommitResolver.cs`, `RemoteCommitResolver.cs` (`GetAllowedPreimageAsync`), `Channels/Safety/HtlcExpiryMonitor.cs`
- **Evidence:** A part of a settled set whose fulfill has not gone out when its channel is force-closed has no Fulfill removal and no forward record, so the resolvers never use the Settled invoice's preimage for it (B5-LCL-RO-02) and the peer times it out. The `HtlcExpiryMonitor` treats every HTLC of an Accepted/Settled invoice as preimage-known, so such parts are never failed back either (reported by W6-B, review finding 2 skipped as out of lane). Update (ABCD wave 7, `4c37998`): `FulfillSetAsync` marks every part with the preimage before the settle; an on-chain or refused part is claimed by the resolvers; marks are taken back when a set is not settled, and a set whose settle failed is retried by its timer (a3cf0ce). Docker `OnchainFinalHopTests` claims the held part on the force-closed channel. The `HtlcExpiryMonitor` half is NL-337.
- **Fix sketch:** In the resolvers, accept the preimage of a Settled invoice for an incoming final-hop HTLC with a matching hash (with basic_mpp), or persist set membership (NL-323).
- **Blocks/Blocked-by:** Part of NL-094; related NL-316, NL-081
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O3/O4, §3.4

### NL-329 A funding tx reorged out and never reconfirmed keeps its old SCID
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Reorg/FundingReconfirmationHandler.cs`, invoice route hints
- **Evidence:** The SCID moves only when the funding tx confirms again; a funding tx that never reconfirms leaves the channel Open with the old SCID, and invoices issued before a move keep the old SCID in their route hints (reported by W6-F).
- **Fix sketch:** Pause or fail a channel whose funding confirmation was rolled back and not re-seen within a grace; document that old invoices' hints go stale.
- **Blocks/Blocked-by:** Related NL-292, NL-096
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T3

### NL-330 Upstream fails made for a close that a reorg replaced are not re-checked
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs` (`RetireReplacedCloseAsync`)
- **Evidence:** When a reorg replaces a close whose HTLC outputs were already resolved (and possibly failed upstream at reasonable depth), the rows of the old close are ignored and a critical `[B5-GEN-06]` alert names them, but the new close's HTLC outputs are not reconciled with what was already told upstream, so a preimage claim on the new close cannot be forwarded to an upstream HTLC already failed (reported by W6-F). Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): the method lives in `OnchainChannelWatcher.cs:216`, not the executor; still open: it only logs the critical `[B5-GEN-06]` alert for already-resolved HTLC rows and marks the old rows Ignored.
- **Fix sketch:** Keep the upstream outcome per HTLC across closes; on a replaced close, resolve the new close's HTLC outputs against it and alert on a conflict.
- **Blocks/Blocked-by:** Part of NL-094; related NL-292
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T3, B5-GEN-06

### NL-335 The on-chain final-hop decision refuses an HTLC whose invoice expired after lock-in
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`, `FinalHop/FinalHopProcessor.cs`, `Onchain/Resolvers/FinalHopClaims.cs`
- **Evidence:** The on-chain final-hop decision checks invoice expiry at decision time, so an HTLC locked in just before its invoice expired and never fulfilled is not claimed on chain (the peer times it out) (reported by W7-B).
- **Fix sketch:** Evaluate expiry against the HTLC's lock-in (or `AddedAt`) instead of now.
- **Blocks/Blocked-by:** Related NL-316
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O4-T2

### NL-336 DustExposureHtlcSwitch can swallow the on-chain final-hop decision
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Fees/DustExposureHtlcSwitch.cs` (decorator of `IHtlcSwitch`)
- **Evidence:** For an unprocessed HTLC over the dust-exposure limit on a Failed/OnchainResolving channel, the decorator tries an off-chain fail that is refused and returns handled, so the inner switch never makes the on-chain final-hop decision (reported by W7-B).
- **Fix sketch:** Skip the dust-exposure fail for channels that are no longer Open, or fall through to the inner switch when the fail is refused.
- **Blocks/Blocked-by:** Related NL-316
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O4-T2

### NL-337 HtlcExpiryMonitor treats every final-hop HTLC of an Accepted/Settled invoice as preimage-known
- **Status:** fixed (c4fab04)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Safety/HtlcExpiryMonitor.cs` (`ResolveIncomingAsync`)
- **Evidence:** PreimageKnown for a final-hop HTLC comes from the invoice status, not from the incoming record's `KnownPreimage` mark plus a Settled invoice (the NL-323 commit point). A duplicate HTLC for a Settled invoice whose 0x400F fail cannot be sent (peer away) makes the monitor force-close the channel at the fulfillment deadline instead of failing it back (reported by W7-B). Update (gossip wave G-B, `5bbfbb5`): fixed by lane M2: an incoming HTLC that was never forwarded is `PreimageKnown` only when `FinalHopClaims.GetAcceptedPreimageAsync` accepts it (mark hashes to the HTLC, no fail removal, invoice Settled with that preimage); anything else is `UnresolvedFinalHop` and is failed back at the fulfillment deadline, never force-closed. Proof: `HtlcExpiryMonitorTests` (duplicate HTLC for a Settled invoice whose 0x400F could not be sent is failed back; marks on Open/Accepted invoices and a mismatching preimage too).
- **Fix sketch:** Use the same test as `FinalHopClaims.GetAcceptedPreimageAsync` (mark, hash, Settled with that preimage, no fail removal).
- **Blocks/Blocked-by:** Related NL-322, NL-323; part of NL-094 (O6-T4)
- **Plan ref:** BOLT2 N9; `BOLT5_ONCHAIN_PLAN.md` O6-T4

---

### NL-379 No on-chain wallet reserve for anchors channels
- **Status:** fixed (41ce320, 083424e, db1afdb)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.cs`, `src/NLightning.Application/Onchain/Resolvers/LocalCommitResolver.cs` (anchors HTLC txs), channel open/accept and funding coin selection
- **Evidence:** With anchors our commitment pays a low fee and our HTLC-timeout/success txs pay none, so the CPFP child and every HTLC tx need confirmed wallet outputs; nothing keeps any. A node whose wallet is empty or spent into fundings cannot bump its commitment nor claim an HTLC on its own commitment, and the resolver only retries every block while the HTLC's `cltv_expiry` passes (upstream loss). LND keeps 10,000 sat per anchors channel (up to 100,000) and refuses opens and spends that would break it (wave O7 integrator, `BOLT5_ONCHAIN_PLAN.md` "O7 wave record and O7-T4 decision"). Update (wave O7b, `c16d6e1`): fixed by lane Y1. `Node:Anchors` (`AnchorReserveOptions`: `ReservePerChannel` 10,000 sat, `MaxReserve` 100,000 sat, `PendingOpenTimeout` 10 min) sets the reserve to min(per channel x anchors channels not Closed/Stale, max); `IAnchorReserveService` (Domain) → `Infrastructure.Bitcoin/Wallet/AnchorReserveService` refuses an opener's anchors open (`not_enough_balance`, nothing locked) and a fundee's anchors channel (`ChannelErrorException` before the temporary channel is stored), counts admitted in-flight opens under one semaphore, and backs the reserve only with outputs the fee selector can spend (`UtxoModel.BacksAnchorReserve`: mined, P2WPKH/P2TR wallet address, 3 confirmations); the 6-argument `LockUtxosToSpendOnChannel` keeps the reserve atomically under the repository lock, including the funding's own worst-case fee (`FundingFeeEstimator`), and throws `AnchorReserveException` (41ce320, 083424e, db1afdb). Fee inputs (`IFeeInputSelector`: CPFP children, anchors HTLC txs) may spend the reserve. `walletbalance` gains response keys 2-5 (reserve, anchors channel count, available, spendable). Docker `AnchorsReserveTests` (LND's anchors open to our empty wallet refused, accepted once funded; our open refused with nothing locked) and `AnchorsReserveGapTests` (the existing channel's reserve counted under the cap; a refusal locks nothing) (db1afdb, 2a086bb, 2b20449). Not added: a per-channel figure in `listchannels` (the reserve is one capped pool). Follow-ups: NL-392, NL-393.
- **Fix sketch:** Keep a per-anchors-channel reserve like LND: refuse to open or accept an anchors channel, and keep fundings and other wallet spends from going below it; surface it in `info`/`listchannels`.
- **Blocks/Blocked-by:** Blocked O7-T4 (done in d4cc3f8); related NL-314, NL-385, NL-392, NL-393
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T4

### NL-380 No package relay: a commitment below the mempool minimum leaves its CPFP child an orphan
- **Status:** fixed (4219c6c, 50f005a)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.cs` (publish), `IBlockchainMonitor.PublishTransactionAsync`
- **Evidence:** The child is published with `sendrawtransaction` only. When our commitment pays less than bitcoind's mempool minimum (a fee spike after the last `update_fee`), the commitment is refused and the child is refused as an orphan; both rows stay pending and are rebroadcast every block without entering a mempool, so B5-FAIL-06 is not met in that case (reported by X2 and the wave O7 integrator). Update (wave O7b, `c16d6e1`): fixed by lane Y2. `IBitcoinChainService.SubmitPackageAsync(parent, child)` (the default method answers Unsupported; `BitcoinChainService` parses the result into `PackageSubmitResult`, remembers a missing or regtest-only RPC and logs it once, never throws); `AnchorCpfpService` sends commitment and child as a 1p1c package when the child is refused or missing from bitcoind's mempool, also from the persisted rows after a restart, and replaces a child whose package was refused for fee at the next block (4219c6c); the CPFP target is floored at `getmempoolinfo` `mempoolminfee` (NL-388, 50f005a). Docker `AnchorsPackageRelayTests`: a second bitcoind with a full 5 MB mempool refuses the commitment alone (`mempool min fee not met`) and accepts and mines the package (2a086bb). Deviation: raising `-minrelaytxfee` cannot prove package relay, because Bitcoin Core 28+ refuses a non-TRUC transaction below `minrelaytxfee` even inside a package; package relay only rescues a v2 commitment between `minrelaytxfee` and the dynamic mempool minimum. Follow-ups: NL-389 (the peer's commitment), NL-391.
- **Fix sketch:** Send parent and child with `submitpackage` (Bitcoin Core 28+, 1p1c) when the parent is refused for fee; fall back to `sendrawtransaction` otherwise.
- **Blocks/Blocked-by:** Blocked O7-T4 (done in d4cc3f8); related NL-314, NL-388, NL-389, NL-391
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2, O7-T4

### NL-381 The peer's commitment is never fee-bumped through our anchor
- **Status:** fixed (8e75a13, 50f005a)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.cs`
- **Evidence:** `AnchorCpfpService` only handles our own `LocalCommitment` broadcast row. When the peer's commitment is the one in the mempool (it force-closed, or we see it after a restart), we neither CPFP it through our `to_remote_anchor` nor sweep that anchor, so HTLCs it offered us with a near deadline wait on the peer's fee (reported by X2 and the wave O7 integrator). Update (wave O7b, `c16d6e1`): fixed by lane Y2 (`Application/Onchain/Anchors/AnchorCpfpService.Peer.cs`). The peer's unconfirmed commitment is found from the O8 `MempoolReactor` hand-over (`IAnchorCpfpService.OnPeerCommitmentInMempool`) or by `getrawtransaction` of its rebuilt current/next txids; while it carries untrimmed HTLCs it gets a child through our anchor (deadline = earliest `cltv_expiry`, stake = our `to_remote` plus those HTLCs, same `PlanChildAsync`, RBF and cap), shares the reservation and release rules of our own children, is abandoned when another close is recorded or after `PeerCommitmentMissingBlocks` (6) blocks missing at the tip, and both anchors are swept after 16 blocks (8e75a13). Review fixes: persisted peer children keep an Open channel in the rounds after a restart, and a crash-orphaned reservation is released once the peer's close is recorded (50f005a). No new `BroadcastPurpose`, no schema change. Docker `AnchorsPeerCommitmentBumpTests`: david force-closes at about 4 sat/vB with its wallet leased; our child confirms its commitment before the HTLC deadline and we claim the HTLC with the preimage (2a086bb, 2b20449). Follow-ups: NL-389, NL-390.
- **Fix sketch:** Also run the CPFP round for an unconfirmed peer commitment seen in the mempool (O8 `MempoolReactor`) when it carries HTLCs with a deadline, spending our anchor on it; sweep it after 16 blocks like ours.
- **Blocks/Blocked-by:** Blocked O7-T4 (done in d4cc3f8); related NL-314, NL-098, NL-389, NL-390
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2, O7-T4

### NL-384 A reorg that unconfirms a CPFP child after its reservation ended does not reserve its inputs again
- **Status:** open (partial: 41ce320)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/FeeInputSelector.cs` (`ConfirmAsync`), `BlockchainMonitorService` rewind
- **Evidence:** Once `ConfirmAsync` has ended a reservation (after the chain monitor processed the spend), a reorg that removes the child re-adds its inputs as spendable wallet UTXOs and nothing reserves them again; only the pending-broadcast exclusion of `ReserveAsync` (a56a013) keeps them from new fee selections while the child's row is pending again, and channel fundings do not have that exclusion (NL-385). Documented on `IFeeInputSelector` (reported by X1). Update (wave O7b, `c16d6e1`): since NL-385 (41ce320) both fee-input and funding selection skip the inputs of a child that a reorg put back to Pending (`PendingBroadcastOutpoints`), so we cannot double-spend them; the reservation itself is still not re-created and there is no reorg proof (wave O7b integrator).
- **Fix sketch:** Re-reserve the inputs of a pending `AnchorCpfp`/anchors HTLC row after a rewind, or make every wallet selection skip outputs of pending broadcasts (NL-385).
- **Blocks/Blocked-by:** Related NL-385, NL-292
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T1

### NL-385 Channel-funding coin selection can pick outputs spent by our own pending broadcasts after a restart
- **Status:** fixed (41ce320)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Memory/UtxoMemoryRepository.cs` (`LockUtxosToSpendOnChannel`)
- **Evidence:** Funding locks and fee reservations live in memory or their own table, and the chain monitor removes a UTXO only when it processes the spending block. After a restart the funding selection can take an output that a pending `BroadcastTransactions` row (a sweep, a CPFP child, an unconfirmed funding) already spends; the new funding tx then conflicts and is refused forever (NL-294), locking its UTXOs (NL-259). `FeeInputSelector` excludes such outpoints since a56a013; the funding path does not (reported by X1's review). Update (wave O7b, `c16d6e1`): fixed by lane Y1: the pending-broadcast parsing moved to the shared `Infrastructure.Bitcoin/Wallet/PendingBroadcastOutpoints`; the funding path (`AnchorReserveService.LockFundingUtxosAsync`, used by `OpenChannelClientHandler`) passes those outpoints as excluded, and the available balance behind the anchors reserve leaves them out (41ce320). Regression: a SQLite restart test shows a pending funding broadcast's input is neither locked nor counted.
- **Fix sketch:** Share the pending-broadcast outpoint exclusion of `FeeInputSelector.ReserveAsync` with `LockUtxosToSpendOnChannel` (or remove spent-by-pending outputs from the memory set at startup).
- **Blocks/Blocked-by:** Related NL-259, NL-294, NL-384
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T1

### NL-386 A confirmed commitment's pending CPFP child keeps its inputs up to 2016 blocks when bitcoind is unreachable
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.cs`, `AnchorCpfpOptions.ConfirmedCommitmentChildWaitBlocks`
- **Evidence:** After our commitment confirms, a pending child keeps its reservation until `IBitcoinChainService` shows our anchor spent, or `ConfirmedCommitmentChildWaitBlocks` (2016) have passed. Without a reachable chain service the wallet inputs stay reserved that long, and abandoning after the wait can still leave the child in other mempools (reported by X2's review).
- **Fix sketch:** Double-spend the child's wallet inputs back to the wallet at the end of the wait (or once the anchor is known spent by someone else) instead of only abandoning the row.
- **Blocks/Blocked-by:** Related NL-314, NL-379 (since wave O7b those reserved inputs also leave the balance that backs the anchors reserve, so they can refuse new anchors opens)
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2

### NL-387 SweepScheduler still says anchors HTLC transactions are never bumped
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Onchain/Fees/SweepScheduler.cs:42` (XML remark)
- **Evidence:** The remark reads "Pre-signed HTLC-timeout/success transactions ... bumped (no anchors: O7)". Since wave O7 `LocalCommitResolver` RBF-bumps anchors HTLC transactions itself (8363358) and `AnchorCpfpService` bumps the CPFP child; `SweepScheduler` still skips `BroadcastPurpose.HtlcTransaction` on purpose (reported by X3). `src/NLightning.Application/CLAUDE.md` was corrected in the wave O7 ledger commit.
- **Fix sketch:** Say that the resolver owns anchors HTLC RBF and the CPFP service owns the child.
- **Blocks/Blocked-by:** —
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T3

### NL-388 A commitment paying the fee estimate but not bitcoind's mempool minimum got no CPFP child
- **Status:** fixed (50f005a)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.cs` (`PlanChildAsync`), `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs`
- **Evidence:** `AnchorCpfpPolicy.DecideChild` made no child when our commitment alone paid the fee estimate, so a commitment below the dynamic mempool minimum was never packaged and stayed out of the mempool (reported by lane Y2 in wave O7b). Fixed by its review step: `IBitcoinChainService.GetMempoolMinFeeRatePerKwAsync` (default null; `getmempoolinfo` `mempoolminfee` in BTC/kvB converted to sat/kw, rounded up) floors the CPFP target for our commitment and the peer's, also the replacement target after a fee refusal (50f005a). Regression: `AnchorCpfpServiceTests.Given_CommitmentPayingTheEstimateButNotTheMempoolMinimum_When_Round_Then_ChildMadeAndPackaged` and 3 parser cases in `BitcoinChainServicePackageTests`.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-380, NL-314
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2

### NL-389 The peer's commitment below our mempool minimum is never packaged with our anchor child
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.Peer.cs`
- **Evidence:** A peer commitment that pays less than our bitcoind's mempool minimum never enters our mempool, so `getrawtransaction` does not find it and it is not bumped; the O8 hand-over has its bytes, but we never `submitpackage` it with our child (reported by lane Y2; documented in `src/NLightning.Application/CLAUDE.md`).
- **Fix sketch:** When `getrawtransaction` misses a handed-over peer commitment, send its bytes with our child through `SubmitPackageAsync`.
- **Blocks/Blocked-by:** Related NL-380, NL-381, NL-098
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2

### NL-390 The peer-commitment hand-over to AnchorCpfpService is kept in memory only
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.Peer.cs`
- **Evidence:** The peer commitment handed over by `MempoolReactor` is not persisted. (a) After a restart it is found again only by `getrawtransaction` of the rebuilt txids and only while the channel is Failed or OnchainResolving, so an Open channel whose peer commitment was seen before the restart and has no persisted child waits for the HTLC deadline monitor to fail it. (b) Without an `IBitcoinChainService` the handed-over commitment is trusted until a close is recorded; if it was evicted and no close comes, our child of it keeps its reservation. (c) A crash-orphaned reservation while the peer's commitment is evicted and no close is ever recorded stays reserved; only the recorded-close case is released (50f005a) (reported by lane Y2).
- **Fix sketch:** Persist the hand-over (or rescan the mempool at startup for commitments of Open anchors channels) and bound the life of a reservation whose parent is gone.
- **Blocks/Blocked-by:** Related NL-381, NL-386
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2

### NL-391 A package refused for fee is remembered in memory only
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Onchain/Anchors/AnchorCpfpService.cs`
- **Evidence:** When `submitpackage` refuses a commitment and child for fee, the child is marked in memory so the next round replaces it without waiting `RbfIntervalBlocks`; after a restart the first round has to see the refusal again, costing one block (reported by lane Y2).
- **Fix sketch:** Persist the mark on the child's `BroadcastTransactions` row, or resubmit the package at the first round after a restart and act on its result in the same round.
- **Blocks/Blocked-by:** Related NL-380
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T2

### NL-463 Wallet address growth: every hand-out reserves an address and restore has no gap limit
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` (`GetUnusedAddressAsync`), `src/NLightning.Application/Onchain/Resolvers/Local/WalletSweepDestinationProvider.cs`, `src/NLightning.Application/Channels/Close/UpfrontShutdownScriptSource.cs`
- **Evidence:** Since NL-280 (f365bc13) every `GetUnusedAddressAsync` reserves a new address, so `getaddress`, every sweep build (`WalletSweepDestinationProvider`: sweeps, anchor CPFP change, anchor sweep; RBF rebuilds each reserve one) and every close consume one; a restore from seed must scan past a larger gap, and no gap-limit logic exists. Upfront scripts reserved for opens that fail after the reservation are never released; the fundee reuse (5aef7305) is in memory only, so each restart can leak one address per pending open (reported by lane l1, wave lh1).
- **Fix sketch:** Give the anchor sweep and CPFP a per-channel destination like the penalties; add a gap limit to the wallet scan; release or persist-for-reuse the upfront reservation of a failed open.
- **Blocks/Blocked-by:** Related NL-280, NL-045
- **Plan ref:** —

## BOLT 7: Gossip

### NL-099 [EPIC] BOLT 7 gossip: announcements, channel_update, queries, graph
- **Status:** fixed (e7b5269, 3ba2e4f, b93dd05, f9c54cc, 7c1ed4c, 5d1a9ed, 5f2a3ee, a49e166, 709030c, 910d085, 57bb15b, 7d318b5, 0c6a9c3, c5c7b5d, 79debc3, c1bb630, 70744d6, 78e5b23, fd92d5d, caf8ee7, 0bf7baf, b515155, 4115b34, f6e76c2, 7fdc993, 2cc2ee0, d77de7f, dd5c4a1, 367fdda, 7501ad6, 2635956, 675f54c, f252d68, d3dda72, 0e0f85c, 9d288bf, 8ecbb2e, 27f8822, fd71c07, 66e773c, 717bd97, bca66aa, 7b21464, c16edf0, 607a91f, 89110b9, 4b4f7b6, b5de7be, a34c9b5, e08e20b, 5673e78, 91cde4c, a70d6c0, f27340c, 2638eff, 072be5a, 3dbc8be, 7399b83, 412f1d5, deda1a5, 0325ef3, b334442, 485a9aa, 4dc0f77, 66b4dbc, 4860494, 2abbec2, 97cdc27, b77d1c1, ff09f96, 1d561f2, 70411ac, e7c628e, aadb9d4, d31cd2f, 0e63795, df56139, b4d0e1c, 41d2e45, e64b5f4, 63ecfbe, 73ba0b7, 0ff9025, 30049c0, 1a77f88, 788d2d3, e2ddd3b, 60d1fca, cdaf2fa, ab55e29, 4a48443, 8c64733, a0561db, 59b1e16, 8c98b15, 2bd6764, 55c0645, 1e386db, 0fa7cf3, c0a71af, 3736a39, 4dc261f, 8b97462, 73a1acd, aa1cc10)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs` (256-259 enum only; 261-265 absent)
- **Evidence:** No messages, no validation, no graph storage, no pathfinding; `channel_update` in failure messages must be empty. Sub-issues: NL-100, NL-101, NL-102, NL-103, NL-008. Update: 256-259 parse as raw `GossipMessage` and are dropped (156c375, NL-100); 261/263/265 are typed and queries get empty `reply_channel_range`/`reply_short_channel_ids_end` (9e17af2, NL-205). Remaining: announcements, channel_update, graph. Update (ABCD wave 0, `0b7e617`): `channel_update` (258) is typed (`ChannelUpdateMessage`/`ChannelUpdatePayload`, unknown trailing fields kept for the signature) and signed/verified with the node key (`ILightningSigner.SignNodeMessage`/`VerifyNodeMessage`); an LND-captured update parses byte-exact and verifies; `FailureChannelUpdateFactory` takes the typed update (e7b5269, 3ba2e4f). Remaining: sending/storing updates (ABCD W1-E), announcements, graph. Update (ABCD wave 1, `342d22e`): direct `channel_update` exchange with the channel peer (W1-E): `Application/Gossip/ChannelUpdateService` sends our signed update once a channel with a scid turns Open (under the channel lock, so it follows channel_ready) and again, unchanged, on every new connection to that peer; option_scid_alias channels use the peer's alias, no update when htlc_minimum exceeds capacity; inbound 258 is kept only if it is for our chain, names a channel with that peer, has the peer's direction, verifies with the peer's node key, is newer, not far in the future and not above capacity (b93dd05, f9c54cc, 7c1ed4c, 5d1a9ed). Docker `ChannelUpdateExchangeTests`: LND `GetChanInfo` shows our fee/CLTV policy and we store alice's. Remaining: announcements, graph, relay; LND never puts us into `addinvoice --private` hints without a node_announcement (NL-255). Update (gossip wave G-A, `164289a`): the library half of G0-G4 landed, nothing is wired into the node yet. G0-T1/T2: 256/257 are typed Domain codecs (`ChannelAnnouncementPayload`, `NodeAnnouncementPayload`; `GossipMessage`/`GossipPayload` deleted) and dropped in `PeerService`; 259 is a typed `AnnouncementSignaturesMessage` on the channel path that gets a channel-scoped "not supported yet" warning until G1-T3 (57bb15b, 0c6a9c3). G0-T3 `IGossipSignatureVerifier`/`GossipSignatureVerifier` (c5c7b5d). G0-T4 address descriptors (NL-008). G0-T5 LND 0.20 and CLN v26.06.8 captures in `Tests.Utils/Vectors/Bolt7Vectors.cs`, byte-exact with every signature verified (7d318b5). G1-T1 storage half (NL-341). G1-T2 `ILightningSigner.SignChannelAnnouncement` with refusals (709030c, 910d085). G2-T1 Domain graph model and `GossipValidator` (78e5b23, caf8ee7). G2-T2 `IFundingOutputLookup` (79debc3, c1bb630). G2-T3 migration `AddGossipGraph` and `IGraphDbRepository` (a49e166). G4-T1 `GraphPathfinder` (fd92d5d, 0bf7baf). Registration of the Bitcoin gossip services and `Gossip` options binding: b515155. Remaining: G1-T1 handlers/IPC, G1-T3..T7, G2-T4..T6, G3, G4-T2..T4, G5. Update (gossip wave G-B, `5bbfbb5`): G1 and G2 are done and proven against LND in Docker. G1 (lane B1): public channels (NL-341, NL-236), `AnnouncementSignaturesMessageHandler` (NL-342), our half sent at depth and after `channel_reestablish` on reconnection with the mainnet gate `CanSendAnnouncementSignatures` (G1-T4), the public `channel_update` (dont_forward clear, real SCID) through `OwnGossipPublisher` (G1-T5), `NodeAnnouncementService` (`Node:Alias`/`Node:Color`, `Gossip:AnnounceAddresses`, timestamp saved before publishing; G1-T6) and `GossipRelayScheduler` (own 256 -> 258 -> 257 to every connected peer of our chain every `Gossip:OwnGossipFlushInterval`, our 257 only after one of our 256s went out on the connection; G1-T7) (4115b34, f6e76c2, 7fdc993, 2cc2ee0, d77de7f, dd5c4a1, 367fdda); in-process proof `Gossip/Announcements/AnnouncementHarnessTests` on `TwoNodeHarness(announceChannel: true)`. G2 (lane B2): `GossipIngress` (bounded queues, duplicate filter, validator, signature verifier, funding lookup at 6 confirmations, orphan replay, B7-CA-04 ban) and write-behind `GraphStore` (7501ad6, 9d288bf); `PeerService` sends 256/257/258 to the ingress and `gossip_timestamp_filter(0, 0xFFFFFFFF)` after our init to `gossip_queries` peers (2635956, 675f54c); `GraphPruner` from the chain monitor's `OnBlockInputs` (spent +72, stale after `DeleteStaleAfter`, lonely nodes, reorgs, funding blocks reorged out; f252d68, 0e0f85c); IPC `listnodes` (17) / `listgraphchannels` (18) (d3dda72); `Gossip:Enabled` unset means on everywhere but mainnet (D12); `GossipGraphHostedService` starts the graph. Lane B3: Docker `Docker/Gossip/` Proofs G0, G1 (a)-(c), G2 (a)-(c) (8ecbb2e, 27f8822, fd71c07; integrated by 66e773c, 717bd97), 16/16 green. New follow-ups: NL-348..NL-356. Remaining: G3 (gossip_queries sync, relay of others' gossip, re-query of dropped SCIDs NL-353), G4-T2..T4 (pathfinding in payments), G5, Proofs G1 (d) (NL-255) and G2 (d) (NL-356). Update (gossip wave G-C, `4dc0f77`): G3 and G4 are done and the BOLT 7 goal proofs are green against LND 0.20 and CLN v26.06.8. G3 (lane C1): strict query codec, CRC32C checksums and the timestamp filter (7b21464); `QueryResponder` answers from the graph and `GossipSyncManager` syncs by range query → SCID batches → `gossip_timestamp_filter`, re-querying dropped SCIDs (c16edf0; NL-205, NL-353); relay of other nodes' gossip with per-peer filters, staggered flushes, origin suppression and a paced backlog, through the outbox port (607a91f; NL-351); `gossip_queries_ex` checked against CLN and advertised Optional (89110b9); review fixes (4b4f7b6). G4 (lane C2): `MissionControl`, graph paths in `PaymentRoutePlanner` (a third candidate source after direct and hint paths, MPP over all three), shadow CLTV on graph routes, G3-T5 refresh on UPDATE failures (a34c9b5, f27340c, 2638eff), invoice hint policy (e08e20b; NL-245), `getroute` = `ClientCommand` 19 (91cde4c), in-process `GraphPaymentHarnessTests` (5673e78). NL-348, NL-349, NL-350, NL-352, NL-354, NL-355 fixed by lane M3; the integrator bound `GossipSyncScidRefresher` and `PeerManager` as `IPeerGossipOutbox` (b5de7be). Docker (lane C3 and integration): goal proofs (b) we pay carol's hint-free invoice, (c) carol pays ours, (d) `getroute` equals LND's `QueryRoutes` and the paid fee, G3 (a)-(c) sync by queries, re-sync after a restart, relay without echo (`PublicPaymentFlowTests`, `GossipSyncFlowTests`), G1 (d) (NL-255), G2 (d) (NL-356), and CLN Proof G3 (d) plus goal proof (e) (`ClnGossipTests`) (072be5a, 3dbc8be, 7399b83, 412f1d5, deda1a5, 0325ef3, b334442, 485a9aa, 4dc0f77); gossip 24/24, CLN 22/22. Not written: Docker Proof G4 (b) and (c) (NL-367). New follow-ups: NL-357..NL-369. Remaining: G5 (limits, spam, performance, `describegraph`, metrics, the mainnet gate D12: sync and relay stay off on mainnet by default). Update (gossip wave G-D, `48a8951`): G5-T2, G5-T3 and G5-T4 are done, G5-T1 and G5-T5 partial. D1 (97cdc27, b77d1c1, ff09f96): `GossipRateLimiter` (channel_update burst 4 then 1 per 60 s per (SCID, direction), node_announcement 1 per 10 min, keep-alive only when >24 h newer; the newest refused validly signed message per key kept and replayed), `GossipMisbehaviourTracker` (invalid signatures, bad encodings and funding mismatches: 5 in 10 min give a warning, a disconnect and a 1 h ban, persisted in `GraphBannedNodes` only for graph nodes, in-memory bans capped at 10,000), `MaxChannels` 200,000 / `MaxNodes` 100,000 enforced before the signature check, future-timestamp drop, relay backlog bound `Gossip:MaxRelayPendingPerPeer` (5,000, evicted by channel group; NL-360 partial), and `Meter("NLightning.Gossip")` (`GossipMetrics`: received/accepted/rejected/orphaned/dropped/relayed/chain lookups/bans, sync duration, queue depth gauge, snake_case tags); proof `GossipFloodTests` (400-message fuzz flood: flooder banned and disconnected once, the honest peer's 30 channels all applied). D2 (66b4dbc, 4860494, 2abbec2): batched write-behind (5,000 rows per save), streamed bulk load, O(1) `IGraphStore.GetMemoryEstimate()`, 200k channels on SQLite load in 1.55 s (target 10 s), store 475 MiB + 39 MiB per snapshot (NL-373); no `AddGossipIndexes` migration needed; `describegraph` = `ClientCommand` 20. D3 (1d561f2, 70411ac, e7c628e, aadb9d4): the template writes the D12 gossip gate per network (off on mainnet), the Docker runner scripts and per-process test ports (NL-358, NL-359), `scripts/mutinynet/soak-gossip.sh` and the first 20 min of the Mutinynet soak in `MUTINYNET.md` (895 channels / 194 nodes synced in under 5 min, 3.2 RPC/min steady, 0 errors). Integration d31cd2f: `GraphStore` records load/flush durations and its write-behind depth in the meter. Docker gossip 3 x 24/24 (Proof G5 regression). Remaining: G5-T1 interned node ids and `Gossip:MaxMemoryMb` enforcement (NL-373), G5-T5 the 24 h soak evaluation (NL-376) before D12 opens; follow-ups NL-370..NL-377, NL-345, NL-346, NL-357, NL-360..NL-369. Update (mainnet gossip probe, integrated on `wip/fafo` at the docs commit after `bc3a2c3`): `tools/NLightning.GossipProbe` ran the node's gossip stack against five mainnet peers (Eclair, two LND, two CLN) and the whole mainnet graph, first with `Gossip:AssumeChannelValid` (new, 0e63795: signed announcements stored `Assumed` without the funding lookup, never relayed or served; refused on mainnet while HTLCs are enabled or public channels are allowed) and then verified against an unpruned mainnet bitcoind (`--chain rpc`, 73ba0b7, 30049c0): about 40,000 channels in 2 min assumed and in about 18 min verified (270,000 read-only RPCs, 0 script/amount/index mismatches), 115 MB live heap, RSS 400 MB (630 MB peak verified), graph reload 0.55 s. Fixed on the way: NL-400..NL-405, NL-410..NL-412; open: NL-406..NL-408, NL-413..NL-416. D12 stays closed; `docs/agents/MAINNET_GOSSIP_PROBE.md` lists what remains. Update (wave d12, `wip/fafo` at `aa1cc10`): D12 decided. Graph and gossip sync are on by default on every network, mainnet included; relay of other nodes' gossip stays off on mainnet (unexercised, NL-417); `AllowPublicChannelsOnMainnet` stays false (73a1acd, aa1cc10). Lanes: Z2 memory budget (NL-373: 60d1fca, cdaf2fa), Z1 pending announcements and partitioned ingress (NL-406, NL-408: ab55e29, 4a48443), Z3 verified-sync efficiency (NL-413, NL-414, NL-415, NL-360 partial: 8c64733, a0561db, 59b1e16, acb7e4d, 55c0645, 69dcecf, 8c98b15, 2bd6764, 1e386db), Z4 Esplora funding-txid source for pruned nodes (NL-346 partial: 0fa7cf3, c0a71af); integration 3736a39 (FundingTxIdSourceOptions bound, budget at promotion, pending count at IPC key 28), probe relay run 4dc261f, 8b97462. d12 verified mainnet run (30 min, fresh database, five peers): 99 % of the graph in 13 min (was 19), 610 MB RSS peak, 179,210 RPCs (5.8 per lookup), download 2.3x (was 3.6x), 0 disconnections, bans or warnings. New follow-ups NL-417..NL-425. Remaining: NL-360, NL-376 (soaks), NL-407, NL-416, NL-417..NL-425. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): closed: the epic's scope (announcements, channel_update, queries, graph) is implemented and proven: BOLT7_GOSSIP_PLAN G0-G4 done, G5-T1..T4 done (NL-373 fixed), G5-T5 done for sync only: its proof, the 24 h Mutinynet/signet soak that was to gate mainnet sync (`BOLT7_GOSSIP_PLAN.md` G5-T5 row), is unmet although D12 opened mainnet sync without it, and is carried by NL-376 (raised to medium); D12 decided (graph and sync on by default everywhere, relay of others' gossip off on mainnet), Docker gossip 28/28 and CLN gossip green at wave B12. Remaining work is follow-ups, each its own entry: the unmet 24 h soak proof of G5-T5 (NL-376, medium), the outbox cap turned on (NL-360, medium), mainnet relay (NL-417), NL-345..NL-347, NL-357, NL-361..NL-372 (NL-369 is the value-object conversion trap the gossip code hit), NL-374, NL-375, NL-377, NL-378, NL-382, NL-394, NL-407, NL-416, NL-418..NL-425. The root `CLAUDE.md` still lists "BOLT 7 announcements/graph" under Not implemented (stale, NL-458).
- **Fix sketch:** Wire types first (so they stop killing peers), then announcement_signatures for public channels, graph store, gossip_queries.
- **Blocks/Blocked-by:** Blocks multi-hop sending in NL-073
- **Plan ref:** `docs/agents/BOLT7_GOSSIP_PLAN.md` (milestones G0-G5, waves G-A..G-D); BOLT_COVERAGE roadmap steps 2, 12

### NL-100 Incoming even gossip types (256, 258, 262, 264) throw and kill the peer
- **Status:** fixed (156c375)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs:51,74`
- **Evidence:** Unregistered even type → `InvalidMessageException`. `gossip_queries` is advertised, so real peers send these.
- **Fix sketch:** Register message classes + serializers (both factory dictionaries) and route to a no-op/logging handler; keep gossip_queries advertised meanwhile.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T5. Note (gossip wave G-A): the raw `GossipMessage`/`GossipPayload` types were replaced by typed 256/257/259 messages (57bb15b, NL-099).

### NL-101 ShortChannelId(ulong) uses wrong masks
- **Status:** fixed (83d529d)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs:54-58`
- **Evidence:** tx index `& 0xFFFF` (should be `0xFFFFFF`), output `& 0xFF` (should be `0xFFFF`). Onion TLVs use the byte[] ctor, which is correct.
- **Fix sketch:** Fix masks; add round-trip tests for max values.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M4 prerequisites

### NL-102 SCID transaction index counts only watched txs (and is ushort)
- **Status:** fixed (7df8f2d)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs:473-499`, `src/NLightning.Domain/Bitcoin/Transactions/Models/WatchedTransactionModel.cs:22`, `WatchedTransactionDbRepository.cs:67`
- **Evidence:** `index++` sits in `finally`, reached only after the `continue` for unwatched txs; `ushort` but BOLT 7 index is 24-bit. Every derived SCID (`ChannelManager.cs` ~317-320) is wrong.
- **Fix sketch:** Count every tx, widen to `uint` end to end incl. the persisted `TransactionIndex` column (3 migrations).
- **Blocks/Blocked-by:** Blocks NL-073, NL-099
- **Plan ref:** ONION M4 prerequisites

### NL-103 scid_alias values are random with no uniqueness check
- **Status:** fixed (1cc0488, 9e0915d, 1a38360)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs`, `ChannelModel.LocalAliases`
- **Evidence:** Collisions across channels are not detected. Aliases are checked against known SCIDs/aliases (incl. persisted ones), reused on re-confirmation and persisted (`ChannelLocalAliases`, `Channels.RemoteAlias`; NL-209).
- **Fix sketch:** Check against existing aliases/SCIDs before use.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-205 gossip_queries negotiated but queries were never answered
- **Status:** fixed (9e17af2, c16edf0)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Node/Services/GossipQueryResponder.cs`, `PeerService.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 3. After NL-110/111, gossip_queries (default Optional) was really negotiated with LND, but query_channel_range / query_short_channel_ids were dropped, while BOLT 7 says the receiver MUST reply. Now: one `reply_channel_range` (sync_complete=1, no ids) and `reply_short_channel_ids_end` with full_information=0; bad queries get a warning; gossip_timestamp_filter is ignored. Update (gossip wave G-C, `4dc0f77`): queries are answered from the graph by `Application/Gossip/Sync/QueryResponder` (real `reply_channel_range` chunks and `query_short_channel_ids` answers with `query_flags`, `full_information` = 1 after our initial sync; `gossip_queries_ex` timestamps and checksums), and `GossipSyncManager` syncs our graph by queries (c16edf0, 89110b9, 4b4f7b6); `GossipQueryResponder` is gone and `PeerService` only answers empty without a sync service (in-process tests).
- **Fix sketch:** Done; real gossip is NL-099.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** —

### NL-209 scid aliases were not persisted and were regenerated after a restart
- **Status:** fixed (1a38360)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelLocalAliasEntity.cs`, `ChannelEntity.RemoteAlias`, `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`, `FundingConfirmedMessageHandler.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 7. The collision set started empty after restart and re-confirmation generated fresh aliases, breaking the BOLT 2 channel_ready alias MUSTs. Now `ChannelLocalAliases` (PK = alias) and `Channels.RemoteAlias` (migration `AddChannelScidAliases`, 3 providers); persisted aliases are reused and avoided.
- **Fix sketch:** Done; the real SCID is NL-225.
- **Blocks/Blocked-by:** Part of NL-103
- **Plan ref:** —

### NL-255 LND never adds route hints through us without a node_announcement
- **Status:** fixed (3dbc8be)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/ChannelUpdateService.cs`
- **Evidence:** LND stores our direct `channel_update` (W1-E, b93dd05) but `addinvoice --private` still never hints through an NLightning node, because LND requires the hint node to be public (known through a node_announcement). The ABCD test uses explicit `route_hints` (roadmap decision B), so this does not block it (reported by W1-E). Update (gossip wave G-B, `5bbfbb5`): our `node_announcement` is now signed and relayed once one of our channels is announced (`NodeAnnouncementService`, `GossipRelayScheduler`, 2cc2ee0, dd5c4a1), and Docker Proof G1 (a) shows bob's `GetNodeInfo` with our alias and color. Whether LND now hints through us (Proof G1 (d)) is not verified, so this stays open. Update (gossip wave G-C, `4dc0f77`): Docker Proof G1 (d) `PublicChannelFlowTests.Given_OurNodeIsPublic_When_DavidInvoicesOverOurPrivateChannel_Then_HisInvoiceHintsThroughUs`: once our node is public (announced channel to alice), david's `addinvoice --private` over his private channel to us carries a one-hop hint from our node (3dbc8be).
- **Fix sketch:** Send a node_announcement (needs at least one announced channel: announcement_signatures, NL-236).
- **Blocks/Blocked-by:** Part of NL-099; related NL-236
- **Plan ref:** ABCD W1-E

---

### NL-341 The fundee ignores `announce_channel` in open_channel
- **Status:** fixed (a49e166, 910d085, 4115b34, d77de7f)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs` (no `ChannelFlags` handling), `ChannelModel` (flag not stored)
- **Evidence:** An LND-opened public channel (`channel_flags.announce_channel = 1`) is treated as private; the flag is never persisted, so neither side can later exchange `announcement_signatures` (BOLT 2 / BOLT 7). Update (gossip wave G-A, `164289a`): storage half done: `ChannelConfigs.AnnounceChannel` (existing rows false), `ChannelParams.AnnounceChannel`/`ChannelModel.AnnounceChannel`, and the columns for the peer's announcement signatures and our send time (a49e166); the signer refuses to sign an announcement for a channel without the flag (910d085). Remaining: `OpenChannel1MessageHandler` still ignores `channel_flags`, and nothing sets the flag on our opens (G1-T1 handlers, lane B1). Update (gossip wave G-B, `5bbfbb5`): handler half done by lane B1: the fundee stores `channel_flags.announce_channel` in `ChannelParams.AnnounceChannel`; the initiator sets it with `openchannel --public` (IPC key 4) and leaves option_scid_alias out of a public channel's type; public + zero-conf is refused (4115b34). `OpenChannel1MessageHandler` refuses a public `open_channel` with `error` when `Gossip:AcceptPublicChannels` is false, and on mainnet unless `Gossip:AllowPublicChannelsOnMainnet` (BOLT 7 would oblige our `announcement_signatures`; d77de7f). Docker Proof G1 (a)/(b) green.
- **Fix sketch:** Store the flag per channel (migration, all 3 providers), honour it on both roles, refuse `announce_channel` together with `option_scid_alias` per BOLT 2.
- **Blocks/Blocked-by:** Blocks BOLT7 G1
- **Plan ref:** BOLT7_GOSSIP_PLAN G1-T1 (GG3)

### NL-342 `announcement_signatures` (259) is dropped as peer gossip instead of routed to its channel
- **Status:** fixed (57bb15b, 0c6a9c3, f6e76c2, 367fdda)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (gossip branch), `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (no case)
- **Evidence:** 259 carries a `channel_id` and belongs to the channel, but it parses as raw `GossipMessage` and is dropped, so public channels can never be announced. Update (gossip wave G-A, `164289a`): typed and routed. `AnnouncementSignaturesMessage` is an `IChannelMessage` and reaches `ChannelManager` (57bb15b); until the G1-T3 handler exists a 259 on a known channel gets a channel-scoped "announcement_signatures is not supported yet" warning and the connection stays up (LND and CLN resend 259 on every reconnect of a public channel, so the warning repeats), an unknown channel gets an error, a Failed channel its stored error (tests 0c6a9c3). Remaining: the handler and the `ChannelManager` case (G1-T3). Update (gossip wave G-B, `5bbfbb5`): `AnnouncementSignaturesMessageHandler` and the `ChannelManager` 259 case replace the interim warning (f6e76c2): unknown channel ignored, another peer's channel error, shutdown/closing ignored, private channel warning, SCID mismatch warning, bad node/bitcoin signature warning + close (D10); a valid half is stored and ours replied once per connection, and with both halves at depth the `channel_announcement` is assembled (all 4 signatures re-verified). A stored peer half that does not sign the current announcement is forgotten and the discard persisted (`CompleteAnnouncementAsync(channel, uow)`), so the channel is no longer treated as announced and our half is re-sent on the next connection (367fdda). Block-driven and reconnect sending of our half: G1-T4 (2cc2ee0). Docker Proof G1 (a)-(c) green.
- **Fix sketch:** Typed message + `ChannelManager` case; invalid signatures -> warning + close (BOLT7 plan D10).
- **Blocks/Blocked-by:** Blocks BOLT7 G1
- **Plan ref:** BOLT7_GOSSIP_PLAN G0/G1 (GG2)

### NL-345 Gossip signature tests for 256/257 only check self-signed messages
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Bitcoin.Tests/Gossip/GossipSignatureVerifierTests.cs`, `src/NLightning.Infrastructure.Bitcoin/Gossip/GossipSignedRanges.cs`
- **Evidence:** The `GossipSignedRanges` 256/257 tests sign their own messages, so a wrong signed range would pass (circular). The captured LND/CLN vectors (`Tests.Utils/Vectors/Bolt7Vectors.cs`, 7d318b5) landed in another lane of the same wave and are not used there; `Bolt7CapturedVectorTests` checks them through the typed payloads only. The 259 captures cannot be verified without the completed announcement.
- **Fix sketch:** Run every captured 256/257 through `GossipSignedRanges` + `GossipSignatureVerifier`; add a 259 vector from our own two-node exchange once G1-T3/T4 land.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G0-T3, G0-T5

### NL-346 Funding output lookup: rate limit counts lookups, not RPCs; pruned path unproven live
- **Status:** open (partial: 8c64733, 0fa7cf3, c0a71af)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Gossip/FundingOutputLookup.cs`, `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs`
- **Evidence:** One lookup costs 3-5 RPCs (getblockcount, getblockhash, getblock on a miss, gettxout, the post-gettxout getblockhash recheck, a second gettxout for a mempool-only spend), so `ChainLookupsPerSecond=50` can mean about 250 RPC/s. `GetUnspentOutputAsync` derives the height from two RPCs and can be off by one when the tip moves (absorbed by one retry, then `ChainMoved`). The `getblock` pruned (-1 "pruned") branch runs only against the fake chain, never a `-prune` regtest bitcoind. Update (wave d12): the height race is fixed (NL-413, 8c64733). Lane Z4 added an opt-in pruned-node path, `Gossip:FundingTxIdSource=Esplora` + `EsploraUrl` (`Gossip/EsploraTxIdSource`): the txid at the scid position from an Esplora index, proven by its merkle proof against our node's `getblockheader` (merkleroot, nTx; nTx <= 0 is refused as transient, CVE-2012-2459 padding refused), the output still from our `gettxout`; rate limited (2 req/s), 429 back-off with `EsploraMaxInlineWait` 5 s, LRU keyed by block hash; a bitcoind that cannot serve a block logs a one-time hint (0fa7cf3, c0a71af; bound by the integrator in `NodeServiceExtensions`, 3736a39). Still open: the per-RPC rate limit, and a live `-prune` run (NL-422).
- **Fix sketch:** Rate-limit per RPC (G5 tuning); add a pruned-node case to the Explicit `FundingOutputLookupBitcoindTests`.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G2-T2, G5-T1

### NL-349 No disabled channel_update when the peer of an announced channel stays offline
- **Status:** fixed (833e8e6, 80fd35f)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Services/ChannelUpdateService.cs`
- **Evidence:** BOLT 7 plan G1-T5 policy: once the peer of an announced channel has been offline longer than `Gossip:DisableAfter` (20 min, like LND/CLN) we should publish a `channel_update` with the disable bit and re-enable it when the peer is back. Only ShuttingDown/Negotiating/Closing/Failed send a disabled update today, so payers keep routing through a channel whose peer is gone (reported by lane B1, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): `ChannelUpdateService` publishes a disabled `channel_update` (relay only) once the peer of an announced channel has been away longer than `Gossip:DisableAfter` (20 min), checked every minute at most (833e8e6); the disable is re-checked under the channel lock (same offline start, link still down), and the channel is re-enabled only when a check finds the link up after `channel_reestablish` (a reconnect alone resends the disabled update as it is) (80fd35f, from the review). Left: the offline time is kept in memory (a restart counts again from the first check) and the re-enable waits up to one check interval after the link is up (NL-364).
- **Fix sketch:** Track peer offline time for announced channels; publish a disabled update after `DisableAfter` and a newer enabled one after `channel_reestablish`.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G1-T5

### NL-350 A reorg that moves an announced channel's SCID does not reset its announcement state
- **Status:** fixed (7a4ef7f)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Onchain/Reorg/FundingReconfirmationHandler.cs`, `ChannelModel.ResetAnnouncementSignatures`
- **Evidence:** After a reorg moves the SCID, nobody calls `ChannelModel.ResetAnnouncementSignatures` and a source-less signer is not re-registered. The peer's old half stays stored, so `ChannelUpdateService.IsPublic` stays true and the public `channel_update` names the new SCID before any announcement for it exists, and the reconnect path does not re-send our half because both halves count as exchanged. Partly mitigated by 367fdda: `TryAssembleAnnouncement` re-verifies, never publishes a bad 256, and forgets a stored half that does not sign the current announcement when it is next tried (reported by lane B1, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): `FundingReconfirmationHandler` resets both announcement halves and our sent time in the reorg save when the SCID moves, notifies the announcement service and re-registers a source-less signer; the post-move `channel_update` is private until the new announcement completes (7a4ef7f). Our old 256/258 stay in our own graph under the old SCID until the pruner drops them (NL-362).
- **Fix sketch:** Reset the announcement signatures (and our sent time) in the reconfirmation's save, re-register a source-less signer, and send a new half once the new funding block is 6 deep.
- **Blocks/Blocked-by:** Part of NL-099; related NL-292, NL-329, NL-343
- **Plan ref:** BOLT7 G1-T4

### NL-351 Our own gossip bypasses the peer's PeerOutbox
- **Status:** fixed (607a91f, 4b4f7b6, b5de7be)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Relay/GossipRelayScheduler.cs` (`IPeerService.SendGossipMessageAsync`)
- **Evidence:** Our 256/258/257 are sent directly through `IPeerService.SendGossipMessageAsync`, not `PeerOutbox`, because `PeerManager` was not owned by lane B1. Transport writes are serialized, but our gossip is not FIFO with the channel messages queued in the outbox (harmless: gossip vs. channel messages). The public `channel_update` also goes out before the replies raised in the same handler (e.g. the 259 reply) (reported by lane B1, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): own and relayed gossip go through `IGossipPeerSender` → `PeerGossipSender` → the Domain port `IPeerGossipOutbox` (607a91f); relay sends are isolated per connection so a stalled peer no longer blocks the others (4b4f7b6); `PeerManager` implements the port (the current connection only; a replaced one gets false) and `AddApplicationServices` registers it, so gossip is FIFO with the peer's `PeerOutbox` (b5de7be). The sync manager's own queries, replies and `gossip_timestamp_filter` still use the direct send (NL-361).
- **Fix sketch:** Move own and relayed gossip onto `PeerOutbox.TryEnqueueGossip` when G3-T3 builds the relay (plan §3.7).
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3-T3, §3.7

### NL-352 Graph funding txids are not persisted, so the pruner looks every channel up again after a restart
- **Status:** fixed (016a523)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GraphPruner.cs`, `GraphChannels` table (no `FundingTxId` column)
- **Evidence:** `GraphStore` keeps funding txids in memory only; after every restart `GraphPruner` calls `IFundingOutputLookup.LookupAsync` once per channel to find them again (rate-limited; fine on regtest/signet, about 50k lookups on mainnet, where the graph is off by default per D12) (reported by lane B2, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): migration `AddGraphFundingTxId` (all 3 providers; nullable `GraphChannels.FundingTxId`), `GraphStore` saves and loads the txids, and after a restart the pruner looks up only rows without one (016a523; SQLite/Postgres seeded upgrade, SQLite restart test, `GraphStoreTests`).
- **Fix sketch:** Migration-owner lane: add the column (all 3 providers), filled by the ingress and the own sink; the pruner then looks up only rows without one.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G2-T3, G2-T5

### NL-353 Gossip dropped by full ingress queues is never requested again
- **Status:** fixed (c16edf0, 4b4f7b6)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs` (queues 2,000 per peer, 20,000 total; `MaxRetries`)
- **Evidence:** `PeerService` gets gossip from the transport's synchronous `MessageReceived` event, so the ingress cannot apply back-pressure: a large bootstrap `gossip_timestamp_filter` dump can overflow the queues, and transient chain answers can exhaust `MaxRetries`, losing those messages for good. Mitigated in 9d288bf: dropped or given-up 256/258 SCIDs are recorded (bounded by `MaxMissedShortChannelIds`, 100,000) for `TakeMissedShortChannelIds()`, and `DroppedCount` is logged; nothing consumes them yet (reported by lane B2 review F3, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): `GossipSyncManager` queries the dropped or given-up SCIDs again from a sync peer every `MissedScidRetryInterval`, keeping a backlog while no peer is connected (c16edf0); query batches are sized to the ingress queue and wait for it to drain (range sync, retries, `QueryScidAsync`), and a timestamp sync no longer asks for the two-week backlog again (4b4f7b6). A signet-sized sync is not measured yet; that is G5-T1.
- **Fix sketch:** G3: send `query_short_channel_ids` for the missed SCIDs, and pace the peer (timestamp-filter windows or range queries) instead of drop-on-full; measure a signet dump (B3).
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3

### NL-354 GraphPolicy equality compares its byte fields by reference
- **Status:** fixed (12d3e74)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Gossip/Graph/GraphPolicy.cs` (record with `ReadOnlyMemory<byte>` `RawUpdate`/`ExtraData`)
- **Evidence:** `GraphPolicy` uses the compiler's record equality, which compares `ReadOnlyMemory<byte>` by reference, so a policy reloaded from the database never equals the same policy before a restart (`GraphChannel.Equals` also ignores `RawAnnouncement`). Tests compare by content (`GraphTestKit.AssertSameGraph`) (reported by lane B2, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): `GraphPolicy` compares every field and `ExtraData` by content; `RawUpdate` is left out, like the raw announcements of `GraphNode`/`GraphChannel` (12d3e74, `GraphModelTests`).
- **Fix sketch:** Give `GraphPolicy` a content `Equals`/`GetHashCode` like `GraphNode` and `GraphChannel`, and decide whether raw bytes take part.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT7 G2-T1

### NL-360 Relayed gossip backlog is held in the unbounded PeerOutbox
- **Status:** open (partial: 97cdc27, ff09f96, 55c0645, 1e386db)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Node/Services/PeerOutbox.cs` (`Channel.CreateUnbounded`), `src/NLightning.Application/Gossip/Relay/GossipRelayScheduler.Relay.cs` (backlog)
- **Evidence:** Since NL-351 (b5de7be) relayed gossip, including the backlog of the whole graph sent after a new `gossip_timestamp_filter`, is queued with `TryEnqueueGossip` in the peer's unbounded outbox, paced only by `Gossip:BacklogMessagesPerSecond` (1,000). Before, the awaited transport write bounded it; now a peer that reads slowly makes our memory grow with the graph size per connection (reported by the G-C integrator). Update (gossip wave G-D, lane D1): the relay's own pending set per peer is bounded by `Gossip:MaxRelayPendingPerPeer` (5,000; oldest evicted first, a 256 together with its waiting 258s and re-sent from the graph before a later 258; counted as `relay_backlog_full`) (97cdc27, ff09f96). Still open: `PeerOutbox` has no depth API (`IPeerGossipOutbox.TryEnqueueGossip` never refuses), so what is already queued on a slow peer's outbox and the paced backlog after a new `gossip_timestamp_filter` are not bounded; that needs a queued count or a bounded enqueue in `PeerManager`/`PeerOutbox` (hub files) and a `Gossip:MaxOutboundQueue` check in the relay. Update (wave d12, lane Z3): `PeerOutbox` counts its gossip (`QueuedGossipCount`/`DroppedGossipCount`) and `TryEnqueueGossip(message, capped)` refuses capped gossip at `Gossip:MaxOutboxGossipPerPeer` (metric `dropped{reason=outbox_full}`, gauge `outbox_gossip`; channel messages, warnings, errors and our own channel_update never refused) (55c0645), but the cap ships **0 (off)** (1e386db) because the relay reads a refusal as ConnectionGone and drops that peer's flush and backlog. Remaining: `IPeerGossipOutbox` must return Full distinct from Gone, `GossipRelayScheduler.SendBacklogAsync`/`FlushPeerAsync` must keep the backlog and pending items on Full, then turn the cap on (10,000).
- **Fix sketch:** Cap the gossip share of each outbox (count or bytes) and let the relay skip or pause a connection whose outbox is over the cap; measure in G5-T1.
- **Blocks/Blocked-by:** Part of NL-099; related NL-351
- **Plan ref:** BOLT7 G5-T1, §3.8

### NL-361 Gossip sync queries, replies and timestamp filters bypass the PeerOutbox
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs` (`session.Peer.SendGossipMessageAsync`)
- **Evidence:** Own and relayed gossip use the outbox port since NL-351, but the sync manager still awaits `IPeerService.SendGossipMessageAsync` for its queries, the query replies and `gossip_timestamp_filter`, so they are not FIFO with the peer's outbox (reported by lane C1).
- **Fix sketch:** Send them through `IGossipPeerSender` like the relay.
- **Blocks/Blocked-by:** Part of NL-099; related NL-351
- **Plan ref:** BOLT7 G3-T2, §3.7

### NL-362 After a reorg moves an announced SCID, our old announcement stays in our graph
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Onchain/Reorg/FundingReconfirmationHandler.cs`, `src/NLightning.Application/Gossip/Graph/GraphStore.cs`
- **Evidence:** NL-350 (7a4ef7f) resets the announcement state, but our old `channel_announcement` and policies stay in our own `GraphStore` under the old SCID until the pruner drops them (the old SCID's funding lookup fails, or the stale rule); the graph sink has no "forget own channel" call (reported by lane M3).
- **Fix sketch:** Add a forget call to `IOwnGossipSink`/`IGraphStore` and use it in the reconfirmation save.
- **Blocks/Blocked-by:** Part of NL-099; related NL-350
- **Plan ref:** BOLT7 G1-T4, G2-T5

### NL-363 Gossip sync peers are chosen first come, first served
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs`
- **Evidence:** Plan §3.7 prefers channel peers as sync peers; the manager takes the first connected `gossip_queries` peers (reported by lane C1).
- **Fix sketch:** Prefer peers we have channels with, then rotate as planned.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3-T2, §3.7

### NL-364 Offline disable of announced channels: offline time in memory, re-enable waits for a check
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Services/ChannelUpdateService.cs` (`_offlineSince`, `GetOfflineCheckInterval`)
- **Evidence:** After NL-349 (833e8e6, 80fd35f) the time a peer has been away lives in memory, so a restart counts it again from the first check; after a reconnect the enabled update waits for the next offline check that finds the link up (at most 1 min), since there is no `MarkLinkUp` hook. Not checked against LND/CLN in Docker (reported by lane M3).
- **Fix sketch:** Re-enable from the link-up hook; optionally persist the offline start.
- **Blocks/Blocked-by:** Part of NL-099; related NL-349
- **Plan ref:** BOLT7 G1-T5

### NL-365 A gossip query timeout or bad reply ends querying on that connection
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs` (`ExpectReplies(null)` after a timeout)
- **Evidence:** Review fix 4b4f7b6 keeps BOLT 7's one-outstanding-query rule by never querying a connection again after a reply timeout (`SyncReplyTimeout`, 2 min per reply) or a broken reply; only a reconnect restores it. A rate-limited LND answering a large graph slowly can therefore stop being a sync peer for the rest of the connection (reported by lane C1 review).
- **Fix sketch:** Keep waiting for the outstanding end marker before sending the next query instead of dropping the peer; measure against a signet-sized graph in G5.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3-T2

### NL-366 The relay diffs the whole graph snapshot every collect interval
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Relay/GossipRelayScheduler.Relay.cs` (`Collect`, `Gossip:RelayCollectInterval` 10 s)
- **Evidence:** New gossip for the relay is found by comparing the whole snapshot with the versions seen before every 10 s, O(graph) per collect, and the snapshot is rebuilt after each change. Fine on regtest/signet; unmeasured at mainnet scale, where the relay is off by default (D12) (reported by lane C1).
- **Fix sketch:** Feed the relay from the ingress's accepted messages, or measure and tune in G5-T1.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T1

### NL-368 The relay can still send a channel_update without its channel_announcement on one connection
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Relay/GossipRelayScheduler.Relay.cs`
- **Evidence:** Review fix 4b4f7b6 keeps a 256 unseen until its channel has a relayable update, but a 256 whose only update falls outside one peer's `gossip_timestamp_filter` at its first flush is dropped for that connection and a later in-filter 258 can go out alone; the peer then ignores the 258 (reported by lane C1 review).
- **Fix sketch:** Track per connection which 256s went out and queue the 256 with any later 258 for that SCID.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3-T3

### NL-370 The misbehaviour peer ban is kept in memory only
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipMisbehaviourTracker.cs`, `GossipIngress.cs`
- **Evidence:** A peer banned for misbehaviour has all its gossip dropped at the door (in-memory peer ban); only the node ban (its own gossip ignored) is persisted, and only for graph nodes (ff09f96). After a restart within the hour, the peer's relayed gossip about other nodes is accepted again until it reaches the threshold again (5 offences) (reported by lane D1, gossip wave G-D).
- **Fix sketch:** Persist the peer ban with its end time (or rebuild it from `GraphBannedNodes` at start) if the soak shows repeat offenders.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T2

### NL-371 Funding mismatches count toward the gossip misbehaviour ban
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs` (`ScoreMisbehaviour`)
- **Evidence:** As plan §3.8 says, `ScriptMismatch`, `AmountMismatch` and `TransactionIndexOutOfRange` count toward the 5-in-10-min ban. An honest peer that does not check funding outputs itself (e.g. an LND neutrino or `assumechanvalid` node relaying unchecked announcements) could be banned for 1 h and disconnected once; its channels are unaffected (reported by lane D1, gossip wave G-D).
- **Fix sketch:** After the G5-T5 soak, count only invalid signatures and bad encodings, or raise the threshold for chain mismatches.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T2, §3.8

### NL-372 Expired gossip bans are never pruned from GraphStore or GraphBannedNodes
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GraphStore.cs` (`_bans`), `src/NLightning.Domain/Gossip/Interfaces/IGraphDbRepository.cs`
- **Evidence:** Expired bans leave memory only on restart (the load reads `GetActiveBansAsync`) and their rows stay in the table for good. Growth is bounded by `MaxNodes` since the ingress persists only graph-node bans (ff09f96) (reported by lane D1 review, gossip wave G-D).
- **Fix sketch:** Prune expired bans in the store's flush and delete their rows in the batched write-behind.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T2, G5-T3

### NL-373 Graph store memory: node ids not interned and Gossip:MaxMemoryMb not enforced
- **Status:** fixed (60d1fca, cdaf2fa, 3736a39)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GraphStore.cs`, `GraphMemoryAccounting.cs`, `GossipGraphOptions`
- **Evidence:** Measured by lane D2 (66b4dbc): 200k channels hold 475 MiB of managed heap in the store (about 2.5 KB per channel, per-channel copies of node ids and bitcoin keys and four dictionaries per channel) plus 39 MiB per snapshot; the store, one snapshot and a second one during a rebuild come to about 553 MiB at the `MaxChannels` cap, above the plan's `Gossip:MaxMemoryMb` default of 512 (lane D2 proposes 768). `IGraphStore.GetMemoryEstimate()` exists but nothing enforces a memory budget; only `MaxChannels`/`MaxNodes` are enforced (D1). A mainnet-sized graph (about 50k channels) estimates at about 130 MiB (reported by lanes D1 and D2, gossip wave G-D). Update (mainnet gossip probe, `docs/agents/MAINNET_GOSSIP_PROBE.md`): the mainnet graph (about 40,000 channels) holds 115 MB of live heap (`dotnet-gcdump`; the store estimate 99 MB, G-D's 2.5 KB per channel holds) but 200-230 MB of GC heap and 400 MB RSS steady; a verified sync peaks at 425 MB GC heap and 630 MB RSS from `getblock` JSON (NL-416). The budget should cover the GC heap, not only the store; the relay's origin tracker (NL-405, fixed) no longer holds 15 MB while the relay is off. Update (wave d12, lane Z2): `Gossip:MaxMemoryMb` (default 1,024 MiB on every network, 0 = off; `MemoryResumePercent` 90, `MemorySampleInterval` 1 s) is enforced by `Gossip/Graph/GossipMemoryBudget` against the process RSS (`Process.WorkingSet64`; a zero or failed reading keeps the last decision): over it `GossipIngress` refuses **new** channels and nodes (`Limited`, reason `memory_budget`, right after the `MaxChannels`/`MaxNodes` checks, before signatures and the chain lookup), known ones keep updating, our own gossip is always applied, and new entries resume below 90 %; metrics `nlightning.gossip.memory.budget.exceeded` and gauge `...memory.working_set`; `describegraph` IPC keys 23-27 (60d1fca, cdaf2fa). The integrator also holds the budget when a pending announcement (NL-406) is promoted (3736a39). The d12 verified mainnet run peaked at 610 MB RSS, never over the budget, 0 refusals. Interning node ids and bitcoin keys and the 200k synthetic load under the budget were dropped by owner decision (1 GB is ample for now); the budget covers the sync's garbage (NL-416).
- **Fix sketch:** Intern node ids and bitcoin keys (G5-T1), then enforce `Gossip:MaxMemoryMb` from the estimate in the ingress (refuse new channels over budget) with a default set from a re-measurement.
- **Blocks/Blocked-by:** Part of NL-099; blocks D12 on mainnet
- **Plan ref:** BOLT7 G5-T1

### NL-374 GraphStore rebuilds the whole snapshot under the writer lock
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GraphStore.cs` (`GetSnapshot`)
- **Evidence:** After any change the next `GetSnapshot` rebuilds the full snapshot under the writer lock: 69-105 ms at 200k channels, which stalls ingress writers each time (reported by lane D2, gossip wave G-D). Related to the relay's full diff (NL-366).
- **Fix sketch:** Build the snapshot outside the lock (copy-on-write) or incrementally.
- **Blocks/Blocked-by:** Part of NL-099; related NL-366
- **Plan ref:** BOLT7 G5-T1, G5-T3

### NL-375 describegraph does not report relay queue depths
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipGraphDescriber.cs`, `Gossip/Relay/GossipRelayScheduler*.cs`
- **Evidence:** `describegraph` (4860494) reports graph counts, the memory estimate, write-behind and ingress queues and per-connection sync state, but not the relay's pending sets: the scheduler exposes no accessor (off-limits to lane D2). The depths are visible only through the `nlightning.gossip` queue-depth gauge (`relay_pending`, 97cdc27) (reported by lane D2, gossip wave G-D).
- **Fix sketch:** Add a per-peer pending-count accessor to `GossipRelayScheduler` and put it in the describe result (append-only IPC keys).
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T4

### NL-376 Mutinynet gossip soak: 24 h evaluation pending; RSS and SQLite WAL grow in the first 20 min
- **Status:** fixed (24 h soaks recorded, see Evidence)
- **Severity:** medium
- **Kind:** test
- **Location:** `scripts/mutinynet/soak-gossip.sh`, `docs/agents/MUTINYNET.md` ("Gossip soak (G5-T5)")
- **Evidence:** The soak started 2026-09-26 19:10:53 UTC (log `~/.nltg/mutinynet/soak/soak-20260926.log`, one sync peer, the faucet LND). In the first 20 min RSS went 178 -> 207 MB (flattening) and `nltg.db`'s WAL grew about 130 KB per 5 min with no checkpoint seen (e7c628e) (reported by lane D3, gossip wave G-D). D12 stays closed until the 24 h results are recorded. Update (wave O7, `8364a01`): the soak sampler sets its trap flags before its traps and `start` runs from the staged copy (lane X5, ddcc598; the script bug found during the soak); the Mutinynet public channel is recorded in `MUTINYNET.md`. The 24 h evaluation is still pending. Update (mainnet gossip probe, `docs/agents/MAINNET_GOSSIP_PROBE.md`): over a 60-minute mainnet run (about 40,000 channels) and a restart, the SQLite WAL stayed bounded (5.8 MiB, checkpointed; 5.2 MiB verified) and RSS flat at 400 MB after the sync, so the WAL concern does not reproduce at mainnet scale on a busy graph; the 24 h Mutinynet evaluation and a multi-day mainnet run are still pending. Update (ledger hygiene lh1 review): severity low to medium: this soak is the unmet proof of `BOLT7_GOSSIP_PLAN.md` G5-T5, the gate the plan set for mainnet sync, and D12 (wave d12, `aa1cc10`) opened graph and sync on mainnet by default without it; with NL-099 closed it is the only entry that tracks that proof.
- **Update (2026-09-28, closed):** Both 24 h soaks are recorded. Mutinynet (`MUTINYNET.md` "24 h results"): RSS peaked at 242 MB and ended at 164 MB, the database ended at 5.4 MB and the WAL was checkpointed. Mainnet (`MAINNET_GOSSIP_PROBE.md` "24 h mainnet soak", five peers, verified against bitcoind): 30,673 channels, RSS flat at 450-640 MB against the 1,024 MB budget, WAL 5.0-5.3 MiB, median CPU 1.2 %, 2 peer disconnections and no bans. The WAL growth this entry was opened for does not reproduce. Relay on mainnet stays with NL-417 and NL-360.
- **Fix sketch:** Record the 24 h samples in `MUTINYNET.md`; check RSS and WAL trends (add a periodic `wal_checkpoint` if the WAL keeps growing); repeat with a second sync peer to exercise rotation.
- **Blocks/Blocked-by:** Part of NL-099 (G5-T5 proof); D12 was decided without it
- **Plan ref:** BOLT7 G5-T5

### NL-400 Core Lightning's wrapping reply_channel_range sequence ended every CLN range sync
- **Status:** fixed (df56139)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Sync/RangeReplyCollector.cs`
- **Evidence:** CLN's `reply_channel_range` sequence has unsorted ids and a `number_of_blocks` that wraps in 32 bits (`918664 + 4294956303`), the next reply starting at the wrapped end, below the previous `first_blocknum`; `RangeReplyCollector` rejected it with a warning, so the sync with every CLN mainnet peer (Blockstream Store, noserver4u) ended at the first reply (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: LND's continuity rule (a reply may start at the previous reply's u32 end or the block before it) is accepted and ids inside the query are kept even outside their own reply's claimed blocks. Upstream CLN issue worth reporting.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3, D12

### NL-401 A pre-2022 channel_update without htlc_maximum_msat closed the connection
- **Status:** fixed (b4d0e1c)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs`
- **Evidence:** LND answers queries with old 128-byte `channel_update`s without `htlc_maximum_msat`; the payload parser rejected them and `MessageService` warned and closed the connection, so every LND peer that stored one was cut about 15 s into the sync (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: a malformed 256/257/258 is ignored with one warning per connection, the connection is kept.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G2, D12

### NL-402 Each range-sync peer asked for everything the other peers had already delivered
- **Status:** fixed (41d2e45)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs`
- **Evidence:** Each sync peer diffed its range reply against the graph once, at the start: with five sync peers 4.9x the graph was downloaded and an LND sync took 11 min (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: every `query_short_channel_ids` batch is diffed again right before it goes out (1.9x, 67 s). With the chain check the store lags the queue and the re-diff sees too little (NL-415).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099; related NL-415
- **Plan ref:** BOLT7 G3-T2

### NL-404 Range sync asked for unknown channels whose updates the peer reports stale
- **Status:** fixed (e64b5f4)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs`, `GossipSyncOptions.cs`
- **Evidence:** With `gossip_queries_ex` timestamps, unknown channels whose both update timestamps are stale or missing were queried anyway (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: they are skipped (`Gossip:SkipChannelsStaleFor`, 14 days; LND's zombie rule); the probe logs the peers' range timestamps (0ff9025). Mainnet peers mark almost no channel stale today (0-2 per reply), so the zombies of NL-406 are not removed by this.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099; related NL-406
- **Plan ref:** BOLT7 G3-T2

### NL-405 OriginTrackingGossipIngress recorded origins while the relay was off
- **Status:** fixed (63ecfbe)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Relay/OriginTrackingGossipIngress.cs`, `RelayServiceCollectionExtensions.cs`
- **Evidence:** The relay's origin tracker kept up to 100,000 origins (about 15 MB in the `dotnet-gcdump` at 18 min) while the relay of others' gossip was off (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: nothing is recorded while the relay is off.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099; related NL-373
- **Plan ref:** BOLT7 G5-T1

### NL-406 Core Lightning floods channel_announcements without any channel_update; we store them
- **Status:** fixed (ab55e29, 4a48443)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs`, `GraphStore.cs`, `GraphPruner.cs`
- **Evidence:** 24 % of the mainnet graph (9,774 of 40,457 channels in the first run, 9,091 of 39,663 verified) are announcements without any update, sent by the CLN peers mostly right after our `gossip_timestamp_filter`, against BOLT 7's "MUST NOT send the `channel_announcement`" without updates; their funding outputs are unspent on chain (verified run), their nodes long gone. We store them; the pruner removes them only by the stale rule (28 days, only while blocks come) and CLN re-sends them on every connection (32,022 re-sent in the restart; in the verified run 28,785 dropped at the CLN peers' full queues) (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`; confirmed by the verified run). Update (wave d12, lane Z1): a signed `channel_announcement` waits in `Gossip/Graph/PendingAnnouncementIndex` outside `IGraphView` (never routed, served or relayed, no chain lookup; raw bytes + sender + time, about 430 B), bounded by `Gossip:MaxPendingAnnouncements` 50,000 (eviction takes the oldest entry of the sender holding the most, `pending_full`) and `Gossip:PendingAnnouncementTtl` 14 days (`pending_expired`), at most 4 candidates per scid, one per sender (`pending_candidates_full`, sybil limit NL-418). The first valid `channel_update` promotes the candidate whose node signed it (`GossipIngress.PromoteAsync`): the chain lookup and a chain-mismatch misbehaviour score now happen at promotion; an update matching no candidate is orphaned and its scid re-queried. Not persisted (peers resend after a restart); `describegraph` `PendingAnnouncements` (IPC key 28). d12 verified mainnet run: 30,537 channels, every one with a policy; 9,775 pending after a restart (about 4 MB). A node_announcement whose node has only pending channels can expire as an orphan (NL-425).
- **Fix sketch:** Keep an announcement without update in a bounded pending cache (TTL, like the orphan cache) and store it only with its first update, or keep a zombie index (LND) of pruned SCIDs. Report upstream to CLN. Needed before relay is enabled on mainnet.
- **Blocks/Blocked-by:** Part of NL-099; blocks D12 relay on mainnet; related NL-373
- **Plan ref:** BOLT7 D12, G5-T1

### NL-407 Eclair stops answering query_short_channel_ids after four queries
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs`
- **Evidence:** In every first-run probe ACINQ (Eclair) answered 4 queries of 200 channels, then the fifth got no `reply_short_channel_ids_end` within `SyncReplyTimeout` (2 min) and we ended the querying of that connection (live gossip kept flowing) (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Not diagnosed. The verified run did **not** reproduce it at its slower, paced rate: Eclair answered 109 queries in run1 (one later query and one in the restart timed out).
- **Fix sketch:** Check Eclair's per-peer query budget in its source and with a peer we have a channel with; pace queries to Eclair if a budget exists.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3-T2

### NL-408 The initial sync orphans nearly every channel_update
- **Status:** fixed (ab55e29)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs`
- **Evidence:** Announcements and updates of one channel are validated by different workers in parallel (12 on the probe machine), so the update usually wins the race and is orphaned, then replayed once the announcement is stored: 53,593 orphaned updates in the first run, 56,093 in the verified run (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Correct, but a second pass per update. Update (wave d12, lane Z1): `GossipIngress` keeps one queue per worker, partitioned by scid (node id for 257, splitmix64), with the global `MaxQueued` counter; a synthetic two-peer sync on 4 workers orphans 0 updates (146 with round-robin queues). d12 verified mainnet run: 540 orphaned updates in the whole sync.
- **Fix sketch:** Partition the ingress queue by short channel id so a channel's messages stay on one worker.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T3

### NL-409 (unused number)
- **Status:** wontfix
- **Severity:** low
- **Kind:** tech-debt
- **Location:** n/a
- **Evidence:** Not assigned: the mainnet gossip probe numbered its first run NL-400..NL-408 and its verified run from NL-410, leaving NL-409 free. Kept as a placeholder so IDs have no gap.
- **Fix sketch:** None.
- **Blocks/Blocked-by:** None
- **Plan ref:** n/a

### NL-410 A dropped channel_update of a stored channel was never asked for again
- **Status:** fixed (788d2d3)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs` (`RetryMissedShortChannelIds`)
- **Evidence:** `RetryMissedShortChannelIds` dropped every missed SCID already in the graph, and a peer without `gossip_queries_ex` never offers the update again; with the chain check the store lags the queue, so updates were orphaned or dropped far more often (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: a stored channel stays in the retry until it has both policies (or is spent); one query per miss.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G3-T2

### NL-411 FundingOutputLookup called getblockhash before every txid-list fetch
- **Status:** fixed (1a77f88)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Gossip/FundingOutputLookup.cs`
- **Evidence:** `GetBlockTxIdsAsync` reads the block hash itself, so the extra `getblockhash` made 7 RPCs per lookup on a cache miss (84 % of mainnet lookups) instead of 6 (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: about 14 % fewer RPCs for a full sync; a cached list is still checked against `getblockhash` first.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 D3

### NL-412 The sync backpressure waited on the whole ingress queue, with a time limit
- **Status:** fixed (788d2d3)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs`, `Gossip/Graph/GossipIngress.cs` (`QueuedCountOf`)
- **Evidence:** The NL-353 backpressure waited for the whole ingress queue to fall to half of one peer's capacity, at most `SyncReplyTimeout` (2 min), then queried anyway: with the chain check one CLN flood held every other sync peer back 2 min per batch and the answer then went into a queue that could drop it (IBD smoke: every peer, every batch) (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: the querier waits for the queried peer's own queue, without a time limit (logged once after the timeout).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-099; related NL-353
- **Plan ref:** BOLT7 G3-T2

### NL-413 GetUnspentOutputAsync derives the output height from a separate getblockcount
- **Status:** fixed (8c64733)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs` (`GetUnspentOutputAsync`)
- **Evidence:** The height is `getblockcount - confirmations + 1` with the `getblockcount` after `gettxout`: a block connected in between shifts it by one and the lookup answers `ChainMoved` (transient, deferred). 0.8 % of lookups while bitcoind connected ~8 blocks/s in IBD, none once synced (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Update (wave d12, lane Z3): the height is `getblockheader(bestblock).height - confirmations + 1`, same RPC count, no `getblockcount` (`Wallet/BitcoinChainServiceUnspentOutputTests`). d12 verified mainnet run: 0 `ChainMoved`. The probe's doc comment `tools/NLightning.GossipProbe/RpcChain.cs` (`CountingChainService`) still says "+ getblockcount".
- **Fix sketch:** Use the height of `gettxout`'s `bestblock` (`getblockheader`), the same RPC count.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 D3

### NL-414 A channel whose funding output a mempool transaction spends is looked up again and again
- **Status:** fixed (a0561db, 2bd6764)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs` (`OutputSpentInMempool` retries), `Gossip/Sync/GossipSyncManager.cs`
- **Evidence:** Looked up every `RetryDelay` for `MaxRetries`, then dropped as missed, re-queried and looked up again for as long as the close stays unconfirmed: 184 lookups (about 1,200 RPCs) for one channel (`968539x658x1`) in the hour (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Update (wave d12, lane Z3): `FundingOutputLookup` keeps an `OutputSpentInMempool` answer per scid until the next block (monitor event, a higher tip, or `Gossip:MempoolSpentRecheckInterval` 10 min), so the ingress retries cost no RPC, and the sync's missed-scid retry waits for that block; the pending view survives the probe's decoration (2bd6764). Proven by unit tests (`FundingOutputLookupMempoolSpentTests`); the d12 mainnet run saw no mempool-spent case. Remaining: one announcement re-download per block per such channel until the ingress reports its own pending scids (NL-420); the lookup counter still counts the reused answers (NL-421).
- **Fix sketch:** Keep mempool-spent SCIDs aside and look them up again once per new block.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 D3

### NL-415 With the chain check, concurrent range syncs download the graph 3.6 times
- **Status:** fixed (59b1e16, 8c98b15, 2bd6764)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Sync/GossipSyncManager.cs`, `Gossip/Graph/GossipIngress.cs`
- **Evidence:** 142,240 `channel_announcement`s for 39,663 channels (1.9x with AssumeChannelValid): NL-402's per-batch re-diff skips only stored channels while thousands wait in the ingress for their lookup, so each of five sync peers was asked for 20,000-25,000 channels (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Costs bandwidth and ingress work, not lookups. Update (wave d12, lane Z3): `Sync/QueriedChannelTracker` (shared by all sessions, `Gossip:QueriedChannelTtl` 10 min) lets a range-sync batch claim its unknown scids; the re-diff skips scids claimed by another session or pending in any `IGossipPendingChannels` (the funding lookup today); a `full_information=0` end releases the claims at once and an ended claim is recycled once into the missed retry (8c98b15). d12 verified mainnet run: download multiple 2.3x (23,257 announcements from the queried peers in total), down from 3.6x; the 1.9x target was not quite reached, NL-420 narrows it further.
- **Fix sketch:** Have the ingress keep the SCIDs whose announcement is queued, deferred or being looked up and let the re-diff skip them, or run fewer concurrent range syncs while the chain is checked.
- **Blocks/Blocked-by:** Part of NL-099; related NL-402; blocks D12
- **Plan ref:** BOLT7 G3-T2, D12

### NL-416 A verified sync peaks at 630 MB RSS from getblock JSON on the large object heap
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs` (`GetBlockTxIdsAsync`)
- **Evidence:** Each `getblock <hash> 1` answer (100-400 KB) is parsed into a Newtonsoft `JToken` tree on the LOH: RSS peak 630 MB (GC heap 425 MB) against 425 MB with AssumeChannelValid, back to 456 MB / 255 MB after the sync (reported by the mainnet gossip probe, verified run against a real bitcoind, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Update (wave d12): the RSS-based memory budget (NL-373, 1,024 MiB) now covers this garbage; the d12 verified mainnet run peaked at 610 MB RSS and stayed at 610 MB after 30 min because the GC had not returned the memory, so after a burst a node near the budget could stay degraded (no new channels) until RSS falls below 90 %. Streaming parser not done.
- **Fix sketch:** Read the txids with a streaming `JsonReader` (or `getblock <hash> 0`), or budget the sync's garbage in NL-373.
- **Blocks/Blocked-by:** Part of NL-099; related NL-373
- **Plan ref:** BOLT7 G5-T1, D3

### NL-417 Relay of other nodes' gossip is unexercised against mainnet peers
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Application/Gossip/Relay/GossipRelayScheduler*.cs`, `tools/NLightning.GossipProbe` (`--relay-to`)
- **Evidence:** The d12 relay run (20 min toward Blockstream Store, 3 min toward ACINQ) was clean (0 warnings, disconnects or echoes, backlog at most 192 and draining) but relayed nothing: no mainnet peer subscribes to a node without channels (Eclair and LND send no `gossip_timestamp_filter`, CLN sends `first_timestamp = 0xFFFFFFFF`, which the relay honours) (reported by the d12 integrator, `docs/agents/MAINNET_GOSSIP_PROBE.md` "D12 runs"). Relay therefore stays off on mainnet (D12).
- **Fix sketch:** Repeat the relay run from a node with a public channel to the peer, or toward a peer we control that asks for everything; then decide the mainnet relay default.
- **Blocks/Blocked-by:** Part of NL-099; blocks the mainnet relay default (D12)
- **Plan ref:** BOLT7 D12, G5-T5

### NL-418 Four forging peers can block a channel's real announcement for the pending TTL
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Gossip/Graph/PendingAnnouncementIndex.cs` (`MaxCandidatesPerChannel` = 4)
- **Evidence:** If four distinct peers announce different forgeries of one scid before the real `channel_announcement` arrives, the real one is refused (`pending_candidates_full`) until those candidates expire (`Gossip:PendingAnnouncementTtl`, 14 days). Needs four peer identities and connections; documented in the Application CLAUDE.md (reported by lane d12-Z1's review step).
- **Fix sketch:** Shorten the TTL of candidates whose scid already has an orphaned `channel_update` that matches none of them, or evict such candidates when a fifth one arrives.
- **Blocks/Blocked-by:** Part of NL-099; follow-up of NL-406
- **Plan ref:** BOLT7 G5-T2

### NL-419 Channel announcements refused over the memory budget are not re-queried
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs` (`RefuseNew`), `Gossip/Sync/GossipSyncManager.cs` (`TakeMissedShortChannelIds`)
- **Evidence:** A new channel refused with reason `memory_budget` is not handed to the missed-scid re-query, so it comes back only with the next range sync or peer rotation (20 min) (reported by lane d12-Z2).
- **Fix sketch:** Mark refused scids missed (`MarkMissed`) once the budget has resumed, or let the sync re-diff after a resume.
- **Blocks/Blocked-by:** Part of NL-099; follow-up of NL-373
- **Plan ref:** BOLT7 G5-T1

### NL-420 GossipIngress does not report its queued or deferred channels as IGossipPendingChannels
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs`, `src/NLightning.Domain/Gossip/Interfaces/IGossipPendingChannels.cs`
- **Evidence:** Only `FundingOutputLookup` implements the Domain `IGossipPendingChannels` seam, so the sync's re-diff does not skip scids whose announcement is queued, deferred or in retry in the ingress, and a channel whose funding output is spent in the mempool is still re-downloaded once per block (reported by lane d12-Z3; `AddGossipSyncServices` picks up any registered implementation). The d12 mainnet download multiple is 2.3x against the 1.9x target of NL-415.
- **Fix sketch:** Implement `IGossipPendingChannels` on the ingress (queued/deferred 256 scids plus `IsPendingAnnouncement`) and register it.
- **Blocks/Blocked-by:** Part of NL-099; follow-up of NL-414, NL-415
- **Plan ref:** BOLT7 G3-T2, G5-T1

### NL-421 The chain lookup counter counts reused mempool answers that make no RPC
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Metrics/GossipMetrics.cs`, `Gossip/Graph/GossipIngress.cs`
- **Evidence:** Since NL-414, `FundingOutputLookup` answers `OutputSpentInMempool` from memory until the next block, but `nlightning.gossip.chain.lookups` still counts each of those answers as a lookup (reported by lane d12-Z3).
- **Fix sketch:** Tag reused answers (e.g. `cached=true`) or skip them in the counter.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G5-T4

### NL-422 The Esplora funding-txid source is unproven against a real pruned or assumeutxo bitcoind
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Gossip/EsploraTxIdSource.cs`, the Explicit `FundingOutputLookupBitcoindTests`
- **Evidence:** Proven only with a fake HTTP index over a header-only fake chain (41 tests, the mempool.space block 100,000 proof as a vector). Blocks below an assumeutxo snapshot that were never downloaded may report no nTx, which is now refused as transient, so such channels cannot be verified that way (reported by lane d12-Z4).
- **Fix sketch:** A Mutinynet/signet run with `-prune=550` and `Gossip:FundingTxIdSource=Esplora` (mempool.space signet API), plus an Esplora-mode case in the Explicit regtest smoke test.
- **Blocks/Blocked-by:** Part of NL-099; related NL-346
- **Plan ref:** BOLT7 D3, D12

### NL-423 An Esplora-mode verified sync from a public index takes more than 12 hours
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Gossip/EsploraTxIdSource.cs` (`EsploraRequestsPerSecond` 2)
- **Evidence:** Two requests per uncached channel (txid at position, merkle proof) at the polite 2 req/s default is about 1 channel/s: roughly 80,000 requests and more than 12 h for a mainnet graph of 40-50k channels against a public server; fine for a self-hosted esplora/electrs with a higher rate. Not measured live (reported by lane d12-Z4).
- **Fix sketch:** Batch per block through `/block/{hash}/txids`, checked against the header's merkle root, and measure an Esplora-mode sync.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 D3

### NL-424 A wrong-network Esplora index fails silently at Debug level
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Gossip/EsploraTxIdSource.cs`
- **Evidence:** An index on another network never yields a wrong answer (our block hash is unknown to it: 404, transient), but every lookup then ends `ChainUnavailable`, logged only at Debug, so a misconfigured `Gossip:EsploraUrl` shows up as a graph that never fills (reported by lane d12-Z4).
- **Fix sketch:** At startup, fetch `block-height/0` from the index and compare it with our genesis hash; log an error and refuse the source on a mismatch.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 D3

### NL-425 A node_announcement whose node has only pending channels can expire as an orphan
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Gossip/Graph/GossipIngress.cs` (orphan cache, 10 min TTL)
- **Evidence:** Since NL-406 a channel waits outside the graph until its first `channel_update`; a `node_announcement` for a node whose channels are all pending is orphaned and is lost if no promoting update arrives within the orphan TTL; the next sync brings it back (reported by lane d12-Z1).
- **Fix sketch:** Keep orphaned node announcements whose node appears in a pending candidate until that candidate is promoted or expires (bounded).
- **Blocks/Blocked-by:** Part of NL-099; follow-up of NL-406
- **Plan ref:** BOLT7 G2-T4

### NL-490 announcement_signatures for a retired SCID gets the SCID-mismatch warning
- **Status:** fixed (c51fe99a)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Handlers/AnnouncementSignaturesMessageHandler.cs` (`CheckShortChannelId`)
- **Evidence:** After a splice lock a late peer half for the old SCID is answered with a warning; BOLT 7 wants the warning only when the SCID matches none of the channel's fundings (reported by lane SP2-B).
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-E: a half whose SCID is in `IRetiredScidMap` for the channel matches one of its fundings, so `ChannelAnnouncementService.ShouldDeferRemoteAnnouncementSignatures` reports it and it is dropped without a warning (`ChannelAnnouncementSpliceTests`).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SP-G-01

### NL-498 The peer's channel_update of our own channel reaches our other peers only as relayed gossip
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/Relay/`
- **Evidence:** Our node sends its own `channel_announcement`, `channel_update` and `node_announcement` to its peers, but the other end's `channel_update` of our channel goes out only as others' gossip: to peers that sent a `gossip_timestamp_filter` (LND sends one only to its active sync peers), and never on mainnet (`Gossip:RelayEnabled=false`, NL-417). In `Day0FlowTests` LND alice, a peer of node A only, never learned B's direction until B connected to her (lane SP2-F, 3d0240ee; `DAY0_RUNBOOK.md` tells operators to give each node its own peers).
- **Fix sketch:** Send the counterparty's `channel_update` of our own announced channels with our own gossip (as LND does), independent of the relay switch.
- **Blocks/Blocked-by:** Related NL-417
- **Plan ref:** —

## BOLT 8: Transport

### NL-104 Transport read loop uses ReadAsync; short TCP reads kill the connection
- **Status:** fixed (d0e0bbb)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Transport/Services/TransportService.cs:267,291`
- **Evidence:** Header (18 B) and body are read with a single `ReadAsync`; a partial read is treated as failure. A 1366-byte+ update_add_htlc often spans segments.
- **Fix sketch:** `ReadExactlyAsync` for both.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT_COVERAGE roadmap step 2; BOLT2 N0-T2

### NL-105 Messages are encrypted before taking the write lock (nonce desync)
- **Status:** fixed (de96c9e, d865a7e)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Transport/Services/TransportService.cs:201-209`
- **Evidence:** `_transport.WriteMessage` (nonce increment) runs before `_networkWriteSemaphore.WaitAsync`, so concurrent senders can write ciphertexts out of nonce order and the peer fails decryption (inferred, not reproduced). A frame is written whole once encrypted; a write fault closes the socket. The fault path has no dedicated test.
- **Fix sketch:** Hold the semaphore across encrypt + write.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T2

### NL-106 Outgoing plaintext capped at 65519 bytes instead of 65535
- **Status:** fixed (9511b5c, 3ab999f)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Transport/Encryption/Transport.cs:105`
- **Evidence:** `payload.Length + 16 > MaxMessageLength (65535)` rejects valid 65520-65535-byte messages.
- **Fix sketch:** Bound the plaintext (not ciphertext) by 65535; size buffers accordingly.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-107 IPv6 listen addresses unsupported
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Transport/Services/TcpService.cs`
- **Evidence:** Only IPv4 listen addresses parse.
- **Fix sketch:** Support `[::]:port` addresses.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-108 MessageService deserializes and runs handlers synchronously under a lock on the read loop
- **Status:** open (partial: d60a891)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs`
- **Evidence:** Slow handlers stall reads (and ping handling) for that peer. Update: channel messages are now only queued on the read loop and handled by the per-peer inbound loop (d60a891); `MessageService` still deserializes on the read loop and ping/gossip replies still run there.
- **Fix sketch:** Queue messages to a per-peer consumer.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N0-T3 (partial)

### NL-228 PeerManager.StopAsync left the TCP listener bound
- **Status:** fixed (3d8bb07)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`StopAsync`)
- **Evidence:** `ITcpService.StopListeningAsync` was never called, so a node restarted in the same process (Docker test node, daemon restart) failed with "Address already in use". Found by the full Docker run. Fixed with `Given_Started_When_StopAsync_Then_TcpServiceStopsListening`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N0-T8

### NL-229 PeerService could lose channel messages and its disconnect before anyone subscribed
- **Status:** fixed (d39ca18)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs`
- **Evidence:** The read loop is live before the constructor returns, so a channel message (e.g. LND's first message after init) or a disconnect that arrived before `PeerManager` subscribed was dropped. Fixed: pending channel messages are kept (up to `MaxPendingChannelMessages`, then disconnect) and replayed in order to the first subscriber; `OnDisconnect` fires once and is replayed to a late subscriber (`PeerServiceLifecycleTests`).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-033, NL-201
- **Plan ref:** BOLT2 N0-T3

### NL-239 NLightning-to-NLightning connect: the responder loses the initiator's init
- **Status:** fixed (df4ca92)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (init handshake), `src/NLightning.Application/Node/Managers/PeerManager.cs`
- **Evidence:** Seen by the ABCD W0-F multi-node harness (`Docker/MultiNodeHarnessTests.cs`): when one of our nodes connects to another, the responder sometimes logs "Failed to receive init message" (the first message is not `init`) and drops the connection. LND peers are not affected. The harness retries such connects (`NLightningTestNode.ConnectToAsync(NLightningTestNode)`, `KnownConnectBugLogFragments`, 6f1a316, cb06e60). Root cause not investigated. Update (ABCD wave 1, `342d22e`): the responder's transport read loop started before the message, peer-communication and peer services subscribed, so the initiator's init was raised to nobody. The transport now starts reading only once `MessageReceived` has a subscriber and each layer attaches to the one below on its own first subscriber. The harness retry tolerance is removed; `MultiNodeHarnessTests` asserts the log line never appears, and `PeerManagerConnectTests` run two real peer managers over loopback.
- **Fix sketch:** Reproduce with two in-process nodes; check whether the initiator's init is read before the responder's read loop/subscriber is ready (compare NL-229) or is consumed by the transport handshake; then drop the harness tolerance.
- **Blocks/Blocked-by:** Blocks the B-C hop of the ABCD e2e (flaky); related NL-229, NL-240
- **Plan ref:** ABCD W0-F

### NL-240 Simultaneous connect between two NLightning nodes can leave no live connection
- **Status:** fixed (df4ca92, 38d2f5e, b681f8a, 9ce68e0)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (simultaneous-connect tie-break), `src/NLightning.Infrastructure/Node/Services/PeerService.cs`
- **Evidence:** Seen by the W0-F harness: on a simultaneous connect the responder's `init` write fails ("Error initializing peer communication") while the other end's LND-style tie-break (lower pubkey's outbound wins, `SimultaneousConnectWindow`) keeps that same dead connection, so neither survives until a reconnect. Tolerated in `MultiNodeHarnessTests` (6f1a316, cb06e60). Update (ABCD wave 1, `342d22e`): writes checked the socket with Poll + Available, which the read loop could drain, so a live connection looked closed and the init write failed; writes now check `TcpClient.Connected`, a failed `PeerService` constructor disposes the stack, and `PeerManager` installs a session only after `IPeerService.WaitForInitAsync` (df4ca92). Follow-ups: an inbound connection whose init arrives after stopping began is closed (38d2f5e), a deterministic regression test for the write-side check (b681f8a), and no session is installed once stopping began; pending init waits are cancelled and inbound setups awaited on stop (9ce68e0).
- **Fix sketch:** Make the tie-break keep only a connection whose init exchange completed, or retry the survivor; test with two in-process nodes; then drop the harness tolerance.
- **Blocks/Blocked-by:** Related NL-239, NL-201
- **Plan ref:** ABCD W0-F

---

## BOLT 9: Features

### NL-109 Features advertised that are not implemented
- **Status:** fixed (0dd030e, 4d06f1b, 3c2b673, e93eb41)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:14,24,31,40,65,67,76,83`
- **Evidence:** Defaults advertise `option_data_loss_protect` (Compulsory), `gossip_queries(_ex)`, `basic_mpp`, `option_dual_fund`, `option_quiesce`, `option_provide_storage`, `option_scid_alias` (partial) with no or partial implementation (route_blinding/attribution_data tracked in NL-074). Peers act on them and we disconnect or hang. Unimplemented features default to No and are refused (config validation) unless `Features:AllowExperimentalFeatures=true`. data_loss_protect stays Optional (ASSUMED) until NL-035; gossip_queries is now answered (NL-205). Update (ABCD wave 6, `3ce3cad`): `basic_mpp` is implemented (NL-081) and defaults to Optional again, outside `ExperimentalFeatures` (ba3db36); `option_simple_close` left the experimental set with N11 but defaults to No (5b9ce68).
- **Fix sketch:** Default each to No until implemented. Exception: keep gossip_queries until NL-100 is fixed (dropping it makes peers flood gossip). data_loss_protect is required by LND/CLN in practice, so fix NL-035 rather than dropping it.
- **Blocks/Blocked-by:** Related NL-019, NL-037, NL-081, NL-010
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T4 (partial)

### NL-110 Feature dependency table has only 2 entries
- **Status:** fixed (0e44e5a)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs:18-23`
- **Evidence:** Only gossip_queries_ex→gossip_queries and zeroconf→scid_alias. Missing e.g. basic_mpp→payment_secret, anchors→static_remote_key, payment_secret→var_onion_optin, route_blinding→var_onion_optin.
- **Fix sketch:** Encode the full BOLT 9 dependency table; test.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-111 Dependencies checked only on the remote set; no per-context feature filtering
- **Status:** fixed (0e44e5a, 4d06f1b, 3c2b673)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs`, `FeatureOptions.cs`
- **Evidence:** No I/N/C/9/B context masks; our own advertised set is not dependency-checked. Negotiated set: Compulsory if either side requires, Optional if both support; ASSUMED bits omitted by the peer count as supported.
- **Fix sketch:** Add context masks and validate local sets at startup.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-112 FeatureSet.DeserializeFromBytes reverses the caller's array in place
- **Status:** fixed (a3d5187, 89a6018)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs`
- **Evidence:** On little-endian hosts the input buffer is mutated. Raised from low: after the clone fix, multi-byte channel_type went out byte-reversed (`GetBytes` is little-endian); `FeatureSet.GetWireBytes()` is now used on both open paths.
- **Fix sketch:** Copy before reversing.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-206 Negotiated Optional features take effect; config could enable unimplemented features
- **Status:** fixed (e93eb41)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs` (`ExperimentalFeatures`, `AllowExperimentalFeatures`, `GetValidationErrors`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 4. After NL-110/111, optional/optional negotiates as Optional, so setting OptionAnchors, OptionQuiesce, DualFund etc. in config would change behaviour with no implementation behind it. Now those features are refused at startup and never advertised unless `Features:AllowExperimentalFeatures=true`. LargeChannels (wumbo) enforcement checked: `ChannelOpenValidator` rejects >= 2^24 sat unless negotiated (tests only).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-109, NL-111
- **Plan ref:** —

### NL-226 FeatureSet.GetBytes dropped a highest set bit at a multiple of 8
- **Status:** fixed (d5a58ef)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs` (`GetBytes`)
- **Evidence:** The byte count was `(lastIndexOfOne + 7) / 8`, so bit 8, 16, …, 48 as the highest set bit was cut off: channel_type zero_fee_commitments (40), payment_metadata compulsory (48), a global set whose only bit is 0. Found by the N1-T4 lane while echoing channel_type (NL-218). Fixed: `lastIndexOfOne / 8 + 1`; `GetSetBits`/`HasSameBits` added; two serializer tests that encoded the truncation were corrected.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-218
- **Plan ref:** BOLT2 N1-T4 (prerequisite)

---

## BOLT 10: DNS bootstrap

### NL-113 DNS seed bootstrap is fully commented out
- **Status:** fixed (f09ff53d, branch `wip/fafo-bolt10`)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/DnsSeedClient.cs` (deleted); now `src/NLightning.Infrastructure.Bitcoin/Bootstrap/`, `src/NLightning.Infrastructure/Protocol/Dns/`, `src/NLightning.Application/Node/Bootstrap/PeerBootstrapService.cs`, `src/NLightning.Domain/Node/Bootstrap/`
- **Evidence:** 0 lines of live code in either file. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): `DnsSeedClient.cs` is still entirely commented out; the test file `test/NLightning.Integration.Tests/BOLT10/DNSBootstrapTests.cs` no longer exists, so the Location now names only the client. Update (lane bolt10, `f09ff53d` on `wip/fafo-bolt10` from `978ad275`): BOLT 10 client and bootstrap implemented. `DnsSeedClient` (Infrastructure.Bitcoin, `IDnsSeedClient`) asks SRV on the seed root, then `_nodes._tcp.<root>`, decodes the target's first label as a bech32 `ln` node id (`LightningNodeIdBech32` over NBitcoin, 33-byte curve point), takes the addresses from the glue or A/AAAA, the port from the SRV record, and drops non-routable addresses and families not asked for (`SeedAddressFilter`); the DNS goes through the `IDnsRecordLookup` seam (`DnsClientRecordLookup` over DnsClient 1.8.0, built on the first query). `PeerBootstrapService` (Application) runs after `PeerManager.StartAsync` only when enabled, on a network with seeds, the chain not halted, fewer than `MinPeers` connected, no saved dialable peer and no graph node with an address; it dials at most `MaxPeersFromBootstrap` new peers, which the peer manager saves as ordinary `Peers` rows. `PeerAddress` reads `pubkey@[ipv6]:port` and `PeerModel` writes it. Owner decisions: D-B10-1 off by default (`Node:Bootstrap:Enabled` unset = false); D-B10-2 per-network seeds (mainnet `nodes.lightning.directory`, `nodes.lightning.wiki`; testnet `test.nodes.lightning.directory`; none on regtest/signets, configured seeds ignored there unless `AllowSeedsOnThisNetwork`); D-B10-3 DNS over TCP by default (UDP with EDNS0 and TCP retry as an option); D-B10-4 bech32 and secp256k1 from NBitcoin in Infrastructure.Bitcoin; D-B10-5 DnsClient 1.8.0, no new package, lazy client; D-B10-6 IPv4 and IPv6, bracketed IPv6 addresses. `Node:DnsSeedServers` is obsolete (copied into `Bootstrap:Seeds` when that is absent, with a warning; changed by the review fixes below). Tests (net10.0, Release): Domain `Node/Bootstrap` 91, Infrastructure 479, Infrastructure.Bitcoin `Bootstrap` 34, Application `PeerBootstrapServiceTests` 24, Daemon `NodeServiceExtensionsTests` 68, all green; the `Explicit` `Category=Live` `DnsSeedLiveTests` found 25 valid candidates (IPv4 and IPv6, ports 9735/9739/9835/8740) from nodes.lightning.directory over 1.1.1.1 on 2026-09-28. Follow-ups NL-541..NL-545. Update (lane bolt10 review fixes, `644bc5a8`): (1) `SeedAddressFilter` also refuses 192.0.0/24, 192.0.2/24, 198.51.100/24, 203.0.113/24, 198.18/15, IPv4-compatible `::/96`, local-use NAT64 `64:ff9b:1::/48`, `100::/64` and `fec0::/10`, and judges NAT64 `64:ff9b::/96` and 6to4 `2002::/16` by their embedded IPv4 (so `64:ff9b::a00:1` is 10.0.0.1 and refused). (2) `DnsSeedClient` keeps one record per (target, port) instead of per target, resolving each target once. (3) The conditional query carries `n<maxResults>` (BOLT 10's default is 25, `MaxPerSeed` goes to 100). (4) The 9 BOLT 10 example labels decode, are curve points and re-encode; the spec's `lseed.bitcoinstats.com` SRV example is replayed with its ports. (5) `BootstrapOptions.MainnetSeeds` no longer claims BOLT 10 lists them (they are LND's and CLN's defaults). (6) New `IPeerManager.DialPeerAsync(info, ct)`: cancellation closes the TCP connect, the handshake or the init wait, so a dial never outlives the bootstrap's `ConnectTimeout` (default now 30 s, validated >= `Node:NetworkTimeout` when enabled) and can no longer exceed `MaxDialConcurrency` or `MaxPeersFromBootstrap`. (7, 11) Only no seeds and `MinPeers` connected end the loop; a halted chain, a saved peer with active channels to reconnect and graph nodes with addresses skip that run and are re-checked every `RetryInterval` up to `MaxRuns`. (8, 10) Saved peers without active channels (dialed once at start, never retried) no longer block the bootstrap. (9) Candidates are deduped by (address, port) and endpoints that failed are not dialed again in the process. (12) `Node:DnsSeedServers` is never used: the old template's list (any order) is ignored silently and keeps the network defaults (testnet no longer gets mainnet seeds), an operator-edited list sets `ObsoleteSeedsIgnored` and gets one warning only when bootstrap is enabled, and its entries are not validated. Tests (net10.0, Release, targeted): Domain 3660, Infrastructure.Bitcoin 1394, Application `NLightning.Application.Tests.Node` 208 (3 runs), Daemon 799, all green. Update (lane b10main, branch `wip/fafo-b10main` from `6a0c3352`, owner decisions 2026-09-28): **D-B10-1 reversed by the owner**: bootstrap is on by default on mainnet (`Node:Bootstrap:Enabled` unset = on on mainnet only, `BootstrapOptions.IsEnabledOn(network)`; the mainnet config template writes `true`, every other network `false`; testnet stays off although it has a seed: testnet3 is being replaced by testnet4, which has none, NL-545). A config file written by the previous template keeps its explicit `"Enabled": false` on mainnet. D-B10-7 (new, the default resolver; owner-visible privacy trade-off): the system resolver (or the configured `NameServers`) is asked first; only a seed it gives no candidate for (SERVFAIL, timeout, no records, targets it cannot resolve) is asked again through `Node:Bootstrap:FallbackNameServers` (default 1.1.1.1, 8.8.8.8; `FallbackToPublicResolvers` true), never when `NameServers` are configured (`IFallbackDnsRecordLookup` → `FallbackDnsRecordLookup`, `DnsSeedClient`). On this Mac the home router (192.168.1.1 and the ISP IPv6 resolver) answers SERVFAIL to the seeds' SRV queries over UDP and TCP while 1.1.1.1 answers 25 records, so without the fallback the mainnet default would find nothing here; the fallback tells Cloudflare/Google that this host looks up Lightning seeds, which the failing system resolver's upstream saw already. Because the mainnet default now runs on nodes whose operator never enabled it, `Bootstrap:ConnectTimeout < NetworkTimeout` is a validation error only with an explicit `Enabled = true`, and the dial waits `BootstrapOptions.GetEffectiveConnectTimeout(NetworkTimeout)` (never less than `NetworkTimeout`); otherwise raising `Node:NetworkTimeout` above 30 s would have stopped a mainnet node from starting. `IPeerBootstrapService.GetStatus()` (`PeerBootstrapStatus`: runs with skip reasons, seed queries with resolver and outcome, dials with `Connected`/`AlreadyConnected`/`Failed`/`TimedOut`) for the probe and later IPC. The gossip probe got `--bootstrap` (no configured peer, the product's `PeerBootstrapService` as the daemon starts it, `bootstrap-*.csv`). Tests (net10.0, Release): Domain 3678, Infrastructure 482, Infrastructure.Bitcoin 1412, Application 3042 (full), Daemon 822, all green. Live (lane b10main, `MAINNET_GOSSIP_PROBE.md` "BOLT 10 bootstrap run (2026-09-28)"): a fresh mainnet probe node with no configured peer got 25 candidates from one seed through the fallback (the router SERVFAILs or times out on both seeds), dialed 10, connected 8 (the third 36 s after start, the 15 s start delay included), synced the whole graph from them (`sync_complete` at 12 min, 30,570 chain-verified channels) and kept all 8 for 40 min; after a restart the saved peers reconnected and the bootstrap ended without a DNS query. Found NL-546 (fixed, the rerun got its first peer after 26 s).
- **Fix sketch:** Restore and test, or delete.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-541 BOLT 10 `l` node query and assisted location of known peers are not used
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Bootstrap/DnsSeedQuery.cs`, `src/NLightning.Infrastructure.Bitcoin/Bootstrap/DnsSeedClient.cs`
- **Evidence:** Lane bolt10 (NL-113). `DnsSeedQuery` builds `l<bech32>` names and `DnsSeedQuery.VirtualHost`, but nothing queries a seed for a known node's address (BOLT 10's "assisted location"). The live seeds answer nothing to the conditions tried (`a2`, `n5`, `r0.a2.n5`), so `UseQueryConditions` is off by default.
- **Fix sketch:** When a saved peer with channels stays unreachable, ask the seeds for its virtual host (A/AAAA of `<ln1...>.<root>`) and try the answer; re-check whether live seeds answer the conditions.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT 10

### NL-542 BOLT 10 bootstrap has no Tor or proxy mode (DNS leak)
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Dns/DnsClientRecordLookup.cs`
- **Evidence:** Lane bolt10 (NL-113). Seed queries go straight to the system or configured resolvers over TCP/UDP. The node has no Tor/proxy mode yet; once it has one, bootstrap must go through it (or stay off), or DNS reveals that the host runs a Lightning node.
- **Fix sketch:** With a proxy mode, send the DNS over the proxy (TCP DNS through SOCKS5) or refuse bootstrap with a clear log.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-543 A node with too few peers does not connect to graph-known nodes
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Node/Bootstrap/PeerBootstrapService.cs` (`CheckGateAsync`)
- **Evidence:** Lane bolt10 (NL-113). The bootstrap skips its run when the graph holds a node with an address ("the node already knows contacts"), but nothing then connects to such nodes when the node has fewer than `MinPeers` peers (CLN and LND keep a minimum of gossip peers from the graph).
- **Fix sketch:** A peer-count keeper that picks addressed graph nodes (announced channels, recent updates) when connected peers stay below `MinPeers`, sharing the bootstrap's dial limits.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-544 The live BOLT 10 smoke test is not run in CI
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Bitcoin.Tests/Bootstrap/DnsSeedLiveTests.cs`
- **Evidence:** Lane bolt10 (NL-113). The only test against a real seed is `[Fact(Explicit = true)]`, `Category=Live` (the default run is hermetic, NL-168), so a change in the seeds' answers (format, ports, targets under another root) is found only by a manual run.
- **Fix sketch:** A scheduled CI job (not the PR gate) that runs `-explicit only -trait Category=Live` and reports failures.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-545 No BOLT 10 seeds for testnet4 and signets
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/BootstrapOptions.cs` (`GetDefaultSeeds`, `IsSeedNetwork`)
- **Evidence:** Lane bolt10 (NL-113). Only mainnet and testnet (testnet3) have seeds; regtest and signets (Mutinynet included) have none, and testnet4 is not a supported network yet (NL-012). Configured seeds there are ignored unless `Node:Bootstrap:AllowSeedsOnThisNetwork`.
- **Fix sketch:** Add seeds when public testnet4/signet seeds exist, and add testnet4 to `IsSeedNetwork` with NL-012.
- **Blocks/Blocked-by:** Related NL-012
- **Plan ref:** —

### NL-546 BOLT 10 seed targets are resolved one at a time
- **Status:** fixed (10822bdd)
- **Severity:** low
- **Kind:** performance
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Bootstrap/DnsSeedClient.cs`
- **Evidence:** Lane b10main, mainnet bootstrap run of 2026-09-28 (`docs/agents/MAINNET_GOSSIP_PROBE.md`, "BOLT 10 bootstrap run"): both mainnet seeds answer 25 SRV records without glue, so the client asked A and AAAA for each target, 50 TCP queries one after the other: about 8.5 s per seed through 1.1.1.1 (the seed census: 10.3 s and 18.7 s per seed including the system resolver's failure). With a slower link the per-seed timeout (10 s) would cut the answer short. This was most of the time to the first peer after the 15 s start delay.
- **Fix sketch:** Resolve the targets concurrently. Done: batches of at most `DnsSeedClient.MaxConcurrentAddressLookups` (8) targets, never more than the candidates still wanted (a cap of 5 still costs 5 queries); a target not resolved when the seed's time runs out is left out and the resolved ones are returned with outcome `Timeout`. The live smoke (`DnsSeedLiveTests`, 1.1.1.1) takes 0.66 s for 25 targets. Tests: `DnsSeedClientTests.Given_ManyTargetsWithoutGlue_*`, `Given_ATargetThatHangs_*`.
- **Blocks/Blocked-by:** Related NL-113
- **Plan ref:** BOLT 10

---

## BOLT 11: Invoices

### NL-114 [EPIC] Invoices not wired into the node: invoice store, create/pay commands, final-hop checks
- **Status:** fixed (6156173, 234607e, 899e36b, 6cfbcd1, c10a78e, 4ae2eb3, ca87313, d1476a4, 0870ab1, 6cb279f, 083a726, f2f1ef6)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11` (no `src/` project references it), `src/NLightning.Domain/Client/Enums/ClientCommand.cs`
- **Evidence:** No invoice/preimage table, no CreateInvoice/PayInvoice IPC, no payment_secret checks. Update (ABCD wave 0, `0b7e617`): `Invoice.Encode` validates first and rejects unknown even and doubled feature bits; the node-key encode path is covered and LND 0.20 invoice fixtures decode (NL-120, 2d8a9fe, 2c9f812, f93d059); `InvoiceModel`, `IInvoiceService`, `IInvoiceDbRepository` and `ClientCommand` 9-12 contracts exist (2ede2ee). No invoice store, service or final-hop processor yet (ABCD W1-B, W1-C). Update (ABCD wave 1, `342d22e`): Application references Bolt11; `InvoiceService : IInvoiceService` creates (CSPRNG preimage/secret), signs with the node key and persists invoices (features 8/14 compulsory, `c` from `Routing.InvoiceMinFinalCltvExpiry`, no route hints, NL-245) and the final-hop checks exist (6156173, 234607e); the invoice table and repository (899e36b); CreateInvoice/ListInvoices/PayInvoice/ListPayments IPC and CLI (6cfbcd1, c10a78e); composition root registration (4ae2eb3). CreateInvoice and ListInvoices work end to end; PayInvoice/ListPayments answer "not available" until `IPaymentService` exists (W2-C). Receive is not wired: the switch still fails every HTLC (W2-B). Update (ABCD wave 2, `a5675cb`): receive and pay are wired. The switch runs the final hop and settles the invoice in the fulfill's save (ca87313, d1476a4). Invoices carry route hints for private channels (NL-245, 0870ab1). `PaymentService : IPaymentService` backs `payinvoice`/`listpayments` (6cb279f, 083a726). It is registered in `AddApplicationServices`, and in-flight payments are reconciled at startup (f2f1ef6). Docker N8: LND pays our invoice (also a trimmed HTLC), we pay LND's invoice, 10 concurrent payments each way, and a restart with our HTLC in flight. ABCD c-send and c-receive are green. No automatic retries and no per-call fee limit (NL-270).
- **Fix sketch:** Reference Bolt11 from Application/Daemon, invoice store (NL-137), IPC commands (NL-152), FinalHopProcessor (M4-T3), PaymentManager (M4-T6).
- **Blocks/Blocked-by:** Blocked-by NL-073
- **Plan ref:** ONION M4-T3, M4-T6; BOLT2 N8-T2, N8-T3 (partial)

### NL-115 MinFinalCltvExpiry returns null instead of the default 18
- **Status:** fixed (0879269, f41f4ba)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFields/MinFinalCltvExpiryTaggedField.cs`, `Invoice.cs`
- **Evidence:** Absent `c` yields null.
- **Fix sketch:** Return 18 when absent.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N8-T3

### NL-116 Only the first r (route hint) field is kept
- **Status:** fixed (3948fda, 35f0809, 0e3a865)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFieldList.cs:32`
- **Evidence:** Uniqueness rule drops later `r` fields; BOLT 11 allows several.
- **Fix sketch:** Allow repeated `r`; expose a list.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-117 RoutingInfo stores u32/u16 spec fields as signed int/short
- **Status:** fixed (dbe4f98, 0e3a865)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Models/RoutingInfo.cs:17-19`
- **Evidence:** fee_base > 2^31-1 or cltv_delta > 32767 fails `IsValid`.
- **Fix sketch:** Use uint/ushort.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-118 Fallback address field has no taproot (witness v1)
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11/Models/TaggedFields/FallbackAddressTaggedField.cs`
- **Evidence:** Unknown versions are skipped.
- **Fix sketch:** Support v1 (P2TR).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-119 Invoice feature bits are not validated
- **Status:** fixed (aadfd91)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/Invoice.cs:553`
- **Evidence:** `TODO: Check feature bits`; unknown even features accepted; writer doesn't force payment_secret/var_onion_optin.
- **Fix sketch:** Reject unknown even bits on decode; set required bits on encode.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N8-T2 (encode side)

### NL-120 Invoice.Encode never runs InvoiceValidationService
- **Status:** fixed (2d8a9fe, f93d059)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11/Services/InvoiceValidationService.cs`, `Models/Invoice.cs`
- **Evidence:** Validation runs on decode only. Update (ABCD wave 0, `0b7e617`): `Invoice.Encode` runs `InvoiceValidationService` before encoding and rejects unknown even and doubled feature bits; `InvoiceNodeEncodingTests` cover encode with a node key, decode and validate (features 9/14 compulsory, multiple `r`), and `LndInvoiceFixtureTests` decode LND 0.20 invoices (2c9f812).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-121 Tagged-field decode errors are swallowed
- **Status:** fixed (ea8b938, 344475d, f41f4ba)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Bolt11/Models/TaggedFieldList.cs:154-181`
- **Evidence:** Exceptions go to `Debug.WriteLine`; an over-long declared length `continue`s without skipping bits, misparsing the rest.
- **Fix sketch:** Skip unknown fields by declared length; fail on malformed known fields.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-122 Invoice API hazards: ToString() NRE without a key manager, setters throw on second set
- **Status:** fixed (6934c2c, 475d22b)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Bolt11/Models/Invoice.cs`
- **Evidence:** Documented in `src/NLightning.Bolt11/CLAUDE.md` gotchas.
- **Fix sketch:** Explicit exceptions / replace semantics.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-123 LightningMoney.Bits() returns the same value as Cents()
- **Status:** fixed (212cc89)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Money/LightningMoney.cs:197-208`
- **Evidence:** Both multiply by `Cent`; tests assert the wrong behaviour. No callers in `src/`.
- **Fix sketch:** Use the bit multiplier (100 sat); fix tests.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-124 BitWriter mask typo and ArrayPool misuse
- **Status:** fixed (04c8bb2, b7f407e)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Utils/BitWriter.cs:26-37,130,217`
- **Evidence:** `(1 >> bits) - 1` should be `(1 << bits) - 1` (L130); `Array.Resize` on the rented buffer returns a non-pooled array to the pool (L217). Used by BOLT 11 and FeatureSet.
- **Fix sketch:** Fix the shift; track the rented array separately.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-213 High-S invoice signatures were accepted when an n field is present
- **Status:** fixed (298919d)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/Invoice.cs` (`CheckSignature`)
- **Evidence:** The BOLT 11 invalid vector "Non canonical signature (high-S) with n field defined" decoded. Now low-S is required with n; recovery without n still accepts both. Found by the bolt11 batch reviewer.
- **Fix sketch:** Done (vector added to the invalid set).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-222 Non-minimal c/x/9 data_length accepted; `Invoice.Signature` stale after re-signing
- **Status:** open
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFields/{MinFinalCltvExpiry,ExpiryTime,Features}TaggedField.cs`, `Invoice.cs` (`Signature`, `Encode(Key)`)
- **Evidence:** BOLT 11 says a reader SHOULD treat c, x or 9 with leading zero groups as invalid; not enforced. `Signature` is get-only and keeps the decoded value after `Encode(Key)`. Reported by the bolt11 batch.
- **Fix sketch:** Reject non-minimal lengths on decode; refresh the signature on encode.
- **Blocks/Blocked-by:** Related NL-122
- **Plan ref:** —

### NL-245 Our invoices carry no route hints
- **Status:** fixed (0870ab1, 083a726, e08e20b, 2638eff)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Invoices/InvoiceService.cs`
- **Evidence:** `InvoiceService` writes no `r` fields, so a payer that is not our direct peer cannot reach us over private channels. Not needed for ABCD variant (c), where Alice pays Bob directly (reported by W1-B). Update (ABCD wave 2, `a5675cb`): `InvoiceService` adds up to 3 `r` hints for our private channels (largest peer balance first). Each hint carries the **peer's** stored `channel_update` policy (BOLT 11: the policy of the channel from the hint node towards the payee), not ours. It skips disabled updates, peers with no update, down links, and (for an amount) channels the peer can't spend it over. scid_alias channels use `RemoteAlias` (not checked against LND). Tests: `InvoiceRouteHintTests`. Update (gossip wave G-C, `4dc0f77`): `Node:Invoices:RouteHints` (`Auto` default, `Always`, `Never`): in `Auto` an invoice carries no `r` field once one of our announced channels is Open, its link up, its peer can send the amount and the channel has been in our own graph with both policies for `Node:Invoices:PublicChannelGracePeriod` (10 min); private-only nodes and nodes without a graph keep the hints (e08e20b, 2638eff). Goal proofs (c) and (e) pay our hint-free invoices from LND and CLN.
- **Fix sketch:** Add hints from our peers' stored `channel_update` policies (W1-E `ChannelUpdateService`) for private channels.
- **Blocks/Blocked-by:** Related NL-114, NL-099
- **Plan ref:** ONION M4-T6

### NL-516 An invoice with two `p` fields decoded with the first payment hash (duplicate payment hash, bolts#1357)
- **Status:** fixed (a0e88690)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFieldList.cs` (`FromBitReader`)
- **Evidence:** The decoder kept the first of several `p` (payment hash) fields and dropped the rest silently. The signature covers every field, so a payee can sign an invoice with two different payment hashes; a service that reads the invoice with one parser (last `p` wins) and pays it with another (first `p` wins) sees its own hash never settle and pays again (the exploit behind [lightning/bolts#1357](https://github.com/lightning/bolts/pull/1357), which adds "a reader MUST fail the payment if more than one `p` field is present" and "a payer MUST use the `p` field as the payment hash"). Inside the node there was no disagreement (every BOLT 11 decode goes through `Invoice.Decode`: `PaymentService.DecodeInvoice` -> `PaymentTarget.FromInvoice` for `payinvoice`; `InvoiceService` only encodes our own invoices), but an operator whose backend parses invoices with a last-wins library and pays through `payinvoice` was exposed, and so is any consumer of the `NLightning.Bolt11` package. Severity high rather than critical because it needs a second, last-wins parser outside NLightning.
- **Fix sketch:** Done: a second `p` field, identical or different, throws `ArgumentException` in `FromBitReader`, so `Invoice.Decode` fails with `InvoiceSerializationException`. Other duplicated non-repeatable fields still keep the first one (BOLT 11 has no rule for them; writers put the most-preferred first). The PR's `bolt11/invoice-test.json` (PR head `03ac8145`) is in `test/NLightning.Bolt11.Tests/Vectors/` and every entry is decoded by `Models/InvoiceSpecVectorTests` (15 valid decode, 13 invalid fail, incl. "Two distinct p fields" and "The same p field twice").
- **Blocks/Blocked-by:** —
- **Plan ref:** —

---

## Persistence

### NL-125 HTLCs don't reload: byte.Equals(enum) is always false
- **Status:** fixed (fd41d73, dd4a969)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:201,204,210,213,223`, `HtlcDbRepository.cs:63,70`
- **Evidence:** Offered/Fulfilled HTLCs are never restored; Expired/Failed all land in the remote lists. In-flight HTLCs are lost on restart.
- **Fix sketch:** Compare `== (byte)HtlcState.X` / `(byte)HtlcDirection.X`; add a Sqlite round-trip test.
- **Blocks/Blocked-by:** Blocks NL-031, NL-035
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT2 N1-T5

### NL-126 Remote funding pubkey replaced by the local one on reload
- **Status:** fixed (1e8c803)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:230-231`
- **Evidence:** `new FundingOutputInfo(..., localKeySet.FundingCompactPubKey, localKeySet.FundingCompactPubKey)`; after restart the funding script is wrong, so signatures and close fail.
- **Fix sketch:** Use `remoteKeySet.FundingCompactPubKey`; test.
- **Blocks/Blocked-by:** Blocks NL-035
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N1-T5

### NL-127 CommitmentNumber rebuilt with (local, remote) basepoints regardless of opener
- **Status:** fixed (1e8c803)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:237-239`
- **Evidence:** Obscuring factor is SHA256(opener || accepter); `ChannelFactory.cs:123` passes the remote (opener) basepoint first for non-initiator channels, but reload always passes local first → wrong obscured commitment numbers after restart.
- **Fix sketch:** Order by `IsInitiator`; test both roles.
- **Blocks/Blocked-by:** Blocks NL-035
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N1-T5

### NL-128 HtlcDbRepository never writes HTLC Signature
- **Status:** fixed (fd41d73)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/HtlcDbRepository.cs` (MapDomainToEntity ~81)
- **Evidence:** `Signature` is read (L102-104) but never set, so it's always null after reload.
- **Fix sketch:** Map it in `MapDomainToEntity`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N1-T5

### NL-129 SQL Server maps RemoteNodeId as varbinary(32) for a 33-byte key
- **Status:** fixed (01af924, d08db67)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/EntityConfiguration/Channel/ChannelEntityConfiguration.cs:83`
- **Evidence:** Uses `TransactionConstants.TxIdLength`; inserts truncate/fail on SQL Server. Migration `FixRemoteNodeIdLength` was generated offline; its later Designer was corrected in NL-208.
- **Fix sketch:** `CryptoConstants.CompactPubkeyLen` + SqlServer migration.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N1-T5

### NL-130 UtxoDbRepository.GetByIdAsync uses an anonymous-object key; mapper drops fields
- **Status:** fixed (069e273)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Bitcoin/UtxoDbRepository.cs:50`
- **Evidence:** `new { txId, index }` makes `PrimaryKeyHelper` throw; `LockedToChannelId`/`UsedInTransactionId` are not mapped.
- **Fix sketch:** Pass `(txId, index)` ValueTuple; map both fields.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-131 ChannelModel.ChangeAddress is never mapped
- **Status:** fixed (df8b1c9)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`
- **Evidence:** Lost on reload.
- **Fix sketch:** Map it (and configure the relationship, see NL-134).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-132 BaseDbRepository.Get pages before ordering
- **Status:** fixed (3ae8fa8)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/BaseDbRepository.cs:35-38`
- **Evidence:** Skip/Take applied before orderBy, so pages are unordered.
- **Fix sketch:** Order first.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-133 UnitOfWork.AddUtxo/TrySpendUtxo can desync memory and DB
- **Status:** fixed (eff727f)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/UnitOfWork.cs`
- **Evidence:** Memory is rolled back only on immediate exceptions (swallowed); a SaveChanges failure leaves them out of sync. Memory changes apply only after a successful save; a deposit and its spend in one unit of work are handled.
- **Fix sketch:** Apply memory changes after a successful save.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-134 Convention-based shadow FKs in the schema
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Persistence/EntityConfiguration/`
- **Evidence:** `PeerEntityNodeId`, `ChangeAddressIsChange`, `ChangeAddressAddressType` were created by convention.
- **Fix sketch:** Configure relationships explicitly (schema change in all 3 providers).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-135 Postgres always enables EnableSensitiveDataLogging
- **Status:** fixed (9e4aee8)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/DependencyInjection.cs:57,67`
- **Evidence:** Parameter values (keys, signatures, preimages later) can reach logs regardless of config. The design-time `NLightningContextFactory` still enables it for Postgres (dotnet ef only).
- **Fix sketch:** Gate on a config flag, default off.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-136 Shachain / per-commitment secrets are not persisted
- **Status:** fixed (e3145a5, e7ca51e, 9dfafda, a604dff)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs`, `src/NLightning.Infrastructure.Persistence/Entities/`
- **Evidence:** Received per-commitment secrets live in memory only; after restart revoked states can't be punished and reestablish can't prove state. Fixed: `RemoteShachainEntity` + `RemoteShachainDbRepository` (migration `AddRemoteShachain`, 3 providers; save idempotent within one unit of work), `SecretStorageService.Export/Load`, and the shachain is the only store of peer secrets (`LastRevealedPerCommitmentSecret` is obsolete, NL-238). Nothing saves or loads it at runtime yet: the revoke_and_ack handler (N6, NL-031) must call `IPerCommitmentSecretVerifier.VerifyAndStore` + `Export` + `SaveAsync` in one transition, and startup must `Load` it. Update (ABCD wave 0, `0b7e617`): `IChannelStateDbRepository.ApplyAsync` now saves `ChannelStateExtras.RemoteShachain` in the same save as the transition and `LoadAsync` returns it (4472a8b); the Application call sites (`Export` on RAA, `Load` at startup) are still ABCD W1-A/W2-A. Update (ABCD wave 1, `342d22e`): the runtime call sites exist: the revoke_and_ack handler loads the peer shachain from `IRemoteShachainDbRepository` per use (`ChannelStateTransitionService.LoadRemoteShachainAsync`), inserts the secret (B2-RAA-R02 failure → warning + close) and saves `Export()` as `ChannelStateExtras.RemoteShachain` in the same save as the transition; `ISecretStorageServiceFactory` is now registered (Application `SecretStorageServiceFactory`) (a604dff).
- **Fix sketch:** Table + repo for shachain; load in `SecretStorageService` (NL-066).
- **Blocks/Blocked-by:** Blocks NL-035, NL-094
- **Plan ref:** BOLT_COVERAGE roadmap step 6; BOLT2 N3-T4

### NL-137 [EPIC] Payment/forwarding persistence: shared secrets, circuits, invoices, attempts, replay set, SCID map
- **Status:** fixed (4472a8b, 2ede2ee, 899e36b, 4ae2eb3, 4ca9b56, ca87313, d1476a4, a2b57ff, 1a38360, 2d1fca6, a49e166)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/HtlcEntity.cs` (+ new entities)
- **Evidence:** No tables for per-HTLC onion shared secret, forwarding circuit, invoices/preimages, payment attempts, replay entries, SCID/alias→channel, or the channel graph. Update (ABCD wave 0, `0b7e617`): `HtlcEntity.OnionSharedSecret` with `IChannelStateDbRepository.Set/GetOnionSharedSecretAsync` (4472a8b); Domain models and repository ports for invoices, payments and forward circuits (2ede2ee, 1390027). Remaining: the tables and repositories (ABCD W1-C), replay set, SCID map. Update (ABCD wave 1, `342d22e`): migration `AddInvoicesPaymentsAndCircuits` (all three providers, no data step): `Invoices`, `Payments` + `PaymentHops` (route with each hop's Sphinx shared secret), `ForwardCircuits` (PK incoming channel/HTLC id, indexes on status and outgoing HTLC), `Htlcs.Origin*` (the `HtlcOrigin` of offered HTLCs, indexed for startup replay) and `Channels.MaxDustHtlcExposureMsat`; `InvoiceDbRepository`/`PaymentDbRepository`/`ForwardCircuitDbRepository` hang off `IUnitOfWork`, and `IChannelStateDbRepository.Set/Get/FindHtlcOrigin` (899e36b); the repositories are resolvable from the scope (4ae2eb3). Container round trips on Postgres and SQL Server migrate seeded pre-migration rows. Remaining: a persistent replay set (NL-078), an SCID/alias → ChannelId map, stored failure reasons for forwards, the graph; `OfferHtlcAsync` does not call `SetHtlcOriginAsync` yet (NL-250). Update (ABCD wave 2, `a5675cb`): `OfferHtlcAsync` stores the `HtlcOrigin` in the add's save (NL-250, 4ca9b56). The switch uses circuits for replay: a Pending circuit adopts the HTLC `FindHtlcsByOriginAsync` finds or is failed upstream, an Offered one resumes, and a Fulfilled/Failed circuit resolves its upstream HTLC from the live or archived outgoing record (ca87313, d1476a4). Payments store every hop's shared secret (6cb279f). No schema change was needed in wave 2. Remaining: persistent replay set (NL-078), stored failure reasons for forwards, the graph. Update (ABCD wave 6, `3ce3cad`): the persistent replay set is done (NL-078, a2b57ff). Remaining: stored failure reasons for forwards, an SCID/alias map, the graph, and per-part rows for MPP sends (NL-321). Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): closed: every table the epic named exists: per-HTLC shared secrets, `Invoices`, `Payments`/`PaymentHops`, `ForwardCircuits`, `OnionReplayEntries` (`NLightningDbContext.cs:61-65`), the SCID/alias map as `Channels.ShortChannelId` (NL-225, 2d1fca6) plus `ChannelLocalAliases` and `Channels.RemoteAlias` (NL-103, 1a38360), which `HtlcSwitch.ResolveOutgoingChannel` resolves from the reloaded channels (`HtlcSwitch.cs:1718`), and the graph (`GraphChannels` etc., `AddGossipGraph`, a49e166). Carried: per-part MPP send rows (NL-321) and the forward failure reason (NL-457, new).
- **Fix sketch:** Entities + 3 migrations each (§4.5 of the onion plan); the shared secret can alternatively be recomputed from `AddMessageBytes`.
- **Blocks/Blocked-by:** Blocks NL-073, NL-114, NL-078
- **Plan ref:** ONION M4-T7; ONION_ROUTING_PLAN §4.5; BOLT2 N5-T1, N8 (partial)

### NL-457 Forward circuits keep no failure reason
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Payment/ForwardCircuitEntity.cs`, `src/NLightning.Domain/Payments/Models/ForwardCircuitModel.cs` (`MarkFailed`)
- **Evidence:** Split out of NL-137 (ledger hygiene lh1): a Failed circuit stores only its status and `ResolvedAt`, not why the forward failed (policy refusal, downstream failure code, on-chain timeout), so an operator cannot list failed forwards with a reason and no forwarding statistics can be built from the table.
- **Fix sketch:** A nullable failure-code column (and the downstream failure source when known) written in the save that marks the circuit Failed, from a migration-owner lane; a `listforwards` IPC can read it.
- **Blocks/Blocked-by:** Split from NL-137
- **Plan ref:** —

### NL-458 Stale docs: root CLAUDE.md "Not implemented" line and the OptionAttributionData remark
- **Status:** open (partial: a3445f3f)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `CLAUDE.md` (project-goal paragraph "**Not implemented:**" and "Missing entirely" in Status & known gaps), `src/NLightning.Domain/Node/Options/FeatureOptions.cs:148`
- **Evidence:** Found by ledger hygiene lh1 (review of lane l5). The root `CLAUDE.md` says "**Not implemented:** anchors (O7), mempool (O8), route-blinding payloads, and BOLT 7 announcements/graph" and "Missing entirely: BOLT 7 announcements/graph", but BOLT 7 is done (NL-099), anchors O7 (NL-379..NL-381, O7-T4 d4cc3f8), mempool O8 (gossip wave G-A M1) and route blinding (NL-079) have landed. The `OptionAttributionData` XML remark still says "Defaults to No until error onions carry attribution data (onion M3b)", but M3b is done (NL-072); the real reason is the missing LND interop proof (NL-332). Update (wave lh1, `a6c633f9`): the root `CLAUDE.md` part is accurate at `a6c633f9`: the project-goal line reads "**Not implemented:** dual funding (NL-037); the long gossip soaks (NL-376) and a mainnet relay proof (NL-417)" (a3445f3f) and "Missing entirely" names only dual funding and the plugin loader. The `OptionAttributionData` remark in `FeatureOptions.cs` still cites onion M3b; a code lane changes it to cite NL-332.
- **Fix sketch:** The integrator rewrites the root `CLAUDE.md` project-goal and "Missing entirely" lines; a code lane changes the remark to cite NL-332.
- **Blocks/Blocked-by:** Related NL-099, NL-072, NL-332
- **Plan ref:** —

### NL-138 ChannelMemoryRepository.TryGetChannel returns the shared mutable model
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Memory/ChannelMemoryRepository.cs`
- **Evidence:** Callers must remember `UpdateChannel` for `OnChannelUpdated` to fire.
- **Fix sketch:** Return copies or make updates go through the repo.
- **Blocks/Blocked-by:** Related NL-033
- **Plan ref:** BOLT2 N5-T2

### NL-191 Channel balances are persisted as whole satoshis
- **Status:** fixed (2d1fca6, a30fc57)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelEntity.cs:96,101`, `EntityConfiguration/Channel/ChannelEntityConfiguration.cs:79-80`, `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:156-157`
- **Evidence:** `LocalBalanceSatoshis`/`RemoteBalanceSatoshis` (`decimal`, SqlServer `bigint`) are written from `LightningMoney.Satoshi`; the msat part is lost on every restart, so our commitment disagrees with the peer's and signatures fail. Fixed: `long` `LocalBalanceMsat`/`RemoteBalanceMsat` columns with a ×1000 data step (3 providers); `ChannelRoundTripTests` checks a 1 msat remainder.
- **Fix sketch:** `LocalBalanceMsat`/`RemoteBalanceMsat` columns with a `sats × 1000` data step (3 migrations).
- **Blocks/Blocked-by:** Blocks NL-031, NL-035
- **Plan ref:** BOLT2 N1-T5

### NL-192 Channel UpdateAsync pushes the HTLC child graph through DbSet.Update
- **Status:** fixed (a8a8578)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:39-43`, `src/NLightning.Infrastructure.Repositories/Database/BaseDbRepository.cs:103-133`
- **Evidence:** `UpdateAsync` maps the whole channel (with `Htlcs`) and calls `Update`, which falls back to `DbSet.Update(graph)`; new HTLC rows would be marked Modified and fail with a concurrency exception (inferred, not reproduced). Reproduced by the persist-channel reviewer (untracked context, new HTLC → `DbUpdateConcurrencyException`). `UpdateAsync` now syncs config/key sets/HTLCs/aliases by primary key (add/update/remove). Update (ABCD wave 0, `0b7e617`): `ChannelDbRepository.UpdateAsync` no longer writes HTLCs at all, nor the snapshot-owned scalars once a snapshot exists or is staged; HTLC, commitment and fee rows are written only by `ChannelStateDbRepository` (4472a8b, a8d1381).
- **Fix sketch:** Write HTLCs and commitment rows explicitly (upsert by PK) in a dedicated state repository; stop touching HTLCs in `UpdateAsync`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N5-T2

### NL-208 SqlServer WidenWatchedTransactionIndex Designer had a stale target model
- **Status:** fixed (d08db67)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence.SqlServer/Migrations/20260925144542_WidenWatchedTransactionIndex.Designer.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 6. `RemoteNodeId` was `varbinary(32)` in the Designer (generated without `FixRemoteNodeIdLength`); the snapshot was right. A regression theory now diffs each Designer against the previous one for all 3 providers.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-129, NL-102
- **Plan ref:** —

### NL-225 ChannelModel.ShortChannelId is not persisted
- **Status:** fixed (2d1fca6, a30fc57)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelEntity.cs`, `ChannelDbRepository.cs`
- **Evidence:** No SCID column or mapping; a reloaded channel has a default SCID until funding confirmation re-runs (flagged by the persistence follow-up, verified by grep). Fixed: nullable `Channels.ShortChannelId` (null until funding confirms), mapped both ways; round-trip tested.
- **Fix sketch:** Add a `ShortChannelId` column (3 migrations) and map it both ways.
- **Blocks/Blocked-by:** Related NL-103, NL-137
- **Plan ref:** BOLT2 N1-T5

### NL-232 The remote key set stores one per-commitment point; current and next remote points are not both persisted
- **Status:** fixed (4472a8b, a604dff)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelKeySetModel.cs` (`CurrentPerCommitmentCompactPoint`), `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelKeySetEntity.cs`
- **Evidence:** channel_ready replaces the peer's first point with its second (NL-051), so the point of the current remote commitment (0) is gone while `RemoteCommitmentNumber` is still 0; the factory now refuses to build a remote commitment with a point that belongs to another number (72a4ac6). The engine keeps `RemoteNextPerCommitmentPoint` in memory only. Plan §3.2 wants the remote current and next points both persisted (reported by the N1-T4 lane). Update (ABCD wave 0, `0b7e617`): the remote current point is stored on the remote commitment row and the next one in `Channels.RemoteNextPerCommitmentPoint`; both are restored into the engine, and the migration copies channel_ready's point for existing channels. Remaining (wiring, ABCD W1-A): `ChannelReadyMessageHandler` still overwrites the key set's first point, so the first snapshot must be created with `IChannelStateDbRepository.InitializeAsync` with both points before that happens. Update (ABCD wave 1, `342d22e`): `ChannelReadyMessageHandler` builds the first `ChannelCommitments` on the first channel_ready from the key set's current point and the message's next point, before the key set is overwritten, stages it with `InitializeAsync` in the same save and attaches it only after the save (a604dff). Channels that received channel_ready before this change have lost commitment 0's point and get no snapshot (NL-246).
- **Fix sketch:** Store remote current and next points (N5-T1 migration) and restore them into the engine.
- **Blocks/Blocked-by:** Part of NL-031; related NL-051, NL-188
- **Plan ref:** BOLT2 N5-T1

### NL-237 Postgres and SQL Server migration data steps never ran against existing rows
- **Status:** fixed (4472a8b, f2e1a4a, 79f7657)
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure.Persistence.{Postgres,SqlServer}/Migrations/*_{SplitChannelParams,StoreMsatBalancesAndShortChannelId,FlagInferredChannelParams,PersistCommitmentNumbers}.cs`
- **Evidence:** The hand-written `migrationBuilder.Sql` data steps (CAST, COALESCE, NOT EXISTS against Htlcs) are exercised with rows only on SQLite (`ChannelMigrationDataTests`); the Docker `PostgresTests`/`SqlServerTests` migrate an empty database (reported by the N1-T4/T5 lane). Update (ABCD wave 0, `0b7e617`): `CommitmentStateMigrationRoundTrip` (channels, key sets, legacy HTLCs, wallet addresses, UTXOs) and `LegacyChannelMigrationRoundTrip` (PersistCommitmentNumbers, SplitChannelParams, StoreMsat, FlagInferredChannelParams data steps) seed rows with raw SQL and migrate forward on SQLite (CI), Postgres and SQL Server (Docker, each in its own database).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N1-T5, N5-T1

### NL-238 Obsolete ChannelKeySet LastRevealedPerCommitmentSecret column is still mapped
- **Status:** fixed (4472a8b, a8d1381)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelKeySetModel.cs`, `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelKeySetEntity.cs`
- **Evidence:** Since NL-136 the shachain is the only store of the peer's secrets; `LastRevealedPerCommitmentSecret` is `[Obsolete]`, never written, but still round-trips a legacy column (9dfafda), which tests must suppress warnings for. Update (ABCD wave 0, `0b7e617`): `ChannelKeySetModel.LastRevealedPerCommitmentSecret` and its column are dropped in `AddCommitmentState`; `UpdateAsync` also keeps snapshot scalars staged by `InitializeAsync`/`ApplyAsync` in the same unit of work (a8d1381).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-136
- **Plan ref:** BOLT2 N5-T1

### NL-242 Reloaded commitment snapshots have no dust-exposure policy
- **Status:** fixed (899e36b, 98d19e6)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (`ToCommitmentParams`), `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`
- **Evidence:** `CommitmentParams.MaxDustHtlcExposureMsat` is not stored with the channel; `ChannelDbRepository` restores snapshots with null, so the dust-exposure check is off after every restart (latent: no config sets the policy yet; reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `ChannelStateDbRepository` stores `next.Params.MaxDustHtlcExposureMsat` in `Channels.MaxDustHtlcExposureMsat`, and `ChannelDbRepository` reloads with `CommitmentParams.FromChannel(model, stored value)`, which also keeps `HasInferredLimits` (899e36b); `ChannelModel.ToCommitmentParams` was removed (98d19e6). A reload now enforces the policy it was saved with; no node option sets one yet (NL-254).
- **Fix sketch:** Pass the node policy when loading (`IChannelStateDbRepository.LoadAsync` with node-policy params) or rebuild the params after load.
- **Blocks/Blocked-by:** Related NL-031
- **Plan ref:** BOLT2 N9-T3; ABCD W1-A/W2-A

### NL-243 Settled HTLC rows are never pruned and every channel load reads them all
- **Status:** fixed (899e36b, a02afa7, ca87313, d1476a4)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs` (`PruneSettledHtlcsAsync`), `ChannelDbRepository.GetByIdAsync`
- **Evidence:** Settled HTLCs stay as an archive with their final state so events can be re-derived after a crash (I8), but nothing calls `PruneSettledHtlcsAsync`; rows grow without limit and `GetByIdAsync` (often used as an existence check) loads them all (reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `IChannelDbRepository.ExistsAsync` is a cheap existence check (899e36b); `LocalOnlyHtlcSwitch` prunes a settled outgoing HTLC's archived row on `OutgoingHtlcSettled` in one save (a02afa7). Remaining: the W2-B `HtlcSwitch` must keep pruning, in an order that respects payments and circuits (it replaces `LocalOnlyHtlcSwitch`); existence checks still using `GetByIdAsync` should move to `ExistsAsync`. Update (ABCD wave 2, `a5675cb`): `HtlcSwitch` prunes a settled outgoing row once the upstream is resolved and the circuit or payment is handled. It prunes incoming final rows on the new `IncomingHtlcSettled` event (NL-256), and startup/link-up replay prunes rows left by older builds (ca87313, d1476a4). The ABCD and three-node proofs assert that no settled rows are left. Leftover, not tracked separately: some existence checks still use `GetByIdAsync` instead of `ExistsAsync`.
- **Fix sketch:** Prune once the switch has consumed an HTLC's settle event (W2-B); optionally add a cheap `ExistsAsync`.
- **Blocks/Blocked-by:** Related NL-137
- **Plan ref:** ABCD W1-A/W2-B

### NL-248 ChannelModel.ToCommitmentParams dropped HasInferredParams on reload
- **Status:** fixed (98d19e6)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (removed `ToCommitmentParams`), `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`
- **Evidence:** The repository reload used `ToCommitmentParams`, which dropped `ChannelParams.HasInferredParams` while `CommitmentParams.FromChannel` kept it, so a reloaded snapshot of an NL-194 migrated channel would enforce guessed limits (reported by W1-A). Fixed: reloads use `CommitmentParams.FromChannel(model, stored dust policy)` (899e36b) and `ToCommitmentParams` was removed (98d19e6).
- **Fix sketch:** Use `CommitmentParams.FromChannel` everywhere.
- **Blocks/Blocked-by:** Related NL-194, NL-242
- **Plan ref:** BOLT2 N5-T2

### NL-369 Value objects with an implicit byte[] conversion misbehave in conditional expressions
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Bitcoin/ValueObjects/TxId.cs` and the other byte-backed value objects (`ChannelId`, `CompactPubKey`, `Hash`)
- **Evidence:** While fixing NL-352, `cond ? txId : null` in `GraphStore` was typed `TxId` through the implicit `byte[]` conversion (CS8625 warning) and would have passed a null `byte[]`; fixed there with an explicit `(TxId?)` cast (016a523). The same trap applies to every value object that converts implicitly from `byte[]` (reported by lane M3).
- **Fix sketch:** Treat CS8625 on these expressions as an error, or add a nullable-aware analyzer check; cast explicitly in new code.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

---

### NL-460 Keysend custom records are stored in the BOLT 12 invoice bytes column
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Payment/{InvoiceDbRepository,PaymentDbRepository}.cs`
- **Evidence:** Lane l3 (wave lh1) stored the keysend custom records of `Invoices` and `Payments` rows in the `Bolt12InvoiceBytes` column (repository-level reuse, documented in the repositories' remarks) to avoid a schema change in a wave with another migration owner.
- **Fix sketch:** Add a dedicated `CustomRecords` column to `Invoices` and `Payments` (all three providers) with a data migration moving keysend rows.
- **Blocks/Blocked-by:** Follow-up of NL-459
- **Plan ref:** —

### NL-514 An inbound peer from a non-loopback address is saved with port 9735 and overwrites its dialable address
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`SaveInboundPeerAsync`)
- **Evidence:** After NL-497 only loopback connections keep a saved dialable address; an inbound peer from any other host is saved with port 9735 (not the port it listens on) and each inbound connection overwrites the row, so a peer listening elsewhere cannot be redialed after a restart (reported by lane SPR-D).
- **Fix sketch:** Keep a saved dialable row on inbound connections (update `LastSeenAt` only), and learn the listening address from the peer's `node_announcement` when it has one.
- **Blocks/Blocked-by:** Related NL-497, NL-201
- **Plan ref:** —

## Daemon / IPC / Client

### NL-139 `--cookie <path>` / `-c <path>` stores the flag itself as the path
- **Status:** fixed (55a8442)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs:88-95`
- **Evidence:** `cookiePath = args[i];` instead of `args[i + 1]`; only `--cookie=<path>` works.
- **Fix sketch:** Use the next arg; unit test.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-140 GetCommand skips the next arg after any option, including `--network=x`
- **Status:** fixed (55a8442, 4348947)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs:26-38`
- **Evidence:** `--network=regtest listpeers` silently runs `node-info`.
- **Fix sketch:** Only skip when the option takes a separate value.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-141 Minor CLI parsing gaps: `-?` unrecognized, NLTG_COOKIE only when NLTG_NETWORK unset
- **Status:** fixed (55a8442)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs`
- **Evidence:** See `src/NLightning.Client/CLAUDE.md` gotchas.
- **Fix sketch:** Recognize `-?`; read NLTG_COOKIE independently.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-142 Client indexes commandArgs without length checks; GetCookiePath runs before --help
- **Status:** fixed (994610e, 4348947)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Client/Program.cs`, `src/NLightning.Client/Handlers/OpenChannelMessageHandler.cs`
- **Evidence:** `connect` with no args prints an error then indexes `[0]`; `getaddress`/`openchannel` unchecked; missing `~/.nltg/<network>` makes even `--help` throw. `open-channel` blocks with `GetAwaiter().GetResult()`.
- **Fix sketch:** Validate lengths and return; resolve paths after the help check inside the try; await.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-143 GetAddressIpcResponse.AddressP2Wsh actually holds a P2WPKH address
- **Status:** fixed (8f42884)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Transport.Ipc/Responses/GetAddressIpcResponse.cs`, `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`
- **Evidence:** Printer labels it P2WSH; client defaults to P2Tr while the request DTO defaults to P2Wpkh.
- **Fix sketch:** Rename (append-only new key) and align defaults.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-144 OpenChannel IPC handlers put the exception message in the error code
- **Status:** fixed (e57eecd)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Ipc/Handlers/OpenChannelIpcHandler.cs:66`, `OpenChannelSubscriptionIpcHandler.cs:71`
- **Evidence:** `CreateErrorEnvelope(envelope, ce.Message, ce.Message)`.
- **Fix sketch:** Use `ce.ErrorCode`.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-145 SignedTransactionFormatter write/read mismatch and null dereference
- **Status:** fixed (232ccac)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Transport.Ipc/MessagePack/Formatters/SignedTransactionFormatter.cs:13-14`
- **Evidence:** Writes TxId with a bin header, reads it raw via `TxIdFormatter`; `value.TxId` dereferenced without null check (CS8602). No DTO uses it yet.
- **Fix sketch:** Use the same formatter both ways; handle null.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-146 Hash/TxId MessagePack formatters write raw bytes without a bin header
- **Status:** fixed (232ccac)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Transport.Ipc/MessagePack/Formatters/{HashFormatter,TxIdFormatter}.cs`
- **Evidence:** Output isn't valid MessagePack for non-.NET readers; default values write 0 bytes. Wire break between builds accepted as NL-210.
- **Fix sketch:** Write `bin 32` (IPC wire change: needs versioning).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-147 Daemon config args: `-n`/`-c` unmapped, bare flags eat the next arg, default network mismatch
- **Status:** fixed (a4ce86f, 45c1e74)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`, `src/NLightning.Daemon/Utilities/DaemonUtils.cs`
- **Evidence:** `AddCommandLine(args)` has no switch mappings; `--daemon --network regtest` loses the network; without `--config` the default dir is mainnet while the template says regtest. A config whose Node:Network differs from its directory now refuses to start.
- **Fix sketch:** Add switch mappings; normalize flags; make defaults agree.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-148 `--password` visible in the process list; IPC cookie never rotated
- **Status:** fixed (76b67cf, d91b1ad, 45c1e74, 7e1a44ae)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Program.cs`, `src/NLightning.Daemon/Services/Ipc/CookieFileAuthenticator.cs`
- **Evidence:** Wallet password on the command line leaks via `ps`; the cookie at `{configPath}/nltg.cookie` is static. Update (wave rf1, `wip/fafo` at `be9fd000`): the `--daemon` child now reads the password from its stdin (`--password-stdin`) and `NLTG_PASSWORD` is removed from its environment (SECURITY_REVIEW SR-11).
- **Fix sketch:** Read the password from stdin/file/env; rotate the cookie per start.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-149 NamedPipeIpcService.StopAsync throws if never started; Linux fork() after runtime start
- **Status:** fixed (f133f04, d91b1ad)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Services/Ipc/NamedPipeIpcService.cs`, `src/NLightning.Daemon/Utilities/DaemonUtils.cs`
- **Evidence:** See `src/NLightning.Daemon/CLAUDE.md` gotchas.
- **Fix sketch:** Null-guard StopAsync; daemonize via re-exec rather than fork.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-150 OpenChannel*IpcHandler resolves its handler with `as ConcreteType`
- **Status:** fixed (e57eecd)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Ipc/Handlers/OpenChannelIpcHandler.cs`, `OpenChannelSubscriptionIpcHandler.cs`
- **Evidence:** A decorator or substitute registration makes the cast return null.
- **Fix sketch:** Resolve the interface.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-151 Dead or unwired code (plugin loader and friends)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Services/PluginLoaderService.cs`, `Models/PluginEntry.cs`, `Helpers/AesGcmHelper.cs`, `Models/FeeRateCacheData.cs`, `src/NLightning.Daemon.Contracts/IControlClient.cs`, `src/NLightning.Domain/Node/Interfaces/IPeerFactory.cs`, `ISecretStorageServiceFactory.cs`, `IChannelKeySetFactory.cs`, `ISignatureValidator.cs`, `src/NLightning.Infrastructure.Bitcoin/Adapters/OutputAdapters/*`, `src/NLightning.Domain/Protocol/Enums/HtlcType.cs`
- **Evidence:** Never registered/called; `IDaemonContext` has no implementation.
- **Fix sketch:** Wire the plugin loader or delete; delete the rest.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-152 Missing IPC commands: close, list channels, invoice, pay, disconnect
- **Status:** fixed (5611156, 2ede2ee, 6cfbcd1, c10a78e, c50fc7b, f2f1ef6, 6d81ecd, c2ae40a, d60c4be5, aa9d67e0)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Client/Enums/ClientCommand.cs`
- **Evidence:** Only NodeInfo, ConnectPeer, ListPeers, GetAddress, WalletBalance, OpenChannel(+Subscription). A user can't close a channel or pay. Update: `ClientCommand.ListChannels = 8` with the `listchannels [peer_id]` CLI (5611156; local/remote commitment numbers since 3c625e1). Close, invoice, pay and disconnect remain. Update (ABCD wave 0, `0b7e617`): Domain side of invoice/payment IPC: `ClientCommand` CreateInvoice=9, PayInvoice=10, ListInvoices=11, ListPayments=12 (next free 13), their request/response DTOs, and `ChannelInfoClientResponse.IsReestablished`/`FeeBaseMsat`/`FeePpm` (2ede2ee, 1390027). Handlers, IPC registration and CLI remain (ABCD W1-D); close and disconnect remain. Update (ABCD wave 1, `342d22e`): CreateInvoice (9), PayInvoice (10), ListInvoices (11) and ListPayments (12) have MessagePack DTOs, daemon IPC handlers on a shared `ClientCommandIpcHandler` base ("not available" while a payment service is unregistered), scoped client handlers and CLI commands with snapshot-tested printers; `RoutingOptions` is bound and the default config writes `Node:EnableHtlcs` (true on regtest) and the `Node:Routing` defaults; hardening after review: exact error mapping, pipe cap 64, payinvoice wait ≤ 300 s, list count ≤ 1000 (6cfbcd1, c10a78e, c50fc7b). Remaining: close and disconnect commands; PayInvoice/ListPayments need `IPaymentService` (W2-C). Update (ABCD wave 2, `a5675cb`): `payinvoice` and `listpayments` work end to end now that `IPaymentService` is registered (f2f1ef6; `PaymentSendIpcTests`, ABCD c-send). The daemon binds `Node:Payments` (`PaymentSendOptions`). Remaining: close and disconnect commands. Update (ABCD wave 3, `c92d837`): `closechannel` (ClientCommand.CloseChannel = 13, next free 14) with daemon handler, CLI and `Node:Close` options (6d81ecd). Remaining: disconnect. Update (ABCD wave 5, `1a5ab49`): `forceclosechannel` (ClientCommand.ForceCloseChannel = 14) and `pendingsweeps` (PendingSweeps = 15, next free 16) with daemon handlers, CLI and printers (c2ae40a); `openchannel` takes an optional push amount as key 3 of the existing request (NL-301). Remaining: disconnect. Update (wave rf1, `wip/fafo` at `be9fd000`): `disconnect` (ClientCommand 24; refused with HTLCs in flight unless forced) and channel/HTLC counts in `listpeers`/`info` (lane R4); the integrator renumbered it from 21 (taken by the backup commands). Next free ClientCommand: 25.
- **Fix sketch:** Append commands per the recipe as each epic lands.
- **Blocks/Blocked-by:** Blocked-by NL-034, NL-114
- **Plan ref:** ONION M4-T6; BOLT2 N0-T8, N8-T2, N8-T3, N10-T3

### NL-153 BitcoinChainService ctor makes a blocking RPC call; key creation needs bitcoind
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs`
- **Evidence:** DI resolution fails when bitcoind is down.
- **Fix sketch:** Lazy/async init with retry.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-154 IPC framing implemented twice with a native-endian length prefix
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Services/Ipc/IpcFraming.cs`, `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`
- **Evidence:** Two copies must change together; host-endian prefix.
- **Fix sketch:** Share one implementation in Transport.Ipc; use big-endian.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-155 Daemon.Contracts and Daemon.Plugins target net9.0 with older packages
- **Status:** fixed (d35784f, 9e6f5ef, 07b383e)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon.Contracts/*.csproj`, `src/NLightning.Daemon.Plugins/*.csproj`
- **Evidence:** Pulls MessagePack 3.1.3 (vulnerable, see NL-170). Update (ABCD wave 4, `6b5d50e`): every project targets net10.0, plus net11.0 when built with SDK 11 (gated in `src/`/`test/Directory.Build.props`); `Daemon.Contracts`/`Daemon.Plugins` dropped net9.0; Microsoft.Extensions 10.0.12; CI installs SDK 10 and 11 (`docs/agents/NET11_PLAN.md`). Docker, NativeAOT and Wasm on SDK 11 are not verified (NL-300).
- **Fix sketch:** Decide on net10.0 or multi-target; bump packages.
- **Blocks/Blocked-by:** Related NL-170
- **Plan ref:** —

### NL-156 DI graph hand-maintained in NodeServiceExtensions and duplicated in Docker tests
- **Status:** fixed (fe4a551)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`, `test/NLightning.Integration.Tests/Docker/{AbcNetworkTests,ChannelOpeningFlowTests}.cs`
- **Evidence:** Layer services (`ChannelFactory`, validators, tx factories, signer) registered only in the daemon; Docker tests rebuild them by hand. Fixed: layer registrations live in `AddApplicationServices`/`AddBitcoinInfrastructure`; `AddNltgNodeServices` is the whole node graph, used by the daemon and by the Docker `NLightningTestNode`.
- **Fix sketch:** Move layer-owned registrations into each `DependencyInjection.cs`; share a test helper.
- **Blocks/Blocked-by:** —
- **Plan ref:** ONION_ROUTING_PLAN §9 risk 8; BOLT2 N0-T8

### NL-157 Application references Infrastructure; PeerService lives in Infrastructure
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/NLightning.Application.csproj`, `src/NLightning.Infrastructure/Node/Services/PeerService.cs:17`
- **Evidence:** Handlers use `IBlockchainMonitor`, tx builders and `ITcpService` directly; `TODO: Eventually move this to the Application layer`.
- **Fix sketch:** Move ports to Domain incrementally; don't add new references.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-210 IPC wire format changed: old clients and new daemons cannot talk
- **Status:** wontfix
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Transport.Ipc/MessagePack/Formatters/{HashFormatter,TxIdFormatter}.cs`, MessagePack package version
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 8. Accepted break: Hash/TxId now write MessagePack `bin` (NL-146) and MessagePack went 3.1.3/3.1.4 → 3.1.10. Client and daemon from one build match. Documented in the Transport.Ipc CLAUDE.md.
- **Fix sketch:** None (accepted). Version the IPC envelope before the next wire change.
- **Blocks/Blocked-by:** Related NL-146, NL-154
- **Plan ref:** —

### NL-241 listchannels counts pending HTLCs from legacy collections that are empty after reload
- **Status:** fixed (e30a845)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/ListChannelsClientHandler.cs:81-82`
- **Evidence:** `OfferedHtlcCount`/`ReceivedHtlcCount` read `ChannelModel.LocalOfferedHtlcs`/`RemoteOfferedHtlcs`, which are no longer persisted since the commitment snapshot (4472a8b) and are always empty after a reload; the ABCD test asserts zero pending HTLCs through this handler (reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `ListChannelsClientHandler` counts the snapshot's non-final HTLCs per direction (legacy lists only for a channel without a snapshot) and also reports the fee policy, `IsReestablished` (false until N7) and `DataLossDetected` from the model.
- **Fix sketch:** Count from `ChannelModel.Commitments.Htlcs` (non-final states per direction).
- **Blocks/Blocked-by:** Related NL-152
- **Plan ref:** ABCD W1-D

### NL-291 No signet or custom signet (Mutinynet) network; testnet chain hash garbled
- **Status:** fixed (e7d9037, 3350020, 959f310, 53accb1, 960cf05)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/ChainConstants.cs`, `src/NLightning.Domain/Protocol/ValueObjects/BitcoinNetwork.cs`, `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`
- **Evidence:** The node knew only mainnet/testnet/regtest; an unknown network fell back to mainnet in the wallet services; the testnet chain hash constant was wrong. Fixed in W4-D: signet chain hash, static custom-signet registration (`Node:CustomSignet`), fail-fast `BitcoinNetwork.Resolve`, signet/Mutinynet daemon defaults with a per-network fee source, `NBitcoinNetworkResolver.ToNBitcoinNetwork()` with no mainnet fallback (e7d9037, 3350020, 959f310); integration binds through `Resolve` and lists signet/mutinynet in the usage text (53accb1, 960cf05). How to run on Mutinynet: `docs/agents/MUTINYNET.md`. The live Mutinynet smoke test is not done; per-call network resolution remains in places (NL-298).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-298
- **Plan ref:** ABCD wave 4 W4-D

### NL-295 The open-channel subscription misses a V1FundingSigned reached before it subscribes
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/OpenChannelClientSubscriptionHandler.cs`
- **Evidence:** The handler only reacts to channel updates raised after it subscribes, so a channel that reaches V1FundingSigned first is never reported to the client. Seen once in the O0 Docker smoke before 5bedf44 moved the update after the publish (reported by W4-A).
- **Fix sketch:** Check the channel's current state right after subscribing.
- **Blocks/Blocked-by:** Related NL-263
- **Plan ref:** —

### NL-298 Network resolution is not unified: builders, signer and some Application code resolve per call
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** Infrastructure.Bitcoin builders, `LocalLightningSigner`, `SecureKeyManager`, `ShutdownScriptProvider`, `FallbackAddressTaggedField` (`Network.GetNetwork(name)`); `ChannelFailureService` (falls back to `Network.RegTest`); `ChannelManager`, `ChannelCloseCoordinator` (`Network.Main` for parsing)
- **Evidence:** Only the `Wallet/` services use `NBitcoinNetworkResolver.ToNBitcoinNetwork()`; `GetNetwork` knows `signet` and throws otherwise, so a custom signet works, but the fallbacks hide misconfiguration (reported by W4-D, `docs/agents/MUTINYNET.md` known gaps).
- **Fix sketch:** Resolve every NBitcoin network through `ToNBitcoinNetwork()` and remove the fallbacks.
- **Blocks/Blocked-by:** Related NL-291
- **Plan ref:** —

### NL-301 openchannel cannot push an amount to the peer
- **Status:** fixed (d363373, ce091a1)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Client/`, `OpenChannelIpcRequest`, `OpenChannelIpcHandler`, `IChannelFactory`
- **Evidence:** `openchannel <node> <sats> [push_sats]` now carries an optional push (key 3 of `OpenChannelIpcRequest`, absent = no push, so no new `ClientCommand` and older clients still work) to the channel factory (d363373); amounts above 21M BTC are a usage error instead of an OverflowException (ce091a1). Used by the Mutinynet smoke (W5-E).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-152
- **Plan ref:** `MUTINYNET.md`

### NL-303 The CLI prints block hashes and funding txids in internal byte order
- **Status:** fixed (83d48c6, c2b36a0)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Client/` printers (`info`, `openchannel`, `listchannels`)
- **Evidence:** `info` prints the best block hash and `openchannel`/`listchannels` the funding txid reversed (`bf2416f6...0000` for block `00000284...24bf`; `dc37ddc9...eb17:0` for funding tx `17eb2731...37dc`); the closing txid is printed in display order (W5-E, Mutinynet). Display only; same class as NL-275. Update (wave O7, `8364a01`): the CLI prints funding txids and the best block hash in display order, and a missing funding output index as a dash (lane X5, 83d48c6, c2b36a0).
- **Fix sketch:** Print hashes and txids in display order everywhere (one helper).
- **Blocks/Blocked-by:** Related NL-275
- **Plan ref:** `MUTINYNET.md` live smoke

### NL-306 Relative database and log paths resolve against the daemon's working directory
- **Status:** open (partial: 5f4e0df)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs` (template `Data Source=nltg.db`, Serilog `logs/log-.txt`)
- **Evidence:** The template's relative paths land in whatever directory the daemon is started from, not `~/.nltg/<network>` (the Mutinynet run first wrote its database and logs into the repo checkout, W5-E). `scripts/mutinynet/start-daemon.sh` now `cd`s into the configuration directory (5f4e0df); the daemon itself does not anchor them.
- **Fix sketch:** Resolve relative paths in the configuration against the configuration directory at startup.
- **Blocks/Blocked-by:** —
- **Plan ref:** `MUTINYNET.md`

### NL-441 No on-chain send (withdraw) command
- **Status:** fixed (4446f400, d2fdae94, 42011660, fc686ff0)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/WalletSpendService.cs`, `src/NLightning.Daemon/Ipc/Handlers/WithdrawIpcHandler.cs`, `ClientCommand.Withdraw` (25)
- **Evidence:** Coins left in the wallet after a close could only leave through another channel open (`MAINNET_CANARY_RUNBOOK.md` known gap, `REMAINING_WORK.md` "Wallet features"). Update (wave M6, lane W1, `wip/fafo` at `641a5fff`; the commits cite "NL-new"): `withdraw <address> <amount_sat|all> [--sat-per-vb N]` (ClientCommand 25). `WalletSpendService` checks the address network, reserves confirmed wallet outputs through `IFeeInputSelector` (never channel-locked or pending-broadcast outputs), keeps the anchors reserve, signs with `SignWalletTransaction`, re-verifies every input and stores a `WalletSend` `BroadcastTransactions` row before sending (rebroadcast until confirmed); dust, out-of-bounds fee rates and a halted chain are refused (4446f400). Withdrawals run one at a time and release orphaned withdraw reservations first, also at startup through `ReleaseOrphanedReservationsAsync`; `--sat-per-vb 1` maps to 253 sat/kw; the anchors reserve is read under its admission gate (42011660). Docker `WithdrawFlowTests` 2/2 against LND (P2WPKH and P2TR outputs, `all` with the reserve kept) (d2fdae94). Wired by `AddWithdrawIpcServices()` in `AddNltgNodeServices`, startup release in `NltgDaemonService` and `NLightningTestNode` (fc686ff0). Left: coin control and consolidation (`REMAINING_WORK.md`).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** `REMAINING_WORK.md` "Payments and wallet"

---

### NL-506 A splice stopped at CommitmentSigned was reported as in progress and the CLI exited 1
- **Status:** fixed (d34ab24b, 9d017a7f)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/SpliceClientHandlers.cs`, `src/NLightning.Client/Printers/SplicePrinter.cs`, `SpliceIpcResponse` (key 5)
- **Evidence:** A `splicein`/`spliceout` whose peer disconnected before `tx_signatures` is kept and completes on the reconnection, but the answer printed 'Splice in progress' with a raw 'stopped before tx_signatures' reason and the CLI exited 1. d34ab24b names the splice (display txid) and says it completes when the peer reconnects, exit 0; 9d017a7f moves that text to `SpliceIpcResponse.Note` (key 5) / `SpliceClientResponse.Note`, leaving `FailureReason` null (lane SPR-B cited it as NL-487, which is the retired SCID map).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SPR-T3

### NL-515 The daemon config template does not write the splice RBF settings
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs` (`Splice` block)
- **Evidence:** The template writes only `RotateFundingKey`, `MinFeeratePerKw`, `MaxFeeratePerKw` and `RequireConfirmedInputs`; the wave spr settings (`MaxRbfAttempts`, `MinRbfInterval`, `MaxRbfFeeShareSatoshis`, `AutoBumpAfterBlocks`, `AutoBumpMaxFeeratePerKw`, `AutoBumpMaxFeeSat`, `AutoBumpMaxWait`) take their code defaults and an operator has to know their names (reported by lanes SPR-A, SPR-B and the integrator).
- **Update (wave d13, `f30f3be3`):** the template now writes `Splice:MinRbfBlocks` (1, NL-520), checked by `Daemon.Tests/Extensions/SpliceConfigTemplateTests`; the other RBF and auto-bump keys are still missing (`MinRbfInterval` is deliberately left out: it is an optional override).
- **Fix sketch:** Add them to the template with their defaults (`AutoBumpAfterBlocks` 0 = off) and to the config template test.
- **Blocks/Blocked-by:** Related NL-489
- **Plan ref:** —

### NL-519 Logs and exception messages printed txids in internal byte order
- **Status:** fixed (8e852a18)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/ValueObjects/TxId.cs` (`ToString`)
- **Evidence:** Mutinynet day-0 rehearsal (2026-09-28): the dual-funded funding tx that bitcoind and mutinynet.com call `5f92ad89...e997` was logged by `DualFundedOpenService`/`LocalLightningSigner`/`SpliceService`/`InteractiveTxDriver`/`BlockchainMonitorService` as `97e94bb8...925f` (the bytes reversed), while other lines of the same services (`BitcoinChainService`, the on-chain `Display` helpers) and the CLI (NL-303) printed the display order; one monitor line mixed both. Every structured-log and interpolated `TxId` went through `TxId.ToString()`, the hex of the internal order. An operator copying a txid from a log into an explorer found nothing.
- **Fix sketch:** Done: `TxId.ToString()` prints the display order (bitcoind, explorers); `ToInternalHex()` keeps the internal hex for the three deterministic sort keys that used `ToString()` (`FeeInputSelector`, `OnchainResolutionDbRepository`, `RevokedCommitmentDbRepository`), so selection and ordering are unchanged. No code parses a `TxId` string back. Proof: `TxIdDisplayTests.Given_TxIdInInternalOrder_When_ToString_Then_PrintsTheDisplayOrderAsBitcoindAndExplorers` (the day-0 funding txid). `Hash.ToString()` (block hashes) still prints the internal order.
- **Blocks/Blocked-by:** Related NL-303
- **Plan ref:** `DAY0_RUNBOOK.md` §5

### NL-526 ClnOfferReceiveTests assert the exact amount but our BOLT 12 invoice paths now carry a dummy hop
- **Status:** fixed (51b40ea8)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnOfferReceiveTests.cs` (`AssertSettledAsync`), `src/NLightning.Application/Payments/` (invoice `AmountReceived` with dummy hops)
- **Evidence:** Lane rbf, on `wip/fafo` `2b5dffd2` plus the rbf commits (nothing of which touches payments or offers): 3 of the 4 cases fail, with the class alone as in the full run: our invoice records 10,000,112 msat received for a 10,000,000 msat invoice (3,000,035 for 3,000,000, 7,777,088 for 7,777,000) and our balance grows by as much. Our invoice path (CLN introduced, then us and one dummy hop, NL-440's `Node:Invoices:BlindedPathDummyHops` default 1) advertises payinfo 3 msat + 21 ppm; CLN (`xpay`, `amount_sent_msat` 10,000,000) pays it, the dummy hop's share arrives in our HTLC and we keep it. BOLT 4 lets the final node accept more than the amount, so the payments are fine; the proof's "exactly the amount" no longer holds since NL-440 (merged just before this lane).
- **Fix sketch:** Owner decision: either the invoice's `AmountReceived` is the final payload's `amt_to_forward` (excluding what our own dummy hops keep) or the proof accepts amount + our dummy hops' fee; then fix the other side and rerun the class.
- **Update (lane dfrbf, owner decision 2026-09-28):** Dummy hops keep realistic fees (each takes the introduction node's `payment_relay`, as NL-440 built them; never zero-fee dummies), and the records store what we really received: the invoice's `AmountReceived` is the settled HTLC set's `HtlcSum` (`HtlcSwitch.SettleWithAsync`), i.e. the HTLCs' `amount_msat` with our dummy hops' share, for BOLT 11, BOLT 12 and keysend alike, and `listinvoices` shows it (`InvoiceInfoIpcResponse.AmountReceived`, the client's "Received (msat)"): no product change was needed. The final-hop checks already see through our dummy hops (`IncomingBlindedHop.ReceivedAmount`/`ReceivedCltvExpiry`: `amt_to_forward` against the amount after every dummy hop's `payment_relay`, as if they were other nodes; the "below the amount" and "more than twice" checks use `total_amount_msat`, which dummy hops do not change), so dummy fees neither fail an exact payment nor count towards the 2x limit. `ClnOfferReceiveTests` now asserts `amount <= received <= bound`, the bound being what a BOLT 4 introduction node forwards (`((amount_msat - fee_base_msat) * 1000000 + 1000000 + fee_proportional_millionths - 1) / (1000000 + fee_proportional_millionths)` with CLN's policy towards us) when the payer pays exactly the path's `blinded_payinfo` fee, computed from our stored invoice's paths (each path's pay info checked to aggregate CLN's policy once per relaying hop), and our balance growing by exactly the recorded amount. Measured: 10,000,112 / 7,777,088 / 3,000,035 msat for 10,000,000 / 7,777,000 / 3,000,000 msat, each exactly at its bound; class 4/4. The other Docker assertions of an exact `AmountReceived` are BOLT 11 invoices without blinded paths (`Node:Invoices:BlindedPaths` is off by default); `Bolt11BlindedInvoiceTests.Given_CarolsBlindedBolt11Invoice_*` now pins the in-process case (Carol's record equals her HTLC's amount, above the amount by at most the path fee less Bob's).
- **Blocks/Blocked-by:** Follow-up of NL-440
- **Plan ref:** `BOLT12_PLAN.md` Proof B12

### NL-527 A peer's tx_abort of our pending tx_init_rbf never reaches the dual-funding host
- **Status:** fixed (0d15bdef)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/InteractiveTx/InteractiveTxDriver.cs` (`ReceiveAbortAsync` without a negotiation, `ReceiveAckRbfAsync` catch branches), `src/NLightning.Application/Channels/DualFunding/DualFundedOpenService.cs` (`BumpAsync`)
- **Evidence:** Found by lane rbf reading the driver for NL-521: when the peer refuses our `tx_init_rbf` with `tx_abort` (before any attempt exists), or our own attempt cannot be built after its `tx_ack_rbf`, the driver clears `PendingRbf` without `IInteractiveTxHost.OnAbortedAsync`, so `DualFundedOpenService.BumpAsync` waits for its `OpenTimeout` (2 min) instead of returning the peer's reason. The shares an RBF took are dropped at the next `BumpAsync`/`DecideRbfAsync` (`RestoreShares`), so nothing is left inconsistent. The splice path handles its own `tx_abort`s and is not affected. A `tx_ack_rbf` whose contribution we refuse does call `OnAbortedAsync` (NL-521).
- **Fix sketch:** Tell the host from the driver's no-negotiation `tx_abort` branch when `PendingRbf` was set (after checking the splice host's `OnAbortedAsync` stays idempotent), and from the two `RejectRbf` catch branches of `ReceiveAckRbfAsync`; test with the dual-funded harness.
- **Update (lane dfrbf, branch `wip/fafo-dfrbf`):** Fixed with a new host callback, `IInteractiveTxHost.OnRbfRequestEndedAsync` (default: nothing), which the driver calls whenever our pending `tx_init_rbf` ends before an attempt exists: the peer's `tx_abort` (no-negotiation branch, with the peer's text), a simultaneous `tx_init_rbf` (ours withdrawn), our attempt that cannot be funded or built after `tx_ack_rbf`, and a disconnection. `DualFundHost` maps it to `DualFundedOpenService.OnAbortedAsync`, so `BumpAsync` returns the reason at once and the RBF's shares are put back. The splice host keeps the default: `SpliceService.BumpAsync` already ends with the quiescence, which the driver terminates on the peer's `tx_abort` and which a disconnection ends (`SpliceRbfHarnessTests.Given_TheDefaultBlockRule_*` covers the refused bump); calling `OnSpliceAbortedAsync` there would end the negotiation before `OnQuiescenceEnded` could release its wallet contribution. Proof: `DualFundRbfEndTests` (the peer's `tx_abort` returns "not today" within 10 s of a 60 s timeout and a later bump completes; a disconnection returns "disconnected").
- **Blocks/Blocked-by:** Part of NL-037; related NL-521
- **Plan ref:** `SPLICING_PLAN.md` wave DF

### NL-528 An RBF'd dual-funded open whose earlier attempt confirms is not followed
- **Status:** fixed (62a43729)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/DualFunding/DualFundedOpenService.cs`, `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`ConfirmFundingAsync`, `ConfirmUnconfirmedChannels`), `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.Splicing.cs` (`LockFunding`), `src/NLightning.Application/Onchain/Reorg/DiscardedSpliceReservations.cs`, table `InteractiveTxSessions`
- **Evidence:** Lane dfrbf, auditing `Node:DualFund:AllowRbf` before turning it on (owner decision 2026-09-28): BOLT 2 "Fee bumping" makes every attempt of an RBF double-spend the others, and any fully signed one may be mined (the peer holds its witnesses and can broadcast it; a miner may have only seen it). The channel kept the latest attempt's outpoint and the peer's commitment signature only (`LastReceivedSignature` overwritten at each attempt, the old one nowhere else, as the service's own "known limits" said), and `ChannelManager.ConfirmFundingAsync` ignored which transaction confirmed: an earlier attempt confirming moved the channel to `ReadyForUs` on an outpoint that never confirms, with the SCID of the other transaction and a first commitment signed for the wrong funding (no unilateral close: the funds depend on the peer's cooperation). Related gaps found on the way: the peer's `channel_ready` arriving before our own confirmation built the first commitment state on whatever attempt the channel was on (the peer does not say which one confirmed); a restart during an RBF attempt lost the signed attempt's copy (`LastSignedFunding`, memory only), so an attempt that then ended unsigned left the channel on the unsigned outpoint; the signer could not move back to a replaced (earlier) attempt; an RBF attempt that added wallet inputs the confirmed attempt does not have (an accepter that funded nothing at the open contributing in the RBF, NL-521) kept them reserved forever; RBF of a public dual-funded open was refused over the signer's outpoint (obsolete since NL-521); the peer's `channel_ready` did not abandon a running RBF (BOLT 2: "If an RBF negotiation is in progress when a channel_ready message is exchanged, the negotiation must be abandoned").
- **Fix:** Migration `AddDualFundAttempts` (all three providers): `InteractiveTxSessions.LocalFundingSatoshis` (our share of the funding output, from the host's `SharedFundingSpec`, written by the driver when the negotiation is constructed) and `TheirCommitmentSignature` (the peer's signature of our first commitment, handed to `IInteractiveTxDriver.OnCommitmentSignedReceivedAsync` by the dual-funding service and saved with the attempt). `DualFundedOpenService.OnFundingConfirmedAsync` (called by `ChannelManager.ConfirmFundingAsync` under the channel's lock before the confirmation is applied) abandons a running RBF attempt and, when another fully signed attempt than the channel's confirmed, moves the channel to it (outpoint, capacity, balances, reserve and in-flight limit from its stored share, the stored peer signature, our signature again, the signer's current funding through `MoveSignerToFunding`, its `Funding` broadcast row pending and the other attempts' rows replaced) in its own save; an attempt it cannot follow (no stored signature: a row of an older build) is logged critical and the confirmation ignored. `LocalLightningSigner.LockFunding` accepts a replaced `Initial` funding on key index 0 while the current one is an unconfirmed initial funding and the channel never moved past commitment 0 (only a dual-funded open's attempts). `ChannelReadyMessageHandler` hands the peer's `channel_ready` of a v2 channel in `V1FundingSigned` to `TryDeferChannelReadyAsync`, which abandons a running RBF and keeps the message (memory) when the open has several signed attempts; `ConfirmFundingAsync` applies it after our confirmation (`ReadyForUs` then `Open`, the first commitment state built on the followed attempt); a deferred `channel_ready` counts as received for the RBF refusal. `ConfirmUnconfirmedChannels` also finds a completed watch of an earlier attempt. `GetOrLoadAsync` rebuilds `LastSignedFunding` (and the shares) from the latest signed row after a restart mid-RBF, and `RestoreLastSignedFundingAsync` moves the signer back and re-signs. `DiscardedSpliceReservations` releases the wallet inputs of the losing attempts once the confirmed funding is irrevocable (`ReleaseDiscardedAsync` with the confirmed attempt's inputs kept) and settles their rows. A public dual-funded open may be bumped. With these, `Node:DualFund:AllowRbf` defaults to true. Proofs: `DualFundRbfFollowTests` (the first attempt confirming after a bump that changed capacity and balances, followed in both roles with payments both ways; the peer's early `channel_ready` deferred then applied on the followed attempt; a restart during an RBF attempt the peer forgot, back on the signed funding from the rows and opened; three attempts through restarts of both nodes, every signed row with its peer signature, the latest confirmed), `DualFundSafetyTests` (RBF of a public open, default on, off refuses both ways), `OnchainSpliceDay0Tests` (NL-528 region: losing attempt's inputs released at the irrevocable depth only, nothing while no attempt confirmed), `InteractiveTxSessionSchemaRoundTrip` (the new columns).
- **Update (lane dfrbf, f4744680, bae1e17f):** `bumpopen` (IPC 38) exposes the opener's bump. Proven against CLN v26.06.8 (`ClnDualFundTests`): our bump through `bumpopen` followed by CLN; CLN's own RBF of its open to us (`openchannel_bump`) followed with our rebuilt contribution; after our bump the **first** attempt mined (`generateblock` with its raw transaction) and both nodes followed it to `CHANNELD_NORMAL`/Open with payments both ways. Between two NLightning nodes: `Day0FlowTests` step 1 (b) bumps the public dual-funded open before it confirms, and the replacement is announced and used. Follow-ups NL-529, NL-530.
- **Blocks/Blocked-by:** Part of NL-037; related NL-521, NL-527, NL-492
- **Plan ref:** `SPLICING_PLAN.md` wave DF, "Lane dfrbf record"

### NL-529 The funding watches of a dual-funded open's losing RBF attempts are never removed
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/DualFunding/DualFundedOpenService.cs` (`StageFundingWatchesAsync`, `OnFundingConfirmedAsync`), `IWatchedTransactionDbRepository`, `IBlockchainMonitor`
- **Evidence:** Lane dfrbf (NL-528): every attempt's `Funding` watched transaction and `FundingOutput` watched outpoint are stored from our `commitment_signed` on, so whichever attempt confirms is seen. Once one confirmed, the others double-spend it and can never confirm or be spent, but their watches stay pending: the chain monitor loads and checks them on every block and start, forever (a handful of rows per RBF'd open; no effect on the channel, which follows the confirmed attempt). `IWatchedTransactionDbRepository` has no delete, and the monitor has `StopWatchingOutpointSpend` only for outpoints.
- **Fix sketch:** At the irrevocable depth of the confirmed funding (where `DiscardedSpliceReservations` releases the losing attempts' inputs), delete or complete the losing attempts' watched transactions and stop watching their funding outpoints (a repository delete plus the monitor's untrack), in the same round that settles their `InteractiveTxSessions` rows.
- **Blocks/Blocked-by:** Follow-up of NL-528
- **Plan ref:** `SPLICING_PLAN.md` "Lane dfrbf record"

### NL-530 The accepter of a dual-funded open cannot start an RBF of it
- **Status:** fixed (0b9f67b8)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/DualFunding/DualFundedOpenService.cs` (`BumpAsync`: "We are not the opener")
- **Evidence:** BOLT 2 "Fee bumping": the sender of `tx_init_rbf` "MAY be either the *initiator* or the *accepter*"; an accepter that sends it becomes the interactive-tx initiator, "MUST send `tx_add_output` for the channel output" and "MUST pay the fees for the shared transaction fields". `BumpAsync` (and `bumpopen`) refuse on a channel we accepted. A peer's RBF is followed in either role (lane dfrbf proofs: CLN's `openchannel_bump` of its own open with our contribution), so an accepter whose open is stuck can only wait for the opener to bump it.
- **Fix sketch:** Let `BumpAsync` run as the accepter: our contribution rebuilt as interactive-tx initiator (the funding output and the common fields charged to us, new wallet inputs when our previous share cannot pay them, or none when we contributed nothing and the wallet cannot), the opener's share from its `tx_ack_rbf`; a harness test in both roles and a CLN proof (CLN's funder plugin answers a peer's `tx_init_rbf` through its `rbf_channel` hook).
- **Fix:** Lane accrbf (owner decision 2026-09-28). `DualFundedOpenService.BumpAsync` runs in either role: the `tx_init_rbf` sender is the interactive-tx initiator of the new attempt (BOLT 2 "Fee bumping": it adds the funding output and pays the common fields; the opener still pays the first commitment's fee, `GetRbfShareViolation`), the opener must still contribute, the accepter may pass 0. Our contribution (`CreateInitiatorRbfContributionAsync`): the previous attempt's inputs re-added with the change paying the initiator's weight (`DualFundingRules.RebuildContributionForFeerate(..., isInitiator: true, ...)`, IT-RBF-01; refused when those inputs cannot pay it), or, for an accepter with none of its inputs in the earlier attempts (the opener's re-added inputs double-spend them), fresh wallet inputs for its share plus `GetOpenerExtraWeight`, also for a share of 0 (`InteractiveTxContributionRequest.FundWeightWithoutAmount`, honoured by `WalletInteractiveTxContributor`). Such a fresh reservation (`DualFundNegotiation.FreshRbfContribution`) goes back when the request ends before an attempt exists (the peer's `tx_abort`, a disconnection, a refused `tx_ack_rbf`); the driver releases an attempt's own. Everything after the commitment step is lane dfrbf's (per-attempt rows, the attempt that confirms followed, restart mid-RBF, losing inputs released at the irrevocable depth). The peer's side (the opener receiving the accepter's `tx_init_rbf`) was already role-agnostic (`DecideRbfAsync` rebuilds as non-initiator). `bumpopen` (IPC 38) works for either role and takes `--contribution-sat 0`. Proofs: `DualFundAccepterRbfTests` (11: the accepter bumps as initiator with the funding output its own and both sides' inputs re-added, a fresh contribution, a fee-only bump with share 0, the first attempt confirming after the accepter's bump followed, the accepter bumping after its restart, a restart during its attempt back on the signed funding, and refusals: no funds, `channel_ready` exchanged, below the IT-RBF-01 floor, RBF off, the opener's `tx_abort` releasing the fresh reservation), `WalletInteractiveTxContributorTests` (fee-only contribution), `BumpOpenIpcHandlerTests` (0 passes the IPC and CLI checks). CLN v26.06.8 refuses an accepter's RBF: dualopend answers our `tx_init_rbf` with `tx_abort` "Only the channel initiator is allowed to initiate RBF" (BOLT 2: the recipient "MAY fail the negotiation for any reason"); `ClnDualFundTests.Given_ClnsUnconfirmedDualFundedOpen_When_WeBumpItAsTheAccepter_*` pins that, the open staying on its first funding and confirming. Between two NLightning nodes: `Day0FlowTests` step 1 (c), B (the accepter) bumps the public open after A's bump and B's attempt is the one that confirms and is announced.
- **Blocks/Blocked-by:** Part of NL-037; related NL-528
- **Plan ref:** `SPLICING_PLAN.md` "Lane dfrbf record"

### NL-531 ClnCloseTests' CLN-funded close hangs when it runs after ClnDualFundTests in one process
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnCloseTests.cs` (`Given_ChannelClnFunded_When_ClnCloses_Then_WeAnswerItsFeeRangeAndBothClose`), `ClnDualFundTests.cs`
- **Evidence:** Lane dfrbf, full CLN runs: the first two (before the fix below) failed 32 and 38 of 72 with CLN's "feerate_per_kw 2500 below minimum 2501/2502": the dual-funding class (two new proofs) had mined enough transactions paid at bitcoind's wallet default to raise the shared bitcoind's estimate, which CLN's `min_acceptable` follows, above our 2,500 sat/kw opens (reproduced with OnionMessage + Splice + DualFund + Close in one process, green without DualFund). `ClnDualFundTests.FundClnAsync` now pays 1 sat/vB, which cleared all of them but one: in the third full run (71/72 + 4 `Explicit`) and in the four-class sequence, CLN's `close` of the channel it funded at its `opening` estimate never returns (our coordinator agreed CLN's `[514, 514]` fee, B2-CLS-R06; CLN logged "Rejecting peer's closing fee offer: closingd must not agree to it"). The class passes alone (twice), as it did in the lane rbf run. Same family as NL-486: an estimate the earlier classes moved.
- **Fix sketch:** Find what CLN's closingd objects to after the estimate moved (compare its `closing_fee_range`/ideal fee with the fee it sent), then pin CLN's feerate in the test as NL-486 did for the open, or keep the fee-moving classes in their own collection.
- **Blocks/Blocked-by:** Related NL-486
- **Plan ref:** `SPLICING_PLAN.md` "Lane dfrbf record"

### NL-532 A peer closing the connection is logged at Error level
- **Status:** fixed (20062e88)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` ("Exception occurred with peer")
- **Evidence:** The 24 h mainnet soak logged both of its errors this way: ACINQ closing the stream (`EndOfStreamException` behind `ConnectionException`) and noserver4u's missed `pong`. Both are routine disconnects, and the reconnect followed. Error level makes them look like faults in the operator's log (`MAINNET_GOSSIP_PROBE.md` "24 h mainnet soak").
- **Fix sketch:** Log a remote close or a ping timeout at Warning (Information for a clean EOF). Keep Error for our own failures.
- **Fix:** Lane accrbf (owner decision 2026-09-28). `Infrastructure/Node/Services/PeerConnectionFailures.GetLogLevel(exception)` walks the exception chain: an end of stream (`EndOfStreamException`, the new `PeerClosedConnectionException` the transport read loop now throws for it and for a socket the peer closed) is Information; a missed `pong` (the new `PingTimeoutException`, a `ConnectionTimeoutException`, raised by `PingPongService` for both timeouts), any timeout, socket or I/O error, and a condition we raised about the peer (a chain ending in `ErrorException`/`WarningException`, e.g. no init, a mismatched `pong`, now a `ConnectionException`) are Warning; anything else (our own failure behind the connection) stays Error. Used by `PeerService.HandleException` ("Peer X closed the connection" / "Connection problem with peer X" / the old error), `PeerCommunicationService.RaiseException` (a peer close is Information) and `PeerOutbox` (a send that fails because the connection went away). Other routine peer events that were errors: the peer's `warning` message is a Warning (it was logged as "Received error message"), a peer without init, with incompatible features or another chain is a Warning, and a malformed message from the peer (`MessageService`) is a Warning. Errors kept: the peer's `error` message, a failed channel, our own handler failures. Tests: `PeerConnectionFailuresTests` (the soak's two cases and the other classes, and `PeerService` logging each at its level with a mocked logger), `PingPongServiceTests` (the timeout type).
- **Blocks/Blocked-by:** Found by NL-376
- **Plan ref:** BOLT7 G5-T5

### NL-533 Day0FlowTests step 4 read the splice-out transaction before it reached bitcoind
- **Status:** fixed (8968ebfa)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Day0/Day0FlowTests.cs` (step 4)
- **Evidence:** Lane accrbf's first Day0 run failed at step 4 with `RPCException` "No such mempool or blockchain transaction": `spliceout` returns once the splice is signed and the splice service publishes afterwards (both nodes' logs show the broadcast accepted a few ms later), while the test called `getrawtransaction` at once. Steps 1-3, including the new step 1 (c), had passed.
- **Fix:** Step 4 waits for the transaction in bitcoind's mempool (`Day0Harness.WaitInMempoolAsync`) before reading it, as steps 5 and 9 do; the rerun was green (3/3 with `Day0UpgradeInPlaceTests`).
- **Blocks/Blocked-by:** Related NL-530
- **Plan ref:** `SPLICING_PLAN.md` "Lane accrbf record"

### NL-534 A refused rebroadcast of a pending transaction is logged at Error by BitcoinChainService
- **Status:** fixed (58fb0c47)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs` ("Failed to broadcast transaction")
- **Evidence:** In lane accrbf's Day0 run both nodes logged `[Error] Failed to broadcast transaction ... bad-txns-inputs-missingorspent` every block for a `Funding` row bitcoind refuses (the monitor itself logs the refusal at Warning, then Debug, and abandons the row after 12 permanent refusals, NL-294). A permanent refusal of a transaction the monitor retries is routine: the Error line duplicates the monitor's and makes it look like a fault (the NL-532 kind of log noise, outside the peer connection path).
- **Fix sketch:** Let the chain service log a refusal (an RPC reject such as `bad-txns-inputs-missingorspent`, `txn-mempool-conflict`, `insufficient fee`) at Warning or Debug and leave the level to the caller; keep Error for an unreachable node or an unexpected RPC failure.
- **Fix:** Lane cli535. `BroadcastRefusalRules.IsNodeRefusal`: an `RPCException` with `RPC_VERIFY_ERROR` (-25, e.g. `bad-txns-inputs-missingorspent`), `RPC_VERIFY_REJECTED` (-26: mempool conflict, fee too low) or `RPC_VERIFY_ALREADY_IN_CHAIN` (-27) is a refusal of the transaction: `BitcoinChainService.SendTransactionAsync` logs it at Debug and rethrows (the callers log it: the monitor at Warning once then Debug, NL-294); an unreachable node, bad bytes or any other RPC error stays Error. The losing RBF siblings of a splice no longer wait for the lock: when a pending splice attempt's spend of the funding output is seen in a block (`ChannelManager` splice branch), the other pending attempts' broadcast rows are abandoned in their own save (`AbandonLosingSpliceAttemptsAsync`; the winner stays pending, so a reorg sends it again; the lock still discards the fundings). In the Mutinynet logs each splice RBF produced 3 refusals (depth 3) before the lock abandoned the sibling. A dual-funded open already marks earlier attempts `Replaced` when an RBF completes and the others when an earlier attempt reaches its depth (`OnFundingConfirmedAsync`); between that attempt's first confirmation and its depth the latest attempt is still refused (now at Warning once, Debug after). Tests: `BitcoinChainServiceSendTests` (the fake RPC node answering -25/-26/-27, an unexpected RPC error, an unreachable node), `ChannelManagerOnchainTests.Given_ASpliceAttemptConfirms_*`.
- **Blocks/Blocked-by:** Related NL-532, NL-294, NL-461
- **Plan ref:** —
### NL-535 openchannel blocks until channel_ready and never prints the first attempt of a dual-funded open
- **Status:** fixed (a254f9f2)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Client/Handlers/OpenChannelMessageHandler.cs` (the subscription loop), `src/NLightning.Daemon/Handlers/OpenChannelClientSubscriptionHandler.cs` (`HandleAsync`)
- **Evidence:** Mutinynet dry run 2 on the .NET 11 build (`DAY0_RUNBOOK.md` §5, 2026-09-28). `openchannel ... --dual-fund` returns only at `channel_ready` (about 2 min on Mutinynet, 3 blocks), so `bumpopen` run after it in the same shell was refused ("channel_ready sent or received") and the first open (`f20939f0...`) could not be bumped. The dual-funded open signs and broadcasts its funding before the client's first subscription call; that call finds the channel already in `V1FundingSigned`, returns at once only for the ready states, and waits for the next channel update. Without an RBF the CLI therefore never prints the funding txid ("Opening Channel", "Peer accepted", "Channel is now open!"); with RBFs it printed T1 and T2 of the second open (`b78b95b7...`) but never its first attempt T0 `22fb4544...`. The run went on with `openchannel` in the background, the txid read from `listchannels`, then both `bumpopen`s (opener and accepter, both followed, the accepter's attempt confirmed).
- **Fix sketch:** Return the funding txid of a dual-funded open in the open's own response (a new `ClientCommand` or response key, NL-210 rules), or let the subscription request carry the txid the client last printed so the daemon answers at once when the channel's current funding differs; and document `openchannel ... &` (or a second terminal) for `bumpopen` in the runbook meanwhile.
- **Fix:** Lane cli535, no new `ClientCommand` (NL-210: new keys on the existing ones). `OpenChannelIpcResponse` keys 1/2 carry a dual-funded open's published funding txid and output index (`OpenChannelClientHandler`, from `DualFundedOpenResult.FundingTxId`); `OpenChannelSubscriptionIpcRequest` keys 1 `KnownFundingTxId` and 2 `ReportFundingChanges` (absent = the old wait for the next update): the daemon subscribes first, then answers at once or on the next update when the channel is ready or runs on a published funding other than the known one (a V2 attempt counts once its `BroadcastTransactions` row exists), so every attempt of either side is reported; a signed funding no longer needs the peer connected nor fails on its disconnection, and a cancelled call never releases the channel's UTXOs. The CLI (`OpenChannelMessageHandler.RunAsync`) prints the channel id and txid as soon as they are published, each new attempt ("The channel's funding transaction changed (replaces ...)"), returns early with `--no-wait`, and exits 0 on Ctrl-C after a txid. `listchannels` lists a V2 open's other signed attempts as `Pending`/`Initial` fundings ("Pending (another attempt of the dual-funded open)"). Tests: `Daemon.Tests` `OpenChannelSubscriptionFundingTests`, `OpenChannelDualFundClientHandlerTests`, `Client/OpenChannelCommandTests`, `ClientAppTests`, `FormatterTests`, `ListChannelsClientHandlerTests`; Docker `Day0FlowTests` step 1 asserts the open's response carries the first attempt, the subscription answers with it at once, and after both bumps with B's attempt on both nodes (green, 1/1, 274 s). `DAY0_RUNBOOK.md` §1/§2.2 no longer need a second terminal.
- **Blocks/Blocked-by:** Related NL-528, NL-530
- **Plan ref:** `DAY0_RUNBOOK.md` §2.2
### NL-536 Routine startup and splice events in the Mutinynet logs were warnings or misleading
- **Status:** fixed (b0e2f645)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` ("Processing missed blocks"), `src/NLightning.Application/Node/Managers/PeerManager.cs` ("not connected, dropping"), `src/NLightning.Application/Channels/Splicing/RetiredScidMap.cs` (`LoadAsync`), `src/NLightning.Application/Gossip/Graph/GraphPruner.cs` ("Graph channel ... closed")
- **Evidence:** The two Mutinynet dry-run logs (`~/.nltg/mutinynet{,-fafo2}/daemon.out`, 2026-09-28): every restart logged `WRN Processing missed blocks from height N to M` and `WRN Peer ... not connected, dropping SpliceLocked` (a splice confirmed at startup before the peer reconnected; the reestablish's `my_current_funding_locked` carries it), `RetiredScidMap Loaded N retired short channel id(s) at height 0` (the host loads the map before the chain monitor has its height, so nothing expired was dropped and the log named height 0), and each splice logged `GraphPruner Graph channel <scid> closed: its funding output was spent`, although a spliced channel is only marked spent and kept for 72 blocks.
- **Fix:** Lane cli535. Missed blocks at Information; a dropped `splice_locked`, `channel_ready` or `announcement_signatures` for an unconnected peer at Information (anything else stays Warning); `RetiredScidMap.LoadAsync(0)` reads the chain monitor's stored `BlockchainState` height (test `RetiredScidMapTests.Given_TheMonitorNotStartedYet_*`); the pruner says "its funding output was spent at block H (a close or a splice); it is forgotten at block H + 72".
- **Blocks/Blocked-by:** Related NL-518, NL-532, NL-534
- **Plan ref:** `DAY0_RUNBOOK.md` §5
### NL-525 Our onion-message paths have no dummy hops
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/OnionMessages/BlindedMessagePathBuilder.cs`, `src/NLightning.Application/OnionMessages/OnionMessageService.cs`, `src/NLightning.Application/Offers/Receive/OfferService.cs`
- **Evidence:** NL-440 added dummy hops (BOLT 4 writer MAY) to our blinded **payment** paths only. Offer paths and reply paths are built by the separate `BlindedMessagePathBuilder`, so a sender still sees exactly how many hops they have, and `OnionMessageService` drops a forward to ourselves as `loop` instead of peeling it as a dummy hop (reported by the NL-440 lane).
- **Fix sketch:** Add a dummy-hop count to the message path builder (the next hop our own node id, same padding), peel a self-forward in the unwrapper before the loop rule, and prove it with CLN's onion-message proof.
- **Blocks/Blocked-by:** Follow-up of NL-440
- **Plan ref:** `BOLT12_PLAN.md` OM-S-08

## Crypto providers and key management

### NL-158 Key file encryption: fixed Argon2 salt, all-zero XChaCha nonce, 64 KiB Argon2 memory
- **Status:** fixed (953a33b, b999208)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs:27-31,176-177`, `src/NLightning.Infrastructure/Crypto/Hashes/Argon2Id.cs:9`
- **Evidence:** Static `s_salt`, a never-filled stackalloc nonce, and `DeriveKeyMemLimit = 1 << 16` (comment says 64 MiB; it's 64 KiB) weaken offline password attacks on the node key file. v2 key file (random salt/nonce, full UTF-8 password); v1 upgraded in place with a `.v1.bak`. Follow-ups: NL-211 (accepted break), NL-212 (Windows legacy fallback), NL-224 (owner/ACL).
- **Fix sketch:** Random per-file salt and nonce stored in the file, Argon2 memory ≥ 64 MiB, versioned key-file format with migration (changing any of these breaks existing files).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-159 Node key is the BIP32 master key; non-standard master derivation from mnemonic
- **Status:** fixed (db262e2a)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs:85,141-143`
- **Evidence:** `GetNodeKeyPair` returns the master key itself; the master is not derived the standard BIP32 way, so the seed can't be restored in other wallets. Update (wave rf1, `wip/fafo` at `be9fd000`): new nodes get v3 key files: a standard BIP32 master with the node key at m/1017'/0'/6'/0/0 (tested against the BIP84 mnemonic vector); v1/v2 files keep their derivation and node id by design (moving an existing node needs a new node); builds before this one cannot read v3.
- **Fix sketch:** Derive the node key on a dedicated path; document/migrate.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-160 JS (WASM) RandomBytes swallows errors and leaves the buffer unfilled
- **Status:** fixed (d10bf55)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs:212-222`
- **Evidence:** `catch (Exception e) { Console.WriteLine(e); }`; a failed RNG call yields predictable (zero) key material. Other JS methods also only log exceptions.
- **Fix sketch:** Throw on failure; audit all JS provider catch blocks.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-161 Argon2 not implemented in the JS (WASM) provider
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs:207-210`
- **Evidence:** `throw new NotImplementedException();`
- **Fix sketch:** Bind libsodium.js `crypto_pwhash`.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-162 JS ChaCha20 keystream never executed; blazorSodium.js export unverified
- **Status:** open
- **Severity:** medium
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/{LibsodiumJsWrapper,SodiumJsCryptoProvider}.cs`, `test/BlazorTests/`
- **Evidence:** Compiled in CI `Release.Wasm` only; no Blazor/Playwright test; unverified whether `blazorSodium.js` needs to re-export `crypto_stream_chacha20_ietf_xor`.
- **Fix sketch:** Add a BlazorTestApp page + Playwright test with the RFC 8439 vector.
- **Blocks/Blocked-by:** Blocked-by NL-169 for local runs
- **Plan ref:** M1-T2 open items

### NL-163 Native provider mlock/munlock are no-ops off Windows
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/Native/NativeCryptoProvider.cs:92-106`
- **Evidence:** TODOs; secrets can be swapped to disk on Linux/macOS under the Native/AOT build.
- **Fix sketch:** P/Invoke `mlock`/`munlock` on Unix.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-164 Hkdf validates key/output lengths with Debug.Assert only
- **Status:** fixed (2f19776)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Crypto/Functions/Hkdf.cs:32-33,55-56,75-76`
- **Evidence:** Release builds skip the checks; current callers pass 32-byte keys.
- **Fix sketch:** Throw `ArgumentException`.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T1 open item

### NL-165 Crypto value-object equality/validation hazards
- **Status:** fixed (1df7537)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Crypto/ValueObjects/*`, `src/NLightning.Domain/Bitcoin/ValueObjects/TxId.cs`, `src/NLightning.Domain/Protocol/Tlv/BaseTlv.cs`, `ChainHash.cs`
- **Evidence:** `PrivKey`/`CompactSignature` compare by reference; `default(TxId/Hash/Secret)` throws in `GetHashCode`; `Hash`/`Secret`/`TxId` accept arrays longer than 32 bytes; `BaseTlv`/`ChainHash` hash codes are inconsistent with `Equals`.
- **Fix sketch:** Content equality, exact-length validation, null-safe hashing.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-166 Implicit byte[]→CompactPubKey conversion turns a bare null into a NullReferenceException
- **Status:** fixed (938655c, 8371531)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Crypto/ValueObjects/CompactPubKey.cs`
- **Evidence:** `cond ? x : null` binds to the implicit operator and throws; tests cast `(CompactPubKey?)null` to avoid it. Binary break recorded in the Domain CHANGELOG.
- **Fix sketch:** Make the conversion explicit or null-tolerant.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2-sphinx open item

### NL-211 Key files upgraded to v2 cannot be read by older builds
- **Status:** wontfix
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 8. Accepted break: v1 files still load (fixed salt, 64 KiB, truncated-UTF-8 fallback) and are rewritten as v2 with a `<file>.v1.bak` copy and a stderr notice; older builds cannot open v2. Recorded in the Infrastructure and Infrastructure.Bitcoin CHANGELOGs.
- **Fix sketch:** None (accepted). Keep the `.v1.bak` for downgrade.
- **Blocks/Blocked-by:** Related NL-158
- **Plan ref:** —

### NL-212 Legacy key-file password fallback misses Windows ANSI-codepage files
- **Status:** fixed (db262e2a)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` (legacy retry), libsodium P/Invoke
- **Evidence:** The old libsodium path marshalled the password as LPStr: UTF-8 on Unix (covered by the truncated-UTF-8 retry) but the ANSI codepage on Windows. A non-ASCII password on a Windows v1 file will not decrypt. Update (wave rf1, `wip/fafo` at `be9fd000`): v1 files only: the password is retried as the exact LPStr (ANSI) marshalling, with a known-answer test.
- **Fix sketch:** On Windows also retry with `Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)` truncated to the char count; test with a known-answer vector.
- **Blocks/Blocked-by:** Related NL-158, NL-211
- **Plan ref:** —

### NL-224 Atomic key-file writes keep the mode but not owner, group or Windows ACL
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` (atomic write)
- **Evidence:** The temp file copies the Unix mode before the move; no chown and no ACL copy (crypto-security batch). Update (wave rf1, `wip/fafo` at `be9fd000`): rewrites and the `.v1.bak` now drop group/other bits (SR-02, db262e2a) and the directory is fsynced (8c66499b); owner preservation when root rewrites the file and the Windows ACL remain (SECURITY_REVIEW SR-14).
- **Fix sketch:** Copy owner/group where permitted; copy the ACL on Windows.
- **Blocks/Blocked-by:** Related NL-158
- **Plan ref:** —

### NL-247 ISha256 is a stateful singleton shared by concurrent users
- **Status:** fixed (03a19fa, 9762e28)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/DependencyInjection.cs:29` (`AddSingleton<ISha256, Sha256>()`), consumers such as `ChannelFactory` and the repositories
- **Evidence:** `Sha256` keeps state between `AppendData` and `GetHashAndReset`, so two concurrent users of the singleton can interleave and get wrong hashes. The update_fulfill_htlc handler now hashes preimages with its own instance (1392489); other users still share it (reported by W1-A). Update (ABCD wave 3, `c92d837`): `ISha256` is registered as `ThreadLocalSha256` (one hash state per thread), and hashing goes through a one-shot `ComputeHash` that resets the state even when appending throws.
- **Fix sketch:** Register it transient (or through a factory) and audit singleton consumers that hold it.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

---

### NL-403 A mainnet node could not read its own key file
- **Status:** fixed (7f72785)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` (`FromFilePath`)
- **Evidence:** `SecureKeyManager.FromFilePath` compared NBitcoin's network name (`Main`) with `mainnet`, so a mainnet key file was refused after the first start (reported by the mainnet gossip probe, first run, `docs/agents/MAINNET_GOSSIP_PROBE.md`). Fixed: the names are mapped; test in `SecureKeyManagerTests`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Blocks any mainnet node
- **Plan ref:** n/a

### NL-427 The restore's channel key index reserver looped forever with a key manager that does not advance
- **Status:** fixed (aa9d67e0)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Backup/SecureKeyManagerKeyIndexReserver.cs`
- **Evidence:** It called `GetNextChannelKey` until the index reached the highest restored one; with a key manager that does not advance (a mock) it never ended and hung Daemon.Tests `RestoredOverIpc` (found by the rf1 integrator). Fixed: it uses `EnsureLastUsedChannelIndexAtLeast` for the file-backed manager and throws otherwise; startup also reconciles the index with `IChannelDbRepository.GetHighestLocalKeyIndexAsync` (SR-19).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-426
- **Plan ref:** —

### NL-436 The BOLT 8 handshake keeps an unwiped copy of the node private key per connection
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Node/Factories/PeerServiceFactory.cs`
- **Evidence:** `GetNodeKeyPair()` copies the node private key for each connection's handshake and never wipes it (SECURITY_REVIEW SR-17, lane R3).
- **Fix sketch:** Do the handshake ECDH through `ISecureKeyManager`, as the onion peel does.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-437 Plaintext key copies in managed strings and NBitcoin objects cannot be wiped
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs`
- **Evidence:** The xprv and password strings and NBitcoin `Key`/`ExtKey` objects stay in managed memory until collected (SECURITY_REVIEW SR-09, lane R3).
- **Fix sketch:** Keep secrets in pinned byte arrays and wipe them; minimise NBitcoin key objects.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-438 GossipProbe still creates v2 key files with the legacy constructor
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `tools/NLightning.GossipProbe/ProbeNode.cs`
- **Evidence:** New nodes get v3 key files since db262e2a, the probe still uses the legacy constructor (reported by lane R3).
- **Fix sketch:** Switch the probe to `SecureKeyManager.CreateNew`.
- **Blocks/Blocked-by:** Related NL-159
- **Plan ref:** —

### NL-439 Security review did not cover the Windows named-pipe ACL or a database file outside the config directory
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Daemon/Services/Ipc/NamedPipeIpcService.cs`, `Database` connection string
- **Evidence:** Wave rf1 security review (SECURITY_REVIEW.md) checked the Unix socket mode and `CurrentUserOnly`, not the Windows pipe ACL end to end, and not the file mode of a database whose path is outside `~/.nltg/<network>` (reported by the rf1 integrator).
- **Fix sketch:** Review both; warn at start when the database file is group/world readable.
- **Blocks/Blocked-by:** Related NL-224
- **Plan ref:** —

## Tests / CI / Build

### NL-167 Application.Tests and Daemon.Tests lack xunit.runner.visualstudio (47 tests skipped in CI)
- **Status:** fixed (34d431d, 9e03b7e, df708a7)
- **Severity:** high
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/NLightning.Application.Tests.csproj`, `test/NLightning.Daemon.Tests/NLightning.Daemon.Tests.csproj`
- **Evidence:** `dotnet test` discovers 0 tests; CI silently skips 24 + 23 tests. They pass via `dotnet run`. `scripts/check-sln-configs.py` (CI step) now fails if a test project lacks the runner.
- **Fix sketch:** Add `xunit.runner.visualstudio` 3.1.5 (and `IsTestProject` for Daemon.Tests).
- **Blocks/Blocked-by:** Blocks NL-073 (M4-T1 tests go here)
- **Plan ref:** ONION M4-T1; BOLT_COVERAGE roadmap step 1; BOLT2 N0-T1

### NL-168 PeerAddressTests HttpAddress test depends on live DNS
- **Status:** fixed (4c5207d, df708a7)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Tests/Protocol/Models/PeerAddressTests.cs` (`Given_HttpAddress_When_ConstructingPeerAddress_...`)
- **Evidence:** Resolves `dnstest.nlightn.ing`; fails offline.
- **Fix sketch:** Inject a resolver or move to an integration category.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-169 Release.Wasm build fails on macOS (linux-x64 pinned npm deps)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/package.json`
- **Evidence:** esbuild/rollup pinned to linux-x64 → `EBADPLATFORM`; Wasm changes can only be verified in CI.
- **Fix sketch:** Use optional platform deps or unpin.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-170 ~570 NuGet vulnerability warnings (NU1902/NU1903)
- **Status:** fixed (525dc43, df708a7)
- **Severity:** high
- **Kind:** tech-debt
- **Location:** package references across `src/` and `test/`
- **Evidence:** Known high/moderate advisories: System.Security.Cryptography.Xml 8.0.2, MessagePack 3.1.4 and 3.1.3 (IPC deserialization surface), SharpCompress 0.41.0, SQLitePCLRaw.lib.e_sqlite3 2.1.11. They bury real warnings.
- **Fix sketch:** Bump or pin patched versions (transitives via `Directory.Packages`/explicit refs); then consider `WarningsAsErrors` for NU190x.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-171 ~20 nullability warnings (10 unique sites)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `FundingConfirmedMessageHandler.cs:52`, `FundingCreatedMessageHandler.cs:74`, `ChannelManager.cs:245,320`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:121,147`, `PeerService.cs:65,256`, `SignedTransactionFormatter.cs:14`
- **Evidence:** CS8602/CS8604/CS8622 in a Release build. Update: 8 CS86xx warnings remain after the swarm (FundingConfirmedMessageHandler, FundingCreatedMessageHandler, ChannelManager, OpenChannelClientSubscriptionHandler, ChannelDbRepository, PeerService); `SignedTransactionFormatter` is fixed (NL-145). Update (ABCD wave 0, `0b7e617`): 7 CS86xx warnings in Release and Release.Native: `FundingCreatedMessageHandler.cs:75`, `ChannelManager.cs:549`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:302,320`, `PeerService.cs:127,413`. Update (ABCD wave 1, `342d22e`): 5 CS86xx warning sites in Release and Release.Native (each reported twice): `FundingCreatedMessageHandler.cs:75`, `ChannelManager.cs:840`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:310,328`; the two `PeerService` warnings went away with the W1-E connect rewrite (df4ca92). Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): a Release build (`-p:NltgTargetNet11=false`) shows the same 5 sites: `FundingCreatedMessageHandler.cs:75`, `ChannelManager.cs:1801`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:328,346` (the title's counts are historical).
- **Fix sketch:** Fix each, then enable nullable warnings as errors.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-172 Solution configuration mappings are hand-maintained and partly wrong
- **Status:** fixed (33cc93c, 9e03b7e, df708a7)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `NLightning.sln` (~L283 and newer projects)
- **Evidence:** Serialization maps Release.Native→Release.Wasm; Repositories, Contracts, Client, Plugins, Transport.Ipc, Application.Tests, Daemon.Tests map every custom config to Debug, so Native/Wasm builds silently use the wrong backend for them.
- **Fix sketch:** Fix the mappings; consider `.slnx` or a check script.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-173 test/NLightning.Node.Tests is an empty orphan
- **Status:** fixed (71e3e57, df708a7)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `test/NLightning.Node.Tests/NLightning.Node.Tests.csproj`
- **Evidence:** 3-byte BOM-only csproj, not in the sln.
- **Fix sketch:** Delete it.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-174 scripts/testwithcoverage.sh and .vscode/launch.json are stale
- **Status:** fixed (edf7b77, 8d18d27, df708a7)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `scripts/testwithcoverage.sh`, `.vscode/launch.json:12`
- **Evidence:** Script lists nonexistent projects and exits on the first; launch.json points at `src/NLightning.NLTG/bin/Debug/net8.0/NLightning.NLTG.dll`. Shared Persistence BaseOutputPath split out as NL-223.
- **Fix sketch:** Update or delete.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-175 No serializer tests for open_channel, accept_channel, funding_created, funding_signed
- **Status:** fixed (3c335a6, 3c4af42)
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Serialization.Tests/Messages/`
- **Evidence:** Only ChannelReady of the v1-open messages has round-trip tests.
- **Fix sketch:** Add round-trip tests, ideally with LND-captured bytes.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 1

### NL-176 BOLT 3 vector coverage gaps
- **Status:** fixed (d69eea3, 82e08d2, 4f9f550, a0dfebd, b222896)
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs:64-`, `test/NLightning.Tests.Utils/Vectors/Bolt3AppendixCVectors.cs`, `Bolt3AppendixFVectors.cs`
- **Evidence:** Appendix B funding test body is commented out (passes vacuously); Appendix F (anchors) unused; `ExpectedCommitTx1..15` unreferenced (only signatures asserted); no HTLC second-stage vectors. Appendix B funding tx and every Appendix C commitment tx asserted byte-for-byte; 9-vector Appendix F theory. HTLC second-stage vectors wait for NL-056 (1 skipped test). Update: Appendix C/F now load from verbatim spec copies (`BOLT3/Vectors/appendix-c.txt`, `appendix-f.json`) and every commitment is built from its msat balances through `CommitmentTxSpec` and compared as a full signed tx (a0dfebd, b222896).
- **Fix sketch:** Restore Appendix B, assert full txs, add Appendix C HTLC-tx and Appendix F tests.
- **Blocks/Blocked-by:** Related NL-061, NL-056
- **Plan ref:** BOLT_COVERAGE roadmap step 1; BOLT2 N2-T2, N2-T5

### NL-177 Fully commented-out test files
- **Status:** fixed (94a8cbf, 017050a, 71d0944)
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Bitcoin.Tests/Outputs/{Base,Change,Funding,ToRemote}OutputTests.cs`, `Transactions/{Commitment,Funding}TransactionTests.cs`, `test/NLightning.Infrastructure.Tests/Node/Models/PeerTests.cs`, `test/NLightning.Integration.Tests/Docker/{Sqlite,Postgres,SqlServer}Tests.cs`, `BOLT10/DNSBootstrapTests.cs`, `test/NLightning.Infrastructure.Serialization.Tests/Tlv/TlvSerializerTests.cs` (mostly)
- **Evidence:** 0 live lines in each (sweep 2026-09-25). Revived or deleted every listed file.
- **Fix sketch:** Revive against the current API or delete.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-178 Untested components
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/`
- **Evidence:** No unit tests for PeerService, PeerCommunicationService, TcpService, ChannelManager, AcceptChannel1/FundingSigned/FundingConfirmed/ChannelReady handlers, MessageFactory, RemoteAddressTlvConverter (beyond IPv4), the IPC stack/formatters/client, and persistence round trips. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): re-scoped: every component listed now has unit tests except `TcpService`: `test/NLightning.Infrastructure.Tests/Node/Services/PeerServiceTests.cs`, `PeerCommunicationServiceTests.cs`, `test/NLightning.Application.Tests/Channels/Managers/ChannelManagerTests.cs`, `Channels/Handlers/{AcceptChannel1,FundingSigned,FundingConfirmed,ChannelReady}MessageHandlerTests.cs`, `Protocol/Factories/MessageFactoryTests.cs`, `RemoteAddressTlvConverterTests.cs` (IPv6, Tor v3, DNS), `test/NLightning.Daemon.Tests/{Ipc,Services/Ipc,Client}/` and `test/NLightning.Integration.Tests/Persistence/{SqlitePersistenceTests,ChannelRoundTripTests}.cs`. Remaining: `src/NLightning.Infrastructure/Transport/Services/TcpService.cs` is exercised only through the Docker suites (`NLightningTestNode`, `CrashableTcpService`); severity lowered to low.
- **Fix sketch:** Add tests as each area is touched; a Sqlite `:memory:` round-trip is the cheapest persistence test.
- **Blocks/Blocked-by:** Related NL-179
- **Plan ref:** —

### NL-179 InternalsVisibleTo gaps
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/AssemblyInfo.cs:3`, `src/NLightning.Domain/AssemblyInfo.cs`, `src/NLightning.Application/AssemblyInfo.cs`
- **Evidence:** Daemon lists a stale `NLightning.Bolts.Tests` and not `NLightning.Daemon.Tests`; Domain.Tests/Application.Tests have no internals access. Update: Application lists `NLightning.Application.Tests` (424ae84) and Daemon lists `NLightning.Daemon.Tests`; Daemon still lists the stale `NLightning.Bolts.Tests`, and Domain.Tests still has no internals access (the N4 engine is public instead).
- **Fix sketch:** Fix the lists.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-180 Interop coverage: Docker e2e only against LND and not in CI
- **Status:** open
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/`, `.github/workflows/`
- **Evidence:** The project goal requires LND/CLN/Eclair/LDK interop; only LND v0.20.0 is exercised, manually. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): re-scoped: CLN v26.06.8 interop is covered (`Docker/Interop/Cln/`, 7c7c8c5e and later: connect, channels both ways, payments, reestablish, close, gossip, onion messages, offers, peer storage; 39 tests at wave B12). Remaining: no Eclair or LDK fixture (Eclair is met only as a mainnet gossip peer by `tools/NLightning.GossipProbe`, NL-407), and CI runs no Docker test (`.github/workflows/dotnet.yml:40` and the other workflows filter `FullyQualifiedName!~Docker`), which needs NL-276.
- **Fix sketch:** Add CLN/Eclair fixtures and a scheduled CI job with Docker.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-181 FakeSha256 returns zeros by default
- **Status:** fixed (82f2713)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/Mocks/FakeSha256.cs`
- **Evidence:** Tests using it bare can pass with wrong hashes.
- **Fix sketch:** Make the default throw or delegate to the real hash.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-223 Persistence provider projects share one BaseOutputPath
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}/*.csproj`, `src/NLightning.Infrastructure.Persistence/scripts/*`
- **Evidence:** All three write to `Persistence/bin`, which can race in parallel builds (not reproduced in 3 runs). The EF migration scripts depend on the shared output (ci-build batch).
- **Fix sketch:** Point `dotnet ef` at each provider's own output, then split the paths.
- **Blocks/Blocked-by:** Related NL-174
- **Plan ref:** —

### NL-233 dotnet-ef loads stale Debug provider assemblies when generating migrations
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Persistence/bin/Debug`, `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh`
- **Evidence:** During the four-lane integration, `dotnet ef` loaded the provider migration assemblies from the shared `Persistence/bin/Debug`; they were stale until the three provider projects were rebuilt in Debug, and until then both the regeneration and the `HasPendingModelChanges` check gave wrong results.
- **Fix sketch:** Build the three provider projects in Debug before `migrations add`/`has-pending-model-changes` (script it), and fix the shared output path (NL-223).
- **Blocks/Blocked-by:** Related NL-223
- **Plan ref:** —

### NL-249 FakeServiceProvider throws for unregistered services instead of returning null
- **Status:** fixed (c53afa2)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/Mocks/FakeServiceProvider.cs`
- **Evidence:** `GetService` throws `KeyNotFoundException` for unregistered types, which breaks the `IServiceProvider` contract; `ChannelManager` now calls `GetService<IHtlcSwitch>()` and resolves `ChannelDomainEventQueue`, so tests using the fake must register them (reported by W1-A). Update (ABCD wave 3, `c92d837`): `FakeServiceProvider.GetService` returns null for unregistered types; `Strict = true` restores the throw.
- **Fix sketch:** Return null for unknown types; keep an opt-in strict mode if some tests rely on the throw.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-261 PeerService.SendErrorAsync has no unit test
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (`SendErrorAsync`), `test/NLightning.Infrastructure.Tests/`
- **Evidence:** W2-A added `IPeerService.SendErrorAsync` (error without disconnect, NL-200); it is covered only through `PeerManager`/Docker, and Infrastructure.Tests was outside that lane (reported by W2-A).
- **Fix sketch:** Add a PeerService test that the error is sent and the connection stays open.
- **Blocks/Blocked-by:** Related NL-200
- **Plan ref:** BOLT2 N6-T3

### NL-262 A restarted LND container can come back on another address; LNUnit RestartByAlias(isLND: true) then hangs
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/ReestablishFlowTests.cs` (`HoldAddressesBelowAsync`), LNUnit 3.0.4
- **Evidence:** OrbStack/Docker gives a restarted container the lowest free address in the network, so alice moved (.5 → .2) once earlier containers were removed; every later test lost her. LNUnit's `isLND: true` path re-adds the node without removing the stale connection and waits forever in `WaitUntilAliasIsServerReady`. Worked around (30c1bb8): idle `nltg-address-hold-N` containers fill the lower addresses during the restart, and the test asserts her address is unchanged (reported by W2-A).
- **Fix sketch:** Fix upstream in LNUnit (drop the stale connection, re-resolve the address), or give fixture containers static IPs.
- **Blocks/Blocked-by:** Related NL-180
- **Plan ref:** BOLT2 N7 proof

### NL-263 Docker ChannelOpeningFlowTests.GivenSingleP2TRInput flake: "No locked UTXOs found"
- **Status:** fixed (5cc32ba)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/ChannelOpeningFlowTests.cs`
- **Evidence:** Failed once in a full Docker run and passed in every other full run and in class reruns (reported by W2-A). Not seen in the integrator's runs at `a5675cb`. Update (ABCD wave 3, `c92d837`): the same message ('No locked UTXOs found for channel', `OpenChannelClientSubscriptionHandler`) failed `NormalOperationFlowTests.Given_InFlightRestart` once during its open (W3-B) and passed on rerun. Possibly related to the wallet address bugs NL-280/NL-283. Update (ABCD wave 4, `6b5d50e`): reproduced at integration: `AcceptChannel1MessageHandler` moved the UTXO locks to the real channel id only after `UpgradeChannel` raised `OnChannelUpgraded`, so the open subscription looked the locks up under the new id too early. The locks now move first (regression test `Given_ValidAcceptChannel_When_ChannelIsUpgraded_Then_UtxoLocksAlreadyCarryTheNewChannelId`); `AbcNetworkTests` + `ChannelOpeningFlowTests` 8/8 after it. A related race (V1FundingSigned raised before the subscription exists) is NL-295. Update (ABCD wave 5, `1a5ab49`): not seen in any wave 5 run (lanes and integration, net10.0 and net11.0).
- **Fix sketch:** Capture the wallet/UTXO state on failure; check for a race between funding the wallet and the lock.
- **Blocks/Blocked-by:** Related NL-180
- **Plan ref:** —

### NL-276 Host dotnet cannot reach Docker bridge container IPs; LNUnit Docker tests fail in the fixture
- **Status:** open (partial: 1d561f2)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/` (LNUnit `LightningRegtestNetworkFixture`), this macOS host
- **Evidence:** In wave 3 the host process got EHOSTUNREACH ('No route to host 192.168.215.x:18443') to container IPs while ping and /usr/bin/curl worked (likely macOS Local Network privacy for the parent app, or OrbStack routing). All 44 LND-based Docker tests failed at setup at integration (`c92d837`), so the ABCD 3-run gate and the N9/N10 Docker proofs were not re-verified there; the CLN and database fixtures publish ports on 127.0.0.1 and passed. Lanes worked around it by running the host-built test dll inside an SDK container on the bridge (`test/CLAUDE.md`). Concurrent Docker runs also force-remove each other's fixture containers (miner/alice/...). Update (ABCD wave 4, `6b5d50e`): still reproduces on the host. Workaround documented in `test/CLAUDE.md` (6b5d50e): run the host-built test dll inside an SDK container with `--network host`, which reaches the bridge addresses, the ports published on 127.0.0.1 and `host.docker.internal`; with it all 77 Docker tests passed at integration (`--network bridge` fails the database and connect-back cases). Update (ABCD wave 5, `1a5ab49`): still reproduces: `scripts/run-onchain.sh` from the host fails at fixture setup with "No route to host (192.168.215.2:18443)"; every wave 5 Docker result comes from the in-container runner (`--network host`). Update (gossip wave G-D, `48a8951`): every Docker runner script (`run-gossip.sh`, `run-onchain.sh`, `run-abcd.sh`) now uses the in-container `--network host` runner (1d561f2, NL-358) and test processes no longer share ports (NL-359); the host route itself still fails, and concurrent suites still force-remove each other's fixture containers, so run one Docker process at a time. Update (ledger hygiene lh1, `wip/fafo` at `d929b879`): re-scoped to test infrastructure: every Docker runner script and `test/CLAUDE.md` use the in-container `--network host` runner, and every Docker result since wave 4 comes from it; no product behaviour is affected, so severity is lowered from high to low. Remaining: the host route itself and serializing concurrent Docker runs; it still blocks running Docker in CI (NL-180).
- **Fix sketch:** Grant Local Network access to the terminal/Claude app or restart OrbStack; longer term publish LNUnit ports on 127.0.0.1 or run Docker tests from a container by default, and serialize Docker runs across agents.
- **Blocks/Blocked-by:** Related NL-180, NL-263
- **Plan ref:** ABCD wave 3 gate

### NL-286 Close interop gaps are proven in-process only
- **Status:** open (partial: 8096700, a38c999)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/CooperativeCloseFlowTests.cs`, `test/NLightning.Application.Tests/Channels/Close/`
- **Evidence:** LND 0.20 ignores fee_range, so the fee_range receive path (B2-CLS-R03..R06) runs only between two of our nodes; there is no Docker restart while ShuttingDown/Negotiating/Closing, no close against CLN/Eclair, and an LND-funded channel closed by LND (our non-funder path) is in-process only (reported by W3-B). Update (ABCD wave 4, `6b5d50e`): close against CLN is proven in Docker (`ClnCloseTests`: we close with and without our `fee_range`, CLN closes a channel we funded and one it funded, each to a confirmed closing tx; CLN's fee, range and our decision asserted from the log). Still missing: a Docker restart while ShuttingDown/Negotiating/Closing, and Eclair. The integrator listed this as fixed; the ledger keeps it open for the restart case.
- **Fix sketch:** Add a Docker restart-while-ShuttingDown case, and CLN close cases on the W3-E `ClnFixture`.
- **Blocks/Blocked-by:** Related NL-034, NL-285
- **Plan ref:** BOLT2 Proof N10

### NL-300 net11.0 Docker suite, NativeAOT publish and Wasm on SDK 11 not verified
- **Status:** open (partial: a5b24e3)
- **Severity:** low
- **Kind:** test
- **Location:** `src/Directory.Build.props`, `test/Directory.Build.props`, `docs/agents/NET11_PLAN.md`
- **Evidence:** The multi-target build and the non-Docker tests pass on net10.0 and net11.0 (SDK 11 rc.1), but the Docker suite ran on net10.0 only (both frameworks would fight over the fixed container names), and NativeAOT and Wasm were not built with SDK 11. Seen with SDK 11 rc.1: building through a symlinked path skipped `CopyToOutputDirectory` items (not reproduced from the real path). `IHost.RunAsync` exits 1 on a failed BackgroundService on net11.0 but 0 on net10.0 (reported by W4-C). Update (ABCD wave 5, `1a5ab49`): the full Docker suite ran on net11.0 at the wave 5 integration from an sdk:11.0 container: LND suite 48/48, `Docker.Utils` 2/2, CLN 17/17, ABCD 10/10, `Docker.Onchain` 14/14 (3 Explicit not run), the same as net10.0. Remaining: NativeAOT publish and Wasm on SDK 11, `allowPrerelease: false` after GA. Update (ABCD wave 7, `4c37998`): NativeAOT publish on SDK 11 rc.1 fails at compile (NL-338). SDK 10 AOT and Wasm on SDK 11 not run. At the wave 7 integration the full Docker suite and non-Docker tests passed on net11.0 from the sdk:11.0 container; host SDK 11 on macOS hung in `dotnet test` for net10.0 (environment, not code).
- **Fix sketch:** Run the Docker suite with `-f net11.0`; publish AOT and build Wasm with SDK 11; after GA set `allowPrerelease: false`.
- **Blocks/Blocked-by:** Related NL-155
- **Plan ref:** `NET11_PLAN.md` step 6

### NL-310 ChainMonitorPersistenceTests.Given_ReorgOfDepth2 failed once under load
- **Status:** fixed (548ba85, 368a057)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Persistence/ChainMonitorPersistenceTests.cs` (several tests)
- **Evidence:** Failed once under machine load in W5-A and passed on rerun; not seen at the wave 5 integration (5718 per framework, green). Possibly a timing assumption in the reorg rewind test. Update (ABCD wave 6, `3ce3cad`): at the wave 6 integration one full parallel run failed a different `ChainMonitorPersistenceTests` test per config (`Given_NewBranchProcessed_When_AStaleOrphanIsDeliveredLate` on Release, `Given_FundingTransactionOfAStoredChannelConfirms` on Release.Native); not reproduced in 8 class runs, 6 project runs and 6 more full runs. Suspected race between the monitor's background task and the test's direct `ProcessNewBlockAsync` (unconfirmed). Update (ABCD wave 7, `4c37998`): root cause found: the tests' ZMQ subscriber used 127.0.0.1:28332, where a local Mutinynet bitcoind publishes rawblock, so foreign blocks raced the tests' direct `ProcessNewBlockAsync`. `BlockchainMonitorService.ProcessNewBlockAsync` now serializes callers and drops a block above bitcoind's tip; tests use `SilentZmqEndpoint` (548ba85). Stress: 3/200 failing before, 252/252 green after (368a057). Any new test that starts a real monitor with a fixed ZMQ port must use `SilentZmqEndpoint`.
- **Fix sketch:** Capture the failure output on the next occurrence; look for a wait on the monitor without a condition.
- **Blocks/Blocked-by:** Related NL-096
- **Plan ref:** —

### NL-319 LND reports insufficient_balance for a fresh private channel before its router has the edge
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Onchain/OnchainO4Tests.cs` (`PayUntilSentAsync`), other LND tests that pay right after opening
- **Evidence:** LND lists a fresh private channel active before its graph holds the edge, so a payment pinned to it right away fails with insufficient_balance (local balance 0) although LND lists 300,000 sat local. O4 (b) failed twice for it at integration and now retries on insufficient_balance/no_route (a5b24e3); other LND tests that pay right after opening may need the same helper. Update (ABCD wave 6, `3ce3cad`): the same LND-restart race showed up as "server is still in the process of starting" on LND's force close in O5 (a); the proof now retries it (e8841d6).
- **Fix sketch:** Move `PayUntilSentAsync` to the shared Docker utils and use it wherever LND pays right after an open.
- **Blocks/Blocked-by:** Related NL-180
- **Plan ref:** —

### NL-331 Docker tests must not assume LND default fees on shared channels
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/` (shared LNUnit fixture)
- **Evidence:** The fixture's alice→bob fee policy differed between fixtures (0 fee in a fresh one, 1000 msat + 1 ppm in an earlier run), so a test that relies on LND's default fees on a shared channel is order-dependent; `PaymentRetryFlowTests` uses a CLTV-delta case instead of fee_insufficient for that reason (reported by W6-C).
- **Fix sketch:** Read the channel policy at test time, or set it explicitly on channels the test owns.
- **Blocks/Blocked-by:** Related NL-263
- **Plan ref:** —

### NL-338 NativeAOT publish of the daemon fails on the configuration binder generator
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Options/BitcoinOptions.cs`, `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs:192`
- **Evidence:** `dotnet publish src/NLightning.Daemon -c Release.Native -f net11.0 -r osx-arm64 -p:PublishAot=true` (SDK 11 rc.1) fails with 6x CS9035: the configuration-binding source generator (on with PublishAot) cannot build `BitcoinOptions`, whose members are `required`. Also SYSLIB1100/1101 for `NodeOptions` (`LightningMoney`, `IPAddress`, `Features`) and IL2026/IL3050/IL207x from reflection-based handler registration, EF migrations, the plugin loader and `SecureKeyManager` JSON (reported by W7-C; log was /private/tmp/w7-aot/net11.log). SDK 10 not compared.
- **Fix sketch:** Drop `required` from `BitcoinOptions` and validate at startup (or `-p:EnableConfigurationBindingGenerator=false`), fix the NodeOptions binding, then address or suppress the trim warnings and smoke-run the binary.
- **Blocks/Blocked-by:** Related NL-300, NL-155
- **Plan ref:** `NET11_PLAN.md` step 6

---

### NL-347 The Postgres case of the multi-node server-database theory is not run in the standard Docker cycle
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/MultiNodeHarnessTests.cs` (`Given_ServerDatabase_When_NodeRestarts_Then_ItReconnectsToTheStoredPeer`)
- **Evidence:** The standard cycle skips SQL Server containers, but the in-container xunit runner cannot exclude one case of a theory, so the gossip wave G-A integration excluded the whole theory and its Postgres case did not run. Update (gossip wave G-B, `5bbfbb5`): the G-B integration excluded the whole theory again, so its Postgres case did not run in this wave either. Update (gossip wave G-D, `48a8951`): excluded again (the whole theory), so its Postgres case did not run in this wave either.
- **Fix sketch:** Split the theory into one test per provider (or tag the SqlServer case with a trait) so the Postgres case runs with `!~SqlServer`.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-355 TwoNodeHarness restarts drop the channel announcement fields
- **Status:** fixed (b9314d3)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Harness/TwoNodeHarness.cs` (`RestartAsync`)
- **Evidence:** `RestartAsync` rebuilds the channel model without the announce flag, the peer's announcement signatures and our sent time, so a restart in the middle of the announcement is not proven in-process; only Docker Proof G1 (c) (our restart at 3 confirmations) covers it (reported by lane B1, gossip wave G-B). Update (gossip wave G-C, `4dc0f77`): the harness store saves and restores the announce flag, the peer's signatures and our sent time; a restart-between-halves proof was added (b9314d3).
- **Fix sketch:** Carry the announcement fields through the harness restart (or reload from the SQLite store) and add the restart-between-halves case.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 G1 proofs

### NL-356 Docker Proof G2 (d) (stale channels) and the spent-channel routing check are missing
- **Status:** fixed (3dbc8be)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Gossip/GraphStoreFlowTests.cs`
- **Evidence:** Wave G-B proved G0, G1 (a)-(c) and G2 (a)-(c). Proof G2 (d) (a channel without updates for two weeks excluded, then forgotten) has no Docker test, and G2 (c)'s check that the spent channel is excluded from `getroute` waits on G4-T4; G1 (d) is NL-255 (reported by the G-B integrator). Update (gossip wave G-C, `4dc0f77`): Docker `GraphStoreFlowTests.Given_StaleAfterTwoMinutes_When_CarolStopsUpdating_Then_HerChannelsLeaveOurRoutes` proves G2 (d) through `getroute` (IPC 19): carol's channels leave our routes after `Gossip:StaleAfter` (2 min) while alice-bob stays routable (3dbc8be, 412f1d5). The spent-channel `getroute` check of G2 (c) is not asserted in Docker (that node has no channel, so no route at all); `GraphPathfinder` skipping spent channels is unit-tested.
- **Fix sketch:** Add G2 (d) with a mocked clock (`TimeProvider`) or `setmocktime`, and the `getroute` exclusion once G4-T4 lands.
- **Blocks/Blocked-by:** Part of NL-099; related NL-255
- **Plan ref:** BOLT7 Proof G2 (d)

### NL-357 CLN Proof G3 (d) depends on CLN's seeker timing
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnGossipTests.cs` (`Given_AnnouncedChannels_When_ClnQueriesOurScids_Then_WeAnswerWithFullInformation`)
- **Evidence:** CLN v26.06.8 has no `dev-query-scids`, so the proof waits for CLN's own seeker to query N1 (it range-probes only its first peer and follows an unknown SCID at its 60 s check, asking the reporting peer about 80 % of the time); N1 nudges it every 30 s with a `channel_update` for an unknown SCID, with a 4 min timeout (485a9aa, 4dc0f77). Green in the integration runs, but it can flake (reported by the G-C integrator).
- **Fix sketch:** Keep the nudge; if it flakes, raise the timeout or drive a query through a CLN plugin or a newer CLN with a dev query command.
- **Blocks/Blocked-by:** Related NL-099
- **Plan ref:** BOLT7 Proof G3 (d)

### NL-358 run-onchain.sh and run-abcd.sh run dotnet test on the host
- **Status:** fixed (1d561f2)
- **Severity:** low
- **Kind:** test
- **Location:** `scripts/run-onchain.sh`, `scripts/run-abcd.sh`
- **Evidence:** Both scripts call `dotnet test` from the host, which cannot reach the containers' bridge addresses on this machine (NL-276), so every run goes through the in-container runner by hand; `scripts/run-gossip.sh` already runs the test dll in an SDK container with `--network host` (reported by the G-C integrator). Update (gossip wave G-D, lane D3): `run-onchain.sh` and `run-abcd.sh` build on the host and run the test dll in `mcr.microsoft.com/dotnet/sdk:10.0` (`sdk:11.0` for net11.0) with `--network host`, like `run-gossip.sh`; extra arguments are xunit v3 runner arguments, logs go to `TestResults/{onchain,abcd}/` (1d561f2). The G-D integrator ran on-chain (with `-explicit on`) and ABCD 3x through them. They print totals only (NL-378).
- **Fix sketch:** Use the same in-container runner as `run-gossip.sh` (optionally behind a flag for hosts that can reach the bridge).
- **Blocks/Blocked-by:** Related NL-276
- **Plan ref:** —

### NL-359 Parallel Docker test processes collide on PortPoolUtil ports
- **Status:** fixed (1d561f2, aadb9d4)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/PortPoolUtil.cs` (ports 49100-49149)
- **Evidence:** The port pool is per process: two Docker test processes run at once (e.g. the LND suite and the CLN suite) hand out the same ports, and `CooperativeCloseFlowTests` failed with "Address already in use" in the G-C integration run; the class passed 7/7 alone (reported by the G-C integrator). Update (gossip wave G-D, lane D3): `PortPoolUtil` gives each process 50 ports: `NLTG_TEST_PORT_BASE` when set (1024..65486), else one of 200 slots in 20000-29999 chosen by (pid + FNV hash of the machine name) % 200, skipping ports already listened on; `PortPoolUtilTests` (12, not Docker, run in CI) (1d561f2); the pool test no longer drains the shared pool (aadb9d4). The integrator still ran one Docker process at a time (the fixtures share container names, NL-276).
- **Fix sketch:** Ask the OS for a free port (bind to port 0) or give each process its own range; until then, do not run Docker suites in parallel.
- **Blocks/Blocked-by:** Related NL-276
- **Plan ref:** —

### NL-367 Docker Proofs G4 (b) and (c) are not written
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Gossip/PublicPaymentFlowTests.cs`
- **Evidence:** Wave G-C wrote Proof G4 (a) and (d) (goal proofs (b) and (d)). G4 (b) (alice disables alice→carol, the next payment goes around it) and G4 (c) (alice raises her fee, the first attempt fails `fee_insufficient`, the retry succeeds and our graph keeps the old policy until the gossip arrives, D9) have no Docker test; the retry around a failing channel with the graph unchanged is proven in process (`GraphPaymentHarnessTests`) (found by the G-C ledger).
- **Fix sketch:** Add both cases to `PublicPaymentFlowTests` with `LndPolicyChanges`.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT7 Proof G4 (b)(c)

### NL-377 Bulk graph repository paths are not proven on SQL Server
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Gossip/GraphDbRepository.cs` (bulk upserts/deletes), `test/NLightning.Integration.Tests/Docker/SqlServerTests.cs`
- **Evidence:** The batched write-behind (66b4dbc) uses `List.Contains` over byte[]-converted keys, sent by EF 10 as scalar parameters in chunks of 500; proven on SQLite and Postgres, but the SQL Server container tests are skipped in the standard cycle and Docker tests do not run in CI, so the SQL Server bulk section of the graph round trip has not run (reported by lane D2, gossip wave G-D).
- **Fix sketch:** Run `SqlServerTests` (graph round trip, bulk section) once per schema-affecting wave, or add a SQL Server service to CI.
- **Blocks/Blocked-by:** Related NL-347
- **Plan ref:** BOLT7 G5-T3

### NL-378 The Docker runner scripts print only totals
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `scripts/run-gossip.sh`, `scripts/run-onchain.sh`, `scripts/run-abcd.sh`
- **Evidence:** The in-container xunit v3 runner prints no per-test PASS lines, so a run's evidence is its totals; a skipped or not-discovered test is visible only as a lower count (reported by the G-D integrator).
- **Fix sketch:** Pass `-reporter verbose` (or write a TRX/xUnit XML result to `TestResults/`) and print the per-test outcome at the end.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-382 GossipFloodTests flake under a loaded full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Gossip/Graph/GossipFloodTests.cs` (`Given_APeerFloodingInvalidSignatures_When_AnotherPeerSendsValidGossip_Then_TheFlooderIsBannedAndTheOtherUnaffected`)
- **Evidence:** Failed once in the wave O7 final Release run (7282 tests); passed 5 times alone and in 2 full project reruns, so a timing assumption under load, not an O7 regression (wave O7 integrator).
- **Update (wave spr, integrated at `a0800ac2`):** failed once in a Release.Native full run, passed alone (reported by the integrator).
- **Fix sketch:** Replace fixed waits with an awaited condition (ban recorded, valid gossip ingested) and a generous timeout.
- **Blocks/Blocked-by:** Related NL-099
- **Plan ref:** BOLT7 G5-T2

### NL-383 CombinedHtlcSigningProxy is no longer needed in the anchors resolver tests
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `test/NLightning.Application.Tests/Onchain/Resolvers/Local/AnchorTestWallet.cs`
- **Evidence:** X3 wrapped the signer in `CombinedHtlcSigningProxy` because `SignLocalHtlcTransaction` refused a multi-input HTLC tx; since the signer seam (658e086) the real signer signs input 0 of a combined anchors HTLC tx, so the proxy only hides it (wave O7 integrator).
- **Fix sketch:** Drop the proxy and run the anchors resolver tests on the real `LocalLightningSigner`.
- **Blocks/Blocked-by:** —
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O7-T3

### NL-394 GossipSyncManagerTests timed-out SCID query test flakes under a loaded full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Gossip/Sync/GossipSyncManagerTests.cs` (`Given_ATimedOutScidQuery_When_ItsLateEndArrivesDuringAnotherQuery_Then_NothingMoreIsAskedOfThatPeer`)
- **Evidence:** Failed once in lane Y2's full non-Docker run (7326 tests) and passed 3 of 3 times alone; not related to the lane's change, so a timing assumption under load like NL-382 (reported by lane Y2 in wave O7b; the integrator's final runs were green).
- **Fix sketch:** Replace fixed waits and timeouts with awaited conditions and a fake time provider.
- **Blocks/Blocked-by:** Related NL-382, NL-099
- **Plan ref:** BOLT7 G3

### NL-429 The MultiNodeHarness restart theory's Postgres case cannot run without its SQL Server case
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/MultiNodeHarnessTests.cs` (`Given_ServerDatabase_When_NodeRestarts...`)
- **Evidence:** xunit cannot filter out one data row, so skipping SQL Server (standard test cycle) also skips the Postgres restart case; it was not run in waves d12, rf1 and M6 (reported by the rf1 integrator).
- **Fix sketch:** Split the theory into one test per provider, or add a trait per row.
- **Blocks/Blocked-by:** Related NL-347
- **Plan ref:** —

### NL-434 Timing-bound tests in MissionControl and GraphPathfinder fail under a loaded full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Payments/Routing/MissionControlTests.cs`, `test/NLightning.Domain.Tests/Routing/GraphPathfinderTests.cs` (50 ms budget)
- **Evidence:** Both failed once in a full run under load and passed alone: a lower-bound assertion affected by clock decay and a 50 ms search budget (reported by lane R2).
- **Update (wave spr, integrated at `a0800ac2`):** the `GraphPathfinderTests` 50,000-channel budget case failed once in a Release full run, passed alone (reported by the integrator).
- **Fix sketch:** Use the fake clock in the MissionControl assertion and a generous or count-based budget in the pathfinder test.
- **Blocks/Blocked-by:** Related NL-382
- **Plan ref:** —

### NL-445 GossipIngressTests retry case failed once under a loaded full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Gossip/Graph/GossipIngressTests.cs` (`Given_AnAnnouncementGivenUpAfterItsRetries_When_ItsUpdateArrivesAgainLater_Then_ItIsMissedUntilStored`, `Assert.Empty` at line 549)
- **Evidence:** Failed once in a full non-Docker run (collection `[110x1x0]` not empty) and passed when its class ran alone (36/36); not reproduced in the integrator's runs (reported by lane M6-C).
- **Fix sketch:** Look for a wall-clock or scheduling dependency in the retry give-up path and drive it with the fake clock.
- **Blocks/Blocked-by:** Related NL-434, NL-382
- **Plan ref:** —

### NL-449 The onion message harness rate-limit test fails when run alone
- **Status:** fixed (d2fcb082, a3db0c0a)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/OnionMessages/OnionMessageHarnessTests.cs` (`Given_ARateLimitOf20PerSecondAtBob_When_AliceSends25InOneSecond_Then_BobDropsTheLast5`)
- **Evidence:** Fails every time when its class or the test alone is run (`OperationCanceledException` in `RecordingHandler.WaitForAsync` after 10 s: carol never sees 20 messages); reproduced 3/3 by the ledger agent at `a3445f3f` (net10.0 Release, `--filter FullyQualifiedName~OnionMessageHarnessTests`), while the whole Application.Tests project passes (2263/2263), so the order or timing of the other tests hides it. Lanes B12-C and B12-D saw it fail on `6f4bdaad`. Suspected, not verified: the M6 onion-message outbox cap on the alice->bob link (wired in 9639b7cf/641a5fff) drops some of the 25 back-to-back sends before bob's limiter is reached. Update (wave lh1, `a6c633f9`): a residual race remained: the test could advance the clock before Bob had seen the last 5 messages, so a late one was admitted (4 drops instead of 5, once in a loaded full run). The test now waits until Bob has counted 5 rate-limit drops before advancing (a3db0c0a); 5 class runs in a row green (lane l2).
- **Fix sketch:** Drain the harness outbox between sends or raise its cap in this test, and assert on the outbox drops separately; add the test to the known-flake list until then. Root cause: `AddOnionMessageServices` registers the production `OnionMessageRateLimiter` by default (per-peer burst 20, system clock), so carol (meant to be unlimited) dropped bob's 21st message whenever the test finished within a real second; the full project ran slowly enough to refill the bucket. Fixed by giving carol its own frozen-clock limiter and asserting it drops nothing; passes alone 5/5 and in its class.
- **Blocks/Blocked-by:** Related NL-446, NL-442
- **Plan ref:** `BOLT12_PLAN.md` OM2-T2, OM3

### NL-456 WalletBalanceIpcResponse raises three MsgPack017 build warnings
- **Status:** fixed (04899411)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Transport.Ipc/Responses/WalletBalanceIpcResponse.cs:17,26,31`
- **Evidence:** Three `init` properties with initializers: MessagePack resets them to the type default when the key is missing (MsgPack017). Present since the anchors reserve fields (41ce3200, wave O7b); the gates grep only `warning CS`, so the baseline missed them. Seen by the B12 integrator and reproduced by the ledger agent with a clean Release build of `NLightning.Transport.Ipc`.
- **Fix sketch:** Drop the initializers (or make the members required) so an older client or daemon cannot silently read the default; count analyzer warnings in the gate. Fixed: the three `LightningMoney` members are `required` without initializers (the daemon sets every field; keys 0-5 unchanged); a clean Release build shows only the 5 CS86xx of NL-171.
- **Blocks/Blocked-by:** Related NL-171
- **Plan ref:** —

### NL-465 PaymentHarnessTests short-timeout case fails under a loaded Release.Native run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Payments/Send/PaymentHarnessTests.cs` (`Given_ShortTimeout_When_TheOutcomeIsLate_*`)
- **Evidence:** Failed once in the wave lh1 integration (Release.Native, full run): the outcome was Failed instead of InFlight with its 50 ms timeout under load; 5 reruns passed (reported by the integrator). Seen again in wave qit by lane IT-A (a loaded full Release run; passed alone).
- **Fix sketch:** Drive the timeout from a controllable clock or widen the gap between the timeout and the late outcome.
- **Blocks/Blocked-by:** Related NL-434
- **Plan ref:** —

### NL-466 GossipGraphReloadTests fail under a loaded full run (Release and Release.Native)
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Persistence/GossipGraphReloadTests.cs` (`Given_GraphWithKnownFundingTxIds_*`)
- **Evidence:** Failed once in the first Release run of the wave lh1 integration and passed on 3 reruns and in the final full run; the error message was not captured, so it needs a repro (reported by the integrator). Wave qit: `Given_ASpentChannel_When_TheNodeRestartsAndBlocksPass_Then_ThePrunerRemovesItFromTheDatabase` (the pruner case, line 109) failed in both full Release.Native runs and passed 11/11 alone (reported by the integrator). Wave sp1: failed again in a loaded full run and passed with its class alone (reported by the integrator).
- **Update (wave spr, integrated at `a0800ac2`):** `GossipGraphReloadTests` failed again in loaded full runs (x2 Release.Native, x1 Release) and passed alone (reported by the integrator).
- **Update (lane accrbf):** `Given_GraphFromCapturedGossip_*` and `Given_ASpentChannel_*` failed in the full Release run; the class passed alone.
- **Fix sketch:** Loop the class under load to reproduce and capture the failure.
- **Blocks/Blocked-by:** Related NL-434, NL-445
- **Plan ref:** —

### NL-469 ReestablishFlowTests could not find alice among the ready LND nodes in a full LND suite run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/ReestablishFlowTests.cs:64` (`Given_OurNodeRestarts_*`, `GetAlice`)
- **Evidence:** In the wave qit full LND suite run (73/74) `GetAlice` found no alice among the fixture's ready LND nodes; the class alone passed 3/3 (reported by the integrator). Likely a shared-fixture ordering issue after another class restarted or replaced a container.
- **Update (wave sp2, `31950b81`):** failed again in the full LND run (2 tests); the class alone passed.
- **Fix sketch:** Have `GetAlice` wait for alice to be ready (bounded) or re-resolve it from the fixture instead of failing at once.
- **Blocks/Blocked-by:** Related NL-262, NL-276
- **Plan ref:** —

### NL-471 OnionMessageServiceTests next-path-key override case failed once in a full solution run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/OnionMessages/OnionMessageServiceTests.cs` (`Given_ANextPathKeyOverride_When_BobForwards_Then_HeSendsThatPathKey`)
- **Evidence:** Failed once in a full solution run of lane Q-A (wave qit) and passed alone and on a project re-run; the failure text was not captured.
- **Fix sketch:** Loop the class under load to reproduce; look for a wait on the forwarded message without a bounded, event-driven condition.
- **Blocks/Blocked-by:** Related NL-449
- **Plan ref:** —

### NL-472 ChannelRestoreServiceTests connect-budget case fails under a loaded Application.Tests run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Backup/ChannelRestoreServiceTests.cs` (`Given_TheConnectBudgetSpent_When_Restored_Then_TheOtherAddressesAreTriedInTheBackgroundAtOnce`)
- **Evidence:** Failed under a loaded full Application.Tests run of lane Q-B (wave qit) and passed alone.
- **Update (wave sp2, `31950b81`):** failed again in an earlier full run of the sp2 integration and passed alone; the integrator filed it as NL-485, a duplicate of this entry.
- **Fix sketch:** Drive the connect budget from a controllable `TimeProvider` instead of wall-clock waits.
- **Blocks/Blocked-by:** Related NL-434, NL-465
- **Plan ref:** —

### NL-477 ClnQuiescenceTests in-flight case fails: CLN errors on our stfu when its fulfill crosses it
- **Status:** fixed (f30f3be3; not reproduced since D13)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnQuiescenceTests.cs` (`Given_OurHtlcInFlight_*`)
- **Evidence:** Fails every run in the wave sp1 integration and also on the pre-wave `03b9a664`, so not a sp1 regression: we send `stfu` only after our add is committed and revoked both ways, CLN's `update_fulfill_htlc` crosses it, and CLN answers with the error "STFU but you still have updates pending?" although its own fulfill is the only pending update (reported by the integrator). Looks like CLN behaviour (compare NL-467).
- **Update (wave sp2, `31950b81`):** still fails in the full CLN run and alone; the only CLN failure besides the order-dependent NL-486.
- **Update (wave spr, integrated at `a0800ac2`):** failed again in the wave spr full CLN run and alone; still the only CLN failure (reported by the integrator).
- **Update (wave d13, `f30f3be3`):** passed in both full CLN runs of wave d13 (the test node sets `OptionQuiesce` itself, but since D13 it also advertises `option_splice` by default, so CLN v26.06.8 sees a splicing peer; CLN's quiescence paths differ for one, compare NL-468). Closed as not reproduced; reopen if it fails again.
- **Fix sketch:** Capture the message order; if our `stfu` is valid per BOLT 2, ask CLN's splicing lead (plan §10) and adapt the proof; otherwise delay our `stfu` until no update of the peer is pending.
- **Blocks/Blocked-by:** Related NL-042, NL-467, NL-470
- **Plan ref:** `SPLICING_PLAN.md` Proof Q

### NL-482 PeerManagerConnectTests two-node connect case fails under a loaded full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Node/Managers/PeerManagerConnectTests.cs` (`Given_TwoNodes_When_OneConnectsToTheOther_*`)
- **Evidence:** Failed once in a full run of the wave sp1 integration with "Expected init as the first message" and passed with its class alone (reported by the integrator).
- **Update (wave spr, integrated at `a0800ac2`):** failed once in the first Release full run, passed alone (reported by the integrator).
- **Fix sketch:** Loop the class under load; look for a read that races the init exchange.
- **Blocks/Blocked-by:** Related NL-434
- **Plan ref:** —

### NL-485 ChannelRestoreServiceTests connect-budget case fails under a loaded run (sp2 integration)
- **Status:** duplicate (NL-472)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Backup/ChannelRestoreServiceTests.cs` (`Given_TheConnectBudgetSpent_*`)
- **Evidence:** The wave sp2 integrator filed this flake as new; it is NL-472.
- **Fix sketch:** See NL-472.
- **Blocks/Blocked-by:** Duplicate of NL-472
- **Plan ref:** —

### NL-486 CLN fee-estimate cases fail after the splice tests in a full CLN run
- **Status:** fixed (de620e7b, 12d9e451)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnCloseTests.cs` (`Given_ChannelClnFunded_*`), `ClnInteropTests.cs` (`Given_ClnFundsAtItsOwnEstimate_*`)
- **Evidence:** Both failed in both full CLN runs of the wave sp2 integration and passed alone (15/15): after the splice tests CLN's own fee estimate is 2495 sat/kw where the tests expect an idle chain (they also failed once in wave sp1) (reported by the integrator).
- **Update (wave spr, integrated at `a0800ac2`):** fixed by lane SPR-E: `ClnInteropTests.Given_ClnFundsAtTheFeerateFloor_*` (renamed) pins CLN's funding feerate at 253perkw, and `ClnCloseTests.Given_ChannelClnFunded_*` asserts CLN's fee is the top of its `fee_range` with the bottom at most the fee (idle [fee, fee], busy [fee / 2, fee]); both green in the wave spr full CLN run.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-021
- **Plan ref:** —

### NL-491 InMemoryChannelRepository raises OnChannelUpdated with null arguments
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Harness/TwoNodeHarness.cs` (`InMemoryChannelRepository`)
- **Evidence:** Harness tests cannot observe `ChannelUpdateService` reactions because the in-memory repository raises `OnChannelUpdated` with null args; `ChannelUpdateServiceSpliceTests` uses a mocked repository instead (reported by lane SP2-B).
- **Fix sketch:** Raise the event with the channel as the real repository does.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-496 Splice test coverage gaps left by wave sp2
- **Status:** open (partial: de620e7b, a0800ac2)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Splicing/SpliceConformanceTests.cs`, `RetiredScidForwardTests.cs`, `test/NLightning.Integration.Tests/Docker/Onchain/OnchainSpliceTests.cs`, `Docker/Interop/Cln/ClnSpliceReestablishTests.cs`
- **Evidence:** Not covered: the SP-T-04, SP-T-05 and SP-T-08 restart variants of SP2-A-T3 (SP-T-03, SP-T-06 and the both-CS-lost case have them; NL-484 unblocked the rest) and a crash at every SP-I7 save; one test with a real splice lock followed by a real HTLC over the old SCID (split between `RetiredScidForwardTests` and `SpliceAnnouncementHarnessTests`; Proof SP2 (c) covers it against CLN); a Docker case where a close is reorged out and the splice is mined instead, and `OnchainSpliceTests` (c) with an HTLC in flight; a unit test of `LocalCommitResolver` HTLC transactions on a splice funding; LND 0.20 learning a spliced channel (Proof SP2 (c) runs against CLN only) (reported by lanes SP2-A, SP2-B, SP2-C, SP2-D).
- **Update (wave spr, integrated at `a0800ac2`):** LND 0.20 learning a spliced channel is proven: `Docker/SpliceLndObserverTests` (a public channel between two NLightning nodes spliced in; LND alice (peer) and bob (through alice's relay) list the new SCID with the new capacity and both policies, alice pays over it, both forget the old SCID) (lane SPR-E, de620e7b; the fundee's anchors reserve funded at integration, a0800ac2). Still open: the SP-T-04/05/08 restart variants and a crash at every SP-I7 save, one test with a real lock then a real HTLC over the old SCID, a Docker close-reorged-out-for-the-splice case, `OnchainSpliceTests` (c) with an HTLC in flight, and a unit test of `LocalCommitResolver` HTLC transactions on a splice funding.
- **Fix sketch:** Add the cases, most usefully the LND check of a spliced public channel before D13.
- **Blocks/Blocked-by:** Part of NL-021
- **Plan ref:** `SPLICING_PLAN.md` SP2-A-T3, Proof SP2 (c), (d)

### NL-499 DualFundRefusalTests first-commitment case fails under a loaded Application.Tests run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/DualFunding/DualFundRefusalTests.cs` (`Given_AFirstCommitmentSignedWithAnHtlcSignature_*`)
- **Evidence:** Failed in all three full Application runs on lane SP2-A's branch (the channel was gone from Alice's memory at line 102, apparently the harness's 2 s open timeout under load) and passed alone and in its namespace; it passed in the one full run on the contracts base (reported by lane SP2-A; probable, not proven, load flake).
- **Fix sketch:** Loop under load; replace the 2 s open timeout with an event-driven wait.
- **Blocks/Blocked-by:** Related NL-434
- **Plan ref:** —

### NL-500 OnionMessageHarnessTests graph-path case failed once in a full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/OnionMessages/OnionMessageHarnessTests.cs` (`Given_AGraphPathToCarol_*`)
- **Evidence:** Failed once in a full run of lane SP2-A (wave sp2); not reproduced (reported by lane SP2-A).
- **Fix sketch:** Loop the class under load.
- **Blocks/Blocked-by:** Related NL-449, NL-471
- **Plan ref:** —

### NL-501 GossipSyncManagerTests late-end case failed once in a full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Gossip/Sync/GossipSyncManagerTests.cs` (`Given_ATimedOutScidQuery_When_ItsLateEndArrivesDuringAnotherQuery_Then_NothingMoreIsAskedOfThatPeer`)
- **Evidence:** Failed once in lane SP2-C's full loaded run and passed 3 of 3 with its class alone (reported by lane SP2-C).
- **Fix sketch:** Drive the query timeout from a controllable `TimeProvider`.
- **Blocks/Blocked-by:** Related NL-434
- **Plan ref:** —

### NL-504 NormalOperationFlowTests trimmed-HTLC case failed once in a full LND run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/NormalOperationFlowTests.cs` (`Given_TrimmedHtlc_*`)
- **Evidence:** Failed once in the wave spr full LND suite (72/73) and passed with its class alone 8/8 (reported by the integrator).
- **Fix sketch:** Capture the failure message on the next occurrence and check the LND wait.
- **Blocks/Blocked-by:** Related NL-469
- **Plan ref:** —

### NL-505 ABCD fixture: LND david answers 'funding failed due to internal error' to carol's open
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/Abcd/` (network build)
- **Evidence:** One of two failed ABCD attempts in the wave spr integration failed while building the test network: LND david refused carol's open with 'funding failed due to internal error'; the other failed because bitcoind was still loading its banlist when the fixture started (the known LNUnit start flake). The next three runs were 10/10 (reported by the integrator).
- **Fix sketch:** Retry the fixture's LND-to-LND open once on this error and log david's funding state.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-508 Splice RBF test gaps left by wave spr
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Harness/TwoNodeHarness*`, `SpliceTestKit`, `test/NLightning.Daemon.Tests/` (`BumpSpliceIpcHandlerTests`)
- **Evidence:** The harness's source-less signer registration (`channel.GetSigningInfo`) has no pending fundings, so a harness node restarted with signed but unlocked splice attempts cannot sign batches until `RegisterFunding` is called again (production loads fundings through `ChannelSigningInfoDbRepository`); the `bumpsplice` refusal cases are proven only as IPC error mapping over a mocked `ISpliceService`, not through the real `SpliceService` and `SpliceRules.CheckSendRbf` (reported by lanes SPR-A and SPR-B).
- **Fix sketch:** Register pending fundings in the harness at restart; add an IPC test over the real `SpliceService` for no pending splice, feerate below the floor, max fee exceeded and quiescence not negotiated.
- **Blocks/Blocked-by:** Part of NL-021; related NL-496
- **Plan ref:** `SPLICING_PLAN.md` SPR-T3

### NL-512 DualFundSafetyTests signed-first timeout case failed once in a loaded full run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/DualFunding/DualFundSafetyTests.cs` (`Given_WeSignedFirst_When_TheOpenTimesOutWithoutThePeersTxSignatures_Then_TheChannelIsKept`)
- **Evidence:** Failed once (13 s) in lane SPR-B's loaded full run and passed alone. Lane SPR-E raised the harness's default open timeout to 60 s for the same wall-clock watchdog cause in `Given_TheDefaultOptions_*` (7c4bbde5), which may cover this case too (reported by lanes SPR-B and SPR-E).
- **Fix sketch:** Drive the open watchdog from a controllable `TimeProvider`.
- **Blocks/Blocked-by:** Related NL-499
- **Plan ref:** —

### NL-513 SpliceConformanceTests one-side commitment_signed reconnect case failed once in a loaded run
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/Channels/Splicing/SpliceConformanceTests.cs` (`Given_OnlyOneSideSentCommitSig_When_Reconnected_Then_TheSpliceIsAbortedOnBothSides`)
- **Evidence:** Failed once in lane SPR-D's full loaded Application run; the class passed alone twice and the full project rerun was green (reported by lane SPR-D).
- **Fix sketch:** Capture the failure on the next occurrence; check for a wall-clock wait in the harness pump.
- **Blocks/Blocked-by:** Related NL-496
- **Plan ref:** —

## Docs

### NL-182 REPO_MAP.md has stale pre-M1 claims
- **Status:** fixed (docs commit "update issue ledger and plans after swarm fixes")
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `docs/agents/REPO_MAP.md` §3.2, §3.4, §6.1, §10.1, §10.2
- **Evidence:** Still says ICryptoProvider has no raw ChaCha20/HMAC, TlvStreamSerializer is a closed switch, BigSize isn't canonical, update_add_htlc uses the UpfrontShutdownScript constant (all fixed; see NL-016, NL-017, NL-029, NL-085). REPO_MAP §1/§3/§4/§5/§8/§9/§11 refreshed; §10 now points at this ledger.
- **Fix sketch:** Point §10 at this ledger and trim the duplicated bug tables.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-183 Docs say blinded-onion-message vector is unused
- **Status:** fixed (docs commit "update issue ledger and plans after swarm fixes")
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `docs/agents/BOLT_COVERAGE.md` (BOLT 4 vectors row), `docs/agents/ONION_ROUTING_PLAN.md` (open follow-ups)
- **Evidence:** `OnionVectorTests.Given_BlindedOnionMessageVector_When_PeelingChain_Then_EachNextPacketMatches` uses it (M2 review). Only `route-blinding-test.json` is unused.
- **Fix sketch:** Correct both lines.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-184 test/CLAUDE.md onion section says there are no BOLT 4 tests
- **Status:** fixed (docs commit "update issue ledger and plans after swarm fixes")
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `test/CLAUDE.md:53-56`
- **Evidence:** BOLT 4 tests now exist in Integration/Domain/Infrastructure/Serialization/Bitcoin test projects.
- **Fix sketch:** Rewrite the section to list them.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-185 VERSIONING.md, CONTRIBUTING.md, Integration.Tests README and CLI help text are stale
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `VERSIONING.md`, `CONTRIBUTING.md`, `test/NLightning.Integration.Tests/README.md`, `src/NLightning.Client/Utils/ClientUtils.cs`
- **Evidence:** CONTRIBUTING says "master" (default is `main`); client help calls the binary `nltg` (it is `NLightning.Client`) and says the cookie file is `nltg.ipc` (it's `nltg.cookie`). Partial (docs commit "update issue ledger and plans after swarm fixes"): CONTRIBUTING, VERSIONING and the Integration.Tests README are current; the cookie text was already fixed. Remaining: usage texts call the binary `nltg` while the assembly is `NLightning.Client` (decide on `AssemblyName`).
- **Fix sketch:** Update text; set `AssemblyName` if `nltg` is intended.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-186 Agent docs stale after onion M1/M2
- **Status:** fixed (5583af2, 2745e9d)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `CLAUDE.md`, `docs/agents/*`, `src/*/CLAUDE.md`
- **Evidence:** Root CLAUDE.md, BOLT_COVERAGE, ONION plan and per-project guides updated for M1/M2.
- **Fix sketch:** Done (residue in NL-182, NL-183, NL-184).
- **Blocks/Blocked-by:** —
- **Plan ref:** —
