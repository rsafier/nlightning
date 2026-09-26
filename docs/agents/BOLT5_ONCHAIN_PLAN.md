# BOLT 5 On-chain Handling: Implementation Plan for NLightning

This is the plan for BOLT 5 "Recommendations for On-chain Transaction Handling": watching the funding output, failing a channel by broadcasting our commitment, resolving every output of a local, remote or revoked commitment (to_local, to_remote, HTLC outputs, second-level HTLC transactions), penalty (justice) transactions, fee management for our sweeps, reorg safety, and later the anchor/CPFP variant. It also covers the BOLT 3 pieces BOLT 5 needs: witness forms, key derivation for sweeps and penalties, and the Appendix A weights. Every repo claim cites a repo-relative path. Claims marked **(unverified)** or **(inferred)** were not proven against running code; check them before you rely on them.

- **Spec source:** `lightning/bolts` master, fetched 2026-09-25: `05-onchain.md` (whole document) and `03-transactions.md` (§Commitment Transaction Outputs, §HTLC-Timeout and HTLC-Success Transactions, §Keys, Appendix A weights, Appendix C secrets). Re-read the requirement block before you implement a resolver.
- **Relation to the other plans:** BOLT2 plan ([`BOLT2_NORMAL_OPERATION_PLAN.md`](BOLT2_NORMAL_OPERATION_PLAN.md)) §"After N10" points here. BOLT2 **N9-T4** (`ChannelFailureService`, the only broadcast path) is milestone **O2** of this plan: one work item, implemented once. BOLT2 **N9-T2** (`HtlcExpiryMonitor`, ABCD lane W3-C) is the trigger that sends HTLCs on-chain; this plan consumes it. BOLT2 **N11-T3** (enable `option_anchors`) depends on **O7**.
- **Issue ledger:** the epic is NL-094 ([`ISSUES.md`](ISSUES.md)); sub-issues NL-095 (revocation watch stub), NL-096 (reorgs), NL-098 (mempool), NL-214/NL-215/NL-216 (block processing), NL-258 (no rebroadcast), NL-067 (wallet signing). New gaps found while writing this plan are listed in §2.2 as `OG#` rows with "new" in the NL column; the ledger agent files them. Tasks say "Resolves NL-…". Update the ledger entry in the same commit as the fix.
- **Status (2026-09-25, `wip/fafo` @ `b38ec28`):** nothing of BOLT 5 exists. Nothing watches the funding output after confirmation, no commitment is ever broadcast by us, and no output of any commitment is swept. A peer that broadcasts a revoked commitment keeps the whole channel. Mainnet use is blocked on O1-O6 (BOLT2 plan §0.6: `EnableHtlcs` stays regtest-only until this plan can sweep).

---

## 0. How to use this document (agents)

1. Do the milestones in order: **O0 → O1 → … → O6**. O7 (anchors) and O8 (mempool) come later. O3, O4 and O5 share the classifier and sweep builders from O2/O3-T1; after O3-T1 lands, O4 and O5 may run in parallel lanes (they touch different resolver files).
2. A task is done only when all of these pass:
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` and `-c Release.Native` (signer and builder code is crypto code);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'`;
   - the task's own tests. Every sweep, claim and penalty builder is checked by **script execution** (NBitcoin `Script.VerifyScript` / `TransactionBuilder.Verify` against the spent output) and by the Appendix A weight bound (§1.8). Where the Appendix C secrets make it possible, the spent transaction is the Appendix C commitment or HTLC transaction itself (§6.2).
3. A milestone is done when its **Proof** passes. Docker proofs run locally only, in a new class `test/NLightning.Integration.Tests/Docker/OnchainFlowTests.cs` (namespace contains `Docker`, so CI skips it): `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker.Onchain"`. These tests close channels and one of them rolls back an LND database, so they run in their **own collection with a fresh fixture** (as `scripts/run-abcd.sh` does for ABCD) and never share an LND node with the ABCD suite (§5, Proof conventions).
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
| OG11 | No channel state for "a commitment is on chain, outputs are being resolved": `Closing = 30` means a mutual close was broadcast, `Failed = 35`, `Closed = 40`. | `Domain/Channels/Enums/ChannelState.cs` | new | O1-T2 |
| OG12 | `IFeeService` returns one feerate; deadline-driven sweeps and penalties need a rate per confirmation target. | `IFeeService.cs` | new | O6-T1 |
| OG13 | No trigger sends an HTLC on chain: `HtlcExpiryMonitor` (BOLT2 N9-T2) does not exist yet (ABCD W3-C). | — | NL-094 | BOLT2 N9-T2 (consumed by O3) |
| OG14 | `CommitmentNumber` cannot decode an obscured number from a tx; `CommitmentEntity` stores no commitment txid (it is rebuilt from the spec). | `CommitmentNumber.cs`, `CommitmentEntity.cs` | new | O2-T4 |
| OG15 | No mempool monitoring. | `BlockchainMonitorService.cs:271-279` | NL-098 | O8 |
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

