# BOLT 5 On-chain Handling: Implementation Plan for NLightning

This is the plan for BOLT 5 "Recommendations for On-chain Transaction Handling": watching the funding output, failing a channel by broadcasting our commitment, resolving every output of a local, remote or revoked commitment (to_local, to_remote, HTLC outputs, second-level HTLC transactions), penalty (justice) transactions, fee management for our sweeps, reorg safety, and later the anchor/CPFP variant. It also covers the BOLT 3 pieces BOLT 5 needs: witness forms, key derivation for sweeps and penalties, and the Appendix A weights. Every repo claim cites a repo-relative path. Claims marked **(unverified)** or **(inferred)** were not proven against running code; check them before you rely on them.

- **Spec source:** `lightning/bolts` master, fetched 2026-09-25: `05-onchain.md` (whole document) and `03-transactions.md` (§Commitment Transaction Outputs, §HTLC-Timeout and HTLC-Success Transactions, §Keys, Appendix A weights, Appendix C secrets). Re-read the requirement block before you implement a resolver.
- **Relation to the other plans:** BOLT2 plan ([`BOLT2_NORMAL_OPERATION_PLAN.md`](BOLT2_NORMAL_OPERATION_PLAN.md)) §"After N10" points here. BOLT2 **N9-T4** (`ChannelFailureService`, the only broadcast path) is milestone **O2** of this plan: one work item, implemented once. BOLT2 **N9-T2** (`HtlcExpiryMonitor`, ABCD lane W3-C) is the trigger that sends HTLCs on-chain; this plan consumes it. BOLT2 **N11-T3** (enable `option_anchors`) depends on **O7**.
- **Issue ledger:** the epic is NL-094 ([`ISSUES.md`](ISSUES.md)); sub-issues NL-095 (revocation watch stub), NL-096 (reorgs), NL-098 (mempool), NL-214/NL-215/NL-216 (block processing), NL-258 (no rebroadcast), NL-067 (wallet signing). New gaps found while writing this plan are listed in §2.2 as `OG#` rows with "new" in the NL column; the ledger agent files them. Tasks say "Resolves NL-…". Update the ledger entry in the same commit as the fix.
- **Status (2026-09-26, `wip/fafo` @ `5bbfbb5`, after gossip wave G-B):** **the O6-T4 blockers are fixed** (lane M2): NL-337 (c4fab04), NL-320 (6ea8b1c, 7e96e28, 9b4e4c7) and NL-311 (eff0196, 1902bb5, e0b3421, e3023b8); the whole on-chain Docker suite (22 + 2 Explicit, incl. the new `OnchainWatchCatchUpTests`) is green at integration, so Proofs O3-O6 pass. **The gate itself is unchanged** (`Node:EnableHtlcs` unset = regtest only): the decision belongs to the G-D integrator after a re-run of N9 `ChannelSafetyFlowTests`, ABCD and the LND/CLN normal-operation suites (ABCD 3/3 and LND/CLN were green at the G-B integration). Evidence and remaining risks: "O6-T4 evaluation evidence" in §5. Still open: O6-T4 decision, O7 anchors (NL-314, NL-067 second half), follow-ups NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330, NL-335, NL-336.
- Status after gossip wave G-A (superseded by the line above; `wip/fafo` @ `164289a`): **O8 done** (lane M1; NL-098 fixed): ZMQ `rawtx` in the chain monitor, `Application/Onchain/Mempool/MempoolReactor` (preimage from the mempool fulfilled upstream at once; penalty behind a revoked commitment broadcast before it confirms), proven by Docker `Onchain/OnchainMempoolTests`; the chain-processing halt is on IPC (`chainstatus`) and gates new HTLCs and channels (NL-216 fixed). NL-067 first half (signer data reloaded from the DB) landed in lane A2. O6-T4 still waits on NL-311, NL-320, NL-337; O7 not started. See "Gossip wave G-A record" in §5.
- Status after ABCD wave 7 (superseded by the line above; `wip/fafo` @ `4c37998`): O0-O6-T3 done; O6-T4 still **open** but no longer blocked by NL-316/NL-322: W7-B made final-hop HTLCs of our invoices claimable on chain (the switch accepts a final-hop HTLC of a Failed/OnchainResolving channel and commits it with the preimage on the incoming `HtlcRecord.KnownPreimage` in the invoice's settle save; `FinalHopClaims.GetAcceptedPreimageAsync` lets `LocalCommitResolver`/`RemoteCommitResolver` claim only with a Settled invoice's preimage and no fail removal, keeping B5-LCL-RO-02) and proved it with Docker `Onchain/OnchainFinalHopTests` (see "ABCD wave 7 record"). HTLCs stay regtest-only by default. Remaining before O6-T4: NL-311, NL-320, NL-337.
- Status after ABCD wave 6 (superseded by the line above): **O0-O6-T3 are done, wired and proven against LND.** Wave 6 (W6-F) added O6-T1 (per-target fee estimates, NL-296; `SweepScheduler` RBF of sweeps, claims and penalties, NL-317) and O6-T3 (rewind of completed watches and wallet UTXOs, re-resolution after reorgs, SCID move of a reconfirmed funding tx; NL-292, NL-293, NL-096) with **Proof O6** `Docker/Onchain/OnchainO6Tests` (a)-(c) green on net10.0 and net11.0. **O6-T4 (mainnet gate) is still closed**: opened in 09052d0 and reverted in 0c0d5c8 until NL-316 (and NL-311, NL-320, NL-322) are fixed. O7 and O8 not started. See "ABCD wave 6 record" in §5.
- Status after ABCD wave 5 (superseded by the line above): **O0-O5 are done, wired and proven against LND** (Docker Proofs O2, O3 (a)-(d), O4 (a)-(e), O5 (a) LND channel.db rollback and (b) deterministic NLightning cheater, green on net10.0 and net11.0). O6-T2 (100-block rule and Closed) is done; O6-T1 has the policy but no `SweepScheduler` (NL-317, NL-296); O6-T3 reorg re-resolution (NL-292, NL-293) and O6-T4 (mainnet gate) are open; O7 and O8 not started. See "ABCD wave 5 record" in §5.
- Status after ABCD wave 4 (superseded by the line above): O0 and O1 are done and wired; the pure/builder pieces of O2-O6 (O2-T1 partial, O2-T3, O2-T4, O3-T1, O3-T2, O3-T5, O4-T1, O4-T2, O5-T1, the O5-T2 planner rows, the O6-T1 policy) are done but **not wired**: no watcher acts on a funding spend yet, so nothing is swept and no penalty is sent. Next: O2-T2 remainder (NL-271, NL-297) and O2-T5 `OnchainChannelWatcher` (NL-272), then O3-T3/T4, O4-T3, O5-T2/T3 execution, O6. See "ABCD wave 4 record" in §5.
- **Status (2026-09-25, `wip/fafo` @ `b38ec28`, when written):** nothing of BOLT 5 exists. Nothing watches the funding output after confirmation, no commitment is ever broadcast by us, and no output of any commitment is swept. A peer that broadcasts a revoked commitment keeps the whole channel. Mainnet use is blocked on O1-O6 (BOLT2 plan §0.6: `EnableHtlcs` stays regtest-only until this plan can sweep).

---

## 0. How to use this document (agents)

1. Do the milestones in order: **O0 → O1 → … → O6**. O7 (anchors) and O8 (mempool) come later. O3, O4 and O5 share the classifier and sweep builders from O2/O3-T1; after O3-T1 lands, O4 and O5 may run in parallel lanes (they touch different resolver files).
2. A task is done only when all of these pass:
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` and `-c Release.Native` (signer and builder code is crypto code);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'`;
   - the task's own tests. Every sweep, claim and penalty builder is checked by **script execution** (NBitcoin `Script.VerifyScript` / `TransactionBuilder.Verify` against the spent output) and by the Appendix A weight bound (§1.8). Where the Appendix C secrets make it possible, the spent transaction is the Appendix C commitment or HTLC transaction itself (§6.2).
3. A milestone is done when its **Proof** passes. Docker proofs run locally only, in a new class `test/NLightning.Integration.Tests/Docker/OnchainFlowTests.cs` (namespace contains `Docker`, so CI skips it): `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker.Onchain"`. These tests close channels and one of them rolls back an LND database, so they never share a fixture (and therefore an LND node) with the other Docker suites: they run in **their own `dotnet test` process** through a new `scripts/run-onchain.sh`, the way `scripts/run-abcd.sh` gets a fresh fixture per run, and their collection is declared with `DisableParallelization = true` so a combined `~Docker` run can never start two fixtures at once (§5, Proof conventions).
4. Schema: one migration per milestone that needs one, all three providers, through `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh <Name>` (root `CLAUDE.md` › "Add an EF migration"). Only the wave's migration owner lane touches Persistence.
5. DI: new services go in the layer's `DependencyInjection.cs` (`AddBitcoinInfrastructure`, `AddApplicationServices`, or an `Add<Area>Services` extension called from it). The Docker node uses `AddNltgNodeServices`, so there is nothing to mirror.
6. **Safety gate:** `EnableHtlcs` defaults to true outside regtest only after O6's proof passes; anchors (`OptionAnchors`) stay `No` until O7's proof passes.
7. Commit shape: one task per commit, lowercase imperative subject, cite the NL IDs and the task id (e.g. `(NL-094 / O3-T2)`).

---

## 1. Spec requirements summary

Requirement ids (`B5-…`) are used by the tasks and the traceability matrix (§6).

### 1.1 General (05 §General Nomenclature, §General Requirements)
- **B5-GEN-01** Once the funding tx is broadcast or a commitment signature for a commitment with an HTLC output was sent, monitor the chain for spends of every output that is not *irrevocably resolved*, until all are.
- **B5-GEN-02** Resolve every output as specified per case. An output is *irrevocably resolved* once the resolving tx is **100 blocks** deep on the most-work chain.
- **B5-GEN-03** Be prepared to resolve outputs several times (**reorgs**).
- **B5-GEN-04** Funding output spent while the channel is not closed: MAY send `error`, SHOULD fail the channel.
- **B5-GEN-05** Ignore invalid transactions.
- **B5-GEN-06** A spend of the funding output that is none of mutual close, local/remote unilateral close or revoked close: MUST warn the user of potentially lost funds (key leak).
- **B5-GEN-07** MAY monitor only the most-work chain; MAY monitor the mempool (lower latency for preimage extraction).

### 1.2 Failing a channel (05 §Failing a Channel)
- **B5-FAIL-01** A local commitment that never had `to_local` or an HTLC output: MAY forget the channel.
- **B5-FAIL-02** Current commitment without `to_local`/HTLC outputs: MAY wait for the remote to close, MUST NOT forget the channel until it does.
- **B5-FAIL-03** Otherwise: with a valid `closing_signed` with sufficient fee, SHOULD mutual-close with it (N10 territory).
- **B5-FAIL-04** If the node knows or assumes its state is outdated (data loss): MUST NOT broadcast its last commitment.
- **B5-FAIL-05** Otherwise MUST broadcast the **last commitment it has a signature for**.
- **B5-FAIL-06** (anchors) MUST spend `to_local_anchor` (or `shared_anchor`) with enough fee to get the commitment mined; SHOULD RBF that child if it is not enough (O7).

### 1.3 Mutual close (05 §Mutual Close Handling)
- **B5-MUT-01** The closing tx resolves the funding output; nothing else to do (the output goes to our `shutdown` script). Detection only; building it is BOLT2 N10.

