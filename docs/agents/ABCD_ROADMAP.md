> Execution roadmap for the ABCD goal (LND Alice → NLightning Bob → NLightning Carol → LND David). Written 2026-09-25 against wip/fafo @ 3c625e1. Decisions in §4 adopted with the recommended defaults (route hints, NLightning-funded channels, in-process Bob/Carol, extended shared fixture). Status per wave is tracked below as waves land (latest: wave 7 @ `4c37998`).

## Status

### Wave 7: integrated into `wip/fafo` @ `4c37998` (2026-09-26), gates GREEN

Wave 7 wired attribution_data end to end, made final-hop HTLCs of our invoices claimable on chain (the last blockers of the O6-T4 gate on the switch side) and cleared the chain-monitor test flake. Three lanes, each with a review step: W7-A attribution_data wiring (migration owner), W7-B final-hop on-chain claims and HTLC-set commitment, W7-C flakes and gates. All 11 lane commits were cherry-picked with `-x` without conflicts (W7-A first). Integrator commits:
- 4751a26: the W7-A hold-time test (99 ms, expected 0) was flaky because `SteppedTimeProvider` added wall-clock time on top of the step; it now uses a manual clock.
- 672ed61: `HtlcSwitch` takes an optional `IAttributionDataService` and uses W7-A's seam (erring and final-node failures, final-hop and set fulfills with the settle still in the fulfill's save, wrapped downstream failures incl. malformed and on-chain timeout, forwarded fulfills via `WrapFulfillment`) only when `NodeOptions.Features.OptionAttributionData` is not No and the incoming add had no `path_key`; `AddOnionReplayBlockPruner()` in `AddNltgNodeServices`, started after and stopped before the chain monitor by `NltgDaemonService` and `NLightningTestNode`; 3 `HtlcSwitchTests`, a Daemon composition check; guides.
- 4c37998: Docker `AttributionFlowTests.Given_ThreeNodesAdvertisingAttribution_…`: three NLightning nodes with the feature on forward through the production switch; both fulfills carry TLV 1 and the payer records both hops' hold times.