"Committed HTLCs with no output" (B5-LCL-LO-04, B5-RMT-LO-03): computed per case from the persisted HTLC states against the HTLCs present in the on-chain commitment. Preimage known → `RaiseFulfilled` at once; else `RaiseFailed` once the commitment is `Onchain:ReasonableDepth` deep (default 6), or at once if no valid commitment (neither our current, the peer's current nor the peer's next) holds an output for it (trimmed everywhere).

### 3.4 HTLC safety rules
- **Irrevocability first (B5-LCL-RO-03, B5-RMT-RO-02):** never claim an incoming HTLC the peer is not irrevocably committed to (engine state below `RcvdAddAckRevocation` 34 in the persisted table).
- **Preimage-extraction guard (B5-LCL-RO-02):** claim an incoming HTLC with a preimage only when `HtlcRecord.KnownPreimage`/our fulfill removal is set for **that** HTLC. The switch sets it only after accepting the HTLC as final hop (invoice checks passed) or after the downstream fulfilled the forward. A preimage that only exists in the invoice table is never used on chain for an HTLC the switch did not accept.
- **Fail upstream only when final:** `OutgoingHtlcFailed` for an on-chain HTLC is raised only when the HTLC-timeout (local case) or our timeout claim (remote case) is at reasonable depth, or the commitment without the output is (B5-LCL-LO-03/04). A fulfill upstream is raised at once when a preimage is seen on chain (and persisted first, I10).
- **Upstream deadline:** if the incoming HTLC is also on chain, nothing is sent upstream; its own resolution runs on its channel. The BOLT2 N9-T2 deadlines (`cltv_expiry + G` for offered, `2R+G+S` for fulfilled) decide when a channel goes on chain; this plan does not change them.

### 3.5 Signer API additions (`ILightningSigner`, `LocalLightningSigner`)
```csharp
// Our own latest commitment, fully signed, for broadcast (N9-T4). Refuses unless number == LocalCommitmentNumber,
// the peer's signature is present and verifies, and the channel is not marked DataLossDetected (I4, I12, B5-FAIL-04).
SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, LocalCommitmentBroadcastContext context);
void MarkDataLoss(ChannelId channelId);                                        // from the persisted flag at registration

// One input of a sweep, claim or penalty. The signer derives the key from the kind; SIGHASH_ALL.
CompactSignature SignSweepInput(ChannelId channelId, SweepSigningContext context);
// SweepSigningContext(tx bytes, input index, witness script (or P2WPKH script code), amount, kind, point?, secret?)
// kind: DelayedPayment  -> delayed_basepoint_secret + SHA256(point || delayed_basepoint)        (to_local, 2nd level)
//       Payment         -> payment_basepoint_secret (static_remotekey)                            (to_remote)
//       HtlcRemotePoint -> htlc_basepoint_secret + SHA256(remote point || htlc_basepoint)         (claims on their commitment)
//       Revocation      -> revocationprivkey(revocation_basepoint_secret, peer per_commitment_secret) (penalties)
```
- The revocation secret passed in is the **peer's** (from our shachain); the signer checks `secret·G` equals the point the caller claims and that the commitment number is below the peer's current one before signing (a penalty for an unrevoked commitment is a bug).
- Low-S, RFC 6979, `MakeCanonical()`, as the HTLC methods. Keys are wiped after use (`using`), as `DeriveRevocationPrivKey` already does for its intermediate terms.
- `SignWalletTransaction` stays unimplemented until O7 (fee inputs for CPFP).

### 3.6 Data model (migration owner lane; all three providers)
| Migration (milestone) | Changes |
|---|---|
| `AddChainWatchAndBroadcasts` (O0) | `WatchedOutpointEntity` (PK `TxId, Vout`; `ChannelId`, `Purpose` byte, `SpentByTxId?`, `SpentAtHeight?`, `SpentBlockHash?`, `CreatedAt`). `BroadcastTransactionEntity` (PK `TxId`; `ChannelId?`, `RawTx` blob, `Purpose` byte, `FeeratePerKw`, `ReplacesTxId?`, `FirstBroadcastHeight`, `ConfirmedHeight?`, `ConfirmedBlockHash?`, `State` Pending/Confirmed/Replaced/Abandoned). `BlockchainStateEntity` gains the recent block hashes it needs for reorg detection (or a `BlockHeaderEntity` ring of the last 100 `(Height, Hash, PrevHash)`). Backfill step: one funding-outpoint row for every non-Closed/Stale channel with a funding output. |
| `AddOnchainResolution` (O1) | `RevokedCommitmentEntity` (PK `ChannelId, Number`; `FeeratePerKw`, `LocalMsat`, `RemoteMsat`, `Htlcs` blob in the `CommitmentEntity` 53-byte format; written only when the revoked commitment had at least one HTLC; D2). `ChannelCloseEntity` (PK `ChannelId`; `Kind` byte, `CommitmentTxId`, `CommitmentNumber?`, `SpentAtHeight`, `BlockHash`). `OutputResolutionEntity` (PK `TxId, Vout`; `ChannelId`, `Descriptor` byte + blob (keys are re-derived, never stored), `HtlcDirection?`, `HtlcId?`, `State` byte, `ResolvingTxId?`, `WaitUntilHeight?`, `DeadlineHeight?`, `ResolvedHeight?`). Drop the unmapped `RevocationWatchEntity` class (no table exists). New `ChannelState.OnchainResolving = 37`. |
| none (O3-O6) | resolvers only add rows to the tables above |
| `AddAnchorSpends` (O7, if needed) | anchor outputs and CPFP child tracking (`BroadcastTransactionEntity.ParentTxId?`) |

Repositories: `IRevokedCommitmentDbRepository` is written by `ChannelStateDbRepository.ApplyAsync` in the **same save** as the `revoke_and_ack` that revokes the commitment (the engine reports the revoked `RemoteCommit` in `ChannelTransition.RevokedRemoteCommit`, O1-T1); `IOnchainResolutionDbRepository` and `IChainBroadcastDbRepository` are `IUnitOfWork` properties; mocks and `CrashingUnitOfWork` are updated (Repositories `CLAUDE.md` step 6). `ChannelRoundTripTests` and the Postgres/SqlServer container round trips get the new tables.

### 3.7 Fee policy (`SweepFeePolicy`, O6)
- **Inputs:** the output's value, its deadline (height after which a competitor can take it: our offered HTLC on a remote commitment → none after we claim, but the peer's success path is open until we confirm; penalty → `revoked commitment height + to_self_delay` for to_local, the HTLC's `cltv_expiry` for HTLC outputs; to_local/to_remote sweeps → no deadline), the tip, and `IFeeService` (extended with a confirmation target, OG12).
- **Rate:** `max(253, estimate(target))` where `target = clamp(deadline − tip − safety, 1, 144)`; no-deadline sweeps use a slow target (`Onchain:SweepConfTarget`, default 36).
- **RBF:** every sweep, claim and penalty signals BIP125 (`nSequence <= 0xFFFFFFFD`, except where a CSV sequence is required, which is also below it). If not confirmed after `Onchain:RbfIntervalBlocks` (default 2) and the deadline is within reach, re-sign at `max(prev × 1.25, prev + minRelay × vsize)` (BIP125 rules 3 and 4). Each replacement is persisted (`ReplacesTxId`) before broadcast.
- **Caps:** a sweep never pays more than 50 % of its input value; a penalty near its deadline may pay up to 100 % of the revoked value (taking it from the cheater still beats letting the cheater's CSV expire), per `Onchain:PenaltyMaxFeeFraction`. An output worth less than its own sweep fee at the floor rate is recorded `Abandoned` (dust) and logged.
- **Pre-signed HTLC txs (no anchors):** their fee is fixed by BOLT 3 at the commitment's feerate and cannot be bumped (the output is CSV-locked, no CPFP). Documented limitation; O7 removes it.
- **Penalty batching (non-anchor):** one penalty tx for all revoked outputs (B5-REV-08 MAY); when `security_delay = 18` blocks remain before any revoked output's deadline and it is still unconfirmed, split that output into its own tx and bump it. With anchors (O7) split from the start of the danger window.

### 3.8 Reorg handling (O0-T3, O6-T3)
- The monitor keeps the last 100 block headers. A block whose `PrevHash` does not match the stored tip triggers a rewind: walk back to the fork point, raise `OnBlockDisconnected(height, hash)` for each removed block, clear `SpentAtHeight`/`ConfirmedHeight` rows above the fork, then process the new branch.
- Resolution state is derived from chain facts, so the planner re-runs: a funding spend that disappeared returns the channel to its previous state **only if** the spend is not back in the new branch within `Onchain:ReorgGraceBlocks` (default 6), and never un-fails a channel (the error was sent). Our own rebroadcast set covers the rest: every `Pending` `BroadcastTransactionEntity` is rebroadcast on every block until confirmed.
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

**Proof conventions:** Docker proofs live in `test/NLightning.Integration.Tests/Docker/OnchainFlowTests.cs` in their own xUnit collection with a fresh `LightningRegtestNetworkFixture` (never the ABCD collection). Channels are opened by us to **david** (the LND node without LND channels, `LightningRegtestNetworkFixture.cs:123`) so closing them and rolling back an LND database never touches alice/bob/carol. LND commands via LNUnit gRPC: `CloseChannel { force = true }` for force closes, hold invoices (`AddHoldInvoice`, `SettleInvoice`, `CancelInvoice`) for in-flight HTLCs **(verify the LNUnit client exposes the invoicesrpc sub-server)**. Blocks are mined with the fixture's bitcoind; the CSV delay LND imposes on us (its `to_self_delay` for our to_local) is read from `ListChannels` rather than assumed **(LND's regtest default is unverified)**. Every proof asserts: our node's per-output resolution states (IPC `PendingSweeps`), the resolving txids confirmed on bitcoind, our wallet balance delta within the expected fee range, and LND's `ClosedChannels` close type.

### O0: Chain plumbing (no BOLT 5 behaviour yet)
| Task | Files | Acceptance |
|---|---|---|
| **O0-T1** Broadcaster with persisted raw tx + rebroadcast. Resolves NL-258 | Domain port `Onchain/Interfaces/IChainBroadcaster.cs`; `BroadcastTransactionEntity` + repo; `BlockchainMonitorService.PublishAsync` (save, then send; a send failure keeps it `Pending`); per-block rebroadcast of `Pending`; `FundingSignedMessageHandler` publishes through it | `BT/Wallet/ChainBroadcasterTests.Given_SendFails_Then_RebroadcastOnNextBlock`; `…Given_Restart_Then_PendingRebroadcast`; funding rebroadcast test for NL-258 |
| **O0-T2** Outpoint watching | `IBlockchainMonitor.WatchOutpointAsync(channelId, outpoint, purpose)`, event `OnWatchedOutpointSpent(ChainTx, height, index, blockHash)`; `WatchedOutpointEntity`; `ChainTx` mapping (`Infrastructure.Bitcoin/Onchain/ChainTxMapper.cs`); funding outpoint registered at `funding_signed` (both roles) and backfilled at startup | `BT/Wallet/BlockchainMonitorServiceTests.Given_BlockSpendsWatchedOutpoint_Then_EventRaisedOnceWithSpender`; replayed block raises it again (idempotent consumer contract) |
| **O0-T3** Block atomicity, tip, halt, reorg. Resolves NL-214, NL-215, NL-216, NL-096 | one unit of work per block; include the tip at startup; expose `IsChainProcessingHalted` + IPC; header ring + `OnBlockDisconnected` + rollback of spent/confirmed heights | `Given_ReorgOfDepth2_Then_HeightsRolledBackAndNewBranchProcessed`; `Given_BlockFailsMidway_Then_NothingPersisted` |
| **O0-T4** Migration `AddChainWatchAndBroadcasts` (migration owner) | entities, configurations, DbContext, three providers, backfill of funding outpoints | `HasPendingModelChanges() == false` for all three; SQLite round trip; Postgres/SqlServer container round trip |

**Proof O0:** unit + SQLite tests above; Docker smoke: a channel's funding outpoint row exists after open, and after restart; `invalidateblock`/`reconsiderblock` on the funding block leaves the channel's SCID consistent.

### O1: Revocation log and resolution tables
| Task | Files | Acceptance |
|---|---|---|
| **O1-T1** Revocation log. Resolves the OG4 gap | `ChannelTransition.RevokedRemoteCommit` set by `ChannelCommitments.ReceiveRevoke`; `ChannelStateDbRepository.ApplyAsync` writes `RevokedCommitmentEntity` in the RAA save; `IRevokedCommitmentDbRepository.GetAsync(channelId, number)` | `IT/Persistence/RevokedCommitmentLogTests.Given_RaaWithHtlcs_Then_LogRowInSameSave` (crash injection: row and shachain bucket commit together); no row for HTLC-less commitments; simulator invariant: for every revoked number with HTLCs the rebuilt tx txid equals the one signed at the time |
| **O1-T2** Resolution tables, state 37, delete the stubs. Resolves NL-095 | `ChannelCloseEntity`, `OutputResolutionEntity`, `ChannelState.OnchainResolving = 37`; delete `RevocationWatchEntity`, `RevocationWatchDbRepository`, `IRevocationWatchDbRepository`, `PenaltyTransactionModel`, `PenaltyTransaction` | round trips on all three providers; `ChannelRoundTripTests` covers state 37 |
| **O1-T3** Migration `AddOnchainResolution` (migration owner) | three providers | as O0-T4 |

**Proof O1:** `TwoNodeHarness` run of 30 HTLC round trips: after every RAA the log row count and contents match the revoked commitments with HTLCs; restart reloads them.

### O2: Classification and fail-the-channel broadcast (= BOLT2 N9-T4)
| Task | Files | Acceptance |
|---|---|---|
| **O2-T1** Signer broadcast guard | `ILightningSigner.SignLocalCommitmentForBroadcast`, `MarkDataLoss` (registration reads `DataLossDetected`) | `BT/Signers/LocalLightningSignerBroadcastTests.Given_OlderNumber_Then_Refused`, `…Given_DataLoss_Then_Refused`, `…Given_Latest_Then_WitnessIsFundingMultisigInKeyOrder` |
| **O2-T2** `ChannelFailureService`. Partial NL-094 | `Application/Onchain/ChannelFailureService.cs`: sign the latest local commitment (stored peer sig from slot 0), persist `BroadcastTransactionEntity`, publish; called from `ChannelManager.PersistFailedChannelAsync` when `MustBroadcast`, from `HtlcExpiryMonitor` (BOLT2 N9-T2) and from IPC; B5-FAIL-01/02 (forget or wait when there is nothing of ours at stake) | `AT/Onchain/ChannelFailureServiceTests.Given_Revoked_Then_OnlyLatestBroadcast`, `…Given_DataLoss_Then_Refused`, `…Given_NoToLocalNoHtlc_Then_NotBroadcastAndKept` |
| **O2-T3** Classifier | Domain `Onchain/FundingSpendClassifier.cs`; `CommitmentNumber.Decode(locktime, sequence)` | table tests: Appendix C obscured number (`0x2bb038521914 ^ 42`) decodes to 42; mutual / local / remote / remote-next / revoked / future / unknown rows; a malformed tx (bad sequence prefix) is Unknown, never a crash |
| **O2-T4** `CommitmentOutputMapper` | `Infrastructure.Bitcoin/Onchain/CommitmentOutputMapper.cs`: rebuild the candidate commitment (existing factory + `BuildWithOutputMap`), compare txids, emit descriptors (§3.3) incl. the "no output" HTLC set | Appendix C commitments 0-15 map every vout to the right descriptor; trimmed HTLCs land in the no-output set |
| **O2-T5** `OnchainChannelWatcher` + `ForceCloseChannel` IPC | `Application/Onchain/OnchainChannelWatcher.cs` (lock, classify, persist close record + descriptors, state 37, error to a connected peer, refuse operations); `ClientCommand` next free, Daemon handler, client handler, CLI `forceclosechannel` | `AT/Onchain/OnchainChannelWatcherTests` (each classification → persisted rows + state; replayed spend is a no-op); Daemon IPC round-trip test |

**Proof O2:** Docker `Given_WeForceClose_Then_LndSeesForceCloseAndChannelResolving` (we call `forceclosechannel` on an idle channel with push; bitcoind has our commitment; after 1 block our channel is `OnchainResolving`, LND lists it pending-force-closed) and `Given_LndForceCloses_Then_WeDetectRemoteCommit` (LND `CloseChannel force`; we classify `RemoteCommit` and fail the channel).

### O3: Local commitment resolution (we force-closed)
| Task | Files | Acceptance |
|---|---|---|
| **O3-T1** Sweep builder + signer `SignSweepInput` | `Builders/SweepTransactionBuilder.cs` (inputs with witness script, CSV/CLTV fields, one wallet output from `IBitcoinWalletService.GetUnusedAddressAsync`), `SweepSigningContext`, all four key kinds | script execution against Appendix C commitment 0 to_local with `local_delayed_privkey` (fails before CSV, passes at CSV); weight ≤ Appendix A bound; wrong key kind rejected |
| **O3-T2** Planner | Domain `Onchain/OutputResolutionPlanner.cs` with §1.4 and §3.4 rows | table tests B5-LCL-* (every row of §6.4) |
| **O3-T3** to_local + HTLC-timeout + HTLC-success + second level | watcher executes planner actions; HTLC-timeout/success from slot 0 sigs + `SignLocalHtlcTransaction` + `HtlcTransactionBuilder.AddWitness`; second-level output → `DelayedToLocal` | `AT/Onchain/LocalCommitResolutionTests` with a fake chain: timeout at `cltv_expiry`, not before; success only with an allowed preimage; second-level sweep after CSV |
| **O3-T4** Switch integration | `HtlcRemovalKind.OnchainTimeout = 4` (no reason bytes); `HtlcSwitch` fails the upstream HTLC with `permanent_channel_failure` created as the erring node for it; `OutgoingHtlcFulfilled` from an extracted preimage (persisted first) | `AT/Payments/Switch/OnchainEventsTests.Given_OnchainTimeoutAtDepth_Then_UpstreamFailedOnce`, `…Given_PreimageOnChain_Then_UpstreamFulfilledBeforeDepth` |
| **O3-T5** Preimage extraction | Domain parser of spend witnesses (offered-HTLC preimage branch; HTLC-success witness) | vectors: Appendix C HTLC-success txs yield their preimages; a timeout witness yields none |
| **O3-T6** `PendingSweeps` IPC | `ClientCommand` next free + DTOs + CLI | IPC round trip |

**Proof O3:** Docker, all through `forceclosechannel` against david: (a) idle channel with push: to_local swept after the CSV LND imposed, wallet delta = to_local − commitment fee share − sweep fee (±1 sat per rounding), LND reports `LOCAL_FORCE_CLOSE` from its side as remote **(check the enum LND 0.20 uses)**; (b) an HTLC we offered to a david hold invoice that david **settles** after our force close: david claims on chain with the preimage, we extract it, the payment shows succeeded; (c) same but david **cancels**: at `cltv_expiry` our HTLC-timeout confirms, the payment fails with `permanent_channel_failure`, after CSV the second-level output is swept; (d) forwarding variant (ABCD-style, second fixture node): an HTLC forwarded through us is failed upstream only after the HTLC-timeout is 6 deep.

### O4: Remote commitment resolution (the peer force-closed)
| Task | Files | Acceptance |
|---|---|---|
| **O4-T1** to_remote sweep (D5) | `PaymentToRemote` descriptor, `Payment` key kind | script execution (P2WPKH, and the anchors CSV-1 form for O7) |
| **O4-T2** HTLC claims on their commitment | planner rows B5-RMT-*; timeout claim (`nLockTime = cltv_expiry`, `<sig> <>`), preimage claim (`<sig> <preimage>`), HTLC key tweaked by **their** point from slot 1 or 2 | script execution against Appendix C commitments built as the remote side; wrong point → invalid |
| **O4-T3** RemoteNext and data loss | classifier + mapper for slot 2; `FutureRemote` sweeps only to_remote and raises a CRITICAL alert (IPC flag) | table + unit tests |

**Proof O4:** Docker, LND david force-closes: (a) idle channel with push to david: our to_remote reaches our wallet in the next blocks; (b) david offered us an HTLC for our invoice and our fulfill did not reach david before its force close (drop the connection after our fulfill is persisted, `CrashableTcpService`): we claim it on chain with the preimage before `cltv_expiry`, the invoice is settled; (c) we offered an HTLC to a david hold invoice never settled: after `cltv_expiry` our timeout claim confirms and the payment fails; (d) force close while our `commitment_signed` is unacked (crash after our CS as in `ReestablishFlowTests` (c), david closes with the new commitment): we classify `RemoteNextCommit` and resolve it.

### O5: Revoked commitment (penalty / justice)
| Task | Files | Acceptance |
|---|---|---|
| **O5-T1** Penalty builder | `Builders/PenaltyTransactionBuilder.cs` (batched and single-output variants), `Revocation` key kind (secret from `DeriveOldSecret(PerCommitmentIndex.From(n))`) | script execution: with `remote_revocation_basepoint_secret` and `x_local_per_commitment_secret` a penalty spends to_local (`<revsig> 1`) and every HTLC output (`<revsig> <revocationpubkey>`) of each Appendix C commitment; witness weights exactly 160 / 243 / 249 for 73-byte sigs, inputs 324 / 407 / 413 |
| **O5-T2** Planner rows + second level | B5-REV-*; their HTLC tx spending a target → re-plan on its output (`<revsig> 1`); preimage extraction from their HTLC-success | table tests; `AT/Onchain/RevokedResolutionTests.Given_TheirHtlcTimeoutConfirmsFirst_Then_SecondLevelPenalized` |
| **O5-T3** Deadlines and split | `security_delay` 18 split and RBF (with O6-T1 policy) | fake-chain test: batched penalty unconfirmed at deadline − 18 → per-output txs broadcast |

**Proof O5 (breach):**
- **(a) LND cheats (database rollback):** open us → david with push; make 3 payments each way (so every revoked state has balance on david's side and one holds an in-flight HTLC); stop david, copy its channel database out of the container with Docker.DotNet `GetArchiveFromContainerAsync` **(path `/root/.lnd/data/graph/regtest/channel.db` in `custom_lnd` is unverified)**; restart david, make 3 more payments; **stop our node**; stop david, restore the old database (`ExtractArchiveToContainerAsync`), start david, `CloseChannel force` (david broadcasts a revoked commitment; it does not know it is outdated because our node is down and cannot send `channel_reestablish`); mine 1 block; start our node before `to_self_delay` blocks pass. Assert: we classify `Revoked(n)`, one penalty confirms, our wallet gains ≈ channel capacity − fees, david's to_local is never spent by david. Variant: the revoked state holds an HTLC that david times out with its HTLC-timeout (mine to its `cltv_expiry`): we penalize the second-level output.
- **(b) Deterministic NLightning cheater:** two NLightning nodes via the multi-node harness (`Docker/MultiNodeHarnessTests` style): the test calls the cheater's `SignLocalCommitmentForBroadcast` at state k, while k is still its latest commitment (the O2-T1 guard allows it), keeps the raw bytes without broadcasting them, payments move the state to k+3, the test sends the captured raw tx with bitcoind `sendrawtransaction`; the victim penalizes. This proof has no LND timing dependence and runs every time; (a) is the interop proof.

### O6: Fees, rebroadcast, reorgs, completion
| Task | Files | Acceptance |
|---|---|---|
| **O6-T1** Fee policy + `IFeeService` target | Domain `Onchain/SweepFeePolicy.cs`; `IFeeService.GetFeeRatePerKwAsync(uint confTarget, ct)`; `SweepScheduler` bumps per block | policy table tests (deadline, caps, BIP125 increments); `SweepSchedulerTests.Given_NotConfirmedAfterInterval_Then_ReplacedWithHigherFee` |
| **O6-T2** Irrevocable resolution and `Closed` | 100-block rule per output; channel `Closed` and watches/log dropped when all are | fake-chain test at depth 99/100 |
| **O6-T3** Reorg re-resolution | watcher reacts to `OnBlockDisconnected`: resolving tx gone → rebroadcast; funding spend gone → rules of §3.8 | fake-chain tests; Docker below |
| **O6-T4** Mainnet gate | `NodeOptions.HtlcsEnabled` default true on all networks only after Proofs O3-O6 pass (BOLT2 §0.6) | options test |

**Proof O6:** Docker (a) `invalidateblock` on the block with our to_local sweep, mine a competing empty block: we rebroadcast and the sweep confirms again; (b) restart our node with a penalty unconfirmed (mempool cleared by bitcoind restart with `-persistmempool=0`): it is rebroadcast from `BroadcastTransactionEntity`; (c) a sweep broadcast at a low feerate (fee estimate forced low) is RBF-bumped until it confirms.

### O7 (later): Anchors
- **O7-T1** Wallet signing (`SignWalletTransaction`, resolves NL-067 second half) and a fee-input selector.
- **O7-T2** CPFP of our commitment through `to_local_anchor` (B5-FAIL-06), RBF of the child; anyone-can-sweep of anchors after 16 blocks.
- **O7-T3** HTLC-timeout/success with `SINGLE|ANYONECANPAY` peer sigs combined with fee inputs (B5-HTX-02); to_remote CSV-1 sweep; penalty split rules from the start of the danger window.
- **O7-T4** Enable `OptionAnchors` (BOLT2 N11-T3) after an anchors variant of Proofs O3-O5 passes against LND.
- `zero_fee_commitments` (v3/TRUC, `shared_anchor`) is out of scope for this plan.

### O8 (optional): Mempool
- ZMQ `rawtx` (NL-098): preimage extraction and penalty reaction from unconfirmed txs; never treat a mempool tx as a confirmation.

---

## 6. Requirements traceability matrix

Status: **MISSING** everywhere at `b38ec28` unless noted. Test prefixes: `DT/` Domain.Tests, `AT/` Application.Tests, `BT/` Infrastructure.Bitcoin.Tests, `IT/` Integration.Tests, `DK/` `IT/Docker/OnchainFlowTests`.

### 6.1 General and failing
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B5-GEN-01 | Monitor unresolved outputs | MISSING (funding watch dropped at confirmation, OG1) | O0-T2 | `BT/…/BlockchainMonitorServiceTests.Given_BlockSpendsWatchedOutpoint_…` |
| B5-GEN-02 | Resolve all; irrevocable at 100 | MISSING | O3-O6 | `DT/Onchain/OutputResolutionPlannerTests` depth rows |
| B5-GEN-03 | Reorg-safe | MISSING (NL-096) | O0-T3, O6-T3 | reorg tests, DK O6 (a) |
| B5-GEN-04 | Funding spent while open → fail | MISSING | O2-T5 | `AT/Onchain/OnchainChannelWatcherTests` |
| B5-GEN-05 | Ignore invalid txs | N/A by design (only confirmed txs are seen) | O0-T2 | — |
| B5-GEN-06 | Unknown funding spend → warn | MISSING | O2-T3, O2-T5 | classifier Unknown row |
| B5-GEN-07 | Mempool MAY | MISSING (NL-098) | O8 | — |
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
| B5-REV-01 | never broadcast a revoked local commitment | O2-T1 | signer tests |
| B5-REV-03 | penalize their to_local | O5-T1 | vectors, DK O5 |
| B5-REV-04/05 | penalize HTLC outputs | O5-T1, O5-T2 | vectors, DK O5 (a) variant |
| B5-REV-06 | penalize their second-level txs | O5-T2 | `RevokedResolutionTests`, DK O5 (a) variant |
| B5-REV-07 | extract preimage | O3-T5 | unit |
| B5-REV-08 | batching and split at security_delay | O5-T3 | fake-chain test |
| B5-REV-09 | handle invalidation by HTLC txs | O5-T2 | `…Given_TheirHtlcTimeoutConfirmsFirst_…` |

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
5. **Revocation log growth:** one row per revoked commitment with HTLCs, for the channel's life. Pruned when the channel closes; size ~ 53 bytes × HTLCs. Monitor in long runs.
6. **LND behaviours (unverified):** channel database path and restore procedure in `custom_lnd`; whether LND 0.20 refuses to force-close after a restore (it should not know without our reestablish); the CSV LND imposes in regtest; close-type enums reported for each case; hold-invoice sub-server availability through LNUnit. Each proof logs LND's version and the values it reads.
7. **Breach proof timing:** our node must stay down while david broadcasts, then come back within `to_self_delay` blocks; tests mine explicitly, never wait on wall-clock blocks.
8. **Fee estimator in regtest** returns a floor rate; RBF tests force low and high estimates through a test `IFeeService`.
9. **Key material in memory:** sweep and penalty signing derive keys per call; they must be wiped (`using`) as the existing signer does. A remote signer (VLS-style) later needs the same `SweepSigningContext` contract, which is why the context carries the script and amount rather than a raw sighash.
10. **Concurrency:** the watcher runs block work under each channel's lock; it must never hold two locks, and must only enqueue (never await a network send) while holding one (root `CLAUDE.md` ordering rules). Broadcasts happen after the save and outside the lock.