### 1.4 Local commitment on chain (05 §Unilateral Close Handling: Local Commitment Transaction)
- **B5-LCL-01** SHOULD spend `to_local` to a convenient address, only after `to_self_delay` (the **remote's** `to_self_delay`, i.e. `ChannelParams.Remote.ToSelfDelay`) with input `nSequence = to_self_delay`, witness `<local_delayedsig> <>`.
- **B5-LCL-02** MAY ignore `to_remote` (it is the peer's).
- **Local offers (our offered HTLCs, 05 §Local Commitment, Local Offers):**
  - **B5-LCL-LO-01** HTLC output spent with a preimage (the peer's direct preimage spend): extract the preimage from the witness; the output is irrevocably resolved.
  - **B5-LCL-LO-02** Timed out (tip height `>= cltv_expiry`) and unresolved: MUST spend it with the **HTLC-timeout** tx (pre-signed, `0 <remotehtlcsig> <localhtlcsig> <>`).
  - **B5-LCL-LO-03** Once the HTLC-timeout is at reasonable depth: MUST fail the corresponding incoming HTLC; MUST resolve the HTLC-timeout output, after `to_self_delay`, witness `<local_delayedsig> 0` (SHOULD, to a convenient address).
  - **B5-LCL-LO-04** A committed HTLC with **no output** in this commitment (dust, not yet committed, already removed): preimage known → MUST fulfill the incoming HTLC; else once the commitment is at reasonable depth MUST fail the incoming HTLC, and MAY fail it sooner if no valid commitment contains an output for it.
- **Remote offers (HTLCs the peer offered us, 05 §Local Commitment, Remote Offers):**
  - **B5-LCL-RO-01** Preimage known **and** we committed an outgoing HTLC for it: MUST resolve with the **HTLC-success** tx (`0 <remotehtlcsig> <localhtlcsig> <payment_preimage>`), then resolve its output after `to_self_delay`.
  - **B5-LCL-RO-02** MUST NOT reveal its own preimage when it is not the final recipient (preimage-extraction attack: an HTLC that carries our invoice's hash but whose onion made us an intermediate hop).
  - **B5-LCL-RO-03** Remote not irrevocably committed to the HTLC: MUST NOT spend it.
  - **B5-LCL-RO-04** Unresolved and expired: irrevocably resolved (the peer times it out; nothing for us).

### 1.5 Remote commitment on chain (05 §Unilateral Close Handling: Remote Commitment Transaction)
- **B5-RMT-01** Handle both valid unrevoked remote commitments: the current one **and** the one we signed and whose `revoke_and_ack` is outstanding (`RemoteNextCommit`).
- **B5-RMT-02** MAY ignore `to_local` (theirs) and `to_remote` (ours: P2WPKH to `remotepubkey`, which with `option_static_remotekey` is our `payment_basepoint`; with anchors it is `<remotepubkey> OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY`, spent with `nSequence = 1`, witness `<remote_sig>`). *(NLightning deviation, D5: we sweep it, because the key is a channel key, not a wallet address.)*
- **B5-RMT-03** Not able to handle it (e.g. data loss, unknown commitment number): MUST inform the user of potentially lost funds. With data loss we can still spend `to_remote` (static_remotekey needs no per-commitment point).
- **Local offers (ours, "received HTLC outputs" from the peer's point of view):**
  - **B5-RMT-LO-01** Spent with the preimage (the peer's HTLC-success tx): MUST extract the preimage from the witness.
  - **B5-RMT-LO-02** Timed out and unresolved: MUST spend it to a convenient address: input `nLockTime = cltv_expiry`, witness `<localhtlcsig> <>` (our HTLC key tweaked by the **remote** point), `nSequence = 1` with anchors.
  - **B5-RMT-LO-03** Same "no output" rules as B5-LCL-LO-04.
- **Remote offers (to us, "offered HTLC outputs" on their commitment):**
  - **B5-RMT-RO-01** Preimage known and outgoing committed: MUST spend it to a convenient address, witness `<localhtlcsig> <payment_preimage>` (direct, no second stage). Same B5-LCL-RO-02 rule.
  - **B5-RMT-RO-02** Remote not irrevocably committed: MUST NOT spend it. Unspent and expired: irrevocably resolved.

### 1.6 Revoked commitment on chain (05 §Revoked Transaction Close Handling)
- **B5-REV-01** MUST NOT broadcast a commitment whose `per_commitment_secret` we exposed (signer guard, BOLT2 I4).
- **B5-REV-02** MAY leave our main output (their `to_remote`, a P2WPKH to us) alone. *(Deviation D5: we sweep it like B5-RMT-02.)*
- **B5-REV-03** MUST resolve the peer's `to_local` with the revocation key: witness `<revocation_sig> 1`.
- **B5-REV-04** MUST resolve the peer's offered HTLCs by: revocation key on the commitment output (`<revocation_sig> <revocationpubkey>`), or the preimage, or by spending their HTLC-timeout tx if they published it.
- **B5-REV-05** MUST resolve our offered HTLCs by: revocation key, or timeout on the commitment output, or by spending their HTLC-success tx.
- **B5-REV-06** MUST resolve the peer's HTLC-timeout and HTLC-success txs with the revocation key: witness `<revocation_sig> 1` on their output.
- **B5-REV-07** SHOULD extract the preimage from an HTLC-success witness.
- **B5-REV-08** Without anchors: MAY resolve all outputs in one tx. With `SIGHASH_SINGLE|ANYONECANPAY` HTLC txs (anchors): MAY batch, but SHOULD split into per-output penalties once `security_delay` (recommended **18**) blocks remain before the revoked output's expiry (pinning).
- **B5-REV-09** MUST handle its transactions being invalidated by HTLC transactions (the cheater's second-level tx spends the output our batched penalty targeted: re-plan against the second-level output).
- **Weights (§Penalty Transactions Weight Calculation, Appendix A):** witnesses `to_local` 160, `offered_htlc` 243, `accepted_htlc` 249; inputs 324 / 407 / 413; non-witness overhead `4*53 + 2`; optional `to_remote` input +272; max 966 HTLC inputs per penalty (so 483 bidirectional fits in one tx).

### 1.7 Generation of HTLC transactions (05 §Generation of HTLC Transactions)
- **B5-HTX-01** Without anchors: HTLC-timeout/success are complete `SIGHASH_ALL` txs with their fee inside; broadcast as is.
- **B5-HTX-02** With anchors: the peer's HTLC signature is `SIGHASH_SINGLE|ANYONECANPAY`, ours `SIGHASH_ALL`; the tx has zero fee and MUST be combined with inputs that pay the fee; MAY be combined with other txs (O7).

### 1.8 BOLT 3 facts the resolvers need
| Item | Rule |
|---|---|
| Commitment number on chain | locktime = `0x20` in the top byte + the lower 24 bits, `txin[0].nSequence` = `0x80` in the top byte + the upper 24 bits, of `number XOR lower48(SHA256(opener payment_basepoint ‖ accepter payment_basepoint))`. Appendix C: obscured = `0x2bb038521914 ^ 42`. |
| to_local script | `OP_IF <revocationpubkey> OP_ELSE <to_self_delay> OP_CSV OP_DROP <local_delayedpubkey> OP_ENDIF OP_CHECKSIG`; same script on HTLC-timeout/success outputs |
| Offered / received HTLC scripts | 03 §Offered/Received HTLC Outputs; anchors add `1 OP_CSV OP_DROP` (spends need `nSequence = 1`) |
| Keys | `privkey = basepoint_secret + SHA256(per_commitment_point ‖ basepoint)` (delayed, htlc); `remotepubkey = payment_basepoint` (static_remotekey, **compulsory** in `FeatureOptions.OptionStaticRemoteKey`); `revocationprivkey = revocation_basepoint_secret·SHA256(revocation_basepoint ‖ per_commitment_point) + per_commitment_secret·SHA256(per_commitment_point ‖ revocation_basepoint)` |
| Second-level fee (no anchors) | timeout `floor(feerate·663/1000)`, success `floor(feerate·703/1000)`; zero with anchors |
| Anchor | 330 sat, `<funding_pubkey> OP_CHECKSIG OP_IFDUP OP_NOTIF OP_16 OP_CSV OP_ENDIF`; anyone may sweep after 16 blocks with `<>` |
| Appendix C secrets | `local_delayed_payment_basepoint_secret`, `remote_revocation_basepoint_secret`, `local_payment_basepoint_secret`, `remote_payment_basepoint_secret`, `x_local_per_commitment_secret` (0x1f1e…0001), `local_delayed_privkey`, `remote_privkey`: enough to sign a to_local sweep and a full penalty against the Appendix C commitments **(which key the vectors' to_remote uses, derived `remote_privkey` or the basepoint, must be checked)** |

---

## 2. Current state (verified 2026-09-25 on `wip/fafo` @ `b38ec28`)

### 2.1 What exists
- **Chain monitor:** `BlockchainMonitorService` (`src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`, interface `Wallet/Interfaces/IBlockchainMonitor.cs`): ZMQ `rawblock`, an ordered block queue with bounded retries (NL-097), `OnNewBlockDetected`, watched **txids** (`WatchTransactionAsync`, `WatchedTransactionModel` with height + in-block index for the SCID, persisted by `WatchedTransactionDbRepository`), wallet deposits and wallet UTXO spends (`CheckBlockForWalletMovement`), `PublishAndWatchTransactionAsync` (watch saved, then `IBitcoinChainService.SendTransactionAsync`). Mempool code is commented out (`:271-279`, `:493-504`).
- **Chain RPC:** `IBitcoinChainService` (`SendTransactionAsync`, `GetTransactionAsync`, `GetCurrentBlockHeightAsync`, `GetBlockAsync`, `GetTransactionConfirmationsAsync`).
- **Transactions:** commitment (`CommitmentTransactionModelFactory` in `src/NLightning.Domain/Bitcoin/Transactions/Factories/`, `CommitmentTransactionBuilder.BuildWithOutputMap` in `src/NLightning.Infrastructure.Bitcoin/Builders/`, HTLC outputs in tx order), HTLC-timeout/success (`HtlcTransactionModelFactory`, `HtlcTransactionBuilder.Build`/`AddWitness`); all Appendix C and F vectors byte-exact (`test/NLightning.Integration.Tests/BOLT3/`). Output scripts: `Outputs/{ToLocalOutput,ToRemoteOutput,ToAnchorOutput,OfferedHtlcOutput,ReceivedHtlcOutput,HtlcResolutionOutput}.cs`.
- **Keys:** `KeyDerivationService` (`src/NLightning.Infrastructure.Bitcoin/Services/`): `DerivePublicKey`, `DerivePrivateKey`, `DeriveRevocationPubKey`, **`DeriveRevocationPrivKey`** (Appendix E vector, `KeyDerivationServiceTests`, `Bolt3IntegrationTests:646`; no runtime caller), `GeneratePerCommitmentSecret`. `CommitmentNumber` (`src/NLightning.Domain/Protocol/Models/CommitmentNumber.cs`) has `ObscuringFactor`, `Obscure`, `LockTime`, `Sequence`; no decode.
- **Signer:** `ILightningSigner` / `LocalLightningSigner` (`src/NLightning.Infrastructure.Bitcoin/Signers/`): channel keys m/6425'/0'/0'/0/i with hardened children funding 0', revocation 1', payment 2', delayed 3', htlc 4', per-commitment seed 5'. `SignChannelTransaction` (input 0, funding key, `SIGHASH_ALL`), `SignLocalHtlcTransaction` (our HTLC tx, `SIGHASH_ALL`), `SignRemoteHtlcTransactions`, `ValidateLocalHtlcSignatures`, revocation guard (NL-189), `SignFundingTransaction` (signs wallet inputs of the funding tx). `SignWalletTransaction` throws `NotImplementedException` (NL-067).
- **State we keep:** `CommitmentEntity` (`src/NLightning.Infrastructure.Persistence/Entities/Channel/CommitmentEntity.cs`) with three slots: our current commitment (spec + the peer's commitment signature + HTLC signatures in output order), the peer's current commitment (spec + its per-commitment point) and the unacked one we signed. `HtlcRecord.KnownPreimage` (I10) and `HtlcRemoval` hold learned preimages. The peer's shachain is persisted on every `revoke_and_ack` (`RemoteShachainDbRepository`; `SecretStorageService.DeriveOldSecret(index)` in `src/NLightning.Infrastructure/Protocol/Services/`). `ChannelEntity.DataLossDetected`, `ErrorSent`, `ChannelState.Failed = 35`.
- **Events:** the switch port `IHtlcSwitch` (`src/NLightning.Domain/Channels/Interfaces/IHtlcSwitch.cs`) consumes `OutgoingHtlcFulfilled(channel, id, hash, preimage)` and `OutgoingHtlcFailed(channel, id, hash, HtlcRemoval)`; it is idempotent and replayed at startup.
- **Fees:** `IFeeService.GetFeeRatePerKwAsync` (one rate, no confirmation target; `src/NLightning.Domain/Bitcoin/Interfaces/IFeeService.cs`).
- **Channel failure:** `ChannelFailedException.MustBroadcast` exists; `ChannelManager.PersistFailedChannelAsync` (`src/NLightning.Application/Channels/Managers/ChannelManager.cs:~600`) only logs it.

### 2.2 Gaps that block BOLT 5
All verified in code unless marked. "Gate" = the task that fixes it.

| # | Gap | Evidence | NL | Gate |
|---|---|---|---|---|
| OG1 | Nothing watches the **funding output** after confirmation: the funding watch is completed and dropped (`BlockchainMonitorService.ConfirmTransaction` removes it from `_watchedTransactions`), and block inputs are only matched against wallet UTXOs (`CheckBlockForWalletMovement` → `TrySpendUtxo`). A force close by the peer is never noticed; the channel stays Open. | `BlockchainMonitorService.cs:539-552,648-649` | NL-094 | O0-T2 |
| OG2 | No reorg handling: heights and SCIDs are never rolled back; only a "possible reorg" warning when a height repeats. | `BlockchainMonitorService.cs:375-376` | NL-096 | O0-T3 |
| OG3 | A failed block can be partly saved; the tip is not processed at startup; a halt is only logged. | `ProcessNewBlock` saves after a halted round; `FillQueueFromChainAsync` stops below `_catchUpHeight` | NL-214, NL-215, NL-216 | O0-T3 |
| OG4 | The HTLC set of a **revoked** remote commitment is lost: on `revoke_and_ack` the `RemoteCurrentSlot` row is overwritten with the next commitment. The shachain is kept, so the peer's `to_local` could be penalized, but its HTLC output scripts (hash, cltv, amount) cannot be rebuilt. | `ChannelStateDbRepository.ApplyAsync` (`src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs:80-84`), `ChannelCommitments.ReceiveRevoke` (`:626-647`) | new | O1-T1 |
| OG5 | `RevocationWatchEntity` stores one pre-signed penalty per revoked commitment; unmapped, no migration; `IRevocationWatchDbRepository` is empty; `PenaltyTransactionModel` and `PenaltyTransaction` are empty classes. A pre-signed penalty also fixes its fee at revocation time. | `Entities/Bitcoin/RevocationWatchEntity.cs`, `Domain/Bitcoin/Interfaces/IRevocationWatchDbRepository.cs`, `Domain/Bitcoin/Transactions/Models/PenaltyTransactionModel.cs`, `Infrastructure.Bitcoin/Transactions/PenaltyTransaction.cs` | NL-095 | O1-T2 (delete, D3) |
| OG6 | No broadcast path for our commitment: `MustBroadcast` is logged only; no service builds the fully signed latest local commitment. | `ChannelManager.cs:~603` | NL-094 (BOLT2 N9-T4) | O2-T2 |
| OG7 | Signer has no sweep, claim or penalty API: `SignChannelTransaction` is input 0 + funding key only; no delayed-payment, payment, remote-point HTLC or revocation-key signing; no guard refusing a broadcast signature for a non-latest local commitment or after `DataLossDetected`. | `ILightningSigner.cs`, `LocalLightningSigner.cs` | NL-094, NL-067 | O2-T1, O3-T1 |
| OG8 | Publishing is tied to a txid watch, the watch is saved before the send, and the raw tx is not stored: nothing can be rebroadcast after a failure, restart or reorg. | `PublishAndWatchTransactionAsync` (`:166-182`) | NL-258 | O0-T1 |
| OG9 | `to_remote` pays our channel `payment_basepoint` key (static_remotekey), which is not a wallet address: the wallet's deposit scan (`_watchedAddresses`) never sees it, so those funds never reach the wallet balance unless swept. | `CheckBlockForWalletMovement` (`:590-651`) | new | O4-T1 |
| OG10 | No way to tell the switch an outgoing HTLC failed **on chain**: `HtlcRemovalKind` is `Fulfill`/`Fail`/`FailMalformed` and a `Fail` carries the downstream peer's encrypted reason; an on-chain timeout has none, so the switch must create `permanent_channel_failure` as the erring node. | `Domain/Channels/Commitments/HtlcRemovalKind.cs`, `OutgoingHtlcFailed.cs` | new | O3-T4 |
| OG11 | No channel state for "a commitment is on chain, outputs are being resolved": `Closing = 30` means a mutual close was broadcast, `Failed = 35`, `Closed = 40`. | `Domain/Channels/Enums/ChannelState.cs` | new (closed by O1-T2, 4fd3617) | O1-T2 |
| OG12 | `IFeeService` returns one feerate; deadline-driven sweeps and penalties need a rate per confirmation target. | `IFeeService.cs` | NL-296 | O6-T1 |
| OG13 | No trigger sends an HTLC on chain: `HtlcExpiryMonitor` (BOLT2 N9-T2) does not exist yet (ABCD W3-C). | — | NL-094 | BOLT2 N9-T2 (consumed by O3) |
| OG14 | `CommitmentNumber` cannot decode an obscured number from a tx; `CommitmentEntity` stores no commitment txid (it is rebuilt from the spec). | `CommitmentNumber.cs`, `CommitmentEntity.cs` | new (decode done 5926d0c; no stored txid) | O2-T4 |
| OG15 | No mempool monitoring. | `BlockchainMonitorService.cs:271-279` | NL-098 (fixed in gossip wave G-A) | O8 |
| OG16 | Anchors: no CPFP, `SignWalletTransaction` throws, HTLC txs with `SINGLE\|ANYONECANPAY` cannot get fee inputs. | `LocalLightningSigner.cs:331-334` | NL-067, NL-094 | O7 |

### 2.3 Assumptions checked against the code
| Topic | Assumption | Code says |
|---|---|---|
| to_remote key | derived from the point | static_remotekey is **compulsory** (`FeatureOptions.OptionStaticRemoteKey = Compulsory`): to_remote is P2WPKH to the peer's-view `payment_basepoint`, i.e. our key m/…/2'. Sweeping needs no per-commitment point, so it works under data loss. |
| Anchors | may be negotiated | `OptionAnchors = No` and experimental-gated (BOLT2 D5). O1-O6 target non-anchor channels only; every resolver still takes `HasAnchors` so O7 does not reshape them. |
| Revoked remote point | must be stored | Not needed: `point = secret·G`, and the secret comes from the shachain (`DeriveOldSecret(PerCommitmentIndex.From(n))`). |
| Our local HTLC signatures | must be re-requested | Not needed: the peer's HTLC signatures for our current commitment are in `CommitmentEntity.HtlcSignatures` (slot 0), in output order; `SignLocalHtlcTransaction` gives ours. |
| Preimages | only in memory | `HtlcRecord.KnownPreimage` is persisted before use (BOLT2 I10); invoice preimages are in the invoice table. |

---

## 3. Design

### 3.1 Layering
- **Domain** (`src/NLightning.Domain/Onchain/`, new, BCL only): value models and pure decisions.
  - `ChainTx` (txid, version, locktime, inputs `(outpoint, sequence, witness stack)`, outputs `(amount sat, scriptPubKey bytes)`), built from NBitcoin in Infrastructure.Bitcoin. Domain never parses Bitcoin bytes itself.
  - `FundingSpendClassifier` (pure): given the channel's static data, the persisted commitment state and the spending `ChainTx`, returns `Mutual | LocalCommit(n) | RemoteCommit(n) | RemoteNextCommit(n) | Revoked(n) | FutureRemote(n) (data loss) | Unknown`. It decodes the commitment number from locktime + `txin[0].nSequence` (`CommitmentNumber.Decode`, O2-T4) and compares txids with the rebuilt candidates.
  - `OutputResolutionPlanner` (pure state machine): per output, from its descriptor (§3.3) and the chain facts (tip height, confirmations, known preimages, HTLC irrevocability, whether an incoming HTLC depends on it), returns actions: `Wait(untilHeight | csv)`, `Broadcast(spendKind)`, `RaiseFulfilled(htlc, preimage)`, `RaiseFailed(htlc)`, `Resolved`, `IrrevocablyResolved`. Exhaustive table tests like `ReestablishPlanner`.
  - `SweepFeePolicy` (pure): feerate for a deadline, RBF steps, value caps (§3.7).
  - Ports: `IOnchainTransactionFactory` (builds sweeps/claims/penalties from descriptors), `IChainBroadcaster` (publish + persist raw tx for rebroadcast), `IOnchainResolutionDbRepository`, `IRevokedCommitmentDbRepository`.
- **Infrastructure.Bitcoin:** the monitor's outpoint watching and reorg handling (`Wallet/BlockchainMonitorService.cs`), `ChainTx` mapping, `Onchain/CommitmentOutputMapper` (rebuilds the expected commitment with the existing factory/builder, maps each vout to a descriptor, and rebuilds the HTLC-timeout/success txs), builders `Builders/SweepTransactionBuilder.cs` (one or more inputs of known scripts → one output), `Builders/PenaltyTransactionBuilder.cs`, and signer extensions (§3.5).
- **Application** (`src/NLightning.Application/Onchain/`, new): `ChannelFailureService` (BOLT2 N9-T4: the **only** path that broadcasts our commitment), `OnchainChannelWatcher` (subscribes to outpoint-spent and new-block events, takes the channel lock, runs the classifier and the planner, persists, broadcasts, raises switch events), `SweepScheduler` (fee bumps and rebroadcasts per block), `AddOnchainServices` extension registered from `AddApplicationServices` (the integrator adds the one line).
- **Persistence/Repositories:** the tables of §3.6, one migration per milestone, all three providers.

### 3.2 Detection flow
1. **Watch:** every channel's funding outpoint is registered with the monitor when the funding tx is broadcast (funder) or `funding_signed` is sent (fundee), and at startup for every channel not `Closed`/`Stale` (backfill). Every output we must resolve and every resolving tx we broadcast is watched the same way (outpoints for spends, txids for depth).
2. **Block:** the monitor processes a block in order: raise `OnNewBlockDetected`; for every input that spends a watched outpoint raise `OnWatchedOutpointSpent(channelId, outpoint, ChainTx spender, height, index, blockHash)`; update watched-tx depths. Everything of one block is saved in **one unit of work** (fixes NL-214), and the event handlers enqueue work instead of saving in the monitor's scope.
3. **Classify** (under the channel lock): the watcher classifies the funding spend (§3.3), persists a `ChannelCloseRecord` and the output descriptors in one save, moves the channel to `OnchainResolving` (or `Closed` for a mutual close past 100 blocks), sends `error` if the peer is connected and the channel was not already failed (B5-GEN-04), refuses every further `IChannelOperations` call, and stops the commitment engine for the channel.
4. **Resolve:** on each block the planner runs for every unresolved output of every closing channel; actions are persisted first, then broadcast (persist-before-broadcast, like I1), then switch events are raised after the save (idempotent, replayed at startup like the BOLT2 events).
5. **Finish:** when every output is irrevocably resolved (100 blocks), the channel becomes `Closed`, its watches are dropped and its revoked-commitment log is deleted.

### 3.3 Output descriptors per case
Keys: *ours* = derived from our basepoint secrets (signer); *point* = the per-commitment point of the commitment on chain (ours for a local commitment, the peer's stored point for a remote one, `secret·G` for a revoked one).

| Case | Output | Descriptor | Resolution (non-anchor) |
|---|---|---|---|
| Local | to_local | `DelayedToLocal(csv = Remote.ToSelfDelay, key = our delayed key @ our point)` | sweep after CSV: `<sig> <>`, `nSequence = csv` (B5-LCL-01) |
| Local | to_remote | ignore | resolved by the commitment |
| Local | our offered HTLC | `LocalOfferedHtlc(id, cltv, stored remote HTLC sig)` | at `cltv_expiry`: HTLC-timeout (pre-signed, fee inside); its output → `DelayedToLocal` sweep after CSV; fail upstream when the HTLC-timeout reaches reasonable depth; a preimage spend by the peer → extract + fulfill upstream |
| Local | their offered HTLC | `LocalReceivedHtlc(id, preimage?)` | preimage known and allowed (§3.4): HTLC-success, then `DelayedToLocal` sweep; else leave it |
| Remote / RemoteNext | to_remote (ours) | `PaymentToRemote(key = our payment_basepoint secret)` | sweep at once (D5) |
| Remote | to_local | ignore | theirs |
| Remote | our offered HTLC | `RemoteReceivedHtlc(id, cltv, key = our HTLC key @ their point)` | at `cltv_expiry`: direct claim `<sig> <>`, `nLockTime = cltv_expiry`; a preimage spend (their HTLC-success) → extract + fulfill upstream |
| Remote | their offered HTLC | `RemoteOfferedHtlc(id, preimage?)` | preimage known and allowed: direct claim `<sig> <preimage>` |
| Revoked | their to_local | `RevokedToLocal(revocation key @ secret)` | penalty `<revsig> 1` |
| Revoked | any HTLC output | `RevokedHtlc(id, direction, cltv, revocation key)` | penalty `<revsig> <revocationpubkey>`; if their HTLC tx spends it first → new descriptor `RevokedSecondLevel` on its output, penalty `<revsig> 1`; extract a preimage from an HTLC-success witness (B5-REV-07) |
| Revoked | to_remote (ours) | `PaymentToRemote` | sweep (D5), may share the penalty tx (+272 weight) |
| FutureRemote (data loss) | to_remote | `PaymentToRemote` | sweep; everything else unrecoverable: CRITICAL alert (B5-RMT-03) |
| Unknown | — | — | CRITICAL alert, channel `Failed`, nothing to sweep (B5-GEN-06) |

"Committed HTLCs with no output" (B5-LCL-LO-04, B5-RMT-LO-03): computed per case from the persisted HTLC states against the HTLCs present in the on-chain commitment. Preimage known → `RaiseFulfilled` at once; else `RaiseFailed` once the commitment is `Onchain:ReasonableDepth` deep (default 6), or at once if no valid commitment (neither our current, the peer's current nor the peer's next) holds an output for it (trimmed everywhere). The same rule applies to a **revoked** commitment on chain (B5-REV-RES-02, §3.4): our offered HTLCs that are committed but have no output in the revoked commitment are failed upstream once the revoked commitment is `ReasonableDepth` deep, or fulfilled at once if the preimage is known.

### 3.4 HTLC safety rules
- **Irrevocability first (B5-LCL-RO-03, B5-RMT-RO-02):** never claim an incoming HTLC the peer is not irrevocably committed to (engine state below `RcvdAddAckRevocation` 34 in the persisted table).
- **Preimage-extraction guard (B5-LCL-RO-02):** claim an incoming HTLC with a preimage only when `HtlcRecord.KnownPreimage`/our fulfill removal is set for **that** HTLC. The switch sets it only after accepting the HTLC as final hop (invoice checks passed) or after the downstream fulfilled the forward. A preimage that only exists in the invoice table is never used on chain for an HTLC the switch did not accept.
- **Fail upstream only when final:** `OutgoingHtlcFailed` for an on-chain HTLC is raised only when the HTLC-timeout (local case) or our timeout claim (remote case) is at reasonable depth, or the commitment without the output is (B5-LCL-LO-03/04). A fulfill upstream is raised at once when a preimage is seen on chain (and persisted first, I10).
- **Revoked case (our offered HTLCs on a revoked commitment; the spec's revoked section has no upstream rule, so these are NLightning rules derived from the local/remote ones):**
  - **B5-REV-RES-01** A preimage seen on chain for one of our offered HTLCs (the cheater's HTLC-success witness, B5-REV-07, or a direct preimage spend of the commitment output) → persist it (I10), then `OutgoingHtlcFulfilled` at once, whether or not our second-level penalty later takes the funds.
  - **B5-REV-RES-02** Our offered HTLC resolved by our penalty (revocation spend of the commitment output, or the second-level penalty of the cheater's HTLC-success/timeout output) without a preimage ever appearing → `OutgoingHtlcFailed` (`OnchainTimeout`, O3-T4) once that penalty is `ReasonableDepth` deep.
  - **B5-REV-RES-03** Our offered HTLC committed but with **no output** in the revoked commitment (dust, not yet in that state, already removed) → preimage known: fulfill at once; else `OutgoingHtlcFailed` once the revoked commitment is `ReasonableDepth` deep (as B5-LCL-LO-04).
  - Their offered HTLCs on a revoked commitment need no upstream action from us (we have no outgoing HTLC depending on them); the incoming side of a forward through us is on a different channel and follows that channel's rules.
  Without these rows the upstream HTLC would hang until the BOLT2 N9-T2 deadline force-closes the upstream channel too.
- **Upstream deadline:** if the incoming HTLC is also on chain, nothing is sent upstream; its own resolution runs on its channel. The BOLT2 N9-T2 deadlines (`cltv_expiry + G` for offered, `2R+G+S` for fulfilled) decide when a channel goes on chain; this plan does not change them.

### 3.5 Signer API additions (`ILightningSigner`, `LocalLightningSigner`)
```csharp
// Our own latest commitment, fully signed, for broadcast (N9-T4). Refuses unless number == LocalCommitmentNumber,
// the peer's signature is present and verifies, and the channel is not marked DataLossDetected (I4, I12, B5-FAIL-04).
// On success it records "broadcast-signed at N" for the channel BEFORE returning the signed tx.
SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, LocalCommitmentBroadcastContext context);
void MarkDataLoss(ChannelId channelId);                                        // from the persisted flag at registration
void MarkBroadcastSigned(ChannelId channelId, ulong commitmentNumber);         // from the persisted broadcast row at registration

// One input of a sweep, claim or penalty. The signer derives the key from the kind; SIGHASH_ALL.
CompactSignature SignSweepInput(ChannelId channelId, SweepSigningContext context);
// SweepSigningContext(tx bytes, input index, witness script (or P2WPKH script code), amount, kind, point?, secret?)
// kind: DelayedPayment  -> delayed_basepoint_secret + SHA256(point || delayed_basepoint)        (to_local, 2nd level)
//       Payment         -> payment_basepoint_secret (static_remotekey)                            (to_remote)
//       HtlcRemotePoint -> htlc_basepoint_secret + SHA256(remote point || htlc_basepoint)         (claims on their commitment)
//       Revocation      -> revocationprivkey(revocation_basepoint_secret, peer per_commitment_secret) (penalties)
```
- **Invariant S1 (a commitment signed for broadcast is never revoked):** once the signer has produced a broadcast signature for local commitment N, it refuses to release `per_commitment_secret` N (and so to build any `revoke_and_ack` for N) and refuses to sign any later local or remote commitment for that channel, for the life of the channel. This is the mirror of the NL-189 guard (which refuses to sign a commitment whose secret was released). Without it a racing `commitment_signed` from the peer on an Open channel (IPC `ForceCloseChannel`, O2-T5) could make us send `revoke_and_ack` for N after N was broadcast, and the peer would then hold the secret for our on-chain commitment and could take our `to_local` with a penalty. The mark survives restarts: registration calls `MarkBroadcastSigned(channelId, n)` for every channel that has a commitment `BroadcastTransactionEntity` row, before the first connection.
- **Ordering that makes S1 hold in the service (O2-T2):** `ChannelFailureService` takes the channel lock, then in **one save** persists `ChannelState.Failed` (with its error, NL-200) **and** the commitment's `BroadcastTransactionEntity` (the raw tx), and only then publishes. The signature is requested under that lock; since every channel mutation and every peer message handling holds the same lock and a Failed channel accepts no further updates, no `revoke_and_ack` can be produced between signing and the save. If the save fails, the signed bytes are discarded and the signer mark still blocks revocation (fail closed: the channel can only be failed, never continued). A crash between signing and the save loses only the in-memory mark, and that is safe because the tx was never published; the service re-runs from the persisted state. The tx is published only after the save, so a published commitment always has a persisted Failed state and broadcast row behind it.
- The revocation secret passed in is the **peer's** (from our shachain); the signer checks `secret·G` equals the point the caller claims and that the commitment number is below the peer's current one before signing (a penalty for an unrevoked commitment is a bug).
- Low-S, RFC 6979, `MakeCanonical()`, as the HTLC methods. Keys are wiped after use (`using`), as `DeriveRevocationPrivKey` already does for its intermediate terms.
- `SignWalletTransaction` stays unimplemented until O7 (fee inputs for CPFP).

### 3.6 Data model (migration owner lane; all three providers)
| Migration (milestone) | Changes |
|---|---|
| `AddChainWatchAndBroadcasts` (O0) | `WatchedOutpointEntity` (PK `TxId, Vout`; `ChannelId`, `Purpose` byte, `SpentByTxId?`, `SpentAtHeight?`, `SpentBlockHash?`, `CreatedAt`). `BroadcastTransactionEntity` (PK `TxId`; `ChannelId?`, `RawTx` blob, `Purpose` byte, `FeeratePerKw`, `ReplacesTxId?`, `FirstBroadcastHeight`, `ConfirmedHeight?`, `ConfirmedBlockHash?`, `State` Pending/Confirmed/Replaced/Abandoned). `BlockchainStateEntity` gains the recent block hashes it needs for reorg detection (or a `BlockHeaderEntity` ring of the last 100 `(Height, Hash, PrevHash)`). Backfill step: one funding-outpoint row for every non-Closed/Stale channel with a funding output. |
| `AddOnchainResolution` (O1) | `RevokedCommitmentEntity` (PK `ChannelId, Number`; `FeeratePerKw`, `LocalMsat`, `RemoteMsat`, `Htlcs` blob in the `CommitmentEntity` 53-byte format; written only when the revoked commitment had at least one HTLC; D2). `ChannelCloseEntity` (PK `ChannelId`; `Kind` byte, `CommitmentTxId`, `CommitmentNumber?`, `SpentAtHeight`, `BlockHash`). `OutputResolutionEntity` (PK `TxId, Vout`; `ChannelId`, `Descriptor` byte + blob (keys are re-derived, never stored), `HtlcDirection?`, `HtlcId?`, `State` byte, `ResolvingTxId?`, `WaitUntilHeight?`, `DeadlineHeight?`, `ResolvedHeight?`). `Channels.RevocationLogFromNumber?` (first revoked number the log covers; set for channels that exist at migration time, §8 risk 5). Drop the unmapped `RevocationWatchEntity` class (no table exists). New `ChannelState.OnchainResolving = 37`. |
| none (O3-O6) | resolvers only add rows to the tables above |
| `AddAnchorSpends` (O7, if needed) | anchor outputs and CPFP child tracking (`BroadcastTransactionEntity.ParentTxId?`) |

Repositories: `IRevokedCommitmentDbRepository` is written by `ChannelStateDbRepository.ApplyAsync` in the **same save** as the `revoke_and_ack` that revokes the commitment (the engine reports the revoked `RemoteCommit` in `ChannelTransition.RevokedRemoteCommit`, O1-T1); `IOnchainResolutionDbRepository` and `IChainBroadcastDbRepository` are `IUnitOfWork` properties; mocks and `CrashingUnitOfWork` are updated (Repositories `CLAUDE.md` step 6). `ChannelRoundTripTests` and the Postgres/SqlServer container round trips get the new tables.

### 3.7 Fee policy (`SweepFeePolicy`, O6)
- **Inputs:** the output's value, its deadline (height after which a competitor can take it: our offered HTLC on a remote commitment → none after we claim, but the peer's success path is open until we confirm; penalty → `revoked commitment height + to_self_delay` for to_local; for revoked HTLC outputs the cheater can claim with the HTLC-timeout path from `cltv_expiry` on (their offered HTLCs) and with HTLC-success **at any time** it knows the preimage (our offered HTLCs), so a revoked HTLC output's deadline is `cltv_expiry` for their offered HTLCs and **immediate** (next block, fastest target) for our offered ones. Either way a lost race is still recoverable by the second-level penalty (B5-REV-06, `revoked HTLC tx height + to_self_delay`), which is the deadline that actually bounds the loss; the fast first-level target only saves a second penalty fee; to_local/to_remote sweeps → no deadline), the tip, and `IFeeService` (extended with a confirmation target, OG12).
- **Rate:** `max(253, estimate(target))` where `target = clamp(deadline − tip − safety, 1, 144)`; no-deadline sweeps use a slow target (`Onchain:SweepConfTarget`, default 36).
- **RBF:** every sweep, claim and penalty signals BIP125 (`nSequence <= 0xFFFFFFFD`, except where a CSV sequence is required, which is also below it). If not confirmed after `Onchain:RbfIntervalBlocks` (default 2) and the deadline is within reach, re-sign with an absolute fee `newFee = max(oldFee × 1.25, oldFee + minRelayFeeRate × newVsize)` (BIP125 rules 3 and 4 are about absolute fees: the replacement pays at least the old fee plus the relay fee for its own size) and derive the new feerate as `newFee / newVsize`. Each replacement is persisted (`ReplacesTxId`) before broadcast.
- **Caps:** a sweep never pays more than 50 % of its input value; a penalty near its deadline may pay up to 100 % of the revoked value (taking it from the cheater still beats letting the cheater's CSV expire), per `Onchain:PenaltyMaxFeeFraction`. An output worth less than its own sweep fee at the floor rate is recorded `Abandoned` (dust) and logged.
- **Pre-signed HTLC txs (no anchors):** their fee is fixed by BOLT 3 at the commitment's feerate and cannot be bumped (the output is CSV-locked, no CPFP). Documented limitation; O7 removes it.
- **Penalty batching (non-anchor):** one penalty tx for all revoked outputs (B5-REV-08 MAY); when `security_delay = 18` blocks remain before any revoked output's deadline and it is still unconfirmed, split that output into its own tx and bump it. With anchors (O7) split from the start of the danger window.

### 3.8 Reorg handling (O0-T3, O6-T3)
- The monitor keeps the last 100 block headers. A block whose `PrevHash` does not match the stored tip triggers a rewind: walk back to the fork point, raise `OnBlockDisconnected(height, hash)` for each removed block, clear `SpentAtHeight`/`ConfirmedHeight` rows above the fork, then process the new branch.
- Resolution state is derived from chain facts, so the planner re-runs. A funding spend that is reorged out **never returns the channel to Open** (or any earlier state): `ChannelModel.UpdateState` only allows strictly increasing values (`OnchainResolving = 37` > `Open`), and the `error` of §3.2 step 3 was already sent, so the peer has failed the channel too. Instead the channel stays `OnchainResolving` (or `Failed` if it never got there), the funding outpoint is re-watched, the close record and descriptors are marked unconfirmed, and: if our local commitment was the spend, it is rebroadcast from its `BroadcastTransactionEntity`; if the peer's commitment was, we wait for it to reappear, and after `Onchain:ReorgGraceBlocks` (default 6) without it we broadcast our latest local commitment through `ChannelFailureService` (never a revoked one, S1/NL-189). Whichever commitment then confirms is classified afresh. Our own rebroadcast set covers the rest: every `Pending` `BroadcastTransactionEntity` is rebroadcast on every block until confirmed.
- Switch events raised for an HTLC resolution are final: a fulfill is safe (preimage knowledge survives reorgs); a failure upstream is raised only at `ReasonableDepth`, which is the reorg bound we accept (documented risk §8).

### 3.9 Channel states
`OnchainResolving = 37` (between `Failed = 35` and `Closed = 40`, strictly increasing per `ChannelModel.UpdateState`): a commitment (ours, theirs or revoked) is confirmed and outputs are being resolved. A failed channel whose commitment we broadcast stays `Failed` until the commitment confirms, then moves to 37. `Closed` only after every output is irrevocably resolved. A mutual close goes `Closing (30) → Closed (40)` after 100 blocks (B5-MUT-01).

### 3.10 IPC (append-only `ClientCommand`)
| Value | Command | Milestone |
|---|---|---|
| next free (13 at `b38ec28`) | `ForceCloseChannel(channelId)` → commitment txid | O2-T5 |
| next free | `ListClosedChannels` / `PendingSweeps` → per channel: close kind, commitment txid, per-output state, resolving txid, wait-until height, amount recovered | O3-T6 |

`ListChannels` gains the close kind and `OnchainResolving` counts. If BOLT2 N10's `CloseChannel` lands first it takes 13; assign at implementation time and never renumber (root `CLAUDE.md`).

---

## 4. Decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | **Watch outpoints, not txids**, for the funding output and every output we must resolve; classify the spender afterwards | A cheater's revoked commitment, the peer's HTLC txs and third-party anchor sweeps have txids we do not know in advance; the outpoint is always known. | Pre-computing every possible txid per commitment (LND's old breach arbiter style): storage grows per update and misses HTLC second-level txs. |
| D2 | **Compact revocation log**: per revoked remote commitment store only the spec (balances, feerate, HTLCs), in the same save as the `revoke_and_ack`; nothing when it had no HTLC | to_local penalties need only keys and the shachain; HTLC scripts need hash, cltv, direction, amount. Same idea as LND's revocation log. Rebuilding the commitment from the spec lets us compare txids exactly. | Storing signed penalty txs per commitment (`RevocationWatchEntity`): fee fixed too early, large, and needs the peer's HTLC tx txids we don't have. |
| D3 | **Delete `RevocationWatchEntity`, `PenaltyTransactionModel`, `PenaltyTransaction` and `IRevocationWatchDbRepository`** and replace them with D2 + `OutputResolutionEntity` | They encode the rejected design of D2 and were never mapped (no table to migrate). | Mapping them as they are (NL-095's original fix sketch). |
| D4 | **Persist before broadcast**: every resolving tx is saved (`BroadcastTransactionEntity` with raw bytes) in the same save as the state change that decided it; rebroadcast every block until confirmed | Mirrors BOLT2 I1; a crash between decide and send loses nothing; reorgs and dropped mempool txs are covered by the same loop. Also fixes NL-258 for funding txs. | Fire-and-forget `SendTransactionAsync`. |
| D5 | **Sweep `to_remote` into the wallet** in every case where it pays us (remote, revoked, data-loss) | With static_remotekey the output belongs to a channel key the wallet does not scan (OG9); leaving it means the balance never shows up and the key must be kept forever. One P2WPKH sweep is cheap. | Registering channel payment keys as wallet addresses (mixes channel keys into the HD wallet's address model). |
| D6 | **Non-anchor first (O1-O6), anchors in O7** | Matches what we negotiate (BOLT2 D5); pre-signed HTLC txs need no wallet inputs. Every descriptor carries `HasAnchors` from day one, so O7 adds builders, not a redesign. | Anchors first: needs wallet signing (NL-067) and CPFP before anything can be swept. |
| D7 | **One Application service owns the chain side of a channel** (`OnchainChannelWatcher`, under the channel lock); `ChannelFailureService` is the only commitment broadcaster | Keeps the "never broadcast revoked / after data loss" rule auditable in one place (BOLT2 D10) and serializes on-chain decisions with peer messages. | Handlers or the switch broadcasting ad hoc. |
| D8 | **Pure planner + table tests**, as `ReestablishPlanner` | Every BOLT 5 branch (preimage known or not, irrevocable or not, timed out or not, spent by whom) is a table row; the watcher stays thin. | Logic inside the watcher with mocks. |
| D9 | **Reasonable depth = 6** (`Onchain:ReasonableDepth`), irrevocable = 100 (spec) | 6 matches common practice for failing upstream; configurable for regtest tests. | 1 (reorg-unsafe) or 100 (upstream HTLCs would expire first). |
| D10 | **Penalty: one batched tx without anchors, split at `security_delay = 18`** | Spec MAY + recommended value; batching saves fees and pinning is an anchors-era problem. | Always one tx per output (more fees, more RBF work). |
| D11 | **New `IFeeService` method with a confirmation target** rather than a second fee service | One fee source; the current single estimate becomes `target = default`. | Hard-coded multipliers only. |
| D12 | **Mempool watching optional (O8)** | Spec MAY; blocks suffice for correctness with the deadlines above. | ZMQ `rawtx` first. |

---

## 5. Milestones

### O6-T4 evaluation evidence (gossip wave G-B, lane m2-gate-onchain, 2026-09-26)

This section records the evidence for the mainnet gate. It does not open the gate: `NodeOptions.HtlcsEnabled` still defaults to regtest only (`Node:EnableHtlcs` unset, `NodeOptions.cs`), and the daemon template still writes the per-network value. The integrator of wave G-D records the decision.

**Blockers named by the wave 6/7 records, all closed in this lane:**
- **NL-337** (`wip/fafo` c4fab04; lane ca979f2): `HtlcExpiryMonitor` treats an incoming HTLC that was never forwarded as `PreimageKnown` only when `FinalHopClaims.GetAcceptedPreimageAsync` accepts it. That is the same test the on-chain claim uses: the record's `KnownPreimage` hashes to the HTLC, there is no fail removal, and the invoice is Settled with that preimage. Anything else is `UnresolvedFinalHop` and is failed back at the fulfillment deadline, never force-closed. Proof: `Application.Tests/Channels/Safety/HtlcExpiryMonitorTests`, where a duplicate HTLC for a Settled invoice whose 0x400F could not be sent is failed back and the channel is not failed. Two more cases fail back too: a mark on an Open or Accepted invoice, and a mark whose preimage differs from the Settled one.
- **NL-320** (`wip/fafo` 6ea8b1c; lane 5f619e3): `RemoteCommitResolver` also resolves `Unknown` closes, handled as data loss: every output is watched, to_remote is swept and a B5-GEN-06 alert is raised. For data-loss and `Unknown` closes it fails each open offered HTLC upstream (`OutgoingHtlcFailed(OnchainTimeout)`) once the tip reaches `cltv_expiry + ReasonableDepth` and the close is `ReasonableDepth` deep. It fulfills instead when the record has a preimage. The rows stay Pending until then, so the fail happens **before** Closed, not 100 blocks later. The switch's `ResumeCircuitAsync` fallback covers an Offered circuit whose outgoing channel is already Closed in the database. Proof: `Application.Tests/Onchain/Resolvers/Remote/RemoteCommitResolverTests` (future close with a forwarded HTLC failed at +6 and raised again, a local payment, not yet reasonably deep, preimage known off chain, an `Unknown` spend) and `Payments/Switch/OnchainEventsTests` (replay at 605 waits, at 606 fails once; an OnchainResolving or Failed outgoing channel is never failed by the fallback).
- **NL-311** (`wip/fafo` eff0196, 1902bb5, e0b3421; lane 2eee446, d40e745, 5567c74): before a channel's first block round in a process, `OnchainResolutionExecutor.CatchUpSavedWatchesAsync` scans bitcoind for spends of the saved watch of every Pending/Waiting/Broadcast row. It starts at the row's parent height, or at the spend the monitor recorded on the watch. So a crash between a save and `TrackWatchedOutpoint`, or between the monitor's block save and the executor's handling of the spend, misses nothing. A scan that cannot reach the tip is retried in the next round. Proofs: `Application.Tests/Onchain/OnchainResolutionExecutorTests` (never-tracked watch, spend recorded on the watch with an unresolved row, resolved and ignored rows left alone), `OnchainRestartCatchUpTests` (restart on a real SQLite database with the production `UnitOfWork`; bitcoind unreachable in the first round), and Docker `Onchain/OnchainWatchCatchUpTests` against LND david. In the Docker proof resolution watches are never tracked, the to_remote sweep confirms without its watch and the row stays `Broadcast`. After the restart the sweep is `Resolved` at its block and recorded on the watch. The unit and SQLite restart tests fail with the catch-up disabled.
- NL-316, NL-322 and NL-323 were closed in wave 7, and O8 (NL-098) and the halt gate (NL-216) in gossip wave G-A.

**Review follow-ups** (`wip/fafo-g-b-m2-gate-onchain-s2-rv`; on `wip/fafo`: 7e96e28 and 9b4e4c7 for NL-320, e3023b8 for NL-311, 58a0e46 guides):
- NL-320, our own payment: a data-loss or `Unknown` close no longer fails a local payment at `cltv_expiry + 6`. The peer can still claim the HTLC with its preimage (we cannot time it out on such a commitment), and a payment has no upstream deadline, so it stays in flight until `cltv_expiry + IrrevocableDepth` (the pre-NL-320 timing) and a failed payment keeps the Unknown rows watched until then. Forwards keep the early fail (the upstream deadline wins). Proof: `RemoteCommitResolverTests` (payment kept in flight until +100; a peer HTLC-success at +50 fulfills it).
- NL-320, forwards over a closed channel while the upstream link stays up: `HtlcExpiryMonitor` treats a forward whose every outgoing HTLC sits on a `Closed`, unloaded channel with no stored preimage as `Unresolved`, so it is failed back at the fail-back deadline instead of waiting for a lock-in replay that never comes. The switch's fallback reads `Node:Onchain:ReasonableDepth` like the resolvers. Proofs: `HtlcExpiryMonitorTests`, `OnchainEventsTests`.
- NL-311, cost of the startup scan: it now runs after every channel's time-critical round, in one block scan for all resolving channels, and a scan stopped by a read error resumes at the block it could not read (Docker `OnchainWatchCatchUpTests` green again on this branch, 1/1). Its lower bound is still the row's parent height (no persisted per-watch scan height; lowering it needs a schema change). Proofs: `OnchainResolutionExecutorTests`.

**Proofs run on this lane's branch** (`wip/fafo-g-b-m2-gate-onchain-s2`, net10.0):
- The whole Docker on-chain suite in one in-container run (`--network host`, `-namespace NLightning.Integration.Tests.Docker.Onchain`), 24 tests, **22 passed, 0 failed**, plus the 2 `Explicit` O5 by-hand variants, which were not run. The suite covers O0 smoke (2, incl. the stale-SCID reorg), O2 (2), O3 (4), O4 (5), O5 (2 end to end), O6 (3: reorged sweep, penalty rebroadcast after a restart, RBF bumps), `OnchainFinalHopTests` (1), `OnchainMempoolTests` (2, O8) and the new `OnchainWatchCatchUpTests` (1, NL-311). **Proofs O3-O6, the O6-T4 criterion, pass**, with the three blockers fixed in the same build.
- Non-Docker, Release: 6539 tests, all pass, no skips (Domain 2321, Application 1193, Integration 644, Serialization 518, Infrastructure 395, Bitcoin 870, Bolt11 278, Daemon 320). Application.Tests also passes under Release.Native (1193).
- Not re-run in this lane: the N9 `ChannelSafetyFlowTests`, the ABCD suite and the LND/CLN normal-operation suites. None of this lane's changes touches their paths except `HtlcExpiryMonitor` (NL-337), whose final-hop branch they do not exercise. The G-D integrator should re-run them before opening the gate.

**Remaining risks for the gate** (open ledger entries that touch real funds; none is a known loss path on a non-anchor channel under the documented conditions):
- **No anchors (O7, NL-314).** Our commitment and pre-signed HTLC transactions cannot be fee-bumped. A feerate spike after the last `update_fee` can keep our commitment out of blocks past an HTLC deadline. Mitigations: the `update_fee` scheduler for channels we fund, and `max_dust_htlc_exposure_msat`. LND and CLN open anchor channels by default, so on mainnet we can only run `static_remotekey` channels with peers that accept them.
- **Revoked states without a revocation-log entry (NL-309).** Their HTLC outputs are not penalized; only to_local and to_remote are. This affects channels that carried HTLCs before O1, which cannot exist on mainnet while the gate is closed.
- **Reorg edge cases**: upstream fails made for a close that a reorg replaced are not re-checked (NL-330, logged as critical); a funding transaction reorged out for good keeps its old SCID (NL-329).
- **Refused broadcasts**: no abandonment rule for a transaction bitcoind refuses for good (NL-294 partial). It is retried every block, which costs nothing but leaves noise.
- **Peer HTLC spend reading** needs the transaction from bitcoind; the fallback alert fires at one depth only (NL-315). The revoked resolver does not persist an on-chain preimage (NL-318). Neither loses funds when the other resolvers see the spend, but upstream fulfillment from a revoked close relies on the mempool reaction or on the switch replay.
- **Unknown funding spends** go to OnchainResolving and not Failed (NL-308, by design since NL-320); `OnchainChannelWatcher` mutates the shared model before its save (NL-307).
- **Final-hop edge cases**: an invoice that expired after lock-in is refused at the on-chain decision (NL-335, fails back and loses no funds); `DustExposureHtlcSwitch` can swallow that decision (NL-336).
- **Operational**: HTLCs added after our shutdown are not failed back (NL-279); UTXO locks of a forgotten funder channel (NL-259); the catch-up scan fetches whole blocks (NL-313, performance only; the NL-311 startup scan adds one pass over the blocks from the oldest unresolved row's parent height per process, shared by all resolving channels and run after their time-critical rounds). A forward failed back by `HtlcExpiryMonitor` over a closed outgoing channel leaves its circuit `Offered` (no loss; the switch skips an incoming HTLC that already has its removal).

### Gossip wave G-A record (status 2026-09-26, `wip/fafo` @ `164289a`)

Lane M1 chain safety (no migration); lane SHAs mapped to `wip/fafo` through the `-x` footers.
- **O8 done, NL-098 fixed** (567197f/30fde1f, 7fde9bf/e2445a2, d51f6ec/8e8bacc, 99ba3ac/054b545): the chain monitor follows ZMQ `rawtx` on its own loop (`Bitcoin:WatchMempool`, default on) and raises `OnWatchedOutpointSpentInMempool` once per transaction (nothing saved or marked spent). `MempoolReactor` (`AddOnchainMempoolServices`, started before the chain monitor): a witness preimage of one of our offered HTLCs is staged into the record (`KnownPreimage`, one save under the channel lock) and `OutgoingHtlcFulfilled` goes to the switch at once; a funding spend is classified as the watcher does, and a revoked commitment gets its penalties prepared (`RevokedCommitResolver.PrepareUnconfirmedPenaltiesAsync`), stored as pending broadcasts and published. The watcher links a prepared penalty (also one mined in the same block as the commitment) as the resolving transaction, abandons one after `Node:Onchain:Mempool:EvictionGraceBlocks` (3) blocks without its commitment (counted only at bitcoind's tip) and revives it when the commitment returns or a resolver rebuilds it.
- **NL-216 fixed** (567197f): `chainstatus` (`ClientCommand` 16); while halted the node refuses `openchannel`/`payinvoice`, a peer's `open_channel`, every HTLC offer and new final-hop acceptances; fulfills, fails, fee updates, closes and broadcasts continue.
- **Proof** (d51f6ec): Docker `Onchain/OnchainMempoolTests` (a) upstream fulfilled from david's unconfirmed preimage claim, (b) the victim's penalty in the mempool behind k before any block, one block confirms both, no second penalty. Integration seam (2138eae): `OnchainO6Tests` (b) runs its victim with `WatchMempool = false` so its penalty stays unconfirmed until the restart; any proof that needs an unconfirmed penalty or claim behind a mempool commitment must do the same.
- Also this wave (lane A2): NL-067 first half, the signer reloads channel signing data from the DB (a49e166, 709030c, 910d085); `SignWalletTransaction` remains for O7-T1.

Next (superseded by the G-B status line): O6-T4 after NL-311, NL-320, NL-337; follow-ups NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330, NL-335, NL-336; O7 anchors (NL-314, NL-067 second half).

### ABCD wave 7 record (status 2026-09-26, `wip/fafo` @ `4c37998`)

Lane W7-B (no migration); lane SHAs mapped to `wip/fafo` through the `-x` footers.
- **NL-316 fixed** (7ca5c60, db00321, a3cf0ce): on a Failed or OnchainResolving incoming channel `HtlcSwitch` acts only on final-hop onions (`FinalHopProcessor` checks as usual), never forwards and never fails off chain; an accepted part is committed by `MarkPartAsync` (preimage on the incoming `HtlcRecord.KnownPreimage`, under the channel lock) with the invoice settle in that save. `Onchain/Resolvers/FinalHopClaims.GetAcceptedPreimageAsync` returns the mark only when it hashes to the payment hash, the record has no fail removal and the invoice is Settled with that preimage; both resolvers use it (B5-LCL-RO-02 kept) and raise `IncomingHtlcLockedIn` every round for an unprocessed HTLC of an Open invoice below cltv_expiry so the switch decides.
- **NL-322 fixed** (7ca5c60, a3cf0ce): `FulfillSetAsync` marks every part before the settle; marks are taken back when a set is incomplete, canceled, fails or times out; a still-complete set whose settle failed is retried by its timer.
- **NL-323 fixed** (7ca5c60, a3cf0ce): Settled is the commit point; an HTLC for a Settled invoice outside the committed set gets 0x400F.
- **Proof** (72e7f49): Docker `Onchain/OnchainFinalHopTests`: LND david sends part 1 of a basic_mpp payment, force-closes that channel, part 2 completes the set on another channel, our `<sig> <preimage>` claim confirms before cltv_expiry and david's attempt succeeds.
- **O6-T4 not done**: the gate stays closed; remaining NL-311, NL-320, NL-337 (the `HtlcExpiryMonitor` still treats every HTLC of an Accepted/Settled invoice as preimage-known).
- New follow-ups: NL-335 (expired invoice at the on-chain decision), NL-336 (`DustExposureHtlcSwitch` can swallow the decision), NL-337.

Next (wave 8): O6-T4 after NL-311, NL-320, NL-337; follow-ups NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330, NL-335, NL-336; O7 anchors (NL-314); O8 mempool (NL-098).

### ABCD wave 6 record (status 2026-09-26, `wip/fafo` @ `3ce3cad`)

Lane W6-F (no migration); lane SHAs mapped to `wip/fafo` through the `-x` footers.
- **O6-T1 done** (6a4eb7d: `IFeeService.GetFeeRatePerKwAsync(confirmationTarget, ct)` per source and `SweepFeePolicy.DecideReplacement`, NL-296; 7c437b3, cde5ebb: `Onchain/Fees/SweepScheduler` in every executor block round re-signs a due `Sweep`/`HtlcClaim`/`Penalty` broadcast with the same inputs at max(BIP 125 minimum, per-target estimate) within the cap, the replacement row in the round's save, published after it; penalties re-signed with the revocation key; retires a pending tx whose input was spent elsewhere (`Abandoned`) and a split penalty batch (`Replaced`), NL-294 partial). Pre-signed HTLC txs and our commitment are never bumped (no anchors). No batching of `to_remote` with preimage claims.
- **O6-T3 done** (e8bb45b, 30584cf, 7dcf472: the chain-monitor rewind rolls back watches completed in disconnected blocks and re-reads wallet outputs from bitcoind, never restoring one whose spend is back in the mempool, and stops rebroadcasting replaced/abandoned rows; b330d8c, 8fcea62: the executor unresolves rolled-back spends, pauses a channel whose funding spend left the chain (never back to Open), re-publishes our own commitment at once or broadcasts it after `ReorgGraceBlocks` (6) when the peer's is gone, retires the rows of a replaced close, publishes a revived resolving tx right after its save; 70cbd33: `FundingReconfirmationHandler` moves the SCID and sends a new channel_update; 6d4b625: critical `[B5-GEN-06]` alert for already resolved HTLC rows of a replaced close). NL-292, NL-293, NL-096 fixed. Residue: NL-329 (a funding tx that never reconfirms keeps its SCID), NL-330 (upstream fails of a replaced close not re-checked).
- **Proof O6** (d3dfff8, fdd2d0a): `OnchainO6Tests` (a) reorged to_local sweep re-sent and confirmed one block higher, (b) penalty rebroadcast after a restart with the mempool cleared, (c) a low-fee sweep RBF-bumped until it confirms; the O0 stale-SCID reproducer is a regular test and passes. Onchain suite 18/18 (+2 `Explicit`) on net10.0 and net11.0; O5 (a) retries LND's force close while its restarted server is still starting (e8841d6, NL-319).
- **O6-T4 not done**: 09052d0 turned HTLCs on for every network, 0c0d5c8 reverted it (NL-316 open; the daemon's config template still wrote false for mainnet/testnet). HTLCs stay regtest-only by default.

Next (wave 7): O6-T4 after NL-316 and NL-322 (claim final-hop HTLCs of our invoices on chain), NL-311, NL-320; the wave 5/6 follow-ups (NL-307..NL-309, NL-312..NL-315, NL-318, NL-329, NL-330); O7 anchors (NL-314); O8 mempool (NL-098).

### ABCD wave 5 record (status 2026-09-26, `wip/fafo` @ `1a5ab49`)

Lanes W5-A (watcher + executor, migration owner), W5-B (local), W5-C (remote), W5-D (revoked) and W5-E (Mutinynet smoke); lane SHAs mapped to `wip/fafo` through the `-x` footers. `dd2d64f` registers the three resolvers after `AddOnchainServices()` in `AddApplicationServices` (`AddLocalCommitResolutionServices`, `AddRemoteCommitResolutionServices`, `AddRevokedCommitResolver`), binds their options from `Node:Onchain` and replaces the `(HtlcRemovalKind)4` casts; `a5b24e3` makes the end-to-end O5 proofs regular tests.
- **O2 done.** Port `Domain/Onchain/Interfaces/IOutputResolver` + `OutputResolverAction`s + `OutputDescriptorData` (152144c). O2-T1 S1 at registration (41f5fc2, NL-297). O2-T2: the `LocalCommitment` `BroadcastTransactions` row with its commitment number (migration `AddBroadcastCommitmentNumber`, three providers, d040654) in the Failed save; `PrepareFailureUnderLockAsync`/`CompleteFailureAsync` for the handler path (41f5fc2, NL-271); the service no longer closes a channel on confirmation. O2-T5: `Application/Onchain/OnchainChannelWatcher` + `OnchainResolutionExecutor` (41f5fc2), `forceclosechannel`/`pendingsweeps` (c2ae40a), review fixes (7f6ebd9: a Closing channel is never force-failed; cbd99c6: catch-up of spends mined before a watch, reorged funding spend height, unmapped-vout alerts, Closed staged on the database copy). **Proof O2** `Docker/Onchain/OnchainO2Tests` 2/2 (567a3c1).
- **O3 done** (W5-B): `LocalCommitResolver` (7d3a6b3), `HtlcRemovalKind.OnchainTimeout = 4` and the switch/`PaymentService` mapping (5af263f), review fixes (037c04b: a lost preimage read back from the spender, small sweeps lowered to dust instead of abandoned, a refused OnchainTimeout fail retried by the switch's lock-in replay). **Proof O3** `OnchainO3Tests` (a)-(d) 4/4 (cc207a8).
- **O4 done** (W5-C): `RemoteCommitResolver` on the shared port (3794c0d, 202341b: claims with a forward's downstream preimage, upstream events re-raised every round until the upstream has its removal, a preimage seen on chain staged into `HtlcRecord.KnownPreimage`, every output watched after data loss). **Proof O4** `OnchainO4Tests` (a)-(e) 5/5 (6bd3645, 202341b; (b) retries LND's payment while its router lacks the fresh private edge, a5b24e3, NL-319).
- **O5 done** (W5-D): `RevokedCommitResolver` + `PenaltyTransactionComposer` (e5a556d, f4b83ff, 7189b71). **Proof O5** `OnchainO5Tests` (b) deterministic NLightning cheater and (a) LND channel.db rollback, both end to end through the executor (bbc51b4, d1f1721, a5b24e3); the two by-hand resolver variants stay `Explicit`.
- **O6-T2 done** (41f5fc2, cbd99c6). O6-T1 scheduler, O6-T3 and O6-T4 open.

Deviations accepted in wave 5:
- An Unknown funding spend goes to `OnchainResolving` with no outputs (critical alert, Closed at the irrevocable depth) instead of Failed (§3.3; NL-308).
- HTLCs without an output are not persisted: the resolvers re-derive them from the channel snapshot every round (`OutputResolutionPlanner.PlanHtlcWithoutOutput`); upstream events repeat every round and rely on the switch being idempotent.
- The resolvers build one tx per output (no batching of to_remote with preimage claims, no RBF, NL-317); the revoked resolver batches penalties and splits a batch once.
- The watcher mutates the shared `ChannelModel` before its save (NL-307, NL-282 class); the executor does not.
- A revoked commitment without a log entry is mapped by script from a stand-in spec (NL-309).
- The Docker proofs run from an SDK container (`--network host`); `scripts/run-onchain.sh` from the macOS host fails at fixture setup (NL-276).
- Out-of-lane touches accepted: `PaymentService.InterpretFailure` (W5-B), `ChannelSafetyFlowTests` now expects `OnchainResolving` after our commitment confirms, `ChainWatchSchemaRoundTrip` asserts `CommitmentNumber` (W5-A).

Next (wave 6): O6-T1 `SweepScheduler` with a per-target estimate (NL-317, NL-296), O6-T3 reorg re-resolution (NL-292, NL-293), Proof O6, O6-T4 mainnet gate, the wave 5 follow-ups (NL-307..NL-320), then O7 anchors.

### ABCD wave 4 record (status 2026-09-26, `wip/fafo` @ `6b5d50e`)

Lanes W4-A (plumbing, migration owner) and W4-B (builders); lane SHAs mapped to `wip/fafo` through the `-x` footers; `960cf05` registers `AddOnchainBitcoinServices()` and the shared fee service in the node.
- **O0 done** (2bfaaf9 EF 10.0.12 bump, 7b4a173 O0-T4 migration `AddChainWatchAndBroadcasts` with the funding-outpoint backfill, f251dde O0-T1..T3, 5bedf44 and a9e33a7 review fixes, 53accb1 integrate). `IChainBroadcaster` and `IOutpointWatcher` are Domain ports implemented by `BlockchainMonitorService`. `IsChainProcessingHalted` has no IPC surface and gates nothing yet (NL-216). The reorg rewind does not undo completed watches or wallet UTXOs (O6-T3, NL-292, NL-293). Proof O0: Docker `Onchain/OnchainSmokeTests` (ba406bd, a9e33a7) passed at integration; its stale-SCID reproducer is `Explicit` (NL-292).
- **O1 done** (4fd3617): O1-T1 revocation log in the RAA save, O1-T2 tables + state 37 + stubs deleted (NL-095), O1-T3 migration `AddOnchainResolution` with the log-start data step. Proof O1: `Application.Tests/Channels/Harness/RevocationLogHarnessTests` (30 HTLC round trips, with and without anchors, a crash halfway; restart reload on SQLite in `IT/Persistence/RevokedCommitmentLogTests`).
- **O2-T1 partial** (dbe4cc8, 0761773): S1 in the signer, atomic per channel, restorable at registration from `ChannelSigningInfo.BroadcastSignedCommitmentNumber`; nothing fills that field yet (NL-297). **O2-T2** is the wave 3 `ChannelFailureService`: the watch is now in the Failed save (eb38c26, W4-E), the rest is NL-271. **O2-T3 done** (5926d0c). **O2-T4 done** (263ab8f). **O2-T5 not started.**
- **O3-T1 done** (369314d, 7394f19, ff7f6cc), **O3-T2 done** (36e8797), **O3-T5 done** (d7a4c73). O3-T3, O3-T4, O3-T6 not started.
- **O4-T1 and O4-T2 done** (7394f19, ff7f6cc; Appendix C and the Appendix F anchor claims script-executed). O4-T3 not started.
- **O5-T1 done** (0df889a), the **O5-T2 planner rows** are in `OutputResolutionPlanner` (36e8797); execution (O5-T2 watcher part, O5-T3) not started.
- **O6-T1 policy done** (369314d: `SweepFeePolicy`); the `SweepScheduler` and `IFeeService.GetFeeRatePerKwAsync(confTarget)` are not (NL-296). O6-T2..T4 not started.

Deviations accepted in wave 4:
- No Domain `ChainTx` port on `OnWatchedOutpointSpent`: the event carries the raw spending tx; `ChainTx`/`ChainTxMapper` exist for the classifier (W4-B).
- The O0 acceptance tests named `BT/Wallet/ChainBroadcasterTests` and `BlockchainMonitorServiceTests` (reorg, atomicity) live in `IT/Persistence/ChainMonitorPersistenceTests` on real SQLite; the Docker class is `Docker/Onchain/OnchainSmokeTests`, not `OnchainFlowTests.cs`. The in-memory `WatchOutpointSpend` of wave 3 is kept for `ChannelManager`.
- O1's "simulator invariant" (a rebuilt revoked txid equals the one signed) is proven in the real-crypto `TwoNodeHarness`, not the Domain simulator (fake signatures).
- §3.5 asks the signer to check a Revocation key's commitment number against the peer's current one; the signer does not track it and instead requires the derived revocation key (or its HASH160) in the witness script and `secret*G` equal to a given point. The caller takes the secret only from the peer's shachain.
- BOLT 5's 160/249 witness weights are upper bounds; `SweepWeights` computes the real witness (to_local 155-156, accepted HTLC 248/249, offered 243), never below the signed weight (NL-299, wontfix).
- Sweep batching: timeout claims go one tx per `cltv_expiry` and are never mixed with other spends; preimage claims go with to_remote at nLockTime 0, below their `cltv_expiry` (W4-B review).
- The classifier recognizes our local commitment by txid only, so the LocalCommit candidate must be the exact tx we broadcast. `OutputResolutionFacts.AllowedPreimage` must be only a preimage we learned for that HTLC (never the invoice table, B5-LCL-RO-02); planner actions repeat every block until on chain, so consumers dedupe.
- Enum reconciliation at integration: `OutputDescriptorKind` keeps W4-A's persisted numbering 1-9 and adds `Unknown = 0`, `PeerOutput = 10`, `OurAnchor = 11`, `PeerAnchor = 12`; W4-B's planner state enum is `PlannedResolutionState` (the persisted `OutputResolutionState` is W4-A's).

**Proof conventions:** Docker proofs live in `test/NLightning.Integration.Tests/Docker/OnchainFlowTests.cs`. **Isolation (chosen approach, used by O0-O6):** every `LightningRegtestNetworkFixture` uses the same hard-coded container names (`ContainerNames = miner, alice, bob, carol, david`, `LightningRegtestNetworkFixture.cs:19`) and force-removes them by name at start and at dispose, so two fixtures in one process would destroy each other's containers; a second in-assembly collection with its own fixture is **not** isolation. The on-chain tests therefore:
1. use a new collection `OnchainRegtestCollection` declared `[CollectionDefinition(Name, DisableParallelization = true)]` with its own `ICollectionFixture<LightningRegtestNetworkFixture>`. xUnit runs non-parallel collections only after the parallel ones have finished, so even an accidental combined `--filter "FullyQualifiedName~Docker"` run does not overlap two fixtures **(verify in O0's smoke test that the `regtest` collection's fixture is disposed before the on-chain fixture starts; if it is not, exclude `Docker.Onchain` from the combined filter instead)**;
2. are run in **their own `dotnet test` process** by a new `scripts/run-onchain.sh [runs] [config]` (filter `FullyQualifiedName~Docker.Onchain`), a copy of `scripts/run-abcd.sh`, which gets a fresh fixture by starting a separate process per run, not by adding a collection. This is the documented way to run the O2-O6 proofs.
(Option (a), a per-fixture container-name prefix and Docker network, was rejected for now: it changes the shared fixture and every test that addresses containers by name, NL-262.) Channels are opened by us to **david** (the LND node the fixture gives no LND channels, `LightningRegtestNetworkFixture.cs:122-123`). david is also the last hop of the ABCD suite, so the separation comes from the separate process and fixture above, not from the choice of node; within the on-chain process, closing channels and rolling back david's database touches only the on-chain tests' own channels. LND commands via LNUnit gRPC: `CloseChannel { force = true }` for force closes, hold invoices (`AddHoldInvoice`, `SettleInvoice`, `CancelInvoice`) for in-flight HTLCs **(verify the LNUnit client exposes the invoicesrpc sub-server)**. Blocks are mined with the fixture's bitcoind; the CSV delay LND imposes on us (its `to_self_delay` for our to_local) is read from `ListChannels` rather than assumed **(LND's regtest default is unverified)**. Every proof asserts: our node's per-output resolution states (IPC `PendingSweeps`), the resolving txids confirmed on bitcoind, our wallet balance delta within the expected fee range, and LND's `ClosedChannels` close type.

### O0: Chain plumbing (no BOLT 5 behaviour yet)
| Task | Files | Acceptance |
|---|---|---|
| **O0-T1** Broadcaster with persisted raw tx + rebroadcast. Resolves NL-258. **Done** (f251dde, 5bedf44, a9e33a7) | Domain port `Onchain/Interfaces/IChainBroadcaster.cs`; `BroadcastTransactionEntity` + repo; `BlockchainMonitorService.PublishAsync` (save, then send; a send failure keeps it `Pending`); per-block rebroadcast of `Pending`; `FundingSignedMessageHandler` publishes through it | `BT/Wallet/ChainBroadcasterTests.Given_SendFails_Then_RebroadcastOnNextBlock`; `…Given_Restart_Then_PendingRebroadcast`; funding rebroadcast test for NL-258 |
| **O0-T2** Outpoint watching. **Done** (f251dde) | `IBlockchainMonitor.WatchOutpointAsync(channelId, outpoint, purpose)`, event `OnWatchedOutpointSpent(ChainTx, height, index, blockHash)`; `WatchedOutpointEntity`; `ChainTx` mapping (`Infrastructure.Bitcoin/Onchain/ChainTxMapper.cs`); funding outpoint registered at `funding_signed` (both roles) and backfilled at startup | `BT/Wallet/BlockchainMonitorServiceTests.Given_BlockSpendsWatchedOutpoint_Then_EventRaisedOnceWithSpender`; replayed block raises it again (idempotent consumer contract) |
| **O0-T3** Block atomicity, tip, halt, reorg. Resolves NL-214, NL-215, NL-216, NL-096. **Done** except the halt IPC/gate (NL-216) and the rollback of completed watches (NL-292) (f251dde, 5bedf44, a9e33a7) | one unit of work per block; include the tip at startup; expose `IsChainProcessingHalted` + IPC; header ring + `OnBlockDisconnected` + rollback of spent/confirmed heights | `Given_ReorgOfDepth2_Then_HeightsRolledBackAndNewBranchProcessed`; `Given_BlockFailsMidway_Then_NothingPersisted` |
| **O0-T4** Migration `AddChainWatchAndBroadcasts` (migration owner). **Done** (7b4a173) | entities, configurations, DbContext, three providers, backfill of funding outpoints | `HasPendingModelChanges() == false` for all three; SQLite round trip; Postgres/SqlServer container round trip |

**Proof O0:** unit + SQLite tests above; Docker smoke: a channel's funding outpoint row exists after open, and after restart; `invalidateblock`/`reconsiderblock` on the funding block leaves the channel's SCID consistent.

### O1: Revocation log and resolution tables
| Task | Files | Acceptance |
|---|---|---|
| **O1-T1** Revocation log. Resolves the OG4 gap. **Done** (4fd3617) | `ChannelTransition.RevokedRemoteCommit` set by `ChannelCommitments.ReceiveRevoke`; `ChannelStateDbRepository.ApplyAsync` writes `RevokedCommitmentEntity` in the RAA save; `IRevokedCommitmentDbRepository.GetAsync(channelId, number)` | `IT/Persistence/RevokedCommitmentLogTests.Given_RaaWithHtlcs_Then_LogRowInSameSave` (crash injection: row and shachain bucket commit together); no row for HTLC-less commitments; simulator invariant: for every revoked number with HTLCs the rebuilt tx txid equals the one signed at the time |
| **O1-T2** Resolution tables, state 37, delete the stubs. Resolves NL-095. **Done** (4fd3617) | `ChannelCloseEntity`, `OutputResolutionEntity`, `ChannelState.OnchainResolving = 37`; delete `RevocationWatchEntity`, `RevocationWatchDbRepository`, `IRevocationWatchDbRepository`, `PenaltyTransactionModel`, `PenaltyTransaction` | round trips on all three providers; `ChannelRoundTripTests` covers state 37 |
| **O1-T3** Migration `AddOnchainResolution` (migration owner). **Done** (4fd3617) | three providers; no backfill of revoked commitments is possible (their HTLC sets are gone), so it records per existing channel the first revoked number the log covers (§8 risk 5) | as O0-T4 |

**Proof O1:** `TwoNodeHarness` run of 30 HTLC round trips: after every RAA the log row count and contents match the revoked commitments with HTLCs; restart reloads them.

### O2: Classification and fail-the-channel broadcast (= BOLT2 N9-T4)
| Task | Files | Acceptance |
|---|---|---|
| **O2-T1** Signer broadcast guard + invariant S1 (§3.5). **Done** (dbe4cc8, 0761773; restored at registration from the `LocalCommitment` row, 41f5fc2, NL-297) | `ILightningSigner.SignLocalCommitmentForBroadcast`, `MarkDataLoss` (registration reads `DataLossDetected`), `MarkBroadcastSigned` (registration reads the commitment broadcast row); the per-commitment-secret release path refuses N after broadcast-signed at N | `BT/Signers/LocalLightningSignerBroadcastTests.Given_OlderNumber_Then_Refused`, `…Given_DataLoss_Then_Refused`, `…Given_Latest_Then_WitnessIsFundingMultisigInKeyOrder`, **`…Given_BroadcastSigned_Then_RevokeRefused`** (secret N and any later commitment signature refused after `SignLocalCommitmentForBroadcast(N)`), `…Given_MarkBroadcastSignedAtRegistration_Then_RevokeRefused` (restart) |
| **O2-T2** `ChannelFailureService`. Partial NL-094. **Done** (wave 3; watch in the Failed save eb38c26; the `BroadcastTransactions` row with its commitment number in the Failed save and the handler path under `ChannelManager`'s lock, d040654, 41f5fc2, NL-271) | `Application/Onchain/ChannelFailureService.cs`: under the channel lock, sign the latest local commitment (stored peer sig from slot 0), persist `ChannelState.Failed` + error **and** the `BroadcastTransactionEntity` in **one save**, release the lock, then publish (§3.5 ordering); called from `ChannelManager.PersistFailedChannelAsync` when `MustBroadcast` (which already holds the lock: the service offers an `…UnderLockAsync` entry that joins that save instead of re-acquiring the non-reentrant lock), from `HtlcExpiryMonitor` (BOLT2 N9-T2) and from IPC; B5-FAIL-01/02 (forget or wait when there is nothing of ours at stake) | `AT/Onchain/ChannelFailureServiceTests.Given_Revoked_Then_OnlyLatestBroadcast`, `…Given_DataLoss_Then_Refused`, `…Given_NoToLocalNoHtlc_Then_NotBroadcastAndKept`, `…Given_SaveFails_Then_NotPublished`, `…Given_CommitmentSignedRacesForceClose_Then_NoRevokeAndAckForBroadcastNumber` (IPC force close on an Open channel while a `commitment_signed` is queued: the handler sees Failed, no `revoke_and_ack` is sent) |
| **O2-T3** Classifier. **Done** (5926d0c) | Domain `Onchain/FundingSpendClassifier.cs`; `CommitmentNumber.Decode(locktime, sequence)` | table tests: Appendix C obscured number (`0x2bb038521914 ^ 42`) decodes to 42; mutual / local / remote / remote-next / revoked / future / unknown rows; a malformed tx (bad sequence prefix) is Unknown, never a crash |
| **O2-T4** `CommitmentOutputMapper`. **Done** (263ab8f) | `Infrastructure.Bitcoin/Onchain/CommitmentOutputMapper.cs`: rebuild the candidate commitment (existing factory + `BuildWithOutputMap`), compare txids, emit descriptors (§3.3) incl. the "no output" HTLC set | Appendix C commitments 0-15 map every vout to the right descriptor; trimmed HTLCs land in the no-output set |
| **O2-T5** `OnchainChannelWatcher` + `ForceCloseChannel` IPC. **Done** (152144c, 41f5fc2, c2ae40a, 7f6ebd9, cbd99c6; NL-272; `ClientCommand.ForceCloseChannel = 14`) | `Application/Onchain/OnchainChannelWatcher.cs` (lock, classify, persist close record + descriptors, state 37, error to a connected peer, refuse operations); `ClientCommand` next free, Daemon handler, client handler, CLI `forceclosechannel` | `AT/Onchain/OnchainChannelWatcherTests` (each classification → persisted rows + state; replayed spend is a no-op); Daemon IPC round-trip test |

**Proof O2:** Docker `Given_WeForceClose_Then_LndSeesForceCloseAndChannelResolving` (we call `forceclosechannel` on an idle channel with push; bitcoind has our commitment; after 1 block our channel is `OnchainResolving`, LND lists it pending-force-closed) and `Given_LndForceCloses_Then_WeDetectRemoteCommit` (LND `CloseChannel force`; we classify `RemoteCommit` and fail the channel).

### O3: Local commitment resolution (we force-closed)
| Task | Files | Acceptance |
|---|---|---|
| **O3-T1** Sweep builder + signer `SignSweepInput`. **Done** (369314d, 7394f19, ff7f6cc) | `Builders/SweepTransactionBuilder.cs` (inputs with witness script, CSV/CLTV fields, one wallet output from `IBitcoinWalletService.GetUnusedAddressAsync`), `SweepSigningContext`, all four key kinds | script execution against Appendix C commitment 0 to_local with `local_delayed_privkey` (fails before CSV, passes at CSV); weight ≤ Appendix A bound; wrong key kind rejected |
| **O3-T2** Planner. **Done** (36e8797) | Domain `Onchain/OutputResolutionPlanner.cs` with §1.4 and §3.4 rows | table tests B5-LCL-* (every row of §6.4) |
| **O3-T3** to_local + HTLC-timeout + HTLC-success + second level. **Done** (7d3a6b3, 037c04b: `Onchain/Resolvers/LocalCommitResolver`; anchor HTLC txs NL-314) | watcher executes planner actions; HTLC-timeout/success from slot 0 sigs + `SignLocalHtlcTransaction` + `HtlcTransactionBuilder.AddWitness`; second-level output → `DelayedToLocal` | `AT/Onchain/LocalCommitResolutionTests` with a fake chain: timeout at `cltv_expiry`, not before; success only with an allowed preimage; second-level sweep after CSV |
| **O3-T4** Switch integration. **Done** (5af263f, 037c04b; `PaymentService.InterpretFailure` maps OnchainTimeout to `PermanentChannelFailure`) | `HtlcRemovalKind.OnchainTimeout = 4` (no reason bytes); `HtlcSwitch` fails the upstream HTLC with `permanent_channel_failure` created as the erring node for it; `OutgoingHtlcFulfilled` from an extracted preimage (persisted first) | `AT/Payments/Switch/OnchainEventsTests.Given_OnchainTimeoutAtDepth_Then_UpstreamFailedOnce`, `…Given_PreimageOnChain_Then_UpstreamFulfilledBeforeDepth` |
| **O3-T5** Preimage extraction. **Done** (d7a4c73) | Domain parser of spend witnesses (offered-HTLC preimage branch; HTLC-success witness) | vectors: Appendix C HTLC-success txs yield their preimages; a timeout witness yields none |
| **O3-T6** `PendingSweeps` IPC. **Done** (c2ae40a; `ClientCommand.PendingSweeps = 15`) | `ClientCommand` next free + DTOs + CLI | IPC round trip |

**Proof O3:** Docker, all through `forceclosechannel` against david: (a) idle channel with push: to_local swept after the CSV LND imposed, wallet delta = the `to_local` output value read from the confirmed commitment − the sweep fee (±1 sat per rounding), and separately `to_local` = our channel balance − the commitment fee (we are the opener) − any trimmed amounts, LND reports `LOCAL_FORCE_CLOSE` from its side as remote **(check the enum LND 0.20 uses)**; (b) an HTLC we offered to a david hold invoice that david **settles** after our force close: david claims on chain with the preimage, we extract it, the payment shows succeeded; (c) same but david **cancels**: at `cltv_expiry` our HTLC-timeout confirms, the payment fails with `permanent_channel_failure`, after CSV the second-level output is swept; (d) forwarding variant (ABCD-style, second fixture node): an HTLC forwarded through us is failed upstream only after the HTLC-timeout is 6 deep.

### O4: Remote commitment resolution (the peer force-closed)
| Task | Files | Acceptance |
|---|---|---|
| **O4-T1** to_remote sweep (D5). **Done** (7394f19) | `PaymentToRemote` descriptor, `Payment` key kind | script execution (P2WPKH, and the anchors CSV-1 form for O7) |
| **O4-T2** HTLC claims on their commitment. **Done** (7394f19, 36e8797, ff7f6cc) | planner rows B5-RMT-*; timeout claim (`nLockTime = cltv_expiry`, `<sig> <>`), preimage claim (`<sig> <preimage>`), HTLC key tweaked by **their** point from slot 1 or 2 | script execution against Appendix C commitments built as the remote side; wrong point → invalid |
| **O4-T3** RemoteNext and data loss. **Done** (3794c0d, 202341b: `Onchain/Resolvers/RemoteCommitResolver`; every output of a future commitment watched; the IPC flag is the B5-RMT-03 alert; upstream gap NL-320) | classifier + mapper for slot 2; `FutureRemote` sweeps only to_remote and raises a CRITICAL alert (IPC flag) | table + unit tests |

**Proof O4:** Docker, LND david force-closes: (a) idle channel with push to david: our to_remote reaches our wallet in the next blocks; (b) david offered us an HTLC for our invoice and our fulfill did not reach david before its force close (drop the connection after our fulfill is persisted, `CrashableTcpService`): we claim it on chain with the preimage before `cltv_expiry`, the invoice is settled; (c) we offered an HTLC to a david hold invoice never settled: after `cltv_expiry` our timeout claim confirms and the payment fails; (d) force close while our `commitment_signed` is unacked (crash after our CS as in `ReestablishFlowTests` (c), david closes with the new commitment): we classify `RemoteNextCommit` and resolve it.

### O5: Revoked commitment (penalty / justice)
| Task | Files | Acceptance |
|---|---|---|
| **O5-T1** Penalty builder. **Done** (0df889a; weights NL-299) | `Builders/PenaltyTransactionBuilder.cs` (batched and single-output variants), `Revocation` key kind (secret from `DeriveOldSecret(PerCommitmentIndex.From(n))`) | script execution: with `remote_revocation_basepoint_secret` and `x_local_per_commitment_secret` a penalty spends to_local (`<revsig> 1`) and every HTLC output (`<revsig> <revocationpubkey>`) of each Appendix C commitment; witness weights exactly 160 / 243 / 249 for 73-byte sigs, inputs 324 / 407 / 413 |
| **O5-T2** Planner rows + second level + upstream resolution. **Done** (planner rows 36e8797; execution e5a556d, f4b83ff, 7189b71: `Onchain/Resolvers/Revoked/RevokedCommitResolver`; pre-log HTLC outputs NL-309, preimage not persisted NL-318) | B5-REV-*, B5-REV-RES-01..03 (§3.4); their HTLC tx spending a target → re-plan on its output (`<revsig> 1`); preimage extraction from their HTLC-success | table tests; `AT/Onchain/RevokedResolutionTests.Given_TheirHtlcTimeoutConfirmsFirst_Then_SecondLevelPenalized`, `…Given_TheirHtlcSuccessRevealsPreimage_Then_UpstreamFulfilledAtOnce`, `…Given_OurOfferedHtlcPenalizedAtDepth_Then_UpstreamFailedOnce`, `…Given_CommittedHtlcWithoutOutputInRevoked_Then_UpstreamFailedAtReasonableDepth` |
| **O5-T3** Deadlines and split. **Done** for the split (e5a556d, 7189b71: `PenaltyTransactionComposer`, one split of an unconfirmed batch at deadline − 18, skipped when it cannot outbid the batch); RBF of a single penalty is O6-T1 (NL-317) | `security_delay` 18 split and RBF (with O6-T1 policy) | fake-chain test: batched penalty unconfirmed at deadline − 18 → per-output txs broadcast |

**Proof O5 (breach):**
- **(a) LND cheats (database rollback):** open us → david with push; make 3 payments each way (so every revoked state has balance on david's side and one holds an in-flight HTLC); stop david, copy its channel database out of the container with Docker.DotNet `GetArchiveFromContainerAsync` **(path `/root/.lnd/data/graph/regtest/channel.db` in `custom_lnd` is unverified)**; restart david, make 3 more payments; **stop our node**; stop david, restore the old database (`ExtractArchiveToContainerAsync`), start david, `CloseChannel force` (david broadcasts a revoked commitment; it does not know it is outdated because our node is down and cannot send `channel_reestablish`); mine 1 block; start our node before `to_self_delay` blocks pass. Assert: we classify `Revoked(n)`, one penalty confirms, our wallet gains ≈ channel capacity − fees, david's to_local is never spent by david. Variant: the revoked state holds an HTLC that david times out with its HTLC-timeout (mine to its `cltv_expiry`): we penalize the second-level output.
- **(b) Deterministic NLightning cheater:** two NLightning nodes via the multi-node harness (`Docker/MultiNodeHarnessTests` style). The production signer cannot be used to capture the stale commitment: invariant S1 (§3.5) makes `SignLocalCommitmentForBroadcast(k)` block every later revocation, so the channel could not move past k. Instead the test builds a **second, test-only `LocalLightningSigner` instance from a copy of the cheater's seed** (outside the cheater node's DI graph, so its S1 mark never reaches the cheater's signer), feeds it the cheater's persisted state at k (slot 0 spec + the peer's commitment signature, read from the cheater's database) and signs commitment k with it; the test keeps the raw bytes without broadcasting them. Payments then move the live channel to k+3 (the cheater's own signer revokes k normally), and the test sends the captured raw tx with bitcoind `sendrawtransaction`; the victim penalizes. This proof has no LND timing dependence and runs every time; (a) is the interop proof.

### O6: Fees, rebroadcast, reorgs, completion
| Task | Files | Acceptance |
|---|---|---|
| **O6-T1** Fee policy + `IFeeService` target. **Done** (369314d policy; 6a4eb7d target estimate, NL-296; 7c437b3, cde5ebb `SweepScheduler`, NL-317) | Domain `Onchain/SweepFeePolicy.cs`; `IFeeService.GetFeeRatePerKwAsync(uint confTarget, ct)`; `SweepScheduler` bumps per block | policy table tests (deadline, caps, BIP125 increments); `SweepSchedulerTests.Given_NotConfirmedAfterInterval_Then_ReplacedWithHigherFee` |
| **O6-T2** Irrevocable resolution and `Closed`. **Done** (41f5fc2, cbd99c6: `OnchainResolutionExecutor`; Closed staged on the database copy) | 100-block rule per output; channel `Closed` and watches/log dropped when all are | fake-chain test at depth 99/100 |
| **O6-T3** Reorg re-resolution. **Done** (cbd99c6, e8bb45b, b330d8c, 30584cf, 7dcf472, 8fcea62, 70cbd33, 6d4b625; NL-292, NL-293; residue NL-329, NL-330) | watcher reacts to `OnBlockDisconnected`: resolving tx gone → rebroadcast; funding spend gone → rules of §3.8 (never back to Open) | fake-chain tests incl. `Given_FundingSpendReorgedOut_Then_StateNotLoweredAndOutpointRewatched`, `Given_RemoteCommitGoneAfterGrace_Then_LocalCommitBroadcast`; Docker below |
| **O6-T4** Mainnet gate. **Open, unblocked** (opened 09052d0, reverted 0c0d5c8; NL-316, NL-322 fixed in wave 7; NL-311, NL-320, NL-337 fixed in gossip wave G-B; the decision is left to the G-D integrator) | `NodeOptions.HtlcsEnabled` default true on all networks only after Proofs O3-O6 pass (BOLT2 §0.6) | options test |

**Proof O6 (done, d3dfff8, `Docker/Onchain/OnchainO6Tests`):** Docker (a) `invalidateblock` on the block with our to_local sweep, mine a competing empty block: we rebroadcast and the sweep confirms again; (b) restart our node with a penalty unconfirmed (mempool cleared by bitcoind restart with `-persistmempool=0`): it is rebroadcast from `BroadcastTransactionEntity`; (c) a sweep broadcast at a low feerate (fee estimate forced low) is RBF-bumped until it confirms.

### O7 (later): Anchors
- **O7-T1** Wallet signing (`SignWalletTransaction`, resolves NL-067 second half) and a fee-input selector.
- **O7-T2** CPFP of our commitment through `to_local_anchor` (B5-FAIL-06), RBF of the child; anyone-can-sweep of anchors after 16 blocks.
- **O7-T3** HTLC-timeout/success with `SINGLE|ANYONECANPAY` peer sigs combined with fee inputs (B5-HTX-02); to_remote CSV-1 sweep; penalty split rules from the start of the danger window.
- **O7-T4** Enable `OptionAnchors` (BOLT2 N11-T3) after an anchors variant of Proofs O3-O5 passes against LND.
- `zero_fee_commitments` (v3/TRUC, `shared_anchor`) is out of scope for this plan.

### O8 (optional): Mempool
- ZMQ `rawtx` (NL-098): preimage extraction and penalty reaction from unconfirmed txs; never treat a mempool tx as a confirmation. **Done** in gossip wave G-A (567197f, 7fde9bf, d51f6ec, 99ba3ac; see the wave record).

---

## 6. Requirements traceability matrix

Status: **MISSING** everywhere at `b38ec28` unless noted. Test prefixes: `DT/` Domain.Tests, `AT/` Application.Tests, `BT/` Infrastructure.Bitcoin.Tests, `IT/` Integration.Tests, `DK/` `IT/Docker/OnchainFlowTests`.

### 6.1 General and failing
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B5-GEN-01 | Monitor unresolved outputs | MISSING (funding watch dropped at confirmation, OG1) | O0-T2 | `BT/…/BlockchainMonitorServiceTests.Given_BlockSpendsWatchedOutpoint_…` |
| B5-GEN-02 | Resolve all; irrevocable at 100 | MISSING | O3-O6 | `DT/Onchain/OutputResolutionPlannerTests` depth rows |
| B5-GEN-03 | Reorg-safe | DONE (ABCD wave 6; NL-096, NL-292, NL-293) | O0-T3, O6-T3 | reorg tests, DK O6 (a) |
| B5-GEN-04 | Funding spent while open → fail | MISSING | O2-T5 | `AT/Onchain/OnchainChannelWatcherTests` |
| B5-GEN-05 | Ignore invalid txs | N/A by design (only confirmed txs are seen) | O0-T2 | — |
| B5-GEN-06 | Unknown funding spend → warn | MISSING | O2-T3, O2-T5 | classifier Unknown row |
| B5-GEN-07 | Mempool MAY | DONE (NL-098; 567197f, 7fde9bf, d51f6ec, 99ba3ac) | O8 | `AT/Onchain/Mempool/MempoolReactorTests`, `BT/Wallet/BlockchainMonitorServiceTests` (rawtx), Docker `Onchain/OnchainMempoolTests` |
| B5-FAIL-01/02 | Forget / wait when nothing at stake | MISSING | O2-T2 | `ChannelFailureServiceTests.Given_NoToLocalNoHtlc_…` |
| B5-FAIL-04 | No broadcast when outdated | PARTIAL (flag persisted, nothing broadcasts) | O2-T1 | `…Given_DataLoss_Then_Refused` |
| B5-FAIL-05 | Broadcast latest signed commitment | MISSING | O2-T2 | DK O2 |
| B5-FAIL-06 | Anchor CPFP | MISSING | O7-T2 | anchors DK |
| B5-MUT-01 | Mutual close detection | MISSING (close is N10) | O2-T3 | classifier Mutual row |

### 6.2 Script and weight vectors (all tasks that build a spend)
| Spend | Spent output (vector source) | Key | Check |
|---|---|---|---|
| to_local sweep | Appendix C commitment to_local | `local_delayed_privkey` | valid at `nSequence = to_self_delay`, invalid below |
| to_local penalty | same | revocationprivkey(`remote_revocation_basepoint_secret`, `x_local_per_commitment_secret`) | valid; witness 160 |
| offered/received HTLC penalty | Appendix C commitment HTLC outputs | same | valid; witness 243 / 249 |
| second-level penalty | Appendix C HTLC-timeout/success txs | same | valid `<revsig> 1` |
| second-level sweep | Appendix C HTLC-timeout/success txs | `local_delayed_privkey` | valid after CSV |
| to_remote sweep | Appendix C commitment to_remote | payment basepoint secret **(check which key the vector uses, §1.8)** | valid |
| HTLC claims on a remote commitment | Appendix C commitment rebuilt as the peer's | HTLC key @ point | timeout claim valid only at `nLockTime >= cltv_expiry`; preimage claim valid with the Appendix C preimage |

### 6.3 Local commitment
| ID | Requirement | Task | Test |
|---|---|---|---|
| B5-LCL-01 | to_local after CSV | O3-T3 | `LocalCommitResolutionTests`, DK O3 (a) |
| B5-LCL-02 | ignore to_remote | O2-T4 | mapper test |
| B5-LCL-LO-01 | preimage spend → extract | O3-T5 | DK O3 (b) |
| B5-LCL-LO-02 | HTLC-timeout at expiry | O3-T3 | DK O3 (c) |
| B5-LCL-LO-03 | fail upstream at depth; sweep second level | O3-T3, O3-T4 | DK O3 (c)(d) |
| B5-LCL-LO-04 | no-output HTLCs | O2-T4, O3-T2 | planner rows |
| B5-LCL-RO-01 | HTLC-success with preimage | O3-T3 | `LocalCommitResolutionTests` |
| B5-LCL-RO-02 | no own-preimage reveal as non-final | O3-T2 | `…Given_InvoicePreimageButForwardedOnion_Then_NotClaimed` |
| B5-LCL-RO-03 | not irrevocable → don't spend | O3-T2 | planner row |

### 6.4 Remote commitment
| ID | Requirement | Task | Test |
|---|---|---|---|
| B5-RMT-01 | current and next remote commitments | O4-T3 | DK O4 (d) |
| B5-RMT-02 | to_remote (swept, D5) | O4-T1 | DK O4 (a) |
| B5-RMT-03 | data loss: inform, salvage to_remote | O4-T3 | unit |
| B5-RMT-LO-01 | extract preimage from their HTLC-success | O3-T5, O4-T2 | unit |
| B5-RMT-LO-02 | timeout claim | O4-T2 | DK O4 (c) |
| B5-RMT-RO-01 | preimage claim | O4-T2 | DK O4 (b) |
| B5-RMT-RO-02 | not irrevocable → don't spend | O4-T2 | planner row |

### 6.5 Revoked commitment
| ID | Requirement | Task | Test |
|---|---|---|---|
| B5-REV-01 | never broadcast a revoked local commitment (NL-189 guard) and never revoke a commitment signed for broadcast (S1) | O2-T1, O2-T2 | signer tests incl. `Given_BroadcastSigned_Then_RevokeRefused`; `ChannelFailureServiceTests.Given_CommitmentSignedRacesForceClose_…` |
| B5-REV-03 | penalize their to_local | O5-T1 | vectors, DK O5 |
| B5-REV-04/05 | penalize HTLC outputs | O5-T1, O5-T2 | vectors, DK O5 (a) variant |
| B5-REV-06 | penalize their second-level txs | O5-T2 | `RevokedResolutionTests`, DK O5 (a) variant |
| B5-REV-07 | extract preimage | O3-T5 | unit |
| B5-REV-08 | batching and split at security_delay | O5-T3 | fake-chain test |
| B5-REV-09 | handle invalidation by HTLC txs | O5-T2 | `…Given_TheirHtlcTimeoutConfirmsFirst_…` |
| B5-REV-RES-01 | preimage on chain → fulfill upstream at once (persisted first) | O5-T2, O3-T4 | `RevokedResolutionTests.Given_TheirHtlcSuccessRevealsPreimage_…` |
| B5-REV-RES-02 | our offered HTLC penalized → fail upstream at `ReasonableDepth` | O5-T2, O3-T4 | `…Given_OurOfferedHtlcPenalizedAtDepth_…`; planner rows |
| B5-REV-RES-03 | committed HTLC with no output in the revoked commitment → fail upstream at `ReasonableDepth` (fulfill if preimage known) | O5-T2 | `…Given_CommittedHtlcWithoutOutputInRevoked_…`; planner rows |

### 6.6 HTLC tx generation
| ID | Requirement | Task | Test |
|---|---|---|---|
| B5-HTX-01 | non-anchor HTLC txs broadcast as is | O3-T3 | exists for building (Appendix C HTLC tx vectors); broadcast in DK O3 (c) |
| B5-HTX-02 | anchors: add fee inputs | O7-T3 | anchors DK |

---

## 7. Mapping to other plans and lanes
| This plan | Other plan | Relation |
|---|---|---|
| O2-T1, O2-T2 | BOLT2 N9-T4 | same work; the BOLT2 row points here |
| O3 trigger | BOLT2 N9-T2 `HtlcExpiryMonitor` (ABCD W3-C) | N9-T2 calls `ChannelFailureService`; until O2 lands it can only fail the channel and log |
| O2-T3 Mutual row | BOLT2 N10 | N10 builds and broadcasts the closing tx through `IChainBroadcaster` (O0-T1); O2 only recognizes it |
| O3-T4 | ONION M4 switch | adds `HtlcRemovalKind.OnchainTimeout` handling in `HtlcSwitch` |
| O7-T4 | BOLT2 N11-T3 | anchors enabled only after O7 |
| O0-T3 | NL-096, NL-214..216 | chain robustness shared with funding confirmation |

---

## 8. Risks

1. **Classification by rebuild depends on exact BOLT 3 reproduction.** A single difference (fee rounding, trimming, output order) makes our own current commitment look Unknown. Mitigation: the builders are already byte-exact on every Appendix C/F vector, the mapper test runs over all of them, and the classifier falls back to the decoded commitment number (then maps outputs by script) before declaring Unknown.
2. **Pre-signed HTLC txs cannot be fee-bumped** without anchors. A fee spike after signing can keep an HTLC-timeout out of blocks past the upstream deadline. Mitigation: go on chain early (N9-T2 deadlines), keep `update_fee` current (N9-T1); anchors (O7) fix it properly.
3. **Offline windows:** a penalty is possible only within `to_self_delay` of the revoked commitment's confirmation. We accept LND's delay for our side; our `Local.ToSelfDelay` for the peer must stay large enough (≥ 144 recommended); document it, and consider a watchtower later.
4. **Reorg after failing upstream** (§3.8): an HTLC failed upstream at depth 6 could reappear in a deeper reorg. Accepted risk, same as other implementations; depth is configurable.
5. **No backfill of the revocation log:** `RevokedCommitmentEntity` rows are written only from O1 onwards. For commitments revoked before the `AddOnchainResolution` migration the HTLC set is lost (OG4), so if such a state is broadcast only its `to_local` can be penalized (and our `to_remote` swept); its HTLC outputs cannot be rebuilt. Accepted: HTLCs are regtest-only until O6 (`EnableHtlcs` gate, O6-T4), so no mainnet channel carries HTLC-bearing revoked states from before O1. O1-T3 records, per channel that already has revoked states at migration time (`RemoteCommitmentNumber > 0`), the first commitment number the log covers (a nullable `Channels.RevocationLogFromNumber` column), so a later breach at an older number is reported as "HTLC outputs unrecoverable (pre-O1 state)" instead of silently under-penalized.
6. **Revocation log growth:** one row per revoked commitment with HTLCs, for the channel's life. Pruned when the channel closes; size ~ 53 bytes × HTLCs. Monitor in long runs.
7. **LND behaviours (unverified):** channel database path and restore procedure in `custom_lnd`; whether LND 0.20 refuses to force-close after a restore (it should not know without our reestablish); the CSV LND imposes in regtest; close-type enums reported for each case; hold-invoice sub-server availability through LNUnit. Each proof logs LND's version and the values it reads.
8. **Breach proof timing:** our node must stay down while david broadcasts, then come back within `to_self_delay` blocks; tests mine explicitly, never wait on wall-clock blocks.
9. **Fee estimator in regtest** returns a floor rate; RBF tests force low and high estimates through a test `IFeeService`.
10. **Key material in memory:** sweep and penalty signing derive keys per call; they must be wiped (`using`) as the existing signer does. A remote signer (VLS-style) later needs the same `SweepSigningContext` contract, which is why the context carries the script and amount rather than a raw sighash.
11. **Concurrency:** the watcher runs block work under each channel's lock; it must never hold two locks, and must only enqueue (never await a network send) while holding one (root `CLAUDE.md` ordering rules). Broadcasts happen after the save and outside the lock.