Gates at `4c37998` (the ledger agent re-ran the net10.0 Release non-Docker tests at `4c37998`: 6097/6097):
- Build: Release and Release.Native under SDK 10 and SDK 11, 0 errors, the same **5** CS86xx warning sites (NL-171).
- `dotnet format --verify-no-changes` clean under SDK 10 and SDK 11.
- Tests: **6097** non-Docker tests per config and framework, 0 failures, 0 skips (Domain 2142, Application 1148, Integration 573, Serialization 487, Infrastructure 380, Infrastructure.Bitcoin 777, Bolt11 278, Daemon 312): net10.0 on the host and net11.0 in the sdk:11.0 container, Release and Release.Native. The Long simulator passes. `HasPendingModelChanges()` false for all three providers after `AddAttributionData` (Integration tests).
- Docker (in-container runner, `--network host`): LND suite **64/64** (incl. Postgres 8, SqlServer 8, `AttributionFlowTests` 5; on net10.0 63/63 before the new proof, then `AttributionFlowTests` 5/5 separately), `Docker.Utils` 2/2, CLN interop **17/17**, ABCD **3 × 10/10**, `Docker.Onchain` **19/19** incl. `OnchainFinalHopTests` (2 `Explicit` not run); identical on net10.0 and net11.0. **114** Docker tests in total (LND 64, Utils 2, CLN 17, ABCD 10, Onchain 21).

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W7-A attribution wiring (migration owner) | done: migration `AddAttributionData` (3 providers: `Htlcs.AttributionData`/`FulfillmentPayload`/`AddedAt`, `PaymentHops.HoldTimeMs`; no data step; seeded round trip on SQLite, Postgres, SQL Server); engine, `IChannelOperations` (`FailHtlcAsync(AttributedErrorPacket)`, `FulfillHtlcAsync(AttributedFulfillment, ...)`, `GetHoldTimeAsync`), handlers, retransmission, `MessageFactory` TLV 1/3; origin verification and hold times in `PaymentService`; `AttributionHarnessTests`; Docker `AttributionFlowTests` (LND 0.20 lacks the feature). Review: a valid preimage is committed before the channel fails over an oversized payload; fulfill hold times only on the matching route. Skipped: taking `OptionAttributionData` out of `ExperimentalFeatures` | 3a54e11, 06b906c, e64da4e | NL-022, NL-325 fixed; NL-326 fixed with integration; NL-072 partial; new NL-332, NL-333, NL-334 |
| W7-B final hop on chain | done: the switch accepts a final-hop HTLC of a Failed/OnchainResolving channel and commits it with the preimage on the incoming `HtlcRecord.KnownPreimage` in the settle's save; `FinalHopClaims.GetAcceptedPreimageAsync` for both resolvers (Settled invoice with that preimage, no fail removal); every part of a set committed before the settle, marks taken back when a set is not settled, a failed settle retried by the timer; 0x400F for HTLCs outside a Settled set; Docker `Onchain/OnchainFinalHopTests` | 7ca5c60, db00321, 72e7f49, a3cf0ce | NL-316, NL-322, NL-323 fixed; new NL-335, NL-336, NL-337 |
| W7-C flakes and gates | done: NL-310 root cause (a local Mutinynet bitcoind publishing on the tests' ZMQ port) fixed with serialized `ProcessNewBlockAsync`, blocks above the tip dropped and `SilentZmqEndpoint` (stress 252/252); `OnionReplayBlockPruner` (coalesced, retries after a failure); `PaymentModel` doc. Partial: NativeAOT publish on SDK 11 reported, not fixed | 548ba85, aa41f2b, ef03b12, 368a057 | NL-310, NL-327 (with integration), NL-328 fixed; NL-300 note; new NL-338, NL-340 |
| Integration | test clock fix, switch attribution seam, pruner wiring, three-node attribution proof, guides | 4751a26, 672ed61, 4c37998 | NL-326, NL-327 fixed; new NL-339 |

Ledger note: the lanes' proposed IDs collided (W7-B suggested NL-331/NL-332; NL-331 already existed); the wave's new items are NL-332..NL-340. The hold times of in-memory MPP parts (W7-A) are a note under NL-321; the pre-NL-323 upgrade limit is a note under NL-323; the stale `IOnionReplayStore` remark is NL-340. NL-072 stays **open (partial)**: everything is wired, but `OptionAttributionData` stays experimental until an interop proof beyond NLightning exists (NL-332).

Deviations accepted in wave 7:
- `OptionAttributionData` stays in `ExperimentalFeatures` and default No: LND 0.20 does not implement it (proven by `AttributionFlowTests`), so the planned LND interop gate cannot pass (NL-332).
- A fulfill refused because the peer is away now commits the set and settles the invoice at once (the part carries the preimage and is fulfilled on replay or claimed on chain); before, the invoice stayed Open until the replay.
- A crash between the set's marks and the settle leaves the invoice Open with marked parts; the marks are honored only once the invoice is Settled, and replay normally completes the set.
- Pre-NL-323 settled sets are failed with 0x400F on replay after an upgrade (no reconciliation; HTLCs are regtest-only).
- A blinded forward drops the downstream fulfillment_payload (NL-339, left for M5).
- Out-of-lane touches: `PaymentHop.HoldTime`/`PaymentModel.RecordHoldTimes` (Domain), `NLightningTestNode.ConfigureServices` hook, `PaymentSchemaRoundTrip.SeedAsync` internal (W7-A).

### Carried into wave 8

- **Mainnet gate (NL-094 O6-T4):** NL-311 (untracked resolution watches after a crash), NL-320 (upstream HTLCs of future/unknown closes), NL-337 (`HtlcExpiryMonitor` final-hop PreimageKnown from the record's mark); then open the HTLC gate on every network (template and default together).
- **Wave 7 follow-ups:** NL-335 (expired invoice at the on-chain decision), NL-336 (`DustExposureHtlcSwitch` swallows the on-chain decision), NL-333 (retry policy ignores the attribution blame), NL-334 (reverted fulfill loses attribution), NL-340 (replay store doc), a three-node harness test of the forward-then-fail case of NL-325.
- **attribution_data gating:** decide on NL-332 (un-gate on the NLightning proof plus CLN/Eclair, or wait for LND); NL-339 with M5.
- **Payments persistence:** per-part MPP send rows (NL-321, also for hold times), forward failure reasons and an SCID map (NL-137).
- **BOLT 5 follow-ups:** NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330, refused-broadcast abandonment for funding txs (NL-294, NL-259); O7 anchors (NL-314, `OptionAnchors`), O8 mempool (NL-098).
- **Close follow-ups:** decide the `OptionSimpleClose` default (NL-285), NL-279, NL-286, NL-045, NL-277, wallet address reuse (NL-280).
- **Switch/monitor:** NL-265, NL-266, NL-267, NL-268, NL-273, NL-274, NL-290, NL-216, NL-298.
- **Not started:** route blinding (M5, NL-079), BOLT 7 graph and pathfinding (NL-099), dual funding (NL-037).
- **Test infra and platform:** NativeAOT publish (NL-338; SDK 10 comparison, Wasm on SDK 11, NL-300), NL-276 (host route), NL-319, NL-331, NL-295, NL-262, NL-261.
- **Mutinynet / ops:** NL-303, NL-306, a longer live run with a force close; carried from earlier waves: NL-152 (disconnect IPC), NL-269, NL-260, NL-138, the Wasm risk.

### Wave 6: integrated into `wip/fafo` @ `3ce3cad` (2026-09-26), gates GREEN

Wave 6 finished the payment side of the node (persistent replay set, basic_mpp receive, retries/fee limit/MPP send), added `option_simple_close` and BOLT 5 O6 (fee bumping and reorg re-resolution), and built the attribution_data library. Six lanes: W6-A persistent onion replay set (migration owner), W6-B basic_mpp receive, W6-C payment retries and MPP send, W6-D attribution_data (M3b library), W6-E option_simple_close (N11), W6-F BOLT 5 O6; each lane had a review step. All 35 lane commits were cherry-picked with `-x` (none skipped). Merge conflicts were in docs, `ThreeNodeHarness` (both properties kept) and `FeatureOptions`/`FeatureOptionsTests` (both BasicMpp and OptionSimpleClose out of the experimental set). ba3db36 was committed with conflict markers in `src/NLightning.Application/CLAUDE.md`; 78a3beb removes them (history left as is). Integrator commits:
- 9692ba4: the daemon binds `Node:Switch` to `HtlcSwitchOptions`; `AddBitcoinInfrastructure` calls `AddOnionAttributionServices()`; `HtlcSwitchTests` moved to `IOnionReplayStore` (W6-A/W6-B seam); `PaymentHarnessTests` expects the split-failure reason now that invoices offer basic_mpp (W6-B/W6-C seam); a Daemon test for the binding and the registration.
- e8841d6: O5 (a) retries LND's force close while its restarted server is still starting (NL-319).
- a45d290: `ThreeNodeHarness.LockAudit` tracks locks per async flow (sibling tasks forked from one context were reported as one flow holding two locks; about half the runs of `Given_BobRestartsAfterDownstreamFulfill` on net11.0 Release.Native in Linux); regression test added, the nested-lock test still catches real nesting.
- 3ce3cad: root and `test/` CLAUDE.md for wave 6.
- Not taken: W6-A's optional block-driven replay prune (the store prunes lazily, NL-327). Kept: W6-F's final regtest-only HTLC default (O6-T4 gate closed, NL-316), W6-E's `--protocol.rbf-coop-close` fixture flag.

Gates at `3ce3cad`:
- Build: Release and Release.Native under SDK 10 and SDK 11 rc.1, 0 errors, the same **5** CS86xx warning sites (NL-171).
- `dotnet format --verify-no-changes` clean under SDK 10 (under SDK 11 clean before the last two test-only integrate commits, not re-run after them); `scripts/check-sln-configs.py` OK.
- Tests: **6009** non-Docker tests per config and framework, 0 failures, 0 skips (Domain 2135, Application 1081, Integration 566, Serialization 487, Infrastructure 380, Infrastructure.Bitcoin 770, Bolt11 278, Daemon 312): net10.0 on the host in both configs, net11.0 in the sdk:11.0 container in both configs. The Long simulator passes. One full run failed one `ChainMonitorPersistenceTests` test per config, not reproduced in 20 reruns (NL-310).
- `HasPendingModelChanges()` false for all three providers after `AddOnionReplaySet` (W6-A).
- Docker (in-container runner, `--network host`; sdk:10.0 for net10.0, sdk:11.0 for net11.0), identical on both frameworks: LND suite **57/57** (incl. Postgres 7, SqlServer 7, `MppFlowTests` 1, `PaymentRetryFlowTests` 3, `CooperativeCloseFlowTests` 5 with the simple-close proofs, Reestablish 3, NormalOperation 8, MultiNodeHarness 6), `Docker.Utils` 2/2, CLN interop **17/17**, ABCD **3 × 10/10**, `Docker.Onchain` **18/18** (O0 2, O2 2, O3 4, O4 5, O5 2, O6 3; the 2 `Explicit` O5 by-hand variants not run). 106 Docker tests in total. O5 (a) failed once on net10.0 before e8841d6; the suite then re-ran green.

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W6-A replay store (migration owner) | done: `PersistentOnionReplayStore` over `OnionReplayEntries` (migration `AddOnionReplaySet`, 3 providers, no data step), owned by the incoming HTLC and pruned past its cltv_expiry; `IOnionReplayCache` removed; `IncomingOnionProcessor.ProcessAsync(..., OnionReplayOwner? replayOwner, ...)` (owner required, null = no check); proofs: SQLite restart replay with a real onion, pruning, seeded-upgrade round trips on SQLite/Postgres/SQL Server, crash between the HMAC and secret saves; ABCD 10/10 with the store in the node. Partial: no Docker replay through LND (LND cannot re-send an onion) | a2b57ff, 53c038c | NL-078 fixed; NL-137 partial; new NL-327 |
| W6-B mpp receive | done: `HtlcSet` per payment hash (memory, rebuilt by replays), `mpp_timeout` after 60 s, whole-set failure on a `total_msat` mismatch, invoice settled in the first fulfill's save, `BasicMpp` Optional in init and invoices; Docker `MppFlowTests`; review fixes (settled-set parts at any replay height, hash-lock re-check, AmountReceived, switch disposal) | ba3db36, 451aa79, 78a3beb | NL-081 fixed; NL-109 note; new NL-322, NL-323 |
| W6-C send retries + mpp | done: per-call `PayInvoiceOptions` (fee limit, parts, timeout), retries by `PaymentRetryPolicy`/`PaymentRoutePlanner`, MPP split over direct channels and hints, `payinvoice --max-fee-msat/--max-parts/--timeout` (optional keys, no new `ClientCommand`); Docker `PaymentRetryFlowTests` (3); review fixes (row fee/route/HTLC of the settled parts, refusal classification, hint bounds with in-flight parts) | 7024f57, 703d83b, 74c9014 | NL-270 fixed; new NL-321, NL-328, NL-331 |
| W6-D attribution | partial: wire TLVs (attribution_data 920 bytes, fulfillment_payload) read strictly; `IAttributionDataService` create/wrap/verify for fail and fulfill, byte-exact at every hop against both inline BOLT 4 traces; not wired into the switch (out of lane) | 6d7e480, d5866a6 | NL-022, NL-072 partial; new NL-324 (fixed), NL-325, NL-326 |
| W6-E simple close | done: `closing_complete`/`closing_sig` (N11-T1), `SimpleCloseCoordinator` with the closer/closee rules and RBF (N11-T2), `ChannelManager` routing, Docker proof against LND 0.20 `--protocol.rbf-coop-close`; review fixes (recognise earlier simple-close txs after a script change, refuse an unsendable bump). `OptionSimpleClose` defaults to No | aa569c8, b67e065, a2331b8, 5b9ce68, d8dd76c, 7ebc93c | NL-020 fixed; NL-034 note; NL-285 partial |
| W6-F BOLT 5 O6 | done: per-target fee estimates, `SweepScheduler` RBF (O6-T1), rewind of watches and wallet UTXOs and re-resolution after reorgs, SCID move with a new channel_update (O6-T3), Docker O6 (a)-(c), stale-SCID reproducer regular; O6-T4 gate opened and reverted (NL-316) | 6a4eb7d, 7c437b3, e8bb45b, b330d8c, d3dfff8, fdd2d0a, 09052d0, 7dcf472, cde5ebb, 8fcea62, 0c0d5c8, 30584cf, 70cbd33, 6d4b625, 1530dfb | NL-292, NL-293, NL-296, NL-317, NL-096 fixed; NL-094, NL-294 partial; new NL-329, NL-330 |
| Integration | option binding and attribution registration, test seams, O5 (a) retry, lock audit per flow, guides | 9692ba4, e8841d6, a45d290, 3ce3cad | NL-310, NL-319 notes |

Ledger note: the lanes proposed colliding IDs (W6-B, W6-C and W6-D each suggested NL-321); they were renumbered NL-321..NL-331. W6-C's "NL-321 fixed" (the payment row's fee after a re-sent part) is recorded in NL-270's evidence; its remaining per-part persistence is NL-321. The W6-D review, W6-E and W6-F lane results reached the ledger agent truncated; their items were taken from their commits and the per-project CLAUDE.md files. NL-292 is marked fixed; its residue (a funding tx that never reconfirms keeps its SCID) is NL-329.

Deviations accepted in wave 6:
- `OptionSimpleClose` defaults to No although the Docker proof passed (N11-T2 said Optional after a proof); the legacy close stays the default path (NL-285).
- HTLC sets and MPP send parts are memory-only (no schema change): a duplicate HTLC with the right secret for a Settled invoice is fulfilled (BOLT 4 MAY, NL-323); send parts added in flight cannot be decrypted after a restart (NL-321).
- The replay store assumes one node process per database and prunes lazily; a replayed onion always fails (no MAY-redeem).
- The attribution_data library is registered but unused; `OptionAttributionData` stays experimental.
- HTLCs stay regtest-only by default (O6-T4 gate closed until NL-316).
- Out-of-lane touches accepted: `HtlcSwitch` owner argument and seven Application test files (W6-A); `FeatureOptions`, `InvoiceService`, `ThreeNodeHarness` hooks (W6-B); `IPaymentService` overload and new Domain models (W6-C); the test fakes `CrashingUnitOfWork`/`HookedUnitOfWork` (W6-A).

### Carried into wave 7

- **Mainnet gate and final-hop on-chain claims (NL-094 O6-T4):** claim our invoice's HTLCs on chain after the peer's force close (NL-316) and the held parts of a settled MPP set (NL-322), then open the HTLC gate on every network (template and default together); NL-311, NL-320.
- **attribution_data in the switch (M3b, NL-072):** migration owner: persist attribution_data (and fulfillment_payload) with HTLC removals and an add-received timestamp for hold times (NL-326); wire `IAttributionDataService` into `HtlcSwitch`/`PaymentService`; enforce the 32 KiB fulfillment_payload MUST (NL-325); then take `OptionAttributionData` out of `ExperimentalFeatures`.
- **Payments persistence:** per-part MPP send rows (NL-321), HTLC set membership (NL-323), block-driven replay pruning (NL-327), forward failure reasons and an SCID map (NL-137), PaymentModel docs (NL-328).
- **BOLT 5 follow-ups:** NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330, refused-broadcast abandonment for funding txs (NL-294, NL-259); later O7 anchors (NL-314, `OptionAnchors`), O8 mempool (NL-098).
- **Close follow-ups:** decide the `OptionSimpleClose` default (NL-285), NL-279, NL-286, NL-045, NL-277, wallet address reuse (NL-280).
- **Switch/monitor:** NL-265, NL-266, NL-267, NL-268, NL-273, NL-274, NL-290, NL-216, NL-298.
- **Not started:** route blinding (M5, NL-079), BOLT 7 graph and pathfinding (NL-099), dual funding (NL-037).
- **Test infra and platform:** NL-276 (host route), NativeAOT and Wasm on SDK 11 (NL-300), NL-310 (chain-monitor test race), NL-319 (move the LND retry helpers to shared utils), NL-331, NL-295, NL-262, NL-261.
- **Mutinynet / ops:** NL-303, NL-306, a longer live run with a force close; carried from earlier waves: NL-152 (disconnect IPC), NL-269, NL-260, NL-138, the Wasm risk.

### Wave 5: integrated into `wip/fafo` @ `1a5ab49` (2026-09-26), gates GREEN

Wave 5 wired BOLT 5 end to end: every force-closed channel is now detected and resolved on chain, penalties included, and proven against LND. Five lanes: W5-A funding-spend watcher + resolution executor (migration owner; three steps incl. a review), W5-B local commitment resolution, W5-C remote commitment resolution, W5-D revoked commitment penalties, W5-E Mutinynet live smoke. 25 lane commits were cherry-picked with `-x` in the order w5a, w5b, w5c, w5d, w5e. Not picked (identical to W5-A's port commit dabbd72 or its format fix): W5-B 7c16f07 and 86805dd (empty), W5-C d59308c and 44f7c0c, W5-D 92ee2c8. Conflicts, all resolved by keeping both sides: `src/NLightning.Application/CLAUDE.md`, `test/CLAUDE.md` (twice), `src/NLightning.Client/CLAUDE.md`, `ClientAppTests` InlineData rows. Three `integrate:` commits:
- dd2d64f: `AddApplicationServices` calls `AddLocalCommitResolutionServices()`, `AddRemoteCommitResolutionServices()` and `AddRevokedCommitResolver()` right after `AddOnchainServices()`; the daemon binds `LocalCommitResolverOptions`, `RemoteResolutionOptions` and `RevokedCommitResolverOptions` from `Node:Onchain`; the `(HtlcRemovalKind)4` casts in `OnchainHtlcRemovals`/`RemoteHtlcSwitchEvents` became `HtlcRemovalKind.OnchainTimeout`; W5-C's test `InMemoryOnchainStore` got W5-A's new repository members; `OnchainClientHandlerTests` checks the composed graph (exactly one resolver per commitment kind, none for Mutual/Unknown) and the option binding.
- a5b24e3: the end-to-end O5 (a) and (b) proofs are regular tests; O4 (b) retries LND's payment while LND's router lacks the fresh private edge (`PayUntilSentAsync`, NL-319).
- 1a5ab49: root, `test/` and `src/NLightning.Application` CLAUDE.md for wave 5.

Gates at `1a5ab49`:
- Build: Release and Release.Native under SDK 10 and SDK 11, 0 errors, the same **5** CS86xx warning sites (NL-171).
- `dotnet format --verify-no-changes` clean under SDK 10 and 11; `scripts/check-sln-configs.py` OK.
- Tests: **5718** non-Docker tests per framework and config, 0 failures, 0 skips (Domain 2110, Application 932, Integration 544, Serialization 466, Infrastructure 372, Infrastructure.Bitcoin 722, Bolt11 278, Daemon 294), green for Release and Release.Native on net10.0 and net11.0. The Long simulator passes.
- `HasPendingModelChanges()` false for all three providers after `AddBroadcastCommitmentNumber` (W5-A).
- Docker (in-container runner, `--network host`; sdk:10.0 for net10.0, sdk:11.0 for net11.0), identical on both frameworks: LND suite incl. Postgres/SqlServer **48/48**, `Docker.Utils` 2/2, CLN interop **17/17**, ABCD **10/10** (three runs in a row on net10.0), `Docker.Onchain` **14/14** (O0 smoke 1, O2 2, O3 4, O4 5, O5 2 end to end; 3 `Explicit` not run in the suite: the two O5 by-hand variants, which passed when run, and the stale-SCID reorg reproducer, which fails as expected, NL-292). The Docker suite now runs on net11.0 too (NL-300 partial).

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W5-A watcher + executor (migration owner) | done: `IOutputResolver` port + action model; migration `AddBroadcastCommitmentNumber` (3 providers); the commitment broadcast row in the Failed save and the handler path under the manager's lock; S1 at registration; `OnchainChannelWatcher` (O2-T5); `OnchainResolutionExecutor` with the 100-block rule and Closed (O6-T2); `forceclosechannel` (14) / `pendingsweeps` (15) IPC (O3-T6); Docker O2; review fixes (catch-up of spends mined before a watch, reorged funding spend height, unmapped-vout alerts, Closed staged on the DB copy, Closing never force-failed) | 152144c, d040654, 41f5fc2, c2ae40a, 567a3c1, 7f6ebd9, cbd99c6 | NL-271, NL-272, NL-297 fixed; NL-094, NL-294 partial; new NL-307, NL-308, NL-309, NL-310, NL-311, NL-312, NL-313, NL-320 |
| W5-B local resolution | done: `LocalCommitResolver` (to_local after CSV, HTLC-timeout/success, second-level sweeps; O3-T3), `HtlcRemovalKind.OnchainTimeout` + switch and `PaymentService` mapping (O3-T4); Docker O3 (a)-(d); review fixes (preimage read back from the spender, dust-floor sweeps, switch replay of a refused on-chain fail) | 5af263f, 7d3a6b3, cc207a8, 037c04b | NL-094 partial; new NL-314, NL-315; NL-280 note |
| W5-C remote resolution | done: `RemoteCommitResolver` on the shared port (to_remote, timeout and preimage claims incl. a forward's downstream preimage, remote-next and future commitments, events re-raised until the upstream has its removal; O4-T1..T3); Docker O4 (a)-(e) | 3794c0d, 6bd3645, 202341b | NL-094 partial; new NL-316, NL-317; NL-292 note |
| W5-D revoked resolution | done: `RevokedCommitResolver` + `PenaltyTransactionComposer` (batched/single/split penalties, second-level penalties, upstream resolution; O5-T2/T3); Docker O5 (a) LND channel.db rollback and (b) deterministic NLightning cheater; review fixes | e5a556d, bbc51b4, f4b83ff, aee5aed, 7189b71, d1f1721 | NL-094 partial; new NL-318; NL-294 note |
| W5-E Mutinynet smoke | done: `openchannel` push over IPC, UTXO wallet addresses loaded at startup, signer names an unsignable input, 21M BTC cap, `scripts/mutinynet/`, live smoke recorded in `MUTINYNET.md` (open with push, pay, receive, restart + reestablish, cooperative close) | d363373, 9a8a0f0, fefdce3, 5f4e0df, ce091a1 | NL-301, NL-302, NL-304 fixed; NL-306 partial; new NL-303; NL-305 duplicate of NL-280 |
| Integration | resolver registration and options, O5 proofs regular, O4 (b) LND retry, guides | dd2d64f, a5b24e3, 1a5ab49 | NL-300 partial (Docker on net11.0); new NL-319 |

Ledger note: NL-272 is marked **fixed** (detection and resolution are both wired and proven by Docker O2-O5); W5-A's own step 2 said "fixed/partial" only because the resolvers were in other lanes. NL-094 stays **open (partial)** for O6-O8. NL-301..NL-306 were cited by W5-E's commits and `MUTINYNET.md` before they had ledger entries; they keep those IDs, and the lanes' proposed new items were numbered NL-307..NL-320. The W5-C, W5-D and W5-E lane results reached the ledger agent truncated; their items were taken from their commits and the per-project CLAUDE.md files.

Deviations accepted in wave 5 (details in `BOLT5_ONCHAIN_PLAN.md` "ABCD wave 5 record"):
- An Unknown funding spend goes to `OnchainResolving` with no outputs instead of Failed (NL-308).
- HTLCs without an output are re-derived from the snapshot every round instead of being persisted; upstream switch events repeat every round (the switch is idempotent).
- One tx per resolved output and no fee bumping yet (NL-317); a revoked commitment without a log entry is mapped by script only (NL-309).
- Out-of-lane touches: `PaymentService.InterpretFailure` + a `PaymentServiceTests` case (W5-B), `ChannelSafetyFlowTests` now expects `OnchainResolving` after our commitment confirms and `ChainWatchSchemaRoundTrip` asserts `CommitmentNumber` (W5-A).

### Carried into wave 6

- **BOLT 5 O6 (NL-094):** `SweepScheduler` with fee bumping and a per-target estimate (NL-317, NL-296), reorg re-resolution and wallet rollback (O6-T3: NL-292, NL-293; drop the stale-SCID reproducer's `Explicit`), Proof O6 (a)-(c), the mainnet gate O6-T4; broadcast abandonment for refused rows (NL-294, NL-259).
- **Wave 5 follow-ups:** startup catch-up of untracked resolution watches (NL-311), upstream HTLCs of future/unknown closes (NL-320), our invoice's HTLC claimed on chain after the peer's force close (NL-316), pre-log revoked HTLC outputs (NL-309), the revoked resolver's preimage persistence (NL-318), the watcher's model-before-save (NL-307, with NL-282), a Failed channel whose mutual close confirms (NL-312), NL-308, NL-313, NL-315.
- **Later BOLT 5:** O7 anchors (NL-314, then `OptionAnchors`), O8 mempool (NL-098).
- **Close follow-ups:** `option_simple_close` (N11, NL-020), NL-279, NL-285, NL-286, NL-045, NL-277, wallet address reuse (NL-280, also for sweep destinations).
- **Switch/monitor:** NL-078, NL-265, NL-266, NL-267, NL-268, NL-273, NL-274, NL-290, NL-216, NL-298.
- **Mutinynet / ops:** display byte order (NL-303), daemon-anchored relative paths (NL-306), a longer live run with a force close on Mutinynet.
- **Test infra and platform:** NL-276 (host route), NativeAOT and Wasm on SDK 11 (NL-300), the LND fresh-edge retry for other tests (NL-319), the reorg test flake watch (NL-310), NL-295, NL-262, NL-261.
- **Carried from earlier waves:** NL-152 (disconnect IPC), NL-269, NL-260, NL-270, NL-138, the Wasm risk.

### Wave 4: integrated into `wip/fafo` @ `6b5d50e` (2026-09-26), gates GREEN

Wave 4 started BOLT 5 and cleared the wave-3 interop bugs. Five lanes: W4-A BOLT 5 plumbing (migration owner; O0 + O1 + review fixes, three steps), W4-B BOLT 5 builders (O2-O6 pure pieces + review fixes, three steps), W4-C net11 multi-targeting, W4-D signet/Mutinynet and wallet fixes, W4-E interop follow-ups (fees, closing timeouts, NL-271). 33 lane commits were cherry-picked with `-x` in the order w4a, w4b, w4d, w4e, w4c. Conflicts resolved by the integrator: `OutputDescriptorKind` (W4-A's persisted 1-9 kept, W4-B's `Unknown = 0`, `PeerOutput = 10`, `OurAnchor = 11`, `PeerAnchor = 12` appended), `OutputResolutionState` (both lanes used the name; the persisted workflow enum stays, W4-B's planner enum became `PlannedResolutionState`), W4-E's `RateMultiplier = 250` dropped for W4-D's `FeeRateConverter`, `Daemon.csproj` package versions, and the CLAUDE.md files. Five `integrate:` commits:
- 53accb1: restores `BitcoinChainService.GetBlockHashAsync` (lost in the W4-D merge; the reorg rewind needs it) and resolves the monitor's network through `ToNBitcoinNetwork()`.
- 960cf05: hub registrations: `AddFeeServices()` (replaces the typed HttpClient), `AddOnchainBitcoinServices()`, an explicit `AddLogging()` (10 Daemon tests failed without it); `CustomSignet?.Register()` and `BitcoinNetwork.Resolve` in PostConfigure; signet/mutinynet in the usage text; the test node uses `AddFeeServices(_ => CreateFixedFeeHandler())`.
- 1161213: root, Repositories and test CLAUDE.md for wave 4; `scripts/run-onchain.sh` pinned to one framework (`ONCHAIN_FRAMEWORK`).
- 5cc32ba: **NL-263 fixed** (reproduced at integration): the UTXO locks move to the real channel id before `UpgradeChannel` raises `OnChannelUpgraded`; regression test `Given_ValidAcceptChannel_When_ChannelIsUpgraded_Then_UtxoLocksAlreadyCarryTheNewChannelId`.
- 6b5d50e: test baselines and the in-container Docker runner with `--network host` (test/CLAUDE.md).

Gates at `6b5d50e`:
- Build: Release and Release.Native, 0 errors, the same **5** CS86xx warning sites (NL-171). Same under SDK 11 rc.1 for both configs (each warning once per framework).
- `dotnet format --verify-no-changes` clean under SDK 10 and 11; `scripts/check-sln-configs.py` OK.
- Tests: **5561** non-Docker tests per config and framework, 0 skips (Domain 2107, Application 815, Integration 538, Serialization 466, Infrastructure 372, Infrastructure.Bitcoin 721, Bolt11 278, Daemon 264); under SDK 11 they pass on net10.0 and net11.0 in both configs (the full SDK 11 run was before 5cc32ba; Application re-run on both after it). The Long simulator passes.
- `HasPendingModelChanges()` false for all three providers after `AddChainWatchAndBroadcasts` and `AddOnchainResolution` (W4-A).
- Docker: **77 tests, all green** (from an SDK container, NL-276). LND suite 48 in one bridge-network run: 32 passed first time, and the 16 environment failures (database round trips on 127.0.0.1, the connect-back and server-database cases) passed on rerun with `--network host` (Postgres 6/6, SqlServer 6/6, `MultiNodeHarness` server-database 2/2, `AbcNetworkTests` + `ChannelOpeningFlowTests` 8/8). `CooperativeCloseFlowTests` 4/4, `ChannelSafetyFlowTests` 2/2 and `FeeUpdateFlowTests` 3/3 (LND-funded case no longer skipped) are now verified on `wip/fafo`. CLN interop 15/15 + `ClnChannelSessionTests` 2/2. **ABCD `scripts/run-abcd.sh 3`: 3 × 10/10** (fresh process and fixture each). BOLT 5 O0 smoke `Docker.Onchain` 1/1 (the stale-SCID reproducer is `Explicit`, NL-292). All on net10.0 only (NL-300).

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W4-A BOLT 5 plumbing (migration owner) | done: EF 10.0.12 bump; O0-T1..T4 (persisted broadcasts + rebroadcast, persisted outpoint watches, one unit of work per block, tip at start, halt flag, reorg header ring + rewind), migration `AddChainWatchAndBroadcasts`; O1-T1..T3 (revocation log in the RAA save, `ChannelCloses`/`OutputResolutions`, `OnchainResolving = 37`, stubs deleted), migration `AddOnchainResolution`; Docker O0 smoke; review fixes (rebroadcast while halted, late orphans dropped, fork search from our tip, periodic refusal warning) | 2bfaaf9, 7b4a173, f251dde, ba406bd, 5bedf44, 4fd3617, a9e33a7 | NL-095, NL-214, NL-215, NL-258 fixed; NL-096, NL-216, NL-272, NL-094 partial; new NL-292, NL-293, NL-294, NL-295 |
| W4-B BOLT 5 builders | done (pure, not wired): O2-T1 S1 (partial: restart), O2-T3 classifier + `CommitmentNumber.Decode`, O2-T4 output mapper, O3-T5 preimage extraction, O3-T1 `SignSweepInput` + `SweepTransactionBuilder`, O4-T1/T2 claims, O5-T1 `PenaltyTransactionBuilder`, O3-T2/O4-T2/O5-T2 `OutputResolutionPlanner`, O6-T1 `SweepFeePolicy`; review fixes (batching rules, S1 atomic, Appendix F claims executed) | dbe4cc8, 5926d0c, 263ab8f, d7a4c73, 0b3dda5, 369314d, 7394f19, 0df889a, 36e8797, 9dbbabc, ff7f6cc, 0761773 | NL-094 partial; new NL-296, NL-297, NL-299 (wontfix) |
| W4-C net11 | done: net10.0 + net11.0 gated on SDK 11, net9.0 dropped, LangVersion 14, Microsoft.Extensions 10.0.12, CI installs SDK 10 and 11, `-f net10.0` in scripts and docs (`NET11_PLAN.md`) | d35784f, 9e6f5ef, 07b383e | NL-155 fixed; new NL-300 |
| W4-D signet | done: signet and custom signet (Mutinynet) networks, fail-fast network resolution, testnet chain hash fixed, signet/Mutinynet defaults (`MUTINYNET.md`), wallet index fix and address-generation lock, configurable fee source, shared started fee service that never reports 0 | e7d9037, 3350020, 959f310, 803df11 | NL-283, NL-288 fixed (with W4-E); new NL-291 (fixed), NL-298 |
| W4-E interop follow-ups | done: sat/vB → sat/kw with a 253 floor, fundee accepts from the relay floor, CLN reproducers regular; watch in the Failed save + precondition; closing timeouts; shutdown address reservation; close fee estimate via the fee service; CLN close proofs with `fee_range`; review fixes | 1036dfe, eb38c26, d8680cd, 10b39e7, 8096700, 8249044, a38c999 | NL-284, NL-288, NL-289, NL-275 fixed; NL-271, NL-280, NL-285, NL-286 partial |
| Integration | seams, hub registrations, NL-263, guides | 53accb1, 960cf05, 1161213, 5cc32ba, 6b5d50e | NL-263 fixed; NL-034 closed (Docker close proof on `wip/fafo`); NL-276 workaround documented |

Ledger note: NL-034 is **closed** (the legacy close epic): `CooperativeCloseFlowTests` passed on `wip/fafo` and every remaining item has its own entry. The integrator listed NL-285 and NL-286 as fixed; the ledger keeps both **open (partial)**: the R09 deviation is unchanged (only its CLN risk is refuted), and there is still no Docker restart while ShuttingDown/Negotiating/Closing. The integrator listed NL-096 and NL-216 as fixed by W4-A; the lane itself reported both partial, and the ledger agrees (NL-292, NL-293; no halt IPC or gate). NL-291 was cited by W4-D's commits before it had a ledger entry; W4-A's proposed new IDs were renumbered to NL-292..NL-294. The `OutputDescriptorKind`/`OutputResolutionState` reconciliation was done at integration and gets no entry. The W4-C, W4-D and W4-E lane results reached the ledger agent truncated; their items were taken from their commits, `NET11_PLAN.md`, `MUTINYNET.md` and the per-project CLAUDE.md files.

Deviations accepted in wave 4 (details in `BOLT5_ONCHAIN_PLAN.md` "ABCD wave 4 record" and the BOLT2 plan "ABCD wave 4 record"):
- O0 tests live in `IT/Persistence/ChainMonitorPersistenceTests` (real SQLite), the Docker proof is `Docker/Onchain/OnchainSmokeTests`; no Domain `ChainTx` on the outpoint-spent event.
- The O1 "simulator invariant" is proven in the real-crypto `TwoNodeHarness`.
- The signer's Revocation-key check is script-based, not number-based (§3.5); BOLT 5 witness weights computed exactly (NL-299).
- A reorg does not undo completed confirmations or wallet UTXOs (NL-292, NL-293); the halt flag has no IPC (NL-216).
- Out-of-lane touches accepted: `CrashingUnitOfWork`, `ThreeNodeHarness` and `HookedUnitOfWork` (`IUnitOfWork` forwarders), new `test/NLightning.Tests.Utils/Mocks/FakeBitcoinChain.cs`, `TwoNodeHarness` `InMemoryChannelStateStore` (revocation log), the Application FundingSigned/FunderRememberRule tests (W4-A); new `Builders/Interfaces/{ISweep,IPenalty}TransactionBuilder.cs` and the Bitcoin.Tests csproj linking the Integration BOLT 3 harness (W4-B); `Daemon.csproj` package bump (W4-A).

### Carried into wave 5

- **BOLT 5 wiring (the next critical step, NL-094):** O2-T5 `OnchainChannelWatcher` + `forceclosechannel` IPC (classify every funding spend, persist `ChannelCloses`/`OutputResolutions`, state 37; NL-272), the NL-271 remainder (the handler `MustBroadcast` path through `ChannelManager`, a `BroadcastTransactions` row for the commitment) and S1 at registration (NL-297); then O3-T3/T4 (local resolution, switch integration, `HtlcRemovalKind.OnchainTimeout`), O3-T6 `PendingSweeps` IPC, O4-T3, O5-T2/T3 (penalty execution), O6 (`SweepScheduler`, per-target estimate NL-296, 100-block completion, reorg re-resolution NL-292, mainnet gate); Docker Proofs O2-O6 through `scripts/run-onchain.sh`.
- **Chain monitor:** wallet rollback on reorg (NL-293, needs a spent-at height: migration owner), broadcast abandonment (NL-294, with NL-259 UTXO locks), halt flag over IPC and as a channel-operation gate (NL-216), unified network resolution (NL-298).
- **Close follow-ups:** `option_simple_close` (N11, NL-020), B2-SHUT-S08 (NL-279), R09 (NL-285), Docker restart while closing (NL-286), local upfront script (NL-045), wallet address reservation (NL-280), `MessageFactory.CreateClosingSignedMessage` (NL-277), in-memory model before save (NL-282).
- **Switch/monitor:** wire and persist `IOnionReplayStore` (NL-078), NL-265, NL-266, NL-267, NL-268, NL-273, NL-274, NL-290.
- **Test infra and platform:** NL-276 (host route; the in-container runner is the workaround), Docker on net11.0 plus NativeAOT/Wasm on SDK 11 (NL-300), the open-subscription race (NL-295), NL-262, NL-261, the live Mutinynet smoke (`MUTINYNET.md`), and the per-wave refresh of the root/`test` CLAUDE.md counts.
- **Carried from earlier waves:** NL-152 (disconnect IPC), NL-269, NL-260, NL-270, NL-138, the Wasm risk.

### Wave 3: integrated into `wip/fafo` @ `c92d837` (2026-09-25), gates PARTIAL

Wave 3 moved from "make ABCD green" to BOLT 2 completion and hardening. Six lanes: W3-A N9 safety, W3-B N10 close (migration owner), W3-C N9 fees, W3-D replay and debt, W3-E CLN interop, W3-F BOLT 5 plan (docs only). 30 lane commits were cherry-picked with `-x` in the order w3b, w3a, w3c, w3d, w3e, w3f; the only conflict was doc text in `src/NLightning.Application/CLAUDE.md` (both kept). Two `integrate:` commits:
- 983b2b4: `AddApplicationServices` calls `AddChannelSafetyServices()` after `AddPaymentSendServices()` and `AddChannelFeeServices()` last (it wraps `IHtlcSwitch` in `DustExposureHtlcSwitch`); `Node:Safety` binds `ChannelSafetyOptions`; `NltgDaemonService` and `NLightningTestNode` start `IChannelFailureService`, `IHtlcExpiryMonitor` and `IFeeUpdateScheduler` after `PeerManager.StartAsync` and the payment reconcile and stop them before the chain monitor (the test node sets `Node:FeeUpdates:Enabled=false`); `ChannelManager` hands a `MustBroadcast` `ChannelFailedException` to `IChannelFailureService` after the lock; the reestablish handler calls `ILightningSigner.MarkDataLoss` after the data-loss save; `ChannelSafetyFlowTests` resolves its services from DI. Tests: ChannelManager theory (failure service only with MustBroadcast, never under the lock), MarkDataLoss assertions, DI resolution of the new services.
- c92d837: root `CLAUDE.md` for wave 3.

Gates at `c92d837`:
- Build: Release and Release.Native, 0 errors, the same **5** CS86xx warning sites (NL-171).
- `dotnet format --verify-no-changes` clean; `scripts/check-sln-configs.py` OK.
- Tests: **4879** non-Docker tests pass in both configs, 0 skips (Domain 1899, Application 782, Integration 512, Serialization 466, Infrastructure 372, Infrastructure.Bitcoin 323, Bolt11 275, Daemon 250). The 10k-seed Long simulator passes.
- `HasPendingModelChanges()` is false for all three providers after `AddShutdownState` (W3-B).
- Docker (67 tests): 23 passed (Postgres 4/4, SqlServer 4/4, CLN interop 9/9, `ClnChannelSessionTests` 2/2, `OnceOnlyBuildTests` 2/2, `PollTests` 2/2); the 2 `Explicit` CLN reproducers were not run; **44 LND-based tests failed at fixture setup** ("No route to host" from the host process to the container IPs, NL-276), before any test code ran. So the ABCD 3-run gate (`scripts/run-abcd.sh 3 Release`) and the N9/N10 LND proofs are **not verified at `c92d837`**. In the lanes: `ChannelSafetyFlowTests` 2/2 (several runs, from an SDK container), `CooperativeCloseFlowTests` 4/4 at 287a956 (not re-run after 8e0e154), ABCD `scripts/run-abcd.sh 1` 10/10 plus `ReestablishFlowTests` 3/3 and `NormalOperationFlowTests` 8/8 during W3-B step 2.

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W3-A N9 safety | done: N9-T2 `HtlcDeadlinePolicy` + `HtlcExpiryMonitor`, N9-T4 `ChannelFailureService`, signer broadcast signing + data-loss lock; review fixes (retry refused publishes, resume at start, final-hop deadline); Docker proofs (offered HTLC force close; forwarded HTLC failed back upstream before its deadline) | 36d2270, 684bc1e, 02b12f7, a681dad, 06da54b | NL-094 partial; new NL-271, NL-272, NL-273, NL-274, NL-275, NL-276 |
| W3-B N10 close (migration owner) | done: N10-T1..T3, `AddShutdownState` (3 providers), `closechannel` (ClientCommand 13), LND interop fixes, crash/chain safety (closing watch in the Closing save, funding-spend watch, startup and per-block completion, reestablish in closing states); Docker close proof 4/4 at step 2 | 34757a3, b38ce86, 6d81ecd, b41e925, 9733937, 287a956, 5d0aafc, 8e0e154 | NL-036, NL-065 fixed; NL-034, NL-045, NL-152 partial; new NL-277..NL-287 (NL-278, NL-281, NL-287 fixed in the wave) |
| W3-C N9 fees | done: N9-T1 `FeeUpdatePolicy`/`FeeUpdateScheduler`, N9-T3 `max_dust_htlc_exposure_msat` + `DustExposureHtlcSwitch`; Docker `FeeUpdateFlowTests` with the LND-funded case skipped | 1dbbc1f, 45cc1b9, 525973a, 9bfa09d, 533330f | NL-254 fixed; new NL-288, NL-290 |
| W3-D replay and debt | done: NL-247, NL-249, NL-251 (real ping before commit), NL-264; `IOnionReplayStore` (not wired); NL-246 documented as unfixable | 03a19fa, c53afa2, 82b7dcc, d7f09a9, eb597d7, 813fc85, c84f81a, 9762e28 | NL-247, NL-249, NL-251, NL-264 fixed; NL-078 partial; NL-246 wontfix |
| W3-E CLN interop | done: `ClnFixture` (own bitcoind + CLN v26.06.8, fee limits on), connect both ways, channels both directions, payments both ways, reestablish after disconnect and restart; two `Explicit` reproducers | 7c7c8c5, 8427c87 | new NL-288 (shared with W3-C), NL-289 |
| W3-F BOLT 5 plan | done: `docs/agents/BOLT5_ONCHAIN_PLAN.md` (spec summary, verified gaps, design, milestones O0-O8, Docker proofs against LND) and its review revision | 3b02972, 6290443 | NL-094 plan ref |
| Integration | wiring, root guide | 983b2b4, c92d837 | NL-094 partial (wired) |

Ledger note: NL-034 stays **open (partial)** until `CooperativeCloseFlowTests` passes on `wip/fafo` (W3-B step 2 said fixed, step 3 changed the close path afterwards and could not re-run Docker). NL-094 stays open: only the broadcast of our own commitment exists. The W3-C lane result reached the ledger agent truncated; its items were taken from its commits and `src/NLightning.Application/CLAUDE.md`.

Deviations accepted in wave 3 (details in the BOLT2 plan "ABCD wave 3 record"):
- B2-CLS-R09: we re-send our closing fee limit to a peer seen converging (LND 0.20 lowers 10 % per round, about 19 rounds) instead of failing; NL-285.
- The closing timeouts B2-CLS-03/R04 are not implemented (NL-284); B2-SHUT-S08 (fail HTLCs added after our shutdown) is not implemented (NL-279).
- `HtlcDeadlinePolicyTests` live in Application.Tests, not Domain.Tests.
- A Closing channel is never turned Failed.
- Out-of-lane touches: `IBlockchainMonitor` (W3-B: `TrackWatchedTransaction`, `PublishTransactionAsync`, `WatchOutpointSpend`, `StopWatchingOutpointSpend`, `OnWatchedOutpointSpent`), `IPeerService` (W3-D: `LastMessageReceivedAt`, `PingAsync`), `TwoNodeHarness` (W3-B).

### Carried into wave 4

- **Gate first:** fix the host-to-container route (NL-276: Local Network permission or OrbStack restart, or run from the SDK container), then run the full Docker suite and `scripts/run-abcd.sh 3 Release`. Close NL-034 when `CooperativeCloseFlowTests` passes; confirm `ChannelSafetyFlowTests` and `FeeUpdateFlowTests` through DI.
- **Interop bugs (high):** fee estimate unit (NL-288; then un-skip the LND-funded `FeeUpdateFlowTests` case and drop the CLN `Explicit`), fundee feerate floor (NL-289), wallet address generation off-by-one (NL-283) and address reuse (NL-280).
- **BOLT 5** (`BOLT5_ONCHAIN_PLAN.md` O0-O8): detect the peer's commitment and any funding spend (NL-272), persisted broadcast intent (NL-271, migration owner), sweeps and HTLC resolution, penalty (NL-095).
- **Close follow-ups:** closing timeouts via `IChannelFailureService` (NL-284), fail back HTLCs added after our shutdown (NL-279), `option_simple_close` (N11, NL-020), local upfront script (NL-045), Docker restart-while-ShuttingDown and CLN close cases (NL-286), R09 (NL-285), `MessageFactory.CreateClosingSignedMessage` (NL-277), in-memory model before save (NL-282).
- **Switch/monitor:** wire `IOnionReplayStore` into the switch and persist it (NL-078), HTLCs from older builds without an origin (NL-265), alias scids in UPDATE failures (NL-266), forward checks at height 0 (NL-267), fee-aware liquidity pre-check (NL-268), per-invoice preimage check in the monitor (NL-274), error through the outbox (NL-273), pre-wave-3 dust limit backfill (NL-290).
- **Carried from wave 2:** NL-258 (funding rebroadcast), NL-259 (UTXO locks), NL-263 (flake), NL-262, NL-261, NL-269, NL-260, NL-270, NL-152 (disconnect), NL-138, the Wasm risk, and the per-wave refresh of the root/`test` CLAUDE.md counts.

### Wave 2: integrated into `wip/fafo` @ `a5675cb` (2026-09-25)

All four lanes are done: W2-A in three steps (base, N7-T5/T6 and the full-suite LND restart, review fixes), W2-B with a review-fix step, W2-C with a review-fix step, and W2-D. 19 lane commits were cherry-picked with `-x` in the order w2b, w2a, w2c, w2d. The only conflicts were doc text in `src/NLightning.Application/CLAUDE.md` and `test/CLAUDE.md`; both sides were kept. No lane touched migrations, and no schema change was needed. Two `integrate:` commits:
- f2f1ef6: `AddApplicationServices` calls `AddHtlcSwitchServices()` and then `AddPaymentSendServices()`. New `Payments/Send/PaymentOutcomeSwitchHandler` bridges `ILocalPaymentHtlcHandler` to `IPaymentOutcomeHandler`. `ReconcileInFlightPaymentsAsync` runs after `PeerManager.StartAsync` in `NltgDaemonService` and in `NLightningTestNode`. `ListChannelsClientHandler` reads `IsReestablished` from `IReestablishTracker`. The daemon binds `Node:Payments` to `PaymentSendOptions`. Daemon DI (ValidateOnBuild) and listchannels tests were added. A CS8602/CS8629 warning from W2-A was fixed. The harness has a `HarnessStateStore.DropOrigins` switch, so the two W2-C "no origin" tests still test that case now that W2-B stores origins.
- a5675cb: the N6/N7 Docker probe payments from LND now carry the MPP record on the last hop. The real final hop rejects a payload without `total_msat` as `invalid_onion_payload` (BOLT 4), and LND's BuildRoute won't attach a payment address for a node outside its graph. Test-only change.

Gates at `a5675cb`:
- Build: Release and Release.Native have 0 errors and the same **5** CS86xx warning sites as wave 1.
- `dotnet format --verify-no-changes` is clean.
- Tests: **3934** non-Docker tests pass in both configs with 0 skips (Domain 1236, Application 563, Integration 512, Serialization 466, Infrastructure 342, Infrastructure.Bitcoin 305, Bolt11 275, Daemon 235). The ledger agent re-ran the Release set and got the same counts.
- The 10k-seed Long simulator passes.
- Docker on OrbStack: **47/47** after a5675cb. Run 1, before a5675cb, was 43/47; the four failures were the LND probe payments without `payment_data`.
- The ABCD suite (`Docker/Abcd/`) passed in both full-suite runs and in `scripts/run-abcd.sh 1` (10/10). That is three green runs so far, which meets the §2 wave-3 bar of 3 in a row. Re-confirm with a single `scripts/run-abcd.sh 3` loop.

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W2-A Reestablish | done: N7-T1..T6 (T5 for every existing state; ShuttingDown/Negotiating are N10), N6-T3 rest, NL-234; I11 harness and Docker Proof N7 (a)(b)(c) in the full suite | 4620895, 4ec83d3, 1ad14ce, a4e95d7, 77c69a2, 82c4c37, 30c1bb8, 22c29ae | NL-035, NL-200, NL-234, NL-252 fixed; NL-036 partial; NL-048 wontfix pinned by test; new NL-258, NL-259, NL-260, NL-261, NL-262, NL-263, NL-269 |
| W2-B HTLC switch | done: M4-T2 wiring, T3 atomic accept, T4 forward, T5 propagation, T7 replay; `ThreeNodeHarness` on SQLite | 4ca9b56, ca87313, c4ad8e9, 5a254c2, d1476a4 | NL-250, NL-253, NL-243 fixed; NL-137 partial; new NL-256 (fixed in d1476a4), NL-257 (not reproduced, wontfix), NL-266, NL-267, NL-268 |
| W2-C Send | done: N8-T3 + M4-T6 `PaymentService` (direct and hinted), origin decrypt, startup reconciliation; invoice route hints | 0870ab1, 6cb279f, 083a726 | NL-245 fixed; NL-114 and NL-073 fixed after integration; new NL-270 |
| W2-D Docker proofs + ABCD | done: Proof N8 (LND pays our invoice, trimmed, we pay LND, 10 concurrent each way, in-flight restart); ABCD suite (reestablish, happy path, a, b1, b2 stop/crash, c-send, c-receive) and `scripts/run-abcd.sh` | fdc80af, 1980a00, 421f1e5 | NL-099 unchanged (route hints, decision B) |
| Integration | wiring and Docker probe fix | f2f1ef6, a5675cb | NL-031, NL-073, NL-114 fixed (epics); NL-152 partial; new NL-264, NL-265 |

Ledger note: the integrator's summary listed NL-243 as fixed, and the ledger agrees. The leftover is that existence checks still use `GetByIdAsync`, which is noted but not tracked. NL-031, NL-073 and NL-114 are closed as epics: every remaining item has its own entry (NL-078 replay set, NL-094/N9-T4 broadcast, NL-266..NL-270). NL-257 was proposed by W2-B ("LocalAliases are not persisted") but is refuted: `ChannelLocalAliases` is persisted and reloaded (NL-103).

Deviations accepted in wave 2 (details in the BOLT2 plan "ABCD wave 2 record" and the ONION plan status):
- The reestablish secret is checked against our secret Y-1 (spec), not L-1 (plan §3.11). Planner tests live in Application.Tests, and the funder rule test is `FunderRememberRuleTests`.
- `channel_reestablish` is sent at connect for V1FundingSigned/ReadyForThem/ReadyForUs/Open. A peer's reestablish that arrives after channel_ready is answered.
- The invoice is Settled in the fulfill's own save (Accept and Settle are staged together), not when the removal is irrevocable. This is safe because both commit atomically with the fulfill.
- Pending events are replayed twice after a reestablish; harmless (NL-264).
- The LND-restart proof holds the free addresses below alice's with idle containers, because OrbStack gives a restarted container the lowest free address (NL-262).
- Route hints carry the **peer's** `channel_update` policy (BOLT 11), not ours.
- Payments use a node-wide fee limit of max(0.5 %, 5000 msat) with no retries (NL-270).
- Shared-file touches accepted: `IPeerService`/`PeerService`/`PeerOutbox` (W2-A); `IChannelOperations`, `ChannelOperationsService`, `ChannelStateTransitionService` and the Domain `IncomingHtlcSettled` event (W2-B); `test/NLightning.Application.Tests.csproj` references Infrastructure.Repositories and Persistence.Sqlite (W2-B harness); `Daemon.Tests/Ipc/Handlers/PaymentSendIpcTests.cs` (W2-C).

### Carried into wave 3

The ABCD goal test is **green** at `a5675cb`, one wave early. Wave 3 therefore changes from "make ABCD green" to "keep it green and harden":
- **W3-A ABCD stabilization:** run `scripts/run-abcd.sh 3` (3 in a row, fresh fixture each) and fix any flake. Watch the known flake NL-263 and the reconnect race tolerated in `AbcdReestablishTests`. Resolve the double replay (NL-264), the no-origin handler gap (NL-265) and forward checks at height 0 (NL-267).
- **W3-B Provider matrix:** the ABCD happy path with Bob on Postgres and Carol on SqlServer; the container tests stay green.
- **W3-C Hardening:**
  - Signed `channel_update` for alias scids in UPDATE failures (NL-266). Variant a2 (`fee_insufficient` → LND retries) is not yet authored.
  - N9-T2 `HtlcExpiryMonitor`: a forwarding node without it can lose funds, so it gates any non-regtest use.
  - Fee-aware liquidity pre-check (NL-268).
  - Optional LND-funded channel proof (receive `update_fee`).
- **Next milestones outside ABCD:**
  - N9-T4 fail-the-channel broadcast and signer refusal after data loss (NL-094).
  - N10 close with Closing/Negotiating resumption and shutdown re-send (NL-034, NL-036, B2-RE-28).
  - Funding tx rebroadcast (NL-258) and UTXO locks of a forgotten funder channel (NL-259).
  - An unverified LND sync edge on a reconnect before channel_ready (NL-269).
- **Test infra:** SendErrorAsync unit test (NL-261); LNUnit restart address fix upstream (NL-262).
- **Carried tech debt:** NL-246, NL-247, NL-249, NL-251, NL-254, NL-138, NL-078 (persistent replay set), NL-260, NL-270, the wave-1 Wasm risk (still unverified on macOS), and the root/`test` CLAUDE.md test counts, which must be refreshed each wave.

### Wave 1: integrated into `wip/fafo` @ `342d22e` (2026-09-25)

All five lanes are done (W1-A and W1-B with review-fix steps). 28 lane commits were cherry-picked with `-x` in the order w1c, w1a, w1b, w1d, w1e; the only conflicts were doc text in `src/NLightning.Application/CLAUDE.md` and `test/CLAUDE.md` (both lanes' text merged). Two `integrate:` commits: 4ae2eb3 (`AddApplicationServices` calls `AddGossipServices()` and `AddPaymentsServices()`; scoped `IInvoiceDbRepository`/`IPaymentDbRepository`/`IForwardCircuitDbRepository` resolve to the scope's `IUnitOfWork` properties; the harness store implements the new origin methods; two Daemon DI tests) and 342d22e (`ChannelUpdateExchangeTests` counts only its own channel's ignored updates, because the shared alice relays other tests' channels; test only).

Gates at `342d22e`: Release and Release.Native build with 0 errors and **5** CS86xx warning sites (each printed twice; the two `PeerService` ones went away with df4ca92, NL-171); `dotnet format --verify-no-changes` clean; **3747** non-Docker tests pass in both configs, 0 skips (Domain 1235, Integration 512, Serialization 466, Application 382, Infrastructure 342, Infrastructure.Bitcoin 305, Bolt11 275, Daemon 230); 10k-seed Long simulator passes; Docker **29/29** on OrbStack after 342d22e (integrator run; `HasPendingModelChanges` false on all three providers).

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W1-A Channel wiring | done: N6-T1, T2, T4, T5; N6-T3 partial | 1de15f9, a604dff, 1392489, f5315c0, a02afa7, a388fc4, 3f7b8bf, aac5f60, e5f7312 | NL-232, NL-235, NL-244 fixed; NL-136 call sites (a604dff); NL-031, NL-200, NL-243 partial; NL-199 receiver needs no code; NL-234 open (moves to W2-A); new NL-246, NL-247, NL-248, NL-249, NL-250, NL-251, NL-252, NL-254 |
| W1-B Payment core | done: M4-T2 processor, M4-T3, M4-T4 policy, M4-T6 build, `InvoiceService` | 6156173, d27adb4, 234607e | NL-073, NL-114 partial; new NL-245, NL-253 |
| W1-C Payment schema (migration owner) | done: `AddInvoicesPaymentsAndCircuits` (3 providers), repositories, HTLC origins, stored dust policy | 899e36b, 2f25527, 98d19e6 | NL-242 fixed, NL-248 fixed; NL-137, NL-243 partial |
| W1-D IPC / CLI | done: CreateInvoice/PayInvoice/ListInvoices/ListPayments IPC + CLI, `RoutingOptions` bound, `EnableHtlcs` in the default config | e30a845, 6cfbcd1, c10a78e, c50fc7b | NL-241 fixed; NL-152 partial |
| W1-E Direct `channel_update` exchange | done; also fixed the NLightning-to-NLightning connect bugs and dropped the harness tolerance | df4ca92, b93dd05, 38d2f5e, f9c54cc, 7c1ed4c, b681f8a, 9ce68e0, 5d1a9ed, 5f2a3ee | NL-239, NL-240 fixed; NL-099 partial; new NL-255 |

Ledger note: the integrator's summary listed NL-137, NL-243 and NL-114 as fixed; the ledger keeps them **open (partial)** because the lanes reported remaining work (replay set and SCID map; the W2-B switch must keep pruning; pay/receive are W2-B/W2-C). NL-242's remaining piece (no node option sets a policy) is tracked as NL-254.

Deviations accepted in wave 1 (details in the BOLT2 plan "ABCD wave 1 record" and the ONION plan status):
- A normal-operation message on a channel that is not Open gets warning + close, not a channel error (no fail-the-channel broadcast yet).
- `LocalOnlyHtlcSwitch` fails a final-hop HTLC with `incorrect_or_unknown_payment_details` (height from the monitor; `temporary_node_failure` while unknown) and non-final onions with `temporary_node_failure`; the N6-T5 proof uses `SendToRouteV2` with a route that has no `payment_addr` (LND refuses to attach one for a node it knows no features for).
- Ping-before-commit is a connection check only (NL-251). Each channel's link is pinned to the connection it turned Open on, so channels loaded at startup never send updates until N7 marks the link (NL-252).
- `FinalHopProcessor` reports 0x0013/0x0012 before the 0x400F invoice checks (as LND/CLN); a replayed onion is failed with `temporary_node_failure`. `HintRouteBuilder.Build` takes a mandatory `maxFee`.
- W1-E: LND never hints through us in `addinvoice --private` (needs node_announcement, NL-255); decision B (explicit `route_hints`) is unaffected. The roadmap's W1-E proof item "addinvoice --private contains the C→D hint" is therefore not met.
- Shared-file touch accepted: `test/NLightning.Application.Tests/NLightning.Application.Tests.csproj` references `NLightning.Infrastructure.Serialization` (W1-B, for the real hop-payload and failure serializers in tests).

### Carried into wave 2

- **W2-A (reestablish; owns `ChannelManager.cs`, `IChannelManager.cs`, `PeerManager.cs`, `Application/DependencyInjection.cs`):** N7-T1..T6; after reestablish call `IPeerLivenessProbe.MarkLinkUp(channelId, peer)` and replay pending events (`QueuePendingDomainEventsAsync` + `RaiseDomainEventsAsync`) (NL-252); retransmit persisted unsigned updates and the stored `SentCommitDiff`, forget the ones BOLT 2 says to; reuse `ChannelStateTransitionService.LoadRemoteShachainAsync`, `SentCommitDiffCodec`, `CreateRevokeAndAck`; re-send `ChannelModel.ErrorSent` on reconnect and send an error without disconnecting (N6-T3 rest, B2-RE-05, NL-200); `IChannelManager.HandleChannelMessageAsync` returns `Task` (NL-234); add a Domain flag so `listchannels` `IsReestablished` becomes true; optionally a real ping (NL-251, needs `IPeerService.LastMessageReceivedAt`).
- **W2-B (HTLC switch):** `services.Replace(ServiceDescriptor.Singleton<IHtlcSwitch, HtlcSwitch>())`; wire `IncomingOnionProcessor` → `HtlcForwardingPolicy`/`FinalHopProcessor` → `IChannelOperations`; final-hop accept as an atomic check-and-mark under a per-payment-hash lock in the fulfill's unit of work (NL-253); on restart act on persisted HTLC state (`ProcessAsync(checkReplay: false)`, since W6-A `replayOwner: null`); persist `HtlcOrigin` with the add via `SetHtlcOriginAsync` (NL-250); `FindHtlcsByOriginAsync` returns archived rows of failed attempts too (retry-aware replay); keep pruning settled rows after their settle event (NL-243); never hold two channel locks.
- **W2-C (send):** `PaymentService` (`PaymentTarget.FromInvoice` → `HintRouteBuilder` with the caller's fee limit → `PaymentOnionFactory` → `OfferHtlcAsync` with `HtlcOrigin.Local`), stores hops' shared secrets, decrypts failures with `FailureInterpreter`; registering `IPaymentService` turns on `payinvoice`/`listpayments`. Optional: a node-level default fee limit in `RoutingOptions`.
- **W2-D (Docker proofs):** N8 proofs; B–C hop can rely on NLightning-to-NLightning connects (NL-239/NL-240 fixed, no retry tolerance).
- Open tech debt to schedule: NL-247 (stateful `ISha256` singleton), NL-246 (pre-wave-1 channels without a snapshot), NL-254 (dust-exposure option), NL-245 (invoice route hints), NL-249 (test `FakeServiceProvider`), NL-138.
- Wasm risk (unverified on macOS): Application now references Bolt11, whose Wasm build uses the renamed `Bolt11.Blazor` assembly; CI's Wasm job builds only BlazorTests, which does not reference Application.

### Wave 0: integrated into `wip/fafo` @ `0b7e617` (2026-09-25)

All six lanes are done; their commits were cherry-picked with `-x` with no conflicts (order w0b, w0a, w0c, w0d, w0e, w0f) and two `integrate:` commits fixed the seams. Gates at `0b7e617`: Release and Release.Native build with 0 errors and the 7 baseline CS warnings; `dotnet format --verify-no-changes` clean; 3367 non-Docker tests pass in both configs, 0 skips (Domain 1232, Integration 492, Serialization 466, Infrastructure 326, Infrastructure.Bitcoin 305, Bolt11 275, Application 147, Daemon 124); 10k-seed Long simulator passes; Docker 24/24 on OrbStack (integrator run).

| Lane | Result | `wip/fafo` SHAs | Ledger |
|---|---|---|---|
| W0-A Engine seam + events | done | 192e212, 2fa8cf4, b166ea0, 7ba115f (+ c68a34d integrate) | NL-230, NL-231 fixed; N4-T4 done; NL-194 follow-up (`HasInferredLimits`); new NL-244 |
| W0-B Persistence (migration owner) | done; shachain runtime calls left to W1-A (Application) | 4472a8b, bb2731a, f2e1a4a, a8d1381, 79f7657 | NL-025, NL-232 (partial: first-snapshot wiring), NL-237, NL-238 fixed; NL-137 partial; new NL-241, NL-242, NL-243 |
| W0-C Contracts | done | 2ede2ee, 1390027 (+ 0b7e617 integrate: options validation, `PeerManager` backoff from `NodeOptions`) | NL-200, NL-152, NL-137 partial (contracts) |
| W0-D Bolt11 for the node | done | 2d8a9fe, 2c9f812, f93d059 | NL-120 fixed |
| W0-E `channel_update` wire + signing | done | e7b5269, 3ba2e4f | NL-099 partial |
| W0-F Multi-node test infra | done; tolerates two known connect bugs | 6f1a316, cb06e60 (+ 0b7e617 integrate) | new NL-239, NL-240 |

Deviations accepted in wave 0 (details in the BOLT2 plan "ABCD wave 0 record"): `CommitmentsResult.Transition` keeps its name (plan said `Persist`); `IHtlcSwitch` has one `HandleAsync(IChannelDomainEvent)`; the `Commitments` table is keyed by slot, not `(Side, Number)`, and has no txid; `IChannelOperations` has no shutdown until N10; `CommitmentSigningService` is a concrete class; `ErrorSent`/`DataLossDetected` columns shipped early.

### Carried into wave 1

- **W1-A (channel wiring)** must also: create the first snapshot with `IChannelStateDbRepository.InitializeAsync` with both remote points before `ChannelReadyMessageHandler` overwrites the key set's first point (NL-232 rest); per transition `ApplyAsync` + one `SaveChangesAsync`, then `ChannelModel.UpdateCommitments`, then send; call shachain `Export` on RAA and `Load` at startup (NL-136); after a restart `RevertUncommitted` and persist it before reestablish; replay `ChannelDomainEvents.DerivePending` per channel inside a try/catch (legacy states throw) into an idempotent switch; pass the dust policy on reload (NL-242); plan pruning (NL-243); NL-234, NL-235, NL-199, NL-138. Keep `services.AddCommitmentEngineServices()` in `Application/DependencyInjection.cs` (W0-A put it there).
- **W1-C (payment schema)** implements the W0-C ports (`IInvoiceDbRepository`, `IPaymentDbRepository`, `IForwardCircuitDbRepository`); the W0-B migration chain is the base (build the provider projects in Debug first, NL-233).
- **W1-D (IPC)** fixes NL-241 (listchannels pending-HTLC counts) and binds `RoutingOptions` (validation already runs at startup since 0b7e617).
- **W1-E** builds on the typed, signed `channel_update` (send after channel_ready, store the peer's).
- **Test infra:** NL-239 and NL-240 (NLightning-to-NLightning connect bugs) are tolerated by `NLightningTestNode.ConnectToAsync` retries; fix them before the B–C hop is relied on (W1 or W2 owner of `PeerManager`/`PeerService`), then drop the tolerance.
- Tech debt: NL-244 (`Htlc.AddMessage` null in the engine adapter).

# Roadmap: `wip/fafo` @ `3c625e1` to a green ABCD Docker e2e test (LND Alice → NLightning Bob → NLightning Carol → LND David)

I only read files; nothing was changed. The working tree has uncommitted doc edits from the integrating agent (CLAUDE.md files, plans, ISSUES.md), so I checked every code claim against source files.

## 0. Where things stand (checked in code)

- **Done and on the branch:**
  - N0–N3: ordered inbound loop, `PeerOutbox`, `ChannelLockProvider`, per-side params, msat balances and SCID, commitment numbers, the BOLT 3 HTLC txs and signer, and the persisted shachain.
  - N4 pure engine: `src/NLightning.Domain/Channels/Commitments/ChannelCommitments.cs` has `SendAdd/ReceiveAdd/…/SendCommit/ReceiveCommit/ReceiveRevoke/RevertUncommitted/ReceiveFee`, plus the simulator.
  - ONION M1–M3: `ISphinxService`, `IHopPayloadSerializer` (already moved to `src/NLightning.Domain/Serialization/Interfaces/`, so N8-T1/NL-075 is effectively done), `IFailureOnionService`, `FailureInterpreter`, `FailureChannelUpdateFactory`.
- **The engine is not usable yet:**
  - There are two signer-port families: `Domain/Channels/Interfaces/ICommitmentSigner.cs` and `Domain/Channels/Commitments/Interfaces/ICommitmentSigner.cs` (NL-230).
  - There are two fee calculators (NL-231).
  - The engine emits no lock-in or irrevocable-removal events; N4-T4 is only partly done.
- **Wire handling:** `ChannelManager.DispatchChannelMessageAsync` only handles the open flow. Every HTLC message, reestablish and close falls to `default`, which sends a warning.
- **Missing entirely:**
  - `NodeOptions.EnableHtlcs`, forwarding fee/CLTV policy options, invoice/payment/circuit storage, and `ClientCommand` values after `ListChannels = 8` (`src/NLightning.Domain/Client/Enums/ClientCommand.cs`).
  - Nothing in `src/` references Bolt11. `Invoice.Encode(Key)` exists; NL-120 (no validation on encode) is still open.
- **Channels are always private:** `OpenChannelClientHandler.cs:112` sends `ChannelFlags(None)`. `FeatureOptions.ScidAlias = No` by default, so channels use the real SCID (NL-225 persists it).
- **Docker infra:**
  - `Fixtures/LightningRegtestNetworkFixture.cs` starts miner, LND alice/bob/carol (LND 0.20.0-beta from `test/Docker/custom_lnd`, LNUnit 3.0.4) with LND–LND channels, in collection `"regtest"`.
  - Our node runs **in-process**: `Docker/Utils/NLightningTestNode.cs` builds `AddNltgNodeServices`, uses SQLite hard-coded, listens on `127.0.0.1:{port}`, and restarts with the same key manager and DB file. It can only connect to an `LNDNodeConnection`.
  - LNUnit exposes `RouterClient`, `AddInvoiceAsync`, `LookupInvoice`, `RestartByAlias`, `WaitUntilSyncedToChain` and interceptors.
  - The Docker tests pass locally because the Docker context is **OrbStack**, which routes container bridge IPs to the Mac. `PostgresFixture`/`SqlServerFixture` connect to the bridge IP, which fails on Docker Desktop.
  - I found no process-wide mutable statics in `src/`, so **two NLightning nodes in one test process is viable**.

---

## 1. Gap analysis for the ABCD goal

### 1.1 Route discovery: the key decision

There is no BOLT 7 gossip, so Alice's LND cannot see B–C or C–D in its graph. Three options:

| Option | What it needs | Verdict |
|---|---|---|
| **A. Public channels plus minimal BOLT 7** | We send and relay `announcement_signatures`, `channel_announcement`, `channel_update` and `node_announcement`. David's C–D announcement must be relayed Carol → Bob → Alice, which means a graph store, signature checks, relay and throttling, honouring `gossip_timestamp_filter`, and real `query_channel_range` replies (we advertise `gossip_queries`; today replies are empty with `full_information=0`). Also 6-conf announce depth and NL-236 (public/private option). | Legitimate but large: 2–3 more waves on NL-099. **Not on the critical path.** |
| **B. Invoice route hints (recommended)** | David's invoice is made with LND `AddInvoice` and explicit `route_hints` (the `lnrpc.Invoice.route_hints` field accepts caller hints): `[{node=Bob, chan_id=scid(B–C), Bob's fee/cltv}, {node=Carol, chan_id=scid(C–D), Carol's fee/cltv}]`. Alice pays the real BOLT11 with `routerrpc.SendPaymentV2`, so LND does the pathfinding (its own A–B channel plus hint edges), computes fees and CLTVs, and builds the onion. | This is how private channels are reached in production. It checks our forwarding, policy enforcement, onion peel/forward, final hop and error wrapping against LND's own maths. It needs **zero** BOLT 7. |
| C. `SendToRouteV2` with a hand-built route | The test builds the hops. | Fallback only if B hits an LND pathfinding quirk. It is weaker: the test, not LND, computes the fees. |

What LND needs for private-channel forwarding under Option B:
- The first hop is Alice's own channel, so she needs no policy from Bob.
- LND treats hint nodes it does not know as TLV-onion capable.
- The scids must be the real SCIDs. There is no scid_alias because `ScidAlias` is No, so nothing is negotiated.
- The only point where `channel_update` matters is **UPDATE-class failures** (`temporary_channel_failure`, `fee_insufficient`, …). BOLT 4 now allows `len=0`, but whether LND 0.20 accepts an empty update is **unverified**.
- LND builds **automatic** private hints (`addinvoice --private`) only when it holds the peer's `channel_update`. So David can only auto-hint C→D if Carol sends her `channel_update` directly after `channel_ready` (BOLT 7 allows this for unannounced channels).

Recommendation:
- B for the main test and variants (a), (b) and (c).
- A small BOLT 7 subset, typed `channel_update` (258) with node-key signing plus direct peer exchange, as a hardening lane. It makes UPDATE failures carry a real, signed update and enables David's auto hints.

### 1.2 Per-component gaps (R = required for the goal; S = strongly recommended; D = defer)

| Component | Gap | Plan IDs / NL | Need |
|---|---|---|---|
| Engine ↔ builder seam | Engine ports not implemented over `CommitmentSigningService`/`PerCommitmentSecretVerifier`; `CommitmentSpec`→`CommitmentTxSpec` adapter; one fee calculator | NL-230, NL-231 ("Remaining before N5" §1) | R |
| Engine events | `IncomingHtlcLockedIn`, `OutgoingHtlcFulfilled` (immediate), `OutgoingHtlcFailed` (only when irrevocable), `OutgoingHtlcSettled`; must be re-derivable from persisted states | N4-T4, B2-NO-03, B2-FWD-01/02/05 | R |
| Commitment persistence | Migration `AddCommitmentState` (HTLC state 10–39, `KnownPreimage`, fee updates, `RemoteNextCommit`, `SentCommitDiff` wire bytes, remote current and next points, `OnionSharedSecret`); `ChannelStateDbRepository.ApplyAsync`; shachain save/load at runtime; SQLite `synchronous=FULL`; crash injection | N5-T1..T3, NL-232, NL-238, NL-025, NL-192 (done), NL-237 | R |
| HTLC wire handlers | 128–135 plus `update_fee` handler and `ChannelManager` cases; state guard (Open and reestablished) | N6-T1, NL-031 | R |
| Operations / scheduler / switch seam | `IChannelOperations` (Offer/Fulfill/Fail/FailMalformed, persist-before-send), `CommitScheduler` (debounce; never sign while `RemoteNextCommit` exists), `IHtlcSwitch`, `EnableHtlcs` (default on regtest only) | N6-T2 | R |
| Failed-channel path | `ChannelState.Failed = 35`, persisted `ErrorSent`, refuse updates | N6-T3, NL-200 | S (keeps a violation from turning into a silent split-brain) |
| Reestablish | `ReestablishPlanner`, lifecycle hooks in `PeerManager`, gating, `RevertUncommitted` on disconnect, retransmit (`SentCommitDiff` verbatim, RAA regenerated, `LastSentOrder`, `channel_ready` when both numbers are 1) | N7-T1..T3, NL-035 | R |
| Data-loss detection | `DataLossDetected` flag | N7-T4 | S (cheap once the planner exists; LND treats data_loss_protect as required) |
| Other N7 | Non-Open startup states (T5); funder remember rule (T6: wontfix plus a test) | N7-T5/T6 | D / trivial |
| Final hop and invoices | Invoice store, preimage/secret generation, BOLT11 encode with the node key (features 9/14 compulsory, `s`, `c`; no `basic_mpp`), `FinalHopProcessor` (0x400F with (htlc_msat, height), 0x0012, 0x0013), settle when removal is irrevocable | N8-T2, ONION M4-T2/T3, NL-114, NL-120 | R |
| Forwarding | `HtlcForwardingPolicy` (BOLT 7 fee `base + amt*ppm/1e6`, `cltv_expiry - outgoing ≥ delta`, `expiry_too_far`, amount ≥ htlc_min, outgoing liquidity → `temporary_channel_failure`), scid (real or alias) → channel via `IChannelMemoryRepository`, offer only after incoming lock-in, never hold two channel locks | ONION M4-T4, B2-FWD-01/04 | R |
| Upstream propagation | Fulfill upstream immediately on downstream preimage; fail upstream only when the downstream removal is irrevocable; wrap with the stored incoming shared secret; convert malformed (M3-T3 helper exists) | ONION M4-T5, B2-FWD-02/05 | R |
| Forwarding persistence | Circuit table in→out (channel, htlc id, amounts, CLTVs, shared secret) plus startup replay, so variant (b) works | ONION M4-T7, NL-137 | R |
| Send side | `PaymentService`: decode BOLT11, route = direct peer, or our channel to `hint[0].node` then the hint hops; multi-hop onion via `ConstructWithSharedSecrets`; final CLTV = height + `c` + 3; CSPRNG session key; `PaymentEntity`; origin decrypt plus `FailureInterpreter` | N8-T3 + M4-T6 (multi-hop via hints) | R for variant (c) |
| IPC | `CreateInvoice` (9), `PayInvoice` (10), plus `ListInvoices`/`ListPayments` and a `ListChannels` "reestablished/usable" flag (next free values); client handlers the Docker test calls in-process; CLI output | §3.9, NL-152 | R (client handlers) |
| Fee/policy config | `RoutingOptions`: `FeeBaseMsat`, `FeeProportionalMillionths`, `CltvExpiryDelta` (≥34, default 40), `MaxCltvExpiryDistance` (2016), `InvoiceMinFinalCltvExpiry` (default 40), HTLC min/max; bound from config | B2-CLTV-07 (part) | R |
| N9 | T1 fee scheduler: D (all test channels are NLightning-funded, so we never receive `update_fee`, and we never send one). T2: forward-time CLTV checks are R (they live in the M4 policy); the block-driven `HtlcExpiryMonitor` (fail incoming before expiry, offerer deadline) is S for safety, not needed for the test. T3 dust exposure: D. T4 `ChannelFailureService` broadcast: D (regtest; `EnableHtlcs` gate). **`update_fee` receive handler: include in N6-T1 anyway** (the engine already has `ReceiveFee`), so a later LND-funded channel does not break. | N9 | see cell |
| BOLT 7 subset | Typed `ChannelUpdateMessage` (258) plus signing with the node key, embedded in UPDATE failures, sent directly to the peer after `channel_ready`/reestablish, peer's update stored | NL-099 (sub), NL-236 | S |
| attribution_data | We don't advertise it. Ignore incoming TLV 1 on fail/fulfill (odd, so the strict reader drops it) and do not relay it upstream. | M3b, NL-072, NL-022 | D |
| Replay cache | Stays in-memory (NL-078); lost on Bob's restart | NL-078 | D (decision) |
| Test infra | 4th LND `david` in the shared fixture; `NLightningTestNode`: node name/log prefix, DB provider parameter, `ConnectToAsync(NLightningTestNode)`, fast reconnect backoff knob; chain-sync barrier; LND helpers (hint invoices, hold invoices, `SendPaymentV2`, `ResetMissionControl`); Postgres/SqlServer fixtures publish `127.0.0.1` ports so they don't depend on OrbStack | NL-156 follow-up, NL-237 | R |

---

## 2. Waves and lanes

Rules for every wave:
- (i) Lanes own **disjoint** file sets.
- (ii) Exactly one lane per wave may touch `Entities/`, `EntityConfiguration/`, `NLightningDbContext.cs`, `Domain/Persistence/Interfaces/IUnitOfWork.cs`, the `UnitOfWork` implementation, and migrations for all three providers.
- (iii) Shared hub files are owned by one lane per wave, named below: `src/NLightning.Application/DependencyInjection.cs`, `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`, `Serialization/Factories/{MessageTypeSerializerFactory,PayloadSerializerFactory}.cs`, `ChannelManager.cs`, `PeerManager.cs`. Other lanes put their registrations in an `Add<Area>Services` extension inside their own folder, and the integrator adds the one-line call.
- (iv) Lanes do **not** edit `docs/agents/ISSUES.md` or the plans. Each lane reports its ledger deltas and the integrator applies them. This avoids merge conflicts on the summary table.
- (v) Every lane ends green on: Release and Release.Native build, `dotnet format --verify-no-changes`, `!~Docker` tests, and the invariant simulator.

### Wave 0: seams, contracts, schema for the commitment state, test infra (6 parallel lanes)

| Lane | Scope | Files owned | Proof |
|---|---|---|---|
| **W0-A Engine seam + events** | NL-230, NL-231, N4-T4 | `src/NLightning.Domain/Channels/Commitments/**` (keep the `ChannelTransition` persistence fields **frozen**; add `Events` to `CommitmentsResult`); new `Domain/Channels/Commitments/Events/*` (`IChannelDomainEvent`, the 4 events); new `Domain/Channels/Interfaces/IHtlcSwitch.cs`; `Domain/Channels/Interfaces/ICommitment{Signer,Verifier}.cs` (merge or delete the duplicates); `Domain/Bitcoin/Transactions/Factories/CommitmentFeeCalculator.cs`; `Application/Channels/Services/CommitmentSigningService.cs` plus new `Application/Channels/Services/Engine*Port.cs` adapters; tests under `test/NLightning.Domain.Tests/Channels/Commitments/**` and `test/NLightning.Application.Tests/Channels/Services/**` | Two-engine test with **real** signatures (txids equal on both sides after add/CS/RAA/fulfill/fail/fee); `…Given_IncomingAdd_When_BothRevoked_Then_IncomingHtlcLockedInOnce`; `…Given_DownstreamFail_Then_OutgoingHtlcFailedOnlyWhenIrrevocable`; 500-seed simulator green |
| **W0-B Persistence (migration owner)** | N5-T1, N5-T2, N5-T3; NL-232, NL-238, NL-025; `HtlcEntity.OnionSharedSecret` now | `src/NLightning.Infrastructure.Persistence/**` (entities, configurations, `NLightningDbContext`, `DependencyInjection.cs` for `synchronous=FULL`), 3 provider projects' `Migrations/**`, `src/NLightning.Infrastructure.Repositories/**` (new `ChannelStateDbRepository`, `ChannelDbRepository.UpdateAsync` stops writing HTLCs, `UnitOfWork`), `Domain/Persistence/Interfaces/IUnitOfWork.cs`, new `Domain/Channels/Interfaces/IChannelStateDbRepository.cs`, `Domain/Channels/Models/ChannelModel.cs` (holds the `ChannelCommitments` snapshot), `test/NLightning.Tests.Utils/Mocks/CrashingUnitOfWork.cs`, `test/NLightning.Integration.Tests/Persistence/**`, `Docker/PostgresTests.cs`, `Docker/SqlServerTests.cs` | SQLite: 50 simulator transitions, reload, equal; reload mid-dance identical; k-th save crash leaves no partial rows; Postgres and SQL Server container tests migrate **seeded pre-migration rows** forward (NL-237) and round-trip the commitment state |
| **W0-C Contracts** | N6-T2 interface part, payments contracts, options, IPC Domain types | New `Domain/Channels/Interfaces/IChannelOperations.cs`; `Domain/Channels/Enums/ChannelState.cs` (`Failed = 35`); new `Domain/Exceptions/ChannelFailedException.cs`; `Domain/Node/Options/NodeOptions.cs` (`EnableHtlcs`); new `Domain/Node/Options/RoutingOptions.cs`; new `Domain/Payments/**` (Invoice/Payment/ForwardCircuit models, `IInvoiceDbRepository`, `IPaymentDbRepository`, `IForwardCircuitDbRepository`, `IInvoiceService`, `IPaymentService`, `IForwardingPolicy`); `Domain/Client/Enums/ClientCommand.cs` (9 CreateInvoice, 10 PayInvoice, 11 ListInvoices, 12 ListPayments; `CloseChannel` gets the next free value later); `Domain/Client/{Requests,Responses}/*Invoice*|*Payment*`; `ChannelInfoClientResponse.cs` (`IsReestablished`, `FeeBaseMsat/FeePpm`) | Build; `RoutingOptions` validation tests (delta ≥ 34); BOLT 7 fee-formula table tests; options binding test |
| **W0-D Bolt11 for the node** | NL-120, encode path used by the node | `src/NLightning.Bolt11/**`, `test/NLightning.Bolt11.Tests/**` | Encode with node key → decode → validate; features 9/14 compulsory, no 17; multiple `r` round-trip; decode 3 real LND 0.20 invoice strings (plain, with custom hints, hold) as fixtures |
| **W0-E BOLT 7 `channel_update` wire + signing** (S) | NL-099 subset | New `Domain/Protocol/Messages/ChannelUpdateMessage.cs` + payload; `Infrastructure.Serialization/Payloads|Messages/Types/ChannelUpdate*`; **hub:** both serializer factories (258 stops being raw `GossipMessage`); `ILightningSigner.cs`/`LocalLightningSigner.cs` (`SignNodeMessage(hash)`); new `Domain/Protocol/Onion/Factories/` overload so `FailureChannelUpdateFactory` takes the typed update; tests in Serialization.Tests / Bitcoin.Tests | Round trip; signature verifies with the node id; an LND-captured 258 parses and verifies |
| **W0-F Multi-node test infra** | Docker harness | `test/NLightning.Integration.Tests/Fixtures/**` (add `david` via `AddPolarLNDNode("david", [])`; Postgres/SqlServer publish ports on `127.0.0.1`), `TestCollections/**`, `Docker/Utils/**` (`NLightningTestNode`: name/log prefix, provider parameter {Sqlite, Postgres, SqlServer}, `ConnectToAsync(NLightningTestNode)`, `ReconnectBackoffInitial` override hook, `CrashAsync()`; new `ChainSync.cs` barrier; new `LndTestHelpers.cs`), `Docker/AbcNetworkTests.cs` (expects 4 LND nodes) | The 12 existing Docker tests still pass; new smoke test: Bob and Carol (in-process) connect to each other and to alice/david, all at the same block height after mining 3 |

W0-A and W0-B only share `ChannelTransition`, which is frozen. W0-B builds `ApplyAsync` against its current shape. The `NodeOptions` reconnect-backoff knob, if needed, belongs to W0-C, and W0-F consumes it after merge.

### Wave 1: HTLC dance on the wire, payment core, payment schema, IPC (5 lanes)

| Lane | Scope | Files owned | Proof |
|---|---|---|---|
| **W1-A Channel wiring** | N6-T1 (+ `update_fee` receive), N6-T2 (`ChannelOperationsService`, `CommitScheduler` with ping-before-commit, `LocalOnlyHtlcSwitch`, `EnableHtlcs` gate), N6-T3, N6-T4; NL-234, NL-235 | `src/NLightning.Application/Channels/**` (new handlers, `Managers/ChannelManager.cs`, `Services/*`), `Application/Node/Managers/PeerManager.cs` (failed-channel error path), **hub:** `Application/DependencyInjection.cs`; `test/NLightning.Application.Tests/Channels/**` including `Harness/TwoNodeHarness.cs` | Handler tests (`Given_PersistFails_Then_NoRevokeSent`, …); harness: 30 HTLCs each way, fee round, txids identical every step (I7); **Docker N6-T5** after merge with W0-B: Alice `SendToRouteV2` to Bob with a random hash, Bob fails back `temporary_node_failure`, channel stays Active, commitment numbers 2/2 |
| **W1-B Payment core (pure/app)** | ONION M4-T2 processor, M4-T3, M4-T4 policy, M4-T6 onion/route build (no channel calls) | New `src/NLightning.Application/Payments/{Onion,FinalHop,Policy,Routing,Invoices}/**` (`IncomingOnionProcessor`, `FinalHopProcessor`, `HtlcForwardingPolicy`, `HintRouteBuilder`, `PaymentOnionFactory`, `InvoiceService`), `Application/NLightning.Application.csproj` (reference Bolt11), `Payments/PaymentsServiceCollectionExtensions.cs`; `test/NLightning.Application.Tests/Payments/**` | Build a 3-hop onion and peel it at each hop with our processor (real Sphinx); final-hop codes 0x400F/0x0012/0x0013; policy table (fee ±1 msat, delta, too-far, below-min); `InvoiceService` produces a BOLT11 that W0-D decodes, and (fixture) LND decodes |
| **W1-C Payment schema (migration owner)** | `AddInvoicesPaymentsAndCircuits` (N8 + M4-T7), NL-137 | Persistence, provider-migration and Repositories trees as in W0-B, plus `IUnitOfWork` | SQLite round trip for each table; container round trips (Postgres/SqlServer) |
| **W1-D IPC / CLI** | §3.9 commands, NL-152 | `src/NLightning.Transport.Ipc/**`, `src/NLightning.Daemon/{Ipc,Handlers,Services}/**`, **hub:** `Daemon/Extensions/NodeServiceExtensions.cs` (+ `RoutingOptions` config binding), `src/NLightning.Client/**`, `test/NLightning.Daemon.Tests/**` | MessagePack round trips; client/IPC handlers with mocked `IInvoiceService`/`IPaymentService`; CLI output snapshot |
| **W1-E Direct `channel_update` exchange** (S) | NL-099 subset, NL-236 half | New `Application/Gossip/ChannelUpdateService.cs` (subscribes to `IChannelMemoryRepository.OnChannelUpdated` → Open), `Domain/Node/Interfaces/IPeerService.cs` + `Infrastructure/Node/Services/PeerService.cs` (send a non-channel message; route inbound 258 to an event; store the peer's update in memory) | Unit tests; Docker: after opening C–D, David `GetChanInfo(scid)` shows Carol's policy, and `addinvoice --private` contains the C→D hint |

### Wave 2: reestablish, forwarding switch, send, single-hop Docker proofs (4 lanes)

| Lane | Scope | Files owned | Proof |
|---|---|---|---|
| **W2-A Reestablish** | N7-T1..T5 (T6 test) | New `Domain/Channels/Reestablish/**`, `Application/Channels/Reestablish/**`, `ChannelReestablishMessageHandler`, **hubs:** `ChannelManager.cs`, `IChannelManager.cs`, `PeerManager.cs`, `Application/DependencyInjection.cs` | Exhaustive planner table; harness disconnect at every message boundary plus crash at every persist point → convergence (I11); **Docker Proof N7** (a) restart us, (b) `RestartByAlias("alice")`, (c) crash after CS persist |
| **W2-B HTLC switch** | M4-T2 wiring, T4 forward, T5 propagation, T7 replay; N8-T2 settle | New `Application/Payments/Switch/**` (`HtlcSwitch` replacing `LocalOnlyHtlcSwitch` through its own extension), `test/NLightning.Application.Tests/Payments/Switch/**`, new `…/Harness/ThreeNodeHarness.cs` | In-process A→B→C: forward, fulfill propagation, final failure decoded at origin with the right source index, malformed conversion, **restart B mid-forward on SQLite** (circuit replay), never two locks held |
| **W2-C Send** | N8-T3 + M4-T6 multi-hop via hints; origin decrypt | New `Application/Payments/Send/**` (`PaymentService`) | Harness: B pays C directly and pays D through C via a hint; failure → `FailureInterpreter` result stored on the payment |
| **W2-D Docker proofs + ABCD authoring** | N8 proofs; ABCD test code | `Docker/NormalOperationFlowTests.cs` (N8: LND pays our invoice; we pay an LND invoice; 10 concurrent payments each way; trimmed HTLC), new `Docker/Abcd/**` | N8 proofs green at wave end; ABCD compiles and runs (expected to go green in Wave 3) |

### Wave 3: ABCD green, provider matrix, hardening (3 lanes)

| Lane | Scope | Files owned | Proof |
|---|---|---|---|
| **W3-A ABCD stabilization** | Fix what the e2e test finds. This lane is **serial and exclusive** across `src/NLightning.Application/**`; other Wave-3 lanes stay out of Application | `src/NLightning.Application/**`, `Docker/Abcd/**` | ABCD suite passes **3 runs in a row** (script loop, fresh fixture each run) |
| **W3-B Provider matrix** | Full-stack runs on real DBs | `Docker/Utils/NLightningTestNode.cs` provider wiring, new `Docker/Abcd/AbcdProviderMatrixTests.cs`, Postgres/SqlServer container tests; migration owner **only if** a schema fix is needed | ABCD happy path with Bob=Postgres and Carol=SqlServer; SQLite and Postgres/SqlServer container tests green |
| **W3-C Hardening** | Signed `channel_update` inside UPDATE failures; `HtlcExpiryMonitor` subset (N9-T2: fail incoming at or before `cltv_expiry - delta`); optional LND-funded channel proof (receive `update_fee`) | `Infrastructure`/`Domain` files only (failure factory, `Domain/Channels/Policies/HtlcDeadlinePolicy.cs`, monitor in `Infrastructure.Bitcoin` or a new Application folder coordinated with W3-A) | Variant a2 (below) green; deadline table tests |

Final gate after Wave 3: Release and Release.Native builds, format, all `!~Docker` tests, the 10k-seed Long simulator, all Docker tests (existing 12 + N6/N7/N8 proofs + ABCD + provider matrix), each ABCD run 3× in a row.

---

## 3. The ABCD test design

**Location:** `test/NLightning.Integration.Tests/Docker/Abcd/`, in collection `"regtest"` (shares `LightningRegtestNetworkFixture`; never runs in parallel with the other Docker classes that force-remove containers).

**Topology:**
- Built once per fixture through a lazily created `AbcdNetwork` held by the fixture (`fixture.GetOrCreateAsync(...)`, disposed with it).
- xUnit class fixtures can't take collection fixtures reliably, so the lazy object on the fixture is the simplest option.
- Each test first checks its preconditions and then asserts **deltas**, so test order does not matter.

Nodes:
- **Alice:** fixture LND `alice`.
- **David:** new fixture LND `david`, no auto channels.
- **Bob, Carol:** `NLightningTestNode`s in this process, each with its own port (`PortPoolUtil`), `FakeSecureKeyManager` and SQLite file. Log prefix `[bob]`/`[carol]`.
- Policies (distinct values, so a fee mix-up shows):
  - Bob: base 1,000 msat, 100 ppm, delta 40.
  - Carol: base 2,000 msat, 500 ppm, delta 40.

Channels (all NLightning-funded, so no `update_fee` is received and the plan's D9 holds):
1. Bob → Alice: 2,000,000 sat, push 1,000,000.
2. Bob → Carol: 2,000,000 sat.
3. Carol → David: 2,000,000 sat.

All at 10,000 sat/kw. Bob and Carol fund their wallets with `FundWalletAsync`. Mine 6, then loop "mine 1, sync barrier" until LND lists each channel `Active`, our `State == Open && IsPeerConnected && IsReestablished`, and `ShortChannelId` is set.

**Reestablish step:**
- `alice.DisconnectPeer(bob)`: Bob reconnects.
- `carol.PeerManager.DisconnectPeer(bob)` then reconnect explicitly.
- Restart Carol (Stop/Start, same key and DB): she reconnects to Bob and David.
- Assert every channel goes back to Active/usable, `channel_reestablish` was sent and received on each (count via `OnResponseMessageReady` plus inbound hook), and commitment numbers are unchanged.

**Happy path:**
- `X = 50,000,123 msat`.
- David `AddInvoice{value_msat=X, private=false, route_hints=[{Bob, scid(B–C), 1000, 100, 40}, {Carol, scid(C–D), 2000, 500, 40}]}`.
- `ResetMissionControl` on Alice.
- Alice `SendPaymentV2{payment_request, max_parts=1, outgoing_chan_ids=[A–B], fee_limit_msat=1e6, timeout_seconds=60}`.

Assertions:
- `SUCCEEDED`; the preimage equals David's `LookupInvoice.r_preimage`; David's invoice is `SETTLED` with `amt_paid_msat == X`.
- `fee_C = 2000 + floor(X*500/1e6)`, `amt_BC = X + fee_C`, `fee_B = 1000 + floor(amt_BC*100/1e6)`.
- Alice's `payment.fee_msat == fee_B + fee_C`; route `hops[0].fee_msat == fee_B`, `hops[1].fee_msat == fee_C`, `hops.Count == 3`.
- Bob via `ListChannels` (msat): A–B local `+ (amt_BC + fee_B)`, B–C local `− amt_BC`. Carol: B–C `+ amt_BC`, C–D `− X`.
- LND balances (sat): Alice A–B local `− floor(...)`, David C–D local `+ ⌊X/1000⌋`, each within 1 sat.
- Zero pending HTLCs (LND `pending_htlcs` empty; our Offered/Received counts 0).
- B–C commitment numbers mirror each other on Bob and Carol.
- Every channel still Active and every peer still connected.

**Variant (a), final-hop failure decodable at Alice:**
- David makes a hinted invoice, then `invoicesrpc.CancelInvoice`, then Alice pays.
- Expect `FAILED` with `FAILURE_REASON_INCORRECT_PAYMENT_DETAILS`, attempt `failure.code == INCORRECT_OR_UNKNOWN_PAYMENT_DETAILS` and `failure_source_index == 3`. That index proves Carol and Bob each wrapped the error onion correctly.
- Balances unchanged, zero pending HTLCs, channels Active.
- **a2 (W3-C stretch):** the hint underprices Carol's fee. Carol fails with `fee_insufficient` plus a signed `channel_update`, LND applies it and **retries successfully** (checks our `channel_update` bytes and signature).

**Variant (b), Bob restarts with an HTLC in flight:**
1. David `AddHoldInvoice(hash(p))` with hints.
2. Alice pays through a streaming call that is not awaited.
3. Wait until David's invoice is `ACCEPTED`, which proves the HTLC is locked in on all three hops.
4. `bob.StopAsync()` (plus a `CrashAsync()` variant); wait until Alice and Carol drop Bob.
5. **b2 (main):** David `SettleInvoice(p)` while Bob is down. Carol learns the preimage and persists it; her upstream fulfill waits.
6. `bob.StartAsync()`. Bob reconnects to both peers and reestablishes; Carol retransmits the fulfill; Bob's circuit replay fulfills Alice.
7. Alice `SUCCEEDED` with preimage `p`; fees and balances as in the happy path; zero pending HTLCs.
- **b1:** settle after Bob is back.
- No blocks are mined while the HTLC is in flight.

**Variant (c):**
- **Bob as sender:** David invoice with hint `[{Carol, scid(C–D), 2000, 500, 40}]`, then `bob.PayInvoiceAsync(bolt11)` through the client handler. Expect a preimage equal to David's, David `SETTLED`; Bob B–C `−(X + fee_C)`, Carol `+fee_C`.
- **Bob as receiver:** `bob.CreateInvoiceAsync(X2)`; Alice `SendPaymentV2` (direct, `fee_msat == 0`). Bob's invoice is Settled, Bob's A–B local `+X2`, the preimage matches.

**Flake avoidance:**
- Poll with deadlines everywhere; no fixed sleeps.
- `ChainSync.WaitAllAtTipAsync()` after every mine: every LND `GetInfo.synced_to_chain && block_height == tip`, and each NLightning `BlockchainMonitor.LastProcessedBlockHeight == tip`. This prevents CLTV disagreements, which LND reports as `expiry_too_soon`/`incorrect_cltv_expiry`.
- Mine only when no HTLC is in flight.
- `max_parts=1`, pinned `outgoing_chan_ids`, `ResetMissionControl` before each payment.
- Select LND channels by channel point, never by index.
- A test-only reconnect backoff of 1 s instead of 5 s.
- Log the LND version.
- Dump `docker logs alice|david` and both node logs on failure.
- Timeouts: 90 s for active, 60 s per payment, 6 min per test.
- Run with `scripts/run-abcd.sh` (proposed): 3× `dotnet test --filter FullyQualifiedName~Docker.Abcd`, stop at the first red.

---

## 4. Decisions for you (with my recommended defaults)

1. **Route discovery: route hints (B) or public channels plus BOLT 7 relay (A).** Default: **B**, with W0-E/W1-E adding direct `channel_update` exchange. A becomes the NL-099 epic afterwards.
2. **Who funds the test channels.** Default: **NLightning funds all three, with push to Alice**, so we never receive LND `update_fee`. The `update_fee` receive handler still ships in N6-T1. An LND-funded variant is a W3-C stretch; LND's funder `update_fee` timing in regtest is unverified.
3. **Empty (`len=0`) vs signed `channel_update` in UPDATE failures.** BOLT 4 allows empty, but LND 0.20 acceptance is **unverified**. Default: signed update once W0-E lands; empty only before then.
4. **attribution_data.** Default: don't advertise it, ignore it inbound, don't relay it (M3b later). Unverified whether LND 0.20 attaches TLV 1 to `update_fail_htlc` regardless; it is odd, so ignoring it is spec-compliant.
5. **Replay cache after restart (NL-078).** Default: keep it in-memory for this goal, and record that a restart loses replay protection (regtest only).
6. **Policy defaults.** `cltv_expiry_delta` 40 (BOLT 2 recommends ≥34; LND uses 80); invoice `c` 40; `max_cltv_expiry_distance` 2016; fee base 1000 msat / 1 ppm in production defaults (the test sets its own values).
7. **`EnableHtlcs`.** Default: true on regtest only, false elsewhere, until N9-T4 and BOLT 5 exist.
8. **Hosting Bob and Carol.** Default: **in-process** (same DI as the daemon, debuggable, restartable; no static state conflicts found). A containerised `nltg` image is optional later.
9. **Fixture.** Default: **extend** the shared regtest fixture with `david` instead of adding a second fixture, since container names like `miner` would clash.
10. **Timing of the restart variant.** Default: settle while Bob is down (b2) as the main assertion, b1 as a sub-case.
11. **`HtlcExpiryMonitor` (N9-T2).** It is not needed for the test to pass, but a forwarding node without it can lose funds. Default: W3-C, and a gate before any non-regtest use.
12. **Ledger IDs.** The next free ID is NL-239. Proposed new entries: route-hint e2e approach, multi-node test harness, direct `channel_update` exchange, `RoutingOptions`, and container fixtures depending on OrbStack routing.

## 5. Risks

- **HTLC signature order and trimming** must match LND on both commitments. The vectors cover it; the Docker N6 proof comes first.
- **Hub-file merge pressure:** `ChannelManager`/`PeerManager` change in Wave 1 and again in Wave 2, and the DI hubs change every wave. Keep the one-owner-per-wave rule.
- **Two NLightning nodes run identical code**, so a symmetric bug can pass between Bob and Carol. LND on both ends plus the reestablish and restart variants guard against this.
- **Two NLightning nodes connecting to each other at the same time:** the LND tie-break is implemented, but between two of our own nodes it is untested. Cover it in the W0-F smoke test.
- **SQL Server runs x64 under emulation on Apple Silicon.** It is slow; give container waits generous deadlines.
- **Engine events must be re-derived at startup** (I8) or variant (b) will double-fulfill or lose the fulfill. W2-B's restart harness is the guard.

### Critical Files for Implementation
- /Users/ms/nlightning/src/NLightning.Domain/Channels/Commitments/ChannelCommitments.cs
- /Users/ms/nlightning/src/NLightning.Application/Channels/Managers/ChannelManager.cs
- /Users/ms/nlightning/src/NLightning.Application/Node/Managers/PeerManager.cs
- /Users/ms/nlightning/test/NLightning.Integration.Tests/Docker/Utils/NLightningTestNode.cs
- /Users/ms/nlightning/test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs
