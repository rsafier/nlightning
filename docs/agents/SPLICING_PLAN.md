# Quiescence, Interactive-tx and Splicing: Implementation Plan for NLightning

This plan covers four waves, each a prerequisite of the next:
- **Wave Q: quiescence** (BOLT 2 "Channel Quiescence", `stfu`, BOLT 9 `option_quiesce` 34/35). It covers the `stfu` handler, the per-channel quiescence state, the gate on updates, the 60-second rule and the hook that dependent protocols (splicing) use to start and end quiescence. Ledger: **NL-042** (and NL-019, which today answers `stfu` with a warning and a disconnect).
- **Wave IT: interactive transaction construction** (BOLT 2 "Interactive Transaction Construction", messages 66-74). It covers a pure negotiation engine, the prevtx and script checks, fee responsibility, wallet contributions, `tx_signatures` ordering, `tx_abort`, the base RBF rules and persistence of a signed-but-unconfirmed negotiation. The same engine is what dual funding (v2 open, `open_channel2`/`accept_channel2`) needs: see §3.9 and the optional wave DF. Ledger: **NL-037** (epic), **NL-041**, NL-219.
- **Wave SP: splicing** (BOLT 2 "Channel Splicing", BOLT 9 `option_splice` 62/63, BOLT 7 splice announcements), in two halves:
  - **SP1**: `splice_init`/`splice_ack`, the shared input and output, several pending fundings in the commitment engine, the signer and persistence, batched `commitment_signed` (`start_batch`), splice-in and splice-out initiated by us and by the peer;
  - **SP2**: `channel_reestablish` with `next_funding`/`my_current_funding_locked`, `splice_locked`, the new short channel id and re-announcement, BOLT 5 on-chain handling of every candidate funding output, reorgs of a splice transaction, and the Docker proofs against CLN.
  Ledger: **NL-021** (splice and `start_batch` messages missing) plus the new IDs in §2.2.
- **Wave SPR: splice RBF** (`tx_init_rbf`/`tx_ack_rbf` on a pending splice, more than one pending splice candidate, batches up to 20).

An optional **wave DF** (dual-funded v2 open) reuses wave IT and can run in parallel with SP1 or later. It is not needed for splicing.

**Status (2026-09-28, `wip/fafo` @ `a0800ac2`): waves Q and IT done (wave qit, "Wave qit record"), wave SP1 done with Proof SP1 green against CLN v26.06.8 and wave DF's DF1/DF2 done with Proof DF green (wave sp1, "Wave SP1 record"), wave SP2 done with Proof SP2 green (wave sp2, "Wave SP2 record"), wave SPR done with Proof SPR green (wave spr, "Wave SPR record"); **D13 decided and applied (owner decision 2026-09-28, wave d13 on `wip/fafo-d13`, "Wave d13 record"): `OptionQuiesce`, `OptionSplice` and `OptionDualFund` are Optional by default on every network, mainnet included, and out of the experimental set (DF3 done with it); splice RBF recency is block-based (NL-520).** The design text below was written at `d929b879` (plan written, nothing started); every repo claim cites a repo-relative path verified at `d929b879`; line numbers drift, so re-check before editing. Claims marked **(unverified)** were not checked against the spec, the code or a running peer. Confirm them before relying on them.

- **Spec source:** `lightning/bolts` master, fetched 2026-09-27 (raw files):
  - `02-peer-protocol.md`: §Interactive Transaction Construction (lines ~100-628), §Channel Establishment v2 (~1137-1490), §Channel Quiescence (~1491-1557), §Channel Splicing (~1559-2046), §Batching channel messages (`start_batch`, ~3040-3097), §`commitment_signed` (~3099-3206), §`channel_reestablish` (~3372-3560);
  - `bolt02/splicing-test.md`: eleven message-flow scenarios for splicing (§1.9); these are our main conformance tests;
  - `03-transactions.md` §Calculating Fees for Collaborative Transactions and **Appendix G: Dual Funded Transaction Test Vectors**;
  - `07-routing-gossip.md`: `announcement_signatures` for splices, the 72-block forget delay;
  - `09-features.md`: rows 28/29, 34/35, 62/63.
- **Spec status (verified from the bolts commit log, 2026-09-27):**
  - **Splicing is merged into BOLT 2 master**: "Channel Splicing (feature 62/63) (#1160)", committed 2026-03-23. The draft's provisional numbers are gone; the final types are `splice_init` 80, `splice_ack` 81, `splice_locked` 77, `start_batch` 127, and the feature is 62/63.
  - Quiescence (`stfu` type 2, `option_quiesce` 34/35) and interactive-tx/dual funding (66-74, 64/65, `option_dual_fund` 28/29) are in master. Quiescence merged with #869 (2024-06-17) and dual funding with #851 (2024-02-13). `next_funding`'s `retransmit_flags` ("Explicit `commit_sig` retransmission for `interactive-tx`") merged with #1289 (2026-02-13), before splicing.
  - The RBF feerate rule changed on 2026-05-04 (#1327): `tx_init_rbf` feerate >= max(25/24 × previous, previous + 25 sat/kw).
  - Zero-fee commitments (40/41, #1228) are merged too; NLightning does not implement them, and this plan does not cover splicing them.
- **Secondary reference: <https://lightningsplice.com/>** (the splicing site of CLN's splicing lead), fetched 2026-09-27. Its pages are an overview and history, not a spec: the spec page links the old proposal PR #863, not the merged #1160, and "Splicing Explained" says each node "waits for 6 confirmations before considering the splice valid", while BOLT 2 says "acceptable depth" (we use the channel's `minimum_depth`, D8). **The BOLTs win**; both differences are recorded in §9 and the questions in §10.
- **Relation to other plans:**
  - `BOLT2_NORMAL_OPERATION_PLAN.md`: the commitment engine (`ChannelCommitments`, invariants I1-I12), `ReestablishPlanner`, close. Its traceability rows B2-BATCH-01..07, B2-CS-S07, B2-CS-R04, B2-RE-13 and B2-RE-26 are "N/A (splice only)"; this plan owns them.
  - `BOLT5_ONCHAIN_PLAN.md`: `OnchainChannelWatcher`, `FundingSpendClassifier`, the resolvers, anchors (O7 done). A splice adds funding outputs that can be spent by a commitment.
  - `BOLT7_GOSSIP_PLAN.md`: `ChannelAnnouncementService`, `GraphPruner` (72-block retention). A splice changes the SCID.
- **Out of scope:** splicing zero-fee-commitment (40/41) or taproot channels (neither exists here); multi-channel splices in one transaction (CLN's `dev-splice` scripts); liquidity ads; 0-conf splices (`option_zeroconf` is not implemented, and a 0-conf splice forbids RBF); PSBT-based external funding of a splice (the wallet funds it).

---

## 0. How to use this document (agents)

1. **Order of work.** Q, then IT, then SP1, then SP2, then SPR. DF may start once IT is integrated. Each wave starts with a **contracts commit by the integrator** (§5 "Contracts first"). The lanes fork from that commit.
2. **Definition of done for a task:**
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` and `-c Release.Native` (IT, SP1 and SP2 touch the signer);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release -f net10.0 --filter 'FullyQualifiedName!~Docker'`;
   - the task's own tests, and the **invariant suite** (`CommitmentPairSimulator`, BOLT2 plan N4-T5) for anything that touches the commitment engine. Never skip or delete an invariant test; SP1 extends it (SP-I1..SP-I8, §3.4);
   - no new CS warnings.
3. **Definition of done for a milestone:** its **Proof** passes. In-process proofs use `TwoNodeHarness` (`test/NLightning.Application.Tests/Channels/Harness/TwoNodeHarness.cs`), which runs the production handlers. Docker proofs run against CLN v26.06.8 in `test/NLightning.Integration.Tests/Docker/Interop/Cln/` (`ClnFixture`, collection `cln-interop`, `[Trait("Category", "Interop.Cln")]`), from the host process like the other CLN tests (`test/CLAUDE.md`). The on-chain splice proofs join `Docker/Onchain/` (their own process, `scripts/run-onchain.sh`).
4. **Schema** (all three providers through `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh`; build the three provider projects in Debug first, NL-233; `HasPendingModelChanges` false × 3):
   - wave Q: **no migration** (quiescence ends on disconnect, so it is never persisted);
   - wave IT: `AddInteractiveTxSessions`, owned by lane **IT-C**;
   - wave SP1: `AddSpliceFundings`, owned by lane **SP1-C**;
   - wave SP2: `AddSpliceLockedState` only if SP1's schema turns out short, owned by lane **SP2-C**; the goal is none;
   - wave SPR: none.
5. **DI.** Register in the owning layer's `DependencyInjection.cs` or a `*ServiceCollectionExtensions` called from it: `AddQuiescenceServices()`, `AddInteractiveTxServices()`, `AddSpliceServices()` (Application); the prevtx inspector and the splice signer pieces in `AddBitcoinInfrastructure`. The Docker node uses `AddNltgNodeServices` (NL-156), so nothing is mirrored by hand.
6. **Safety gates (feature bits):**
   - `OptionQuiesce` stays in `FeatureOptions.ExperimentalFeatures` (`src/NLightning.Domain/Node/Options/FeatureOptions.cs:25-30`) after wave Q: a peer that quiesces with us expects a dependent protocol, and we have none until SP1 (D2).
   - `OptionDualFund` stays experimental until wave DF.
   - A new `FeatureOptions.OptionSplice` (default No, in `ExperimentalFeatures`) is added in the SP1 contracts. `OptionSplice` and `OptionQuiesce` leave the experimental set together in the SP2 integration commit, after Proof SP2 (D13), and default to **Optional**.
   - Splicing requires both 35 and 63 negotiated. BOLT 9 lists no dependency for 62/63, so we check it at use, not in `FeatureSet` (D14).
7. **Commit shape:** one task per commit, lowercase imperative subject citing the NL ID and the task ID (for example `(NL-042 / Q1-T2)`), plus the session trailer the integrator gives you.
8. **Unverified CLN behaviors** (§7, §10) are settled by the proofs. When CLN differs from this plan, adjust the proof and record what CLN did. Never bend a spec rule to match CLN.

---

## 1. Spec requirements summary

Requirement IDs are used by the tasks and by the traceability matrix (§6).

### 1.1 Quiescence (`stfu`, type 2)
| ID | Requirement |
|---|---|
| Q-W-01 | `stfu` = `channel_id ‖ u8 initiator`. It is a channel message |
| Q-S-01 | MUST NOT send `stfu` unless `option_quiesce` is negotiated |
| Q-S-02 | MUST NOT send `stfu` if any of the **sender's** HTLC additions, removals or fee updates are pending for either peer |
| Q-S-03 | MUST NOT send `stfu` twice. A reply sets `initiator` = 0; otherwise 1 |
| Q-S-04 | After sending `stfu`: the channel is quiescing, and the sender MUST NOT send an update message |
| Q-R-01 | Receiver that already sent `stfu`: the channel is now quiescent |
| Q-R-02 | Otherwise: SHOULD NOT send more updates, and MUST reply with `stfu` once it can (its own pending changes committed and revoked both ways) |
| Q-R-03 | Both: MUST disconnect after 60 s of quiescence if HTLCs are pending |
| Q-R-04 | On disconnection the channel is no longer quiescent |
| Q-R-05 | Simultaneous `stfu` with both `initiator` = 1: the channel funder (sender of `open_channel`) is the initiator |
| Q-R-06 | Dependent protocols MUST specify every state that ends quiescence (splicing: `tx_signatures` exchanged, or `tx_abort`; see SP-Q-01) |

### 1.2 Interactive transaction construction (66-74)
| ID | Requirement |
|---|---|
| IT-W-01 | `tx_add_input` 66 = `channel_id ‖ u64 serial_id ‖ u16 prevtx_len ‖ prevtx ‖ u32 prevtx_vout ‖ u32 sequence ‖ tlvs`; TLV 0 `shared_input_txid` (sha256) |
| IT-W-02 | `tx_add_output` 67, `tx_remove_input` 68, `tx_remove_output` 69, `tx_complete` 70 |
| IT-W-03 | `tx_signatures` 71 = `channel_id ‖ txid ‖ u16 num_witnesses ‖ witnesses ‖ tlvs`; TLV 0 `shared_input_signature` (64-byte signature) |
| IT-W-04 | `tx_init_rbf` 72 = `channel_id ‖ u32 locktime ‖ u32 feerate ‖ tlvs` (0 `funding_output_contribution` s64, 2 `require_confirmed_inputs`); `tx_ack_rbf` 73 = `channel_id ‖ tlvs` (same TLVs); `tx_abort` 74 = `channel_id ‖ u16 len ‖ data` |
| IT-S-01 | Initiator sends even `serial_id`s, non-initiator odd; unique per currently added input/output; `sequence` <= 0xFFFFFFFD; never retransmit the peer's inputs |
| IT-S-02 | Turn-based: the initiator starts; the negotiation ends after **two consecutive** `tx_complete`s |
| IT-S-03 | Fees: the initiator pays the common fields (version, segwit marker/flag, input/output counts, locktime); each contributor pays for its own inputs and outputs at the agreed feerate (BOLT 3 "Calculating Fees for Collaborative Transactions") |
| IT-R-01 | `tx_add_input` receiver fails the negotiation on: sequence 0xFFFFFFFE/F; `prevtx_len` 0 without a matching `shared_input_txid`, or a second shared input; a duplicate prevtx+vout; an invalid prevtx; `prevtx_vout` out of range; a scriptPubKey that is not a witness program (1-byte push 0-16 then a 2-40 byte push); duplicate or wrong-parity `serial_id`; the 4096th `tx_add_input` **received in this negotiation** |
| IT-R-02 | `tx_add_output` receiver: MUST accept P2WSH, P2WPKH, P2TR; MAY fail non-standard; MUST fail on duplicate or wrong-parity `serial_id`, the 4096th received `tx_add_output`, `sats` < dust limit, `sats` > MAX_MONEY |
| IT-R-03 | `tx_remove_*` receiver fails if the `serial_id` was not added by the sender or is not currently added |
| IT-R-04 | `tx_complete` receiver fails if: the peer's inputs < its outputs (its share of the funding output counts); the peer's paid feerate < the agreed feerate; as non-initiator, the initiator's fees don't cover the common fields; > 252 inputs; > 252 outputs; estimated weight > 400,000 |
| IT-SIG-01 | `tx_signatures` order: the side with the lower total `tx_add_input` value (tie: lower `node_id`) sends first. Witnesses ordered by `serial_id`, one per input it added, SIGHASH_ALL only |
| IT-SIG-02 | Receiver fails the negotiation on an empty witness, a wrong count, a txid mismatch, non-standard witnesses or a non-SIGHASH_ALL flag; SHOULD apply and broadcast; MUST reply with its own if not sent |
| IT-SIG-03 | A `tx_signatures` is sent only after a valid `commitment_signed` was received for the new funding |
| IT-ABT-01 | `tx_abort`: MUST NOT be sent after our `tx_signatures`. The receiver MUST echo it if it has not sent one, and SHOULD forget the negotiation, unless it already sent `tx_signatures`: then it MUST NOT forget the channel until an input of the negotiated tx is spent. Don't print non-printable `data` |
| IT-RBF-01 | `tx_init_rbf` feerate >= max(⌊25/24 × previous⌋, previous + 25 sat/kw); the sender that contributed before MUST double-spend every previous attempt (re-add at least one input of each); the recipient answers `tx_ack_rbf` or `tx_abort` and MUST abort a too-low feerate |

### 1.3 Splicing: negotiation (`splice_init` 80, `splice_ack` 81)
| ID | Requirement |
|---|---|
| SP-W-01 | `splice_init` = `channel_id ‖ s64 funding_contribution_satoshis ‖ u32 funding_feerate_perkw ‖ u32 locktime ‖ point funding_pubkey ‖ tlvs` (2 `require_confirmed_inputs`) |
| SP-W-02 | `splice_ack` = `channel_id ‖ s64 funding_contribution_satoshis ‖ point funding_pubkey ‖ tlvs` (2 `require_confirmed_inputs`) |
| SP-S-01 | `splice_init` only when: quiescent **and** we are the quiescence initiator; `channel_ready` sent and received; no splice being negotiated; no negotiated splice not yet locked both ways (RBF is the way to change it, SPR); no `shutdown` sent |
| SP-S-02 | Contribution: negative = amount subtracted from the sender's balance (splice-out), positive = added (splice-in). `require_confirmed_inputs` when we require it. SHOULD use a new `funding_pubkey` (D5) |
| SP-R-01 | Receiver: not quiescent, sender not the quiescence initiator, a splice being negotiated or unlocked, `shutdown` received, or a negative contribution larger than the sender's balance → warning + close or error + fail. Unacceptable feerate → `tx_abort`. Accept → `splice_ack`, reject → `tx_abort` |
| SP-R-02 | `splice_ack` receiver: the same balance check; accept → start the interactive-tx session as initiator; reject → `tx_abort`; a `splice_ack` without our `splice_init` → warning + close or error + fail |
| SP-Q-01 | Quiescence ends when `tx_signatures` are exchanged ("MUST consider the channel no longer quiescent"). A `tx_abort` before that also ends it (**inferred**: the spec says only that the negotiation is reset; CLN and Eclair resume the channel, **unverified**, question Q1 in §10) |

### 1.4 Splicing: transaction construction
| ID | Requirement |
|---|---|
| SP-TX-01 | The splice initiator adds the current funding output with `tx_add_input` + `shared_input_txid` = previous funding txid, **no prevtx**, `prevtx_vout` = previous funding index |
| SP-TX-02 | The receiver aborts (`tx_abort`) if `shared_input_txid` or `prevtx_vout` doesn't match the current funding output |
| SP-TX-03 | The splice initiator adds the new funding output (from both `funding_pubkey`s) with amount = previous capacity + both contributions, and pays for the shared input's and shared output's weight |
| SP-TX-04 | `require_confirmed_inputs` from the peer → only confirmed inputs |
| SP-TX-05 | `tx_complete` receiver: new balance per side = previous balance + its contribution. Abort unless: exactly one input spends the current funding output; exactly one funding output with the right keys and amount; an RBF pays at least the previous attempt's total fee; a side that adds a non-funding output keeps at least the reserve for the **new** capacity |

### 1.5 Splicing: signatures
| ID | Requirement |
|---|---|
| SP-CS-01 | After `tx_complete`, each side sends `commitment_signed` for a commitment spending the **new** funding output: contributions added to each side's main balance, same feerate and **same commitment number** as the existing commitment, signatures for every pending HTLC. It MUST remember the splice |
| SP-CS-02 | The receiver MUST NOT answer that CS with `revoke_and_ack`. It sends its own CS if not yet sent, then `tx_signatures` if it signs first. The shared input counts 100% for the **initiator** in the "who signs first" rule |
| SP-CS-03 | On reconnection, if `next_funding` matches the splice: retransmit the CS |
| SP-SIG-01 | `tx_signatures` MUST carry `shared_input_signature` (our funding key's ECDSA signature for the shared input); missing, invalid or high-S → error + fail the channel. After it, the channel is no longer quiescent |
| SP-SIG-02 | On reconnection, if `next_funding` matches: retransmit `tx_signatures` |

### 1.6 Splicing: operation while pending (`start_batch` 127, `commitment_signed` 132)
| ID | Requirement |
|---|---|
| SP-OP-01 | With the splice signed but not locked, updates must be valid for **every active commitment** (the current funding and each pending splice). ("Payments must be valid for all splice transactions.") |
| SP-OP-02 | `commitment_signed` MUST always set TLV 1 `funding_txid` (already done, NL-199) |
| SP-OP-03 | With N > 0 pending splices: send `start_batch(batch_size = N + 1, message_type = 132)`, then one CS for the current funding and one per splice, each with its `funding_txid`, with nothing else in between |
| SP-OP-04 | `start_batch` receiver: `batch_size` <= 1 → ignore + SHOULD warn; > 20 → warning + close or error + fail; group the next `batch_size` messages; a message for another channel → warning + close or error + fail; `message_type` missing or not 132 → ignore the `start_batch` and process sequentially |
| SP-OP-05 | Pending splices and a CS **not** in a batch → error + fail. In a batch: a CS without `funding_txid` → error + fail; each CS validated against its funding; a funding without its CS → error + fail; then **one** `revoke_and_ack` |
| SP-OP-06 | No pending splices but a batch arrives: ignore CS whose `funding_txid` is not the current funding (obsolete ones sent before our `splice_locked` arrived); missing current → error + fail |
| SP-OP-07 | `revoke_and_ack` is unchanged: one per batch, revoking the commitment number on **every** funding at once (the per-commitment secret is per number, not per funding) |

### 1.7 Splicing: completion (`splice_locked` 77)
| ID | Requirement |
|---|---|
| SP-LK-01 | `splice_locked` = `channel_id ‖ sha256 splice_txid`. Send it when any splice transaction reaches acceptable depth |
| SP-LK-02 | Receiver: a `splice_txid` that matches none of its pending splices → warning + close or error + fail |
| SP-LK-03 | Sent and received for the **same** txid: stop sending CS for its RBF siblings and ancestors; MAY discard them; if `announce_channel`, MUST send `announcement_signatures` for the new SCID. **Different** RBF candidates: SHOULD ignore, MAY fail |
| SP-LK-04 | No `splice_init` or `tx_init_rbf` after we sent `splice_locked` (and the receiver fails an RBF from a peer that sent it) |
| SP-G-01 | BOLT 7: after `splice_locked` both ways and a reorg-safe depth, MUST send `announcement_signatures` for the splice; SHOULD defer a peer's `announcement_signatures` for a splice until we sent `splice_locked`; a new `channel_announcement` per splice lets the network keep the channel |
| SP-G-02 | BOLT 7: a node SHOULD forget a channel 72 blocks after its funding output is spent, so a splice's new announcement can arrive first (`GraphPruner` already uses 72) |

### 1.8 Splicing: `channel_reestablish`
| ID | Requirement |
|---|---|
| SP-RE-01 | Sender: if it sent CS for an interactive tx but did not receive `tx_signatures`, MUST include TLV 1 `next_funding` = txid ‖ `retransmit_flags`, with bit 0 (`commitment_signed`) set if it did not receive the peer's CS for that tx; otherwise MUST NOT include it |
| SP-RE-02 | With `option_splice`: include TLV 5 `my_current_funding_locked` = the latest splice that reached depth while disconnected, else the last `splice_locked` sent, else the funding txid if `channel_ready` was sent, else omit. Its `retransmit_flags` bit 0 (`announcement_signatures`) = 1 when public and we did not receive the peer's `announcement_signatures` for that tx |
| SP-RE-03 | Receiver of `next_funding` matching the latest interactive tx: without the peer's `tx_signatures`: retransmit our CS if bit 0 is set; send `tx_signatures` if we already received their CS and sign first. With their `tx_signatures` already: send ours. Both set `next_funding` with different txids → error + fail. Otherwise (unknown txid) → `tx_abort` |
| SP-RE-04 | Receiver of `my_current_funding_locked` matching a pending splice for which we have no `splice_locked` → process it as `splice_locked`. Bit 0 set and we are ready → retransmit `announcement_signatures` |
| SP-RE-05 | `channel_ready` is retransmitted only when both `next_commitment_number`s are 1 **and** neither message carries `my_current_funding_locked` or `next_funding` for a splice |
| SP-RE-06 | The commitment-number rules (B2-RE-08..24) are unchanged and apply across all fundings |

### 1.9 Conformance flows (`bolt02/splicing-test.md`)
Each scenario becomes one `TwoNodeHarness` test (SP-T-##) that asserts the exact message sequence and the final active commitments:

| ID | Scenario |
|---|---|
| SP-T-01 | Successful single splice |
| SP-T-02 | Multiple splices with concurrent `splice_locked` |
| SP-T-03 | Disconnection with one side sending `commit_sig` |
| SP-T-04 | Disconnection with both sides sending `commit_sig` |
| SP-T-05 | Disconnection with one side sending `tx_signatures` |
| SP-T-06 | Disconnection with both sides sending `tx_signatures` |
| SP-T-07 | Disconnection with both sides sending `tx_signatures` and channel updates |
| SP-T-08 | Disconnection with concurrent `splice_locked` |
| SP-T-09 | Disconnection after `tx_signatures`, one side sends `commit_sig` for a channel update |
| SP-T-10 | Disconnection after `tx_signatures`, both send `commit_sig`, `revoke_and_ack` not received |
| SP-T-11 | Disconnection after `tx_signatures`, both send `commit_sig` for a channel update |

There are **no byte-level splice vectors** in the bolts repo (`bolt02/` holds only `splicing-test.md`; `bolt03/` holds `zero-fee-commitments-test.json`). Interactive-tx has BOLT 3 **Appendix G** (dual-funded funding transaction construction: inputs, outputs, serial ids, locktime 120, feerate 253, the resulting txid and witnesses), which IT uses byte-exact.

### 1.10 Features (BOLT 9)
| ID | Requirement |
|---|---|
| F-01 | `option_quiesce` 34/35, context IN. `Feature.OptionQuiesce = 35` exists (`src/NLightning.Domain/Enums/Feature.cs:121`) |
| F-02 | `option_dual_fund` 28/29, IN (wave DF) |
| F-03 | `option_splice` 62/63, IN, **no listed dependency**. `Feature.OptionSplice = 63` exists (`Feature.cs:201`) and is in the context table (`src/NLightning.Domain/Node/FeatureSet.cs:83`), but `FeatureOptions` has no property for it |
| F-04 | Pre-standard bits: Eclair's prototype used bit 155 (LDK 0.2.2 moved off it, "a compatibility issue with eclair nodes due to the use of the same splicing feature flag (155)"); Eclair 0.14 removed its prototype. We never advertise or honour 154/155 or any other provisional bit |

---

## 2. Current state (verified at `d929b879`)

### 2.1 What exists
- **Wire messages:** `MessageTypes` has `Stfu = 2`, `OpenChannel2 = 64`, `AcceptChannel2 = 65`, `TxAddInput = 66` ... `TxAbort = 74` (`src/NLightning.Domain/Protocol/Constants/MessageTypes.cs:11,32-47`), with messages, payloads and serializers. There is **no** 77, 80, 81 or 127.
- **`stfu`:** `StfuMessage : BaseMessage` (not a channel message, `src/NLightning.Domain/Protocol/Messages/StfuMessage.cs:15`) although `StfuPayload : IChannelMessagePayload` (`src/NLightning.Domain/Protocol/Payloads/StfuPayload.cs:12`). `PeerService.HandleMessage` answers it with a channel-scoped warning and disconnects (`src/NLightning.Infrastructure/Node/Services/PeerService.cs:389-399`, NL-019). `IMessageFactory.CreateStfuMessage(channelId, initiator)` exists (`src/NLightning.Application/Protocol/Factories/MessageFactory.cs:91`).
- **Features:** `OptionQuiesce` defaults to No and is experimental (`FeatureOptions.cs:27,142`); `OptionDualFund` experimental; no `OptionSplice` option.
- **Interactive-tx:** a stub.
  - `InteractiveTransactionService` (82 lines, `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs`) holds peer inputs/outputs in dictionaries and runs the validators. It is **not in DI**, has no local side, no turn-taking, no `tx_complete` logic, no tx build, and `IsValidPrevTx` returns `true` (`:51-55`), `IsStandardScript` is `script.Length > 0` (`:77-81`).
  - `TxAddInputValidator.GetOutputCount` returns 1 and `IsScriptPubKeyValid` returns true (`src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs:49-61`, NL-041). The cap compares the **current** input count with `MaxInputsAllowed = 252` (`InteractiveTransactionConstants.cs`), not the 4096 **received** messages (NL-219).
  - `TxAddInputPayload` has **no TLV** (`shared_input_txid` missing); `TxSignaturesPayload` has **no TLV** (`shared_input_signature` missing).
  - `TxInitRbfMessage` carries `FundingOutputContributionTlv` and `RequireConfirmedInputsTlv` (`src/NLightning.Domain/Protocol/Messages/TxInitRbfMessage.cs`).
  - The validators live in `Infrastructure/Protocol/Validators/`, and the interface in `Infrastructure/Protocol/Interfaces/` (not Domain).
- **`channel_reestablish`:** `NextFundingTlv` (type 1, txid ‖ flags) is modeled; TLV 5 is ignored as unknown odd (NL-197; `ChannelReestablishMessage.cs`). `ReestablishPlanner` (`src/NLightning.Domain/Channels/Reestablish/ReestablishPlanner.cs`, 175 lines) answers `next_funding` on a v1 channel with `tx_abort` (B2-RE-25).
- **`commitment_signed`:** always sets `FundingTxIdTlv` (NL-199); the receiver ignores it (`CommitmentSignedMessageHandler`, `src/NLightning.Application/Channels/Handlers/CommitmentSignedMessageHandler.cs`).
- **One funding output per channel everywhere:**
  - `ChannelModel.FundingOutput` is a single `FundingOutputInfo?`, set once (`AddFundingOutput` throws if already set, `src/NLightning.Domain/Channels/Models/ChannelModel.cs:280-286`); `ShortChannelId` is one value (`:23`).
  - `CommitmentParams.FundingSatoshis` is one number, read from `channel.FundingOutput` (`src/NLightning.Domain/Channels/Commitments/CommitmentParams.cs:23-58`).
  - `ChannelCommitments` (795 lines) holds one `LocalCommit`, one `RemoteCommit` and one `RemoteNextCommit`; `ICommitmentSigner.SignRemoteCommitment(channelId, number, spec)` has no funding argument (`src/NLightning.Application/Channels/Services/EngineCommitmentSignerPort.cs:31`).
  - `ChannelSigningInfo` has one funding outpoint, amount and one local/remote funding key (`src/NLightning.Domain/Channels/ValueObjects/ChannelSigningInfo.cs:64-78`); `ChannelModel.GetSigningInfo()` builds it (`ChannelModel.cs:397-410`).
  - `LocalLightningSigner`: `SignChannelTransaction` and `SignLocalCommitmentForBroadcast` sign input 0 against the registered funding output (`src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:437-520, 1000-1013`). S1 is "one broadcast-signed commitment number per channel" (`:51-56, 467-471`).
  - Persistence: `ChannelEntity.FundingTxId/FundingOutputIndex/FundingAmountSatoshis` (`src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelEntity.cs:30-40`); `CommitmentEntity` slots 0/1/2 = LocalCurrent/RemoteCurrent/RemoteNext with no funding column (`Entities/Channel/CommitmentEntity.cs`); `RevokedCommitmentEntity` keyed by `(ChannelId, Number)` with one set of balances.
  - On-chain: `OnchainChannelWatcher.HandleFundingSpentAsync` treats the spend of the one funding outpoint (`src/NLightning.Application/Onchain/OnchainChannelWatcher.cs:85-150`); `FundingSpendKind` has no "splice" value (`src/NLightning.Domain/Onchain/Enums/FundingSpendKind.cs`), so a splice tx spending the funding output would be classified `Unknown`/mutual-like and could start a resolution of a live channel.
  - Gossip: `ChannelAnnouncementService.OnShortChannelIdChanged` handles a reorg-moved funding (`src/NLightning.Application/Gossip/Announcements/ChannelAnnouncementService.cs:144-172`), which is the seam for a splice's new SCID. `GraphPruner` retains spent channels 72 blocks (`src/NLightning.Application/Gossip/Graph/GraphPruner.cs:28-29`).
- **Channel states are monotonic** (`ChannelModel.UpdateState`; `ChannelState` `Open = 22` ... `Closed = 40`, `src/NLightning.Domain/Channels/Enums/ChannelState.cs`). Quiescence and a pending splice cannot be `ChannelState` values: they are sub-states of `Open` (D1, D6).
- **Dispatch:** `ChannelManager.HandleChannelMessageAsync` routes by type; anything else hits `default` → `CreateNotImplementedWarning` (`src/NLightning.Application/Channels/Managers/ChannelManager.cs:1275-1277`). The interactive-tx messages end there today.
- **Wallet:** UTXO locking for opens (`IUtxoMemoryRepository.LockUtxosToSpendOnChannel`, `src/NLightning.Domain/Bitcoin/Interfaces/IUtxoMemoryRepository.cs:25,38`), fee-input reservations (wave O7, `IFeeInputReservationDbRepository`), and `ILightningSigner.SignWalletTransaction(tx, spentOutputs)` / `(tx, reservationId, ...)` (`src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs:285-299`). These are what a splice contribution uses.
- **IPC:** `ClientCommand` ends at `FetchInvoice = 30` (`src/NLightning.Domain/Client/Enums/ClientCommand.cs:42`); the next free value is **31**.
- **CLN fixture:** `elementsproject/lightningd:v26.06.8` with `--developer --dev-bitcoind-poll=1 --ignore-fee-limits=false` (`test/NLightning.Integration.Tests/Fixtures/ClnFixture.cs:34-36,216-231`). No splice flag is needed (§7). **LND fixture:** 0.20.0-beta (`test/Docker/custom_lnd/Dockerfile:3`), which does not splice.

### 2.2 Gaps (the ledger agent files the "new" ones from the next free ID, NL-457 at `d929b879`)
| # | Gap | NL |
|---|---|---|
| SG1 | `stfu` is not a channel message, no quiescence state, no update gate, no 60 s timer, no dependent-protocol hook | NL-042, NL-019 |
| SG2 | Interactive-tx has no engine: no local side, turns, `tx_complete` checks, fee accounting, tx build, `tx_signatures` ordering, `tx_abort`, RBF; prevtx/script checks are TODOs; caps count the wrong thing | NL-037, NL-041, NL-219 |
| SG3 | Missing wire: `splice_init` 80, `splice_ack` 81, `splice_locked` 77, `start_batch` 127 (+ TLV 1 `message_type`), `tx_add_input` TLV 0 `shared_input_txid`, `tx_signatures` TLV 0 `shared_input_signature`, `splice_init`/`splice_ack` TLV 2, `channel_reestablish` TLV 5 `my_current_funding_locked` modeled | NL-021 + new |
| SG4 | One funding per channel in the model, engine, signer, persistence, watcher and gossip | new |
| SG5 | No inbound batching: the per-peer loop hands every message to its handler alone | new |
| SG6 | `FundingSpendClassifier` does not know splice transactions: a splice tx spending the funding output is not a close | new (critical once splicing is on) |
| SG7 | Reestablish has no splice rules (SP-RE-01..05) | new |
| SG8 | No `OptionSplice` in `FeatureOptions`; no check that both 35 and 63 are negotiated before splicing | new |
| SG9 | No IPC to splice (`splicein`/`spliceout`/`bumpsplice`), and `listchannels` shows one funding | new |
| SG10 | HTLC switch and invoices map a channel by one real SCID (plus aliases); after a splice the old SCID must keep resolving for forwards in flight and for payers using the old `channel_update` | new |

---

## 3. Design

### 3.1 Layering
| Layer | Q | IT | SP1/SP2/SPR |
|---|---|---|---|
| Domain (BCL only) | `Channels/Quiescence/{QuiescenceState, QuiescenceRules}` (pure: can we send `stfu`, who is initiator, what ends it) | `Protocol/InteractiveTx/` (`InteractiveTxSession` pure state machine, `InteractiveTxContribution`, `InteractiveTxRules` (moved from `Infrastructure/Protocol/Validators`), `CollaborativeFeeCalculator`, `Interfaces/{IPrevTxInspector, IInteractiveTxBuilder, IInteractiveTxContributor, IInteractiveTxSessionDbRepository}`) | `Channels/Splicing/` (`ChannelFunding` record, `FundingSet` (current + pending), `SpliceNegotiation` state, `SpliceRules` (SP-S/R/TX), `SpliceLockPlanner`), engine changes in `Channels/Commitments/` (per-funding commitments, §3.3), `ReestablishPlanner` extensions, `Onchain/Classifiers` (splice spend kind) |
| Infrastructure.Serialization | `stfu` becomes a channel message (type serializer unchanged, factory mapping) | TLVs on 66 and 71 | `splice_init`/`splice_ack`/`splice_locked`/`start_batch` serializers in both factory dictionaries; TLV 5 on 136 |
| Infrastructure (TLV converters) | — | `SharedInputTxIdTlvConverter`, `SharedInputSignatureTlvConverter` | `StartBatchMessageTypeTlvConverter`, `MyCurrentFundingLockedTlvConverter` |
| Infrastructure.Bitcoin | — | `InteractiveTx/{PrevTxInspector, InteractiveTxBuilder}` (NBitcoin parsing, witness program check, weight, txid) | `Splicing/SpliceTransactionBuilder`, signer partial `LocalLightningSigner.Splicing.cs` (per-funding keys, shared-input signature, per-funding S1) |
| Application | `Channels/Quiescence/{QuiescenceService, StfuMessageHandler, QuiescenceTimeoutMonitor}` | `InteractiveTx/{InteractiveTxDriver, WalletInteractiveTxContributor, Tx*MessageHandler}` (one handler per message type, all delegating to the driver) | `Channels/Splicing/{SpliceService, SpliceInitMessageHandler, SpliceAckMessageHandler, SpliceLockedMessageHandler, SpliceDepthWatcher, SpliceAnnouncementCoordinator}`, `CommitmentSignedBatchHandler`, `Node` inbound batch grouping |
| Persistence | none | `InteractiveTxSessionEntity` (migration `AddInteractiveTxSessions`) | `ChannelFundingEntity`; `CommitmentEntity` and `RevokedCommitmentEntity` keyed by funding (migration `AddSpliceFundings`) |
| Daemon / Client / Transport.Ipc | — | — | `splicein`, `spliceout`, `bumpsplice` (SPR), `listchannels` fundings |

### 3.2 Quiescence (wave Q)
- **State** (`QuiescenceState`, in memory, per channel, owned by `QuiescenceService`, a singleton keyed by channel id): `None` → `LocalStfuSent` / `RemoteStfuReceived` (quiescing) → `Quiescent(initiator: Local|Remote)` → `None`. It is cleared on disconnect (Q-R-04) and never persisted.
- **Routing:** `StfuMessage` becomes `BaseChannelMessage` and goes through `ChannelManager` (new `case MessageTypes.Stfu`) under the channel lock; the `PeerService` warning arm is removed (NL-019).
- **Sending `stfu`** (`IQuiescenceService.RequestAsync(channelId, purpose, ct)`, called by `SpliceService`): the request is queued; `stfu(initiator = 1)` is sent under the lock only when `QuiescenceRules.CanSendStfu`: both 35 negotiated, `Open`, reestablished on this connection, and none of **our** updates pending for either side (Q-S-02: no HTLC or fee update of ours in a state `SendCommit`/`RecvCommit`/revoke would move, i.e. our adds not yet locked in both commitments and our removals not final). While a request is queued, `ChannelOperationsService` and `CommitScheduler` **stop proposing new updates** (they still sign and revoke what is pending, which drains the channel).
- **Receiving `stfu`:** if we sent one → `Quiescent`; the initiator is whoever sent `initiator = 1` first; both 1 → the funder (Q-R-05). If we did not → stop proposing, reply `stfu(0)` as soon as `CanSendStfu` holds (checked after every CS/RAA transition under the lock).
- **Gate:** while quiescing or quiescent, `ChannelOperationsService` refuses sends with a retryable "channel quiescent" result (the switch treats it like a link that is down: forwards wait or fail back with `temporary_channel_failure` as today for a missing link), and `update_*` received from the peer after its `stfu` → warning + disconnect (Q-S-04 violated by the peer).
- **Ending:** only a dependent protocol ends quiescence (`IQuiescenceService.Terminate(channelId, reason)`), or disconnect. With no dependent protocol (wave Q alone), a quiescence we did not ask for is ended by our `tx_abort`: when the peer is the initiator and sends anything but a message we can handle, we answer per its own rules (for `splice_init` before SP1: `tx_abort`, D2).
- **Timeout** (`QuiescenceTimeoutMonitor`, `Node:Quiescence:Timeout`, default 60 s, like LND's `htlcswitch.quiescencetimeout`): quiescent with HTLCs pending for 60 s → disconnect (Q-R-03). Without HTLCs we still disconnect after `Node:Quiescence:IdleTimeout` (default 5 min) so a stuck peer cannot freeze the channel (MAY; not in the spec).

### 3.3 Several fundings in the commitment engine (SP1, the core change)
The spec's model: the HTLC set, fee updates and commitment numbers are **shared**; only the funding outpoint, capacity and the main balances differ between the active commitments. So:

- **`ChannelFunding`** (Domain record): `FundingTxId`, `OutputIndex`, `CapacitySat`, `LocalFundingPubKey`, `RemoteFundingPubKey`, `LocalFundingKeyIndex` (D5), `LocalBalanceDeltaMsat`/`RemoteBalanceDeltaMsat` relative to the **current** funding (the contributions), `Kind` (Initial / Splice / SpliceRbf), `Status` (Current / Pending / Locked / Discarded), `SpliceTx?` (unsigned + our witnesses + theirs), `ConfirmedHeight?`, `ShortChannelId?`, `SpliceLockedSent/Received`, `AnnouncementSignaturesReceived`, `FeeratePerKw`, `Locktime`, `RbfOf?` (parent splice attempt).
- **`FundingSet`**: `Current` + ordered `Pending` (at most one splice and its RBF attempts, SP-S-01: a new `splice_init` is refused while any splice is unlocked; RBF adds siblings). `Active` = `Current ∪ Pending`. `ChannelModel.FundingOutput` stays as a view of `Current` so the untouched code keeps working.
- **Engine:** `ChannelCommitments` keeps one `HtlcStateTable` and one `FeeUpdates` list. `LocalCommit`, `RemoteCommit` and `RemoteNextCommit` become **per funding**: each holds a map `FundingTxId → (txid, signature, HTLC signatures)` plus the shared number, point and spec base. `BuildSpec(side, funding)` = the shared reduce with the base balances shifted by the funding's deltas.
  - `SendCommit(signer)` signs one remote commitment per active funding (same number) and returns a **batch** outbound: `start_batch(N+1, 132)` then the CSs, current funding first (SP-OP-03). With no pending splice it is a single CS, byte-identical to today.
  - `ReceiveCommit(batch, verifier)` requires exactly one CS per active funding (SP-OP-05), ignores CSs for unknown fundings only when no splice is pending (SP-OP-06), verifies each, then produces **one** RAA (SP-OP-07).
  - `ReceiveRevoke` rotates every funding's remote commitment at once.
  - **Validation for every funding** (SP-OP-01): `UpdateValidator` runs the prospective-spec checks (reserve, funder fee incl. anchors, fee-spike buffer, dust exposure, max in-flight) against **each** active funding and rejects the update if any fails. The reserve per funding is `max(announced reserve, 1 % of that funding's capacity)` for a spliced channel (D9).
  - **Splice signing step** (not a normal CS): `SignSpliceCommitment(funding)` and `ReceiveSpliceCommitment(funding, cs)` sign/verify the commitment for the **new** funding at the **current** local/remote numbers (SP-CS-01), without advancing numbers and without an RAA (SP-CS-02).
  - **Lock:** `LockFunding(txid)` makes it `Current`, drops siblings and ancestors from `Active`, and folds the deltas into the base balances. Discarded fundings keep their revocation data (§3.6).
- **Conservation (I6 per funding):** for each active funding, `to_local + to_remote + Σ htlc == capacity_msat` (before fees/anchors).

### 3.4 Invariants added by splicing
Each has a test; the extended `CommitmentPairSimulator` (two engines, random splice/RBF/lock/disconnect steps) checks SP-I1..SP-I6 after every step.

| ID | Invariant |
|---|---|
| SP-I1 **No unprotected shared input** | We release our `shared_input_signature` (i.e. sign away the current funding output) only after a commitment spending the **new** funding output, with the peer's valid signature and HTLC signatures, is **persisted** (the splice analogue of "no funding broadcast before `funding_signed`"). Enforced in the signer (`SignSpliceSharedInput` refuses unless the channel's registered signing info already holds a verified remote signature for that funding at the current number) and by the save order |
| SP-I2 **Every active funding broadcastable** | The persisted state holds, for every active funding, our latest local commitment + remote sig + HTLC sigs (I5 per funding) |
| SP-I3 **Same number, same secret** | All active commitments share the commitment number; revealing `per_commitment_secret(n)` revokes commitment n on **every** funding at once, so it is released only when commitment n+1 is persisted and verified on **every** active funding (I3 across fundings) |
| SP-I4 **Per-funding S1** | Once we sign commitment n of funding F for broadcast, we never reveal secret(n), never advance past n, and never sign a *different* number for broadcast on any funding. Signing the same n for another active funding F′ is allowed (needed when F′ confirms instead of F) |
| SP-I5 **Revocation data outlives the funding** | Revoked-commitment data (balances, HTLCs, per funding) of a discarded or replaced funding is kept until that funding output is spent and the spend is irrevocably deep (100 blocks, as O-plan). The spec's "MAY discard ancestors" applies to signing, never to penalty data |
| SP-I6 **Valid for all** | No update is accepted or proposed that is invalid for any active funding (SP-OP-01) |
| SP-I7 **Persist before send, splice edition** | `splice_init`/`splice_ack` state, the constructed tx, our commitment signature for the new funding, `tx_signatures` and `splice_locked` are each persisted in one save before the message goes out (I1) |
| SP-I8 **Quiescence-safe start** | A splice is negotiated only while quiescent (SP-S-01), so both sides agree on the HTLC set and the commitment numbers the splice commitments use |

### 3.5 Splice negotiation flow (SP1)
1. **Operator:** `splicein <channel> <sat> [--feerate]` or `spliceout <channel> <sat> [--address <addr>] [--feerate]` → `SpliceService.StartAsync(request)`: checks 35+63 negotiated, `Open`, no `shutdown`, no unlocked splice, the amount against our balance (splice-out) or the wallet (splice-in, using the fee-input reservation machinery), then `IQuiescenceService.RequestAsync`.
2. **Quiescent, we are initiator:** persist the `SpliceNegotiation` (our contribution, feerate, locktime, our new funding key index) → send `splice_init`.
3. **Peer as initiator:** `SpliceInitMessageHandler` checks SP-R-01; our policy is to accept with contribution 0 (D10) and a new funding key; persist → `splice_ack`.
4. **Interactive tx** (`InteractiveTxDriver` with a `SpliceContributionPolicy`): initiator adds the shared input (`shared_input_txid`, no prevtx) and the new funding output; each side adds its wallet inputs and change (splice-in) or its splice-out output. `tx_complete` checks SP-TX-05 and IT-R-04.
5. **Commitments:** persist the constructed tx + our signature on the peer's commitment for the new funding (`SignSpliceCommitment`) → send CS. On the peer's CS: verify and persist (`ReceiveSpliceCommitment`); no RAA.
6. **`tx_signatures`:** the side with less input value signs first, counting the shared input 100 % for the initiator (SP-CS-02). SP-I1 holds before our `shared_input_signature` is produced. Our witnesses for wallet inputs are signed by `SignWalletTransaction` for exactly the inputs we added. On the peer's `tx_signatures` (shared signature verified, SP-SIG-01): assemble the full witness (2-of-2 for the shared input in key order), persist the signed tx as a `BroadcastTransactions` row (rebroadcast until confirmed, O0) and the funding as `Pending`, then **end quiescence** (SP-Q-01). Watch the new funding outpoint and the splice tx.
7. **Normal operation resumes** with batched CSs (§3.3).
8. **Lock** (`SpliceDepthWatcher` on each block, under the channel lock): the first pending splice at `minimum_depth` (D8) → persist `SpliceLockedSent` → send `splice_locked`. When both are for the same txid → `LockFunding`, persist the new current funding, SCID and discarded siblings in one save → update the SCID map (old SCID kept as an alias for 72 blocks, D12), `ChannelUpdateService` sends a `channel_update` for the new SCID, and if public, `SpliceAnnouncementCoordinator` sends `announcement_signatures` once the splice is 6 deep (SP-G-01).

### 3.6 On-chain (SP2; BOLT 5 across splices)
- **Watch** every active funding outpoint (current and pending), not only the current one. `WatchedOutpoints` rows per funding.
- **Classify** (`FundingSpendClassifier`): a spend of the current funding output by one of our **pending splice txids** is `FundingSpendKind.Splice` (new value, next free byte): not a close. The channel stays `Open`; the spent funding is marked "spent by splice at height h". Any other spend is classified as today, **against the commitments of the funding it spends** (local/remote/next/revoked per funding, §3.3 data).
- **Force close while a splice is pending** (`ChannelFailureService`): broadcast our latest commitment for the **current** funding; also prepare the one for each pending splice. If a splice tx confirms instead (it double-spends nothing we broadcast: our commitment and the splice both spend the current output, so exactly one confirms), broadcast the commitment for **that** splice funding. SP-I4 allows it (same number).
- **A commitment of the current funding confirms while a splice is pending:** the splice tx is now invalid; the pending fundings are `Discarded`; resolution runs on the confirmed commitment as today. Our wallet inputs in the splice tx are released (the reservation is freed once the conflict is irrevocable).
- **Revoked commitments across splices:** the per-commitment secret is per number, so a revoked commitment of **any** funding (current, pending, discarded, or an ancestor spent by a now-locked splice that gets reorged out) is punished by `RevokedCommitResolver` with that funding's revoked balances (SP-I5). `RevokedCommitments` rows gain the funding txid (§3.8).
- **Reorgs** (O6-T3 machinery):
  - before lock: a splice tx that leaves the chain returns to `Pending` (unconfirmed); rebroadcast continues;
  - after lock, a reorg deeper than `minimum_depth` that removes the locked splice tx: the old funding output is unspent again. The channel keeps operating on the locked funding (the tx is still valid and rebroadcast); we alert CRITICAL. If a different tx spends the old funding output (only possible with an old commitment), resolution runs per funding with SP-I5 data;
  - `splice_locked` with txids on different forks (SP-LK-03): ignore, wait (D11).
- **Anchors:** each active commitment has its own anchors; the CPFP machinery (O7, `AnchorReserveService`) targets the confirmed funding's commitment. The anchor reserve (`IAnchorReserveService`) must count every channel's capacity **after** a pending splice-in (the higher of current/pending). The splice tx itself is fee-bumped only by RBF (SPR), never CPFP from the channel (**inferred**, question Q6).
- **HTLCs across splices:** pending HTLCs are in every active commitment with identical amounts and CLTVs (only the funding outpoint and main balances differ). The HTLC deadline monitor (`HtlcExpiryMonitor`) is unchanged: it fails the channel, and the failure path picks the confirmed funding.

### 3.7 Gossip (SP2)
- `announcement_signatures` for a splice only after `splice_locked` both ways and 6 confirmations (SP-G-01); a peer's early `announcement_signatures` for the splice SCID is deferred until we sent `splice_locked` (not a warning).
- `channel_announcement` for the new SCID is built with the **new** funding keys (D5), signed by `SignChannelAnnouncement` with the funding key of that funding (signer change).
- Our `channel_update` switches to the new SCID; the old SCID stays resolvable in the switch for 72 blocks (D12, CLN does the same since #8387).
- Graph (others' splices): a spent funding with a new announcement for the same two node ids and a funding tx that spends the old one → keep the channel usable during the 72 blocks (already true: `GraphPruner` only removes after 72). Linking old→new SCID for pathfinding is optional (G-plan follow-up).

### 3.8 Persistence
| Migration (wave) | Changes |
|---|---|
| `AddInteractiveTxSessions` (IT, lane IT-C) | `InteractiveTxSessions` (PK `ChannelId, SessionId`; `Purpose` (Splice/DualFund/SpliceRbf), `IsInitiator`, `FeeratePerKw`, `Locktime`, `Inputs` blob (serial, outpoint, prevtx?, sequence, owner, shared?), `Outputs` blob, `ConstructedTx?`, `OurWitnesses?`, `TheirWitnesses?`, `CommitmentSignedSent/Received`, `TxSignaturesSent/Received`, `State`, `CreatedAt`). Rows exist only from "CS sent" on (the spec's "remember the details"); an unsigned negotiation is lost on disconnect by design |
| `AddSpliceFundings` (SP1, lane SP1-C) | `ChannelFundings` (PK `ChannelId, FundingTxId`; the `ChannelFunding` fields of §3.3). `Commitments` PK becomes `(ChannelId, Slot, FundingTxId)` with a data step that fills `FundingTxId` from `Channels.FundingTxId`. `RevokedCommitments` gains `FundingTxId` in its key, same data step. `Channels.FundingTxId/FundingOutputIndex/FundingAmountSatoshis` stay as the **current** funding (denormalized; updated in the lock save) so every existing reader keeps working. `ChannelKeySet` gains nothing: funding keys per splice are derived from `LocalFundingKeyIndex` (D5) |
| none in SP2 / SPR (goal) | SP2-C owns `AddSpliceLockedState` only if a field is missing |

`ChannelStateDbRepository.ApplyAsync` writes the per-funding commitment rows explicitly (never `DbSet.Update(graph)`); `ChannelRoundTripTests` (`test/NLightning.Integration.Tests/Persistence/ChannelRoundTripTests.cs`) is extended with a channel carrying two pending fundings and one discarded one.

### 3.9 Interactive-tx engine and its shared value (IT, DF)
- **`InteractiveTxSession`** (Domain, pure): inputs, `(role, turn, ourContribution, theirs, received-message counters)` → `Step(message) → (Next, Outbound, Completed?)`. It owns IT-S-01/02, IT-R-01..04 and the consecutive-`tx_complete` rule; it knows the shared input and shared output only through a `SharedFundingSpec` (null for a dual-funded open, set for a splice).
- **Ports:** `IPrevTxInspector` (Infrastructure.Bitcoin: parse prevtx, output count, witness-program check, txid; confirmed-input check against bitcoind for `require_confirmed_inputs`); `IInteractiveTxBuilder` (sorted by serial id, weight, txid); `IInteractiveTxContributor` (what we add: `WalletInteractiveTxContributor` selects UTXOs with the fee-input reservation machinery and adds change).
- **Driver** (`InteractiveTxDriver`, Application): one per channel negotiation, run under the channel lock by the `Tx*MessageHandler`s; the negotiation's purpose (`ISpliceNegotiationHost` or, in DF, `IDualFundOpenHost`) supplies the shared spec, the commitment step and the completion hook.
- **Shared value with dual funding:** the same session, contributor, builder, prevtx checks, `tx_signatures` ordering, `tx_abort` and RBF rules serve `open_channel2`/`accept_channel2`. Wave DF adds only: v2 channel id derivation (from both revocation basepoints), the 64/65 handlers, the zero-HTLC first commitment, the funding confirmation path, and `option_dual_fund` out of experimental. **DF gives IT a Docker proof of its own** (CLN opens a dual-funded channel to us) before splicing uses it, which is why it is worth scheduling right after IT if capacity allows.

### 3.10 IPC (append-only `ClientCommand`; numbers by the integrator)
| Command | Request | Response | Wave / lane |
|---|---|---|---|
| `splicein` | channel_id, amount_sat, feerate? | splice txid, new capacity, status | SP1-E |
| `spliceout` | channel_id, amount_sat, address?, feerate? (no address = our wallet) | same | SP1-E |
| `bumpsplice` | channel_id, feerate, max_fee_sat? | new txid | SPR-B |
| `listchannels` (existing) | — | + `fundings[]` (txid, capacity, status, depth, scid) | SP2-D |

At `d929b879` the next free value is **31**; expect `splicein` 31, `spliceout` 32 if nothing lands first, and `bumpsplice` after.

---

## 4. Decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | **Quiescence is in-memory runtime state**, owned by `QuiescenceService`, never a `ChannelState` and never persisted | The spec ends quiescence on disconnect; `ChannelState` is monotonic | A `Quiescent` channel state (cannot go back to `Open`) |
| D2 | **`option_quiesce` stays experimental until splicing ships** (SP2 integration, together with 63) | A peer that quiesces with us expects a dependent protocol; with none, the only exit is disconnect or `tx_abort` games. Advertising 35 alone buys nothing | Advertising 35 after wave Q |
| D3 | **`stfu` becomes a `BaseChannelMessage`** routed by `ChannelManager` under the channel lock | All other channel messages are ordered and locked this way (NL-033, NL-193) | Keeping a `PeerService` arm |
| D4 | **One interactive-tx engine in Domain**, pure, with Bitcoin work behind ports | Testable like the commitment engine; reused by DF; the old `Infrastructure` validators move into it and the stub service is deleted | Growing `InteractiveTransactionService` in Infrastructure.Bitcoin |
| D5 | **Rotate our funding key per splice** (SHOULD in SP-S-02), derived deterministically from the channel key and a per-channel funding index stored on `ChannelFunding` | Deterministic derivation keeps static channel backup and restore working (NL-426) and costs one index column; CLN supports rotation (#7719) | Reusing the funding key (allowed, but weaker privacy; kept as a config fallback `Splice:RotateFundingKey=false` for interop debugging) |
| D6 | **A pending splice is a sub-state of `Open`**, represented by `FundingSet.Pending`; `ChannelState` gets no new value | Monotonic states; the channel is fully usable while pending | New channel states |
| D7 | **Per-funding commitments inside the one engine** (shared HTLC table, per-funding base balances and signatures) | Mirrors the spec ("same commitment number", "valid for all"); one state machine, the simulator extends naturally | One `ChannelCommitments` instance per funding (would duplicate HTLC state and drift) |
| D8 | **Acceptable depth = the channel's `minimum_depth`** (`ChannelParams.MinimumDepth`), at least 3 on mainnet, and `announcement_signatures` at 6 | BOLT 2 says "acceptable depth"; Eclair uses a fixed 8, CLN docs say 6 (**unverified for CLN v26.06**, question Q4). Both sides must reach it; the later side decides | A fixed 6 (lightningsplice.com's wording) |
| D9 | **Reserve after a splice = max(announced reserve, 1 % of the new capacity)** for both sides, applied per active funding | The spec says "the channel reserve that matches the new channel capacity" without defining it for v1 channels; v2 fixes 1 %. **Inferred**, question Q3 | Keeping the v1 absolute reserve (would let a large splice-in leave a tiny reserve) |
| D10 | **As splice acceptor we contribute 0** (no wallet inputs) in SP1; contributing is a later option | Simplest and what Phoenix/LSP peers expect; avoids liquidity griefing (BOLT 2 "Liquidity griefing") | Matching contributions |
| D11 | **`splice_locked` txids on different forks: ignore and wait** (SHOULD), never fail | Reorgs resolve it; failing force-closes a healthy channel | Failing the channel (MAY) |
| D12 | **Old SCID keeps resolving for 72 blocks** after lock (switch SCID map, invoices' `r` hints refreshed) | In-flight forwards and payers with the old `channel_update`; matches BOLT 7's 72 blocks and CLN #8387 | Dropping the old SCID at lock |
| D13 | **`option_splice` + `option_quiesce` Optional by default only after Proof SP2** (integration commit), removed from `ExperimentalFeatures` together. **Decided 2026-09-28 (owner):** after Proofs SP2 and SPR, `option_splice`, `option_quiesce` **and `option_dual_fund`** (DF3) are Optional by default on every network, mainnet included; `openchannel` stays v1 unless `--dual-fund`; the splice RBF recency default became a block rule (NL-520). Applied in wave d13 | Root `CLAUDE.md` rule; splicing without reestablish and on-chain handling is unsafe with real funds | Advertising after SP1 |
| D14 | **Splice only when both 35 and 63 are negotiated**, checked at use | BOLT 9 lists no dependency for 62/63, but the protocol needs quiescence; a peer with 63 and not 35 cannot splice | Adding a `FeatureSet` dependency (would reject a peer's init that the spec allows) |
| D15 | **Inbound `start_batch` grouping in the per-peer loop**, not in `PeerService` | The loop already orders a peer's channel messages; the group is handed to one handler under one lock (`CommitmentSignedBatch`) | Buffering inside the CS handler (would span several lock acquisitions) |
| D16 | **Splice-out fees come from the initiator's channel balance** when it adds no wallet inputs (its contribution is the amount out plus its fee share) | The initiator pays common fields + shared input/output; with no wallet input the only source is its channel balance. **Unverified** how CLN/Eclair encode it, question Q2 | A mandatory wallet input for fees |

---

## 5. Milestones

### Contracts first (every wave)
Before the lanes fork, the integrator lands one commit with interfaces, records and enum values only:
- **Q-0:** `StfuMessage : BaseChannelMessage`; `Domain/Channels/Quiescence/{QuiescenceState, IQuiescenceService, QuiescencePurpose}`; a "channel quiescent" refusal that `IChannelOperations` callers can tell apart from a hard failure (new; shape chosen by the integrator).
- **IT-0:** `Domain/Protocol/InteractiveTx/{InteractiveTxSession (signatures only), InteractiveTxContribution, SharedFundingSpec, IPrevTxInspector, IInteractiveTxBuilder, IInteractiveTxContributor, IInteractiveTxSessionDbRepository, InteractiveTxSessionModel}`; `SharedInputTxIdTlv`, `SharedInputSignatureTlv` (records); `IUnitOfWork.InteractiveTxSessionDbRepository`.
- **SP1-0:** `MessageTypes.SpliceLocked = 77, SpliceInit = 80, SpliceAck = 81, StartBatch = 127`; payload/message records; `StartBatchMessageTypeTlv`, `MyCurrentFundingLockedTlv`; `Domain/Channels/Splicing/{ChannelFunding, FundingSet, SpliceNegotiationModel, ISpliceService}`; `ICommitmentSigner`/`ICommitmentVerifier` gain a `ChannelFunding` argument (existing call sites pass `Current`); `ILightningSigner` splice members (signatures only); `FeatureOptions.OptionSplice` (No, experimental); `FundingSpendKind.Splice`.
- **SP2-0:** `ReestablishLocalState` splice fields; `ISpliceDepthWatcher`; a new Domain port `IRetiredScidMap` (retired SCID → channel id, with expiry height) that `HtlcSwitch.ResolveOutgoingChannel` (`src/NLightning.Application/Payments/Switch/HtlcSwitch.cs:914`) consults after the live SCIDs and aliases.
- **SPR-0:** `ISpliceService.BumpAsync`; `FundingSet` RBF sibling API.

### Wave Q: quiescence
**Q1: `stfu` and quiescence** (lanes Q-A, Q-B)
| Task | Files | Acceptance |
|---|---|---|
| Q1-T1 Route `stfu` as a channel message | `src/NLightning.Domain/Protocol/Messages/StfuMessage.cs`, `Infrastructure.Serialization` factory mapping check, `PeerService.cs` (remove the warning arm), `ChannelManager.cs` (`case MessageTypes.Stfu`) | A `stfu` reaches `StfuMessageHandler` under the channel lock; round trip unchanged (`ST/Messages/StfuMessageTests`); NL-019 closed |
| Q1-T2 `QuiescenceRules` + state | `src/NLightning.Domain/Channels/Quiescence/**` | Table tests: Q-S-01..04, Q-R-01, Q-R-02, Q-R-05 (both initiator=1 → funder), "our updates pending" computed from `ChannelCommitments` states (a received-but-unrevoked peer add does not block **our** `stfu`; our unacked fee update does) |
| Q1-T3 `QuiescenceService` + `StfuMessageHandler` | `src/NLightning.Application/Channels/Quiescence/**` | Reply `stfu(0)` right after the last blocking RAA; request → `stfu(1)` only when allowed; second `stfu` from the peer → warning + disconnect; cleared on disconnect |
| Q1-T4 Update gate | `src/NLightning.Application/Channels/Services/{ChannelOperationsService,CommitScheduler}.cs` (quiescence check only), `ChannelManager.cs` (peer `update_*` while quiescent → warning + close) | Offer/fulfill/fail/fee while quiescing return `Quiescent`; the switch retries fulfills after the end (fulfills must never be lost: they are re-queued, not dropped); pending changes still get signed and revoked |
| Q1-T5 Timeout | `.../Quiescence/QuiescenceTimeoutMonitor.cs`, `NodeOptions` keys `Node:Quiescence:Timeout`/`IdleTimeout`, the daemon template | Fake clock: HTLCs pending 60 s → disconnect; none pending → `IdleTimeout` |
| Q1-T6 Harness | `test/NLightning.Application.Tests/Channels/Quiescence/QuiescenceHarnessTests.cs` (on `TwoNodeHarness`) | Both initiate simultaneously; one initiates with HTLCs in flight both ways (the reply waits until they are committed); fulfill arriving while quiescing is delivered after; disconnect ends it; I1-I12 hold |

**Proof Q** (lane Q-C, Docker, `ClnQuiescenceTests`; our node with `AllowExperimentalFeatures` and `OptionQuiesce = Optional`):
- (a) CLN initiates: CLN's quiescence RPC on the channel (`stfu_channels` from #6980; **unverified** that it exists un-dev'd in v26.06.8 and what ends it, question Q7) → we reply `stfu(0)` (CLN log shows the channel quiescent); then CLN's `abort_channels` (or a disconnect) → the channel works again (a payment both ways).
- (b) We initiate (test hook `IQuiescenceService.RequestAsync` through `NLightningTestNode`) → CLN replies `stfu(0)`; we end with `tx_abort` → payments both ways work (**unverified** that CLN resumes on `tx_abort` without a splice, question Q1).
- (c) HTLCs in flight (a held invoice on CLN) during the request: our `stfu` waits until they are committed.
- (d) Optional: LND 0.20 as peer, if it advertises 35 and exposes a quiescence trigger (LND has `htlcswitch.quiescencetimeout` since 0.20, **unverified** how to trigger it; dev RPC `Quiesce` **unverified**).

**Estimate:** one wave, **~2 h, 3 lanes** (Q-A wire+routing+rules, Q-B service+gate+timeout+harness, Q-C Docker proof).

### Wave IT: interactive transaction construction
**IT1: Engine** (lane IT-A)
| Task | Files | Acceptance |
|---|---|---|
| IT1-T1 `InteractiveTxSession` | `src/NLightning.Domain/Protocol/InteractiveTx/InteractiveTxSession.cs`, `InteractiveTxRules.cs` (moved from `Infrastructure/Protocol/Validators/Tx*Validator.cs`, which are deleted with their tests migrated) | Every IT-S/IT-R row as a table test; turn-taking; consecutive `tx_complete`; 4096 **received** messages per type (NL-219); 252 inputs/outputs at `tx_complete`; shared input rules (IT-R-01 prevtx_len 0) |
| IT1-T2 Fee accounting | `.../InteractiveTx/CollaborativeFeeCalculator.cs` | Initiator pays common fields; per-contributor weight × feerate; shared input/output counted for the initiator when `SharedFundingSpec` says so; weight estimates for P2WPKH/P2TR/P2WSH inputs; boundary tests at the agreed feerate |
| IT1-T3 `tx_signatures` order and `tx_abort` | `.../InteractiveTx/TxSignaturesOrder.cs`, session abort states | IT-SIG-01 (incl. equal amounts → lower node id; shared input 100 % to initiator); IT-ABT-01 echo, "never after our `tx_signatures`", remember-after-signatures |
| IT1-T4 RBF base rules | `.../InteractiveTx/InteractiveTxRbfRules.cs` | IT-RBF-01 incl. the #1327 additive 25 sat/kw rule; the double-spend-previous-attempts check |

**IT2: Bitcoin side** (lane IT-B)
| Task | Files | Acceptance |
|---|---|---|
| IT2-T1 `PrevTxInspector` | `src/NLightning.Infrastructure.Bitcoin/InteractiveTx/PrevTxInspector.cs` (+ DI line) | Invalid tx, vout out of range, P2PKH/P2SH/bare scripts rejected, P2WPKH/P2WSH/P2TR accepted (NL-041); confirmed-input check through the chain source |
| IT2-T2 `InteractiveTxBuilder` | `.../InteractiveTx/InteractiveTxBuilder.cs` | **BOLT 3 Appendix G byte-exact**: inputs/outputs sorted by serial id, locktime 120, feerate 253, the vector's txid and signed tx (vector file under `test/NLightning.Integration.Tests/BOLT3/Vectors/`, README with the upstream commit) |
| IT2-T3 Wallet contributor | `src/NLightning.Application/InteractiveTx/WalletInteractiveTxContributor.cs`, uses fee-input reservations | Selects confirmed UTXOs when required; change above dust; the reservation is released on `tx_abort` and kept after our `tx_signatures` until an input is spent (IT-ABT-01); `SignWalletTransaction(tx, reservationId, ...)` signs only our inputs |

**IT3: Wire and persistence** (lane IT-C, migration owner `AddInteractiveTxSessions`)
| Task | Files | Acceptance |
|---|---|---|
| IT3-T1 TLVs | `SharedInputTxIdTlv` (type 0 on 66), `SharedInputSignatureTlv` (type 0 on 71): Domain TLVs, converters, `TlvConverterFactory`, strict known sets in `TxAddInputMessageTypeSerializer`/`TxSignaturesMessageTypeSerializer`, `TlvStreamSerializerTests` samples | Round trips; unknown even TLV rejected |
| IT3-T2 Migration + repository | `Entities/Channel/InteractiveTxSessionEntity.cs`, configuration, `NLightningDbContext`, migration ×3, `InteractiveTxSessionDbRepository`, `UnitOfWork`, `CrashingUnitOfWork` | `HasPendingModelChanges` ×3 false; SQLite round trip of every field; Postgres case in `Docker/PostgresTests` |

**IT4: Driver and handlers** (lane IT-D)
| Task | Files | Acceptance |
|---|---|---|
| IT4-T1 `InteractiveTxDriver` + host port | `src/NLightning.Application/InteractiveTx/**` | Drives a session per channel under the lock; persists at "CS sent"; host callbacks for shared spec, commitment step, completion |
| IT4-T2 Handlers | `src/NLightning.Application/Channels/Handlers/Tx{AddInput,AddOutput,RemoveInput,RemoveOutput,Complete,Signatures,InitRbf,AckRbf,Abort}MessageHandler.cs` + `ChannelManager` cases | No negotiation in progress → `tx_abort` (for 66-73) or ignore (`tx_abort` itself, after echo rules); malformed → `tx_abort` not a channel failure |
| IT4-T3 In-process harness | `test/NLightning.Application.Tests/InteractiveTx/InteractiveTxHarnessTests.cs` with a **test host** (a dummy "shared funding" purpose) | Two nodes build a tx with both contributing; remove/re-add; abort in every state; RBF with a higher feerate double-spends the first; the resulting tx is valid in a regtest bitcoind (Integration test with `SilentZmqEndpoint`) |

**Proof IT:** Appendix G byte-exact, the harness, and **no Docker proof** (nothing dependent exists yet), unless wave DF runs next (its Proof DF is the IT interop proof).

**Estimate:** one wave, **~3 h, 4 lanes** (IT-A engine, IT-B Bitcoin side, IT-C wire+schema, IT-D driver+handlers).

### Wave qit record (waves Q and IT run as one wave; integrated at `b7d14056`, 2026-09-27)

Contracts Q-0/IT-0 at `9355ad92`. Seven lanes, each with a review/fix step; IT-C shipped `AddInteractiveTxSessions` (all three providers). Ledger: NL-019 and NL-219 fixed, NL-041 fixed, NL-042 and NL-037 partial, new NL-467..NL-474. Full gate record in `ABCD_ROADMAP.md` "Wave qit".

| Task | Status | `wip/fafo` SHAs |
|---|---|---|
| Q-0, IT-0 contracts | done | 9355ad92 |
| Q1-T1 route `stfu` | done (`StfuMessageHandler` lives in `Application/Channels/Handlers/`, not `Channels/Quiescence/`, owned by Q-A; it is the only `IChannelMessageHandler<StfuMessage>`, a registration test pins it) | 3380b680, 3f230897 |
| Q1-T2 `QuiescenceRules` | done (78 table cases; an unsolicited `stfu(0)` is a violation, as CLN) | 9303e6d4 |
| Q1-T3 `QuiescenceService` | done; deviation: `ChannelStateTransitionService.CommitAsync` schedules the owed `stfu` (`IStfuReleaseScheduler.ScheduleRelease`) instead of publishing it inline, so it goes out after the `revoke_and_ack` the handler returns; `QuiescenceRules` applied at integration | 6534018d, ac3a1aa8, ef806980 |
| Q1-T4 update gate | done: send side in `ChannelOperationsService` (`ChannelQuiescentException`), receive side (peer `update_*` after its `stfu` → warning + close) wired by the integrator in `ChannelManager` | 5614a07f, b7d14056 |
| Q1-T5 timeout | done: a timed-out quiescence keeps blocking updates until its disconnection (Q-R-04), the close is repeated every `Timeout` | 6534018d, ac3a1aa8 |
| Q1-T6 harness | done (`QuiescenceHarnessTests` 9, `QuiescenceSwitchTests` 2); `stfu` still routed by reflection in `QuiescenceTestPair` (NL-470) | 5b9d079e |
| D2 probe exit | done: a `Probe` we started is ended with our `tx_abort` through `IInteractiveTxDriver.AbortQuiescence` | b7d14056 |
| Proof Q (a)-(c) | done, adapted (below); (d) LND not attempted | 34664a14, 65a20108, b7d14056 |
| IT1-T1..T4 engine | done; deviations: dust from Bitcoin Core thresholds (NL-473), separate serial-id namespaces for inputs and outputs (as CLN, Eclair, LDK), random serial ids, a peer's P2TR key-path input charged a 66 WU witness, after our `tx_signatures` a peer `tx_abort` is neither echoed nor forgotten | 081d599f, 8ee516c5, 21be3935 |
| IT2-T1..T3 Bitcoin side | done (Appendix G byte-exact; durable IT-ABT-01 guard; startup sweep of orphaned `itx:` reservations) | 8ed3aaa8, acd0f711, ef806980 |
| IT3-T1, T2 wire + schema | done (`InteractiveTxSessions` has no FK to `Channels`; `DeleteByChannelIdAsync` must be called by whoever forgets a channel) | 940baca2, 99be2451, f513a369 |
| IT4-T1..T3 driver + handlers | done; negotiations ended on disconnection under each channel's lock at integration | 471d7e6b, 633980df, 65fb5b21, f03470fb, c502fdb6 |

Proof Q against CLN v26.06.8 (`ClnQuiescenceTests`, 5/5): `stfu_channels`/`abort_channels` answer error 354 ("Peer does not support splicing") while we advertise no splice bit, so (a) starts CLN's quiescence with `dev-quiesce` and ends it with our `tx_abort`; CLN acks, restarts channeld in place and resumes on the same connection without `channel_reestablish`; the case of CLN's `tx_abort` and our echo is proven in-process only (NL-468). (c) CLN holds back a fulfill queued while quiescent until the next reestablish (NL-467); the proof reconnects if the payment is still in flight 20 s after `tx_abort`. (c) also covers an HTLC held by our `basic_mpp` switch across the quiescence. CLN has no idle quiescence timeout (120 s idle, still connected). Not covered on the wire: a CLN add not yet revoked at our request, the Q-R-03 timeout with HTLCs pending (in-process only; NL-470).

### Optional wave DF: dual-funded open (reuses IT)
DF1 v2 channel id + `open_channel2`/`accept_channel2` handlers; DF2 first commitment (zero HTLCs) + `tx_signatures` + funding confirmation + reestablish `next_funding` for opens; DF3 `option_dual_fund` out of experimental after **Proof DF**: CLN opens a dual-funded channel to us (`fundchannel` against a v2 peer; CLN's dual funding may need `--experimental-dual-fund`, **unverified** for v26.06.8) and we open one to CLN; RBF of an unconfirmed open. **~3 h, 4 lanes.** Not a splicing prerequisite; it is listed because it proves IT against CLN before SP1 depends on it.

### Wave SP1: splicing core
**SP1-A: Wire** (lane SP1-A)
| Task | Files | Acceptance |
|---|---|---|
| SP1-A-T1 Messages 77/80/81/127 | payloads, messages, serializers, both factory dictionaries, `IMessageFactory.Create*` | Round trips; `s64` contribution sign handled; `start_batch` TLV 1 `message_type` strict set {1} |
| SP1-A-T2 TLV 5 on `channel_reestablish` | `MyCurrentFundingLockedTlv` + converter, strict set {1,5} | Round trip; B2-RE-W02 now parsed |
| SP1-A-T3 Inbound batch grouping | `src/NLightning.Application/Node/Managers/PeerManager.cs` inbound loop (grouping only), `CommitmentSignedBatch` record | SP-OP-04 rows as tests: size <= 1 ignored + warning, > 20 → warning + close, other channel → warning + close, missing `message_type` → sequential; a batch is handed to one handler call |

**SP1-B: Engine** (lane SP1-B)
| Task | Files | Acceptance |
|---|---|---|
| SP1-B-T1 `ChannelFunding`/`FundingSet` + per-funding specs | `src/NLightning.Domain/Channels/Splicing/**`, `Channels/Commitments/{ChannelCommitments,LocalCommit,RemoteCommit,RemoteNextCommit,CommitmentParams,CommitmentsResult}.cs` | I6 per funding; with one funding every existing engine test is unchanged (byte-identical outbound) |
| SP1-B-T2 Batched send/receive and one RAA | same | SP-OP-01, 03, 05, 06, 07 as engine tests |
| SP1-B-T3 Splice commitment step + lock | same | SP-CS-01/02 (same number, no RAA); `LockFunding` folds deltas, discards siblings |
| SP1-B-T4 Validation for all fundings | `Channels/Validators/UpdateValidator.cs` (and dust/fee policies' funding argument) | SP-I6: an add valid on the current funding but violating the reserve (D9) on the smaller pending splice-out funding is refused both ways |
| SP1-B-T5 Simulator | `test/NLightning.Domain.Tests/Channels/Commitments/CommitmentPairSimulator.cs` | Random splice-in/out, lock, discard, disconnect steps; SP-I2, I3, I6 after every step; 500 seeds in CI, 10k `Category=Long` |

**SP1-C: Signer + persistence** (lane SP1-C, migration owner `AddSpliceFundings`)
| Task | Files | Acceptance |
|---|---|---|
| SP1-C-T1 Per-funding signing info | `ChannelSigningInfo` (fundings list), `LocalLightningSigner.Splicing.cs` (new partial), `ILightningSigner` members from SP1-0 | Commitment and HTLC signatures per funding; funding key i derived deterministically (D5) and restorable from the SCB path; BOLT 3 Appendix C/F vectors still byte-exact on the initial funding |
| SP1-C-T2 Shared-input signature with SP-I1 | same | Refuses before a verified remote commitment signature for the new funding is registered; low-S; SIGHASH_ALL; key order in the witness |
| SP1-C-T3 Per-funding S1 (SP-I4) | same | Tests: sign n on F then n on F′ allowed; n+1 anywhere refused; reveal(n) refused; restored at registration |
| SP1-C-T4 Migration + repositories | entities, configurations, migration ×3 with the data step, `ChannelStateDbRepository`, `ChannelDbRepository` (current funding columns), `CrashingUnitOfWork` | `HasPendingModelChanges` ×3; the SQLite data test moves an existing channel's commitment rows under its funding txid; `ChannelRoundTripTests` with 3 fundings; crash injection at every splice save (I1/SP-I7) on SQLite |

**SP1-D: Negotiation** (lane SP1-D)
| Task | Files | Acceptance |
|---|---|---|
| SP1-D-T1 `SpliceRules` | `src/NLightning.Domain/Channels/Splicing/SpliceRules.cs` | SP-S-01/02, SP-R-01/02, SP-TX-01..05 as tables (each returns the requirement ID and the action: tx_abort / warning+close / error+fail) |
| SP1-D-T2 `SpliceService` + handlers | `src/NLightning.Application/Channels/Splicing/{SpliceService,SpliceInitMessageHandler,SpliceAckMessageHandler,SpliceNegotiationHost}.cs` + `ChannelManager` cases | The §3.5 flow both as initiator and acceptor over the IT driver; quiescence requested/ended (SP-Q-01); splice tx persisted as a broadcast row; watch added; D10 |
| SP1-D-T3 Splice harness | `test/NLightning.Application.Tests/Channels/Splicing/SpliceHarnessTests.cs` | SP-T-01 (message sequence exactly as `splicing-test.md`); splice-in and splice-out, each side initiating; payments during pending (batched CS both ways); lock; SP-T-02 |

**SP1-E: IPC + Docker** (lane SP1-E)
| Task | Files | Acceptance |
|---|---|---|
| SP1-E-T1 `splicein`/`spliceout` | Transport.Ipc DTOs, Daemon IPC + client handlers, Client CLI + printers | IPC round trips (`Daemon.Tests`); refused when 35/63 not negotiated, when shutdown sent, when a splice is unlocked |
| SP1-E-T2 **Proof SP1** | `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnSpliceTests.cs` | see below |

**Proof SP1** (Docker, CLN v26.06.8; our node with experimental 35 + 63):
- (a) **CLN splices in** (`splicein <our channel> 100000` or `splice_init`/`splice_update`/`splice_signed` — record which works): we accept as non-initiator with contribution 0; the splice tx confirms; both send `splice_locked` at depth; CLN's `listpeerchannels` shows the new funding txid and capacity; our `listchannels` agrees; payments both ways after.
- (b) **We splice in** (`splicein`): CLN accepts; same checks.
- (c) **We splice out** to a wallet address; the address receives the amount; the channel capacity drops by amount + our fee share.
- (d) **CLN splices out** (`spliceout`).
- (e) **Payments while pending**: after `tx_signatures`, before confirmation, 5 payments each way; CLN and we exchange `start_batch` + 2 CS per update (asserted from our logs/metrics); after lock, single CS again.
- (f) The splice tx is a valid 2-of-2 spend in bitcoind; the fee paid matches IT-S-03 within one vbyte.

**Estimate:** **~3 h, 5 lanes** (SP1-A..E). This is the heaviest wave; if a lane runs long, SP1-E's Docker proof moves to the start of SP2.

### Wave SP1 record (wave sp1, integrated at `3660bff2`, 2026-09-27)

Contracts SP1-0/DF-0 at `52338a14` (lane `5ad7acb4`). Seven lanes (SP1-A..E, plus SP1-F = wave DF's DF1/DF2 and SP1-G = per-channel routing policies), each with a review/fix step; SP1-C was the migration owner (`AddSpliceFundings`, all three providers, with the dual-funding columns and the `ChannelPolicies` table). Lane commits cherry-picked with `-x` in the order contracts → SP1-C → SP1-A → SP1-B → SP1-D → SP1-F → SP1-G → SP1-E. Ledger: NL-021 and NL-037 partial, NL-470 partial, new NL-475..NL-483 (NL-475, NL-476 fixed). Full gate record in `ABCD_ROADMAP.md` "Wave sp1".

| Task | Status | `wip/fafo` SHAs |
|---|---|---|
| SP1-0, DF-0 contracts | done | 52338a14 |
| SP1-A-T1 wire 77/80/81/127 | done; also fixed the `tx_init_rbf` locktime/feerate swap (NL-475) and made `funding_output_contribution` an s64 in satoshis (NL-476) | 830610fc, e9f17f6b |
| SP1-A-T2 TLV 5 on `channel_reestablish` | done (strict set {1,5}; parsed, processed in SP2-A) | e9f17f6b |
| SP1-A-T3 inbound batch grouping | done; grouping only with negotiated `option_splice` (else `start_batch` is dropped as before); a partial batch ends with its connection; we always take warning + close, never error + fail | 15099a4c, acbab68b |
| SP1-B-T1..T5 engine | done: `FundingSet`, per-funding specs, batched CS + one RAA, splice commitment step, `LockFunding`/`DiscardPendingFundings`, validation on every active funding, simulator splicing mode (500 seeds in CI, 10k `Category=Long` green). Deviation (SP-OP-06): members for a funding that is no longer active are ignored also while a splice is pending (BOLT 2 rationale, Eclair). Review: `start_batch` gated before reestablish, our shutdown held while a splice is unlocked, lenient receive reserve on a spliced funding (D9/Q3 open, NL-480), revoked fundings survive a discard or lock | c2ff9279, d37427ae, b50bdfbc, c7936b7e, 1cd7515d, 29b4ae25 |
| SP1-C-T1..T3 signer | done: funding key `m/0'` for i = 0 and `m/0'/i'` after (restorable from the index), per-funding signing, SP-I1 shared-input guard, per-funding S1; Appendix C/F still byte-exact | be14108b |
| SP1-C-T4 migration + repositories | done: `AddSpliceFundings` ×3 with a hand-written data step and a guarded `Down`; `ChannelFundingDbRepository`, `ChannelPolicyDbRepository`; revocation log read per funding; Postgres round trip in `Docker/PostgresTests` (SQL Server not run). Revocation rows for pending fundings are not written yet and the on-chain readers are unscoped (NL-479) | 01b70bf1, e33ae3d2, 48b0339f |
| SP1-D-T1 `SpliceRules` | done (65 table rows) | 7410e221 |
| SP1-D-T2 `SpliceService` + handlers | done: initiator and acceptor (contribution 0) over the IT driver, quiescence requested and ended, splice tx stored as a broadcast row with its watch, discard after `tx_abort`, our own splice tx left to the splice by the funding-spend routing; minimal `splice_locked` and `SpliceDepthWatcher` (ahead of SP2-B-T1) | 940da49c, 4577c997, c7badd17, cf4db235, 11eeced9, df9a869a, ccb98862, 9cbd7bac |
| SP1-D-T3 splice harness | done: SP-T-01 and SP-T-02 on the real engine, splice-in and splice-out each side initiating, batched CS both ways, the lock | 957c1519, a00b8cbe |
| SP1-E-T1 `splicein`/`spliceout` | done (`ClientCommand` 33/34) | 03691f28, d7c77659 |
| SP1-E-T2 **Proof SP1** | done: `ClnSpliceTests` 5/5 against CLN v26.06.8 on the integrated branch; it first failed 5/5 and found two integration bugs, fixed in 2ba4fe09: the locked splice never got its SCID (now from the confirmation's block and index) and the anchors were built on the original funding keys (CLN: "Bad commit_sig" after the lock). (d) CLN splices out to its own wallet (v26.06.8 refuses an external address) | b22e204e, d7c77659, 2ba4fe09 |
| DF1, DF2 (lane SP1-F) | done: `ChannelIdV2`, `DualFundingRules`, `open_channel2`/`accept_channel2`, `DualFundedOpenService`/`DualFundHost`, first commitment, `tx_signatures`, `channel_ready`, `next_funding` retransmission, RBF of an unconfirmed open (off by default, `Node:DualFund:AllowRbf`; refused for public or confirmed opens); `openchannel --dual-fund`; **Proof DF** (`ClnDualFundTests`) green. DF3 not done (`OptionDualFund` stays experimental) | 966cad6e, c9603fb9, 27cca704, 7b325699, 5e49fdc3, d90dd68c, d618dbf3, 7c109ca2, d934a566, 01bc76e0, e45b61b4, c6a4a779, 17066201 |
| Channel policies (lane SP1-G) | done: `setchannelpolicy`/`getchannelpolicy` (`ClientCommand` 35/36), a fresh `channel_update` on change, per-channel forwarding checks, the effective policy in `listchannels`, replaced policies honoured for 10 minutes; Docker `ChannelPolicyFlowTests`/`ChannelPolicyPublicFlowTests` against LND | af9efdd7, 2859dfb1, 24d3a08e |
| Integration | registrations (`AddDualFundingServices`, splice/dual-fund options, splice and policy IPC), `ChannelPolicyStore.LoadAsync` before `PeerManager` and `SpliceDepthWatcher.CatchUpAsync` after the chain monitor (daemon and `NLightningTestNode`), the SCID at the lock and anchors on the current funding keys | d778d100, 2ba4fe09, 3660bff2 |

Carried to SP2: SG7 reestablish (SP-RE, TLV 5 processing, retransmission of splice CS/`tx_signatures`), the full `splice_locked` rules, announcement and SCID map (SG10, NL-478 for the announcement keys), BOLT 5 across fundings (SG6 classifier, NL-479), `listchannels` fundings, Proof SP2; follow-ups NL-480, NL-483; NL-481 goes with SPR.

### Wave SP2: reestablish, lock, gossip, on-chain
**SP2-A: Reestablish** (lane SP2-A)
| Task | Files | Acceptance |
|---|---|---|
| SP2-A-T1 Planner | `src/NLightning.Domain/Channels/Reestablish/{ReestablishPlanner,ReestablishLocalState,ReestablishPlan}.cs` | SP-RE-01..06 table tests; B2-RE-25 now: `next_funding` unknown → `tx_abort`; known → CS/`tx_signatures` retransmission per flags; SP-RE-05 channel_ready rule |
| SP2-A-T2 Handler + retransmission | `ChannelReestablishMessageHandler.cs`, `ReestablishService.cs` | Retransmitted splice CS and `tx_signatures` byte-identical to the originals (from `InteractiveTxSessions`); `my_current_funding_locked` processed as `splice_locked` |
| SP2-A-T3 Conformance flows | `test/NLightning.Application.Tests/Channels/Splicing/SpliceConformanceTests.cs` | **SP-T-03..SP-T-11**, each asserting the exact message sequence of `splicing-test.md` and the final active commitments; plus a crash (not just a disconnect) at every SP-I7 save |

**SP2-B: Lock and gossip** (lane SP2-B)
| Task | Files | Acceptance |
|---|---|---|
| SP2-B-T1 `SpliceDepthWatcher` + `splice_locked` handler | `src/NLightning.Application/Channels/Splicing/{SpliceDepthWatcher,SpliceLockedMessageHandler}.cs` | SP-LK-01..04; D8 depth; D11 fork case ignored |
| SP2-B-T2 SCID change | `ChannelModel` SCID, `IRetiredScidMap` implementation + its use in `HtlcSwitch.ResolveOutgoingChannel`, `ChannelUpdateService` (new SCID), invoices' `r` hints | D12: a forward to the old SCID within 72 blocks succeeds, after 72 fails `unknown_next_peer` |
| SP2-B-T3 Announcements | `ChannelAnnouncementService` (splice path, `OnShortChannelIdChanged` reuse), `SignChannelAnnouncement` with the funding's key | SP-G-01; peer's early splice `announcement_signatures` deferred; retransmit flag bit 0 honoured both ways |

**SP2-C: On-chain** (lane SP2-C; `AddSpliceLockedState` migration owner **only if needed**)
| Task | Files | Acceptance |
|---|---|---|
| SP2-C-T1 Watch + classify | `OnchainChannelWatcher.cs`, `FundingSpendClassifier.cs`, `FundingSpendKind.Splice` | A pending splice tx spending the current funding → `Splice`, channel stays Open, no resolution; any other spend classified against the right funding |
| SP2-C-T2 Force close with pending splice | `ChannelFailureService.cs` | Commitment for the current funding broadcast; if the splice confirms instead, the commitment for that funding is broadcast (SP-I4) |
| SP2-C-T3 Resolvers per funding | `Onchain/Resolvers/{Local,Remote,Revoked}CommitResolver.cs` (funding argument), `RevokedCommitments` per funding | SP-I5: a revoked commitment of a **discarded** funding is punished |
| SP2-C-T4 Reorgs | `Onchain/Reorg/**`, `SpliceDepthWatcher` | Splice tx reorged before lock → pending again; after lock → CRITICAL alert, channel keeps operating; wallet reservation rollback |

**SP2-D: IPC, features, Docker proofs** (lane SP2-D)
| Task | Files | Acceptance |
|---|---|---|
| SP2-D-T1 `listchannels` fundings | IPC DTO + printer | Shows current/pending/locked with depth and SCID |
| SP2-D-T2 **Proof SP2** | `Docker/Interop/Cln/ClnSpliceReestablishTests.cs`, `Docker/Onchain/OnchainSpliceTests.cs` | see below |

**Proof SP2** (Docker):
- (a) **Disconnect mid-splice** against CLN: kill the connection after our CS, after CLN's CS, after one `tx_signatures`, after both (SP-T-03..06 against a real peer); the splice completes after reconnect; **restart our node** in the same four places (crash consistency); **restart CLN** once.
- (b) **Concurrent `splice_locked` over a disconnect** (SP-T-08): mine to depth while disconnected; `my_current_funding_locked` completes the lock.
- (c) **Public channel re-announced**: a public NLightning–CLN channel is spliced; after 6 blocks both sides exchange `announcement_signatures` for the new SCID; LND 0.20 (connected to CLN, in the shared LND fixture or a CLN-side `connect`) learns the new `channel_announcement` and `describegraph` shows the new SCID while the old one is removed after 72 blocks (**unverified** that LND 0.20 accepts a splice announcement without special handling; record).
- (d) **On-chain** (`OnchainSpliceTests`, own process): force close while a splice is pending (current funding's commitment confirms; pending discarded; wallet inputs released); force close where the **splice** confirms first (our commitment for the splice funding confirms; resolution completes); a **revoked commitment of the pre-splice funding** broadcast by a test peer (NLightning test node with a hook, as the O5 proofs do) after the lock is **reorged out** → penalty.
- (e) **Reorg of a pending splice** (`invalidateblock`) and of a locked splice (below `minimum_depth` on regtest with depth 3).

**Integration:** after Proof SP2, `OptionSplice` and `OptionQuiesce` Optional by default and out of `ExperimentalFeatures` (D13); `BOLT_COVERAGE.md`, `REPO_MAP.md`, root `CLAUDE.md` updated.

**Estimate:** **~3 h, 4 lanes** (SP2-A..D).

### Wave SP2 record (wave sp2, integrated at `31950b81`, 2026-09-27)

Contracts SP2-0 at `81e337f8` (lane `3560f3a9`). Six lanes (SP2-A..D as planned, plus SP2-E = static channel backups and peer storage across splices (NL-478) and SP2-F = the day-0 Docker proofs and `DAY0_RUNBOOK.md`), each with a review/fix step; SP2-C was the migration owner and shipped no migration (the schema already had what it needed). Lane commits cherry-picked with `-x` in the order contracts → SP2-C → SP2-A → SP2-B → SP2-E → SP2-D → SP2-F. Ledger: NL-478 and NL-479 fixed, NL-021 and NL-037 partial, new NL-484..NL-501 (NL-484, NL-487 fixed; NL-485 a duplicate of NL-472). Full gate record in `ABCD_ROADMAP.md` "Wave sp2". **D13 is not applied**: `OptionSplice`, `OptionQuiesce` and `OptionDualFund` stay No and experimental.

| Task | Status | `wip/fafo` SHAs |
|---|---|---|
| SP2-0 contracts | done | 81e337f8 |
| SP2-A-T1 planner | done: `next_funding` and `my_current_funding_locked` built and answered (SP-RE-01..06, 47 table cases); deviation: `bolt02/splicing-test.md` still draws `next_commitment_number` = the current number where BOLT 2 uses `retransmit_flags` bit 0; we send per BOLT 2 and accept both | 059383f4 |
| SP2-A-T2 handler + retransmission | done: splice CS and `tx_signatures` retransmitted byte-identical (from the stored rows after a restart), `my_current_funding_locked` processed as `splice_locked`, unknown `next_funding` answered through the driver's `tx_abort` echo guard; the dual-funded open uses the same path | 059383f4, a887c488, ff87bd93 |
| SP2-A-T3 conformance flows | done for SP-T-03..11 on the real engine (`SpliceConformanceTests`), with restart variants for SP-T-03, SP-T-06 and both CS lost (990d3381); SP-T-04/05/08 restart variants and a crash at every SP-I7 save not added (NL-496) | a887c488, ff87bd93, 990d3381 |
| SP2-B-T1 `splice_locked` | done: lock only on the same txid both ways, duplicates ignored, unknown txid warning + close, other RBF candidate remembered (D11), `HandlePeerFundingLockedAsync` (SP-RE-04); SP-LK-04's receive side goes with SPR (NL-489) | 64374dd0 |
| SP2-B-T2 SCID change | done: `RetiredScidMap` (72 blocks, D12; alias-only channels never retire their real SCID), `ChannelUpdateService` follows the new SCID, invoice hints read the current SCID; the hosts load the map before the peers start (a31c6c0d, NL-487) | 64374dd0, 505a3b90, a31c6c0d |
| SP2-B-T3 announcements | done: current funding keys (NL-478), early splice halves deferred, re-announced at 6 confirmations, halves reset in the lock's own save; bit 0 of TLV 5 handled by SP2-A (the funding row's received flag is never set, NL-488); a late half for the old SCID still gets a warning (NL-490) | 64374dd0, 505a3b90 |
| SP2-C-T1 watch + classify | done: `ClassifyAny` over every funding, a splice never a close, the new funding watched from our splice CS's save, mempool reaction across fundings | 7bfeb108, 82b98af3, c8d4257d |
| SP2-C-T2 force close with a pending splice | done: our commitment on whichever funding confirms (SP-I4, `ISpliceCommitmentBroadcaster`), a close reorged out while its discarded splice confirms retired in one save | 82b98af3, d5e13781 |
| SP2-C-T3 resolvers per funding | done: revocation log per funding and pending slots in step (NL-479), resolvers and penalty data source on the spent funding, fallback rebase of another funding's entry; `SignedOnFundings` not persisted (NL-494) | 768f8642, 82b98af3, 2b6910ac, d5e13781 |
| SP2-C-T4 reorgs | partial: locked-splice reorg alert, pending splice reorg idempotent, replaced funding without a watch reported; in-memory alert, `SpliceDepthWatcher` reorg handling and wallet reservation rollback open (NL-492, NL-493) | 2b6910ac, ba5bb413, d5e13781 |
| SP2-D-T1 `listchannels` fundings | done: fundings (IPC key 23) and retired SCIDs (24) with depth and SCID | 71df7272, 1339ed01 |
| SP2-D-T2 **Proof SP2** | done: `ClnSpliceReestablishTests` 11/11 against CLN v26.06.8 ((a) four cut points x {reconnect, restart}, (a') CLN restarted mid-splice, (b) lock over a disconnect, (c) re-announcement, forwards to the new and the retired SCID, `WIRE_UNKNOWN_NEXT_PEER` after 72 blocks); `OnchainSpliceTests` 5/5 ((d), (e)). Not covered: LND 0.20 learning the spliced channel (NL-496). Integration fixed the restart cases (990d3381, NL-484) and the (c) gossip flush and CLTV margin (7abc96b2) | d786e4f8, 1339ed01, b9bd0183, ba5bb413, 990d3381, 7abc96b2 |
| SP2-E backups across splices | done: SCB entries with the current funding, key index and pending splices, restore follows splice spends, backup rewritten at the lock, peer storage blob v2; CLN `ClnSpliceBackupRestoreTests` | 252063df, ba8ecfd6, 5f078964 |
| SP2-F day-0 proofs | done: `Day0FlowTests` (dual-funded public open, splices, restart mid-splice, policy, backups, close between two NLightning nodes) and `Day0UpgradeInPlaceTests` (pre-sp1 database migrated in place), `DAY0_RUNBOOK.md`; found NL-497 and NL-498 | c8d6ad6b, 3d0240ee, af6b1bcd, db276852 |

Carried: wave SPR (NL-481, NL-489), D13 (Proof SP2 is green; the decision to advertise `option_splice`/`option_quiesce` Optional remains, ideally after the LND check in NL-496 and NL-477), follow-ups NL-480, NL-483, NL-488, NL-490, NL-492..NL-497.

### Wave SPR: splice RBF
| Task | Lane | Files | Acceptance |
|---|---|---|---|
| SPR-T1 RBF on a pending splice (both directions) | SPR-A | `SpliceService` (RBF path), `TxInitRbf/TxAckRbfMessageHandler`, `SpliceRules` (tx_init_rbf splice rows) | SP rules for `tx_init_rbf`/`tx_ack_rbf`: quiescent + quiescence initiator, not after `splice_locked`, feerate rule (IT-RBF-01), >10 attempts high feerate, "recently created" → `tx_abort`; the attempt double-spends the previous; RBF total fee >= previous (SP-TX-05) |
| SPR-T2 Many candidates | SPR-A | engine and `FundingSet` | Batches up to `N + 1 <= 20`; our own cap `Splice:MaxRbfAttempts` (default 8) keeps batches small; lock of any candidate discards the rest |
| SPR-T3 `bumpsplice` + `SweepScheduler`-style auto-bump | SPR-B | IPC, `Onchain/Fees/SweepScheduler.cs` (splice target) | Operator bump; optional auto-bump after `Splice:AutoBumpAfterBlocks` |
| SPR-T4 **Proof SPR** | SPR-C | `Docker/Interop/Cln/ClnSpliceRbfTests.cs` | We RBF our splice; CLN RBFs its splice (CLN "a splice on a channel with a pending splice performs an RBF", #8021); payments during 3 candidates (batch of 4 CS); the lower-fee candidate never confirms; reconnect with RBF pending (SP-RE with RBF) |

**Estimate:** **~2 h, 3 lanes**.

### Wave SPR record (wave spr, integrated at `a0800ac2`, 2026-09-28)

Contracts SPR-0 at `2a386cb8` (lane `b72a42ea`: `ISpliceService.BumpAsync`/`SpliceBumpRequest`, `FundingSet` RBF sibling API, `SpliceRules` RBF signatures, `ISpliceAutoBumper`, `bumpsplice` DTOs, `RemoteCommit.WithSignedOnFundings`). Five lanes, each with a review/fix step: SPR-A (RBF protocol), SPR-B (bump), SPR-C (Proof SPR), plus SPR-D = day-0 hardening and the wave's migration owner (`AddSpliceHardening`, all three providers: `Commitments.SignedOnFundings`, `Peers.IsInboundOnly`; guarded `Down`) and SPR-E = day-0 follow-ups (NL-490, NL-492, NL-493, NL-486, the LND observer proof). Cherry-picked with `-x` in the order contracts → SPR-D → SPR-A → SPR-E → SPR-B → SPR-C, no conflicts. Ledger: NL-481, NL-486, NL-489, NL-490, NL-492, NL-494, NL-495, NL-497 fixed, NL-488, NL-493, NL-496 partial, new NL-502..NL-515 (NL-503, NL-506 fixed). Gate record in `ABCD_ROADMAP.md` "Wave spr". **D13 is not applied.**

| Task | Status | `wip/fafo` SHAs |
|---|---|---|
| SPR-0 contracts | done | 2a386cb8 |
| SPR-T1 RBF on a pending splice (both directions) | done: sender violations refused; receiver MUSTs (not quiescent, sender not the quiescence initiator, after the sender's `splice_locked` = SP-LK-04, zero-conf, a splice-out above the sender's balance) warning and close; `tx_abort` for nothing pending, a negotiation running, feerate below max(floor(25/24 x prev), prev + 25), `Splice:MinRbfInterval`, more than 10 attempts below the quick estimate, a batch above 20, a feerate above `Splice:MaxFeeratePerKw`; SP-TX-05; our contribution rebuilt from the latest attempt (negative for a splice-out, NL-481) and our share of a peer's RBF capped (`Splice:MaxRbfFeeShareSatoshis`, half of what we move); the bumped attempt stays Pending until the lock; a splice RBF always sends `funding_output_contribution`, 0 included (NL-503, found against CLN). Not supported: fresh wallet inputs in an RBF (NL-510) | 7a044d49, 9d13d0de, f3635ed7, c1e20033, 96d32442 |
| SPR-T2 many candidates | done: `FundingSet.AddRbfSibling`/`Siblings` (IT-RBF-01 feerate, fewer than 20 active), our cap `Splice:MaxRbfAttempts` in `CheckSendRbf`, `start_batch` over every attempt, the lock discards and abandons the siblings; discarded siblings' wallet inputs released at irrevocable depth by SPR-E's `DiscardedSpliceReservations` (NL-492) | 7a044d49, 9d13d0de, f9c6b7f0, 8566caf2 |
| SPR-T3 `bumpsplice` + auto-bump | done: `bumpsplice <channel_id> <feerate_per_kw> [--max-fee-sat <sats>]` (ClientCommand 37); `SpliceAutoBumper` (a new `ISpliceAutoBumper`, not a `SweepScheduler` purpose: a bump is a new negotiation), off unless `Splice:AutoBumpAfterBlocks` > 0, with `AutoBumpMaxFeeratePerKw` 25,000, `AutoBumpMaxFeeSat` 100,000 and `AutoBumpMaxWait` 120 s; started after the chain monitor and the peers in both hosts; a splice stopped at CommitmentSigned answered with `SpliceIpcResponse.Note` (NL-506). Open: in-memory state (NL-507), refusals not proven through the real service (NL-508), config template keys (NL-515) | d34ab24b, a1e2b6c5, 9d017a7f, 8a3ad0a3, fd704b26 |
| SPR-T4 **Proof SPR** | done: `ClnSpliceRbfTests` 5/5 against CLN v26.06.8 ((a) we bump our splice, (b) CLN bumps its splice through `splice_init`, (c) CLN's same-feerate RBF refused with `tx_abort` naming the feerate rule, driven through CLN's `splice_init` since CLN fails the channel after a refused `splicein` RBF (NL-502), (d) three attempts with batches of four, (e) a bumped splice across a disconnection and a restart); `Day0FlowTests` step 9 (bump between two NLightning nodes, LND sees the new SCID); CLN deviations NL-511 | c5943068, e25249ea, b7b23099, 96d32442 |
| SPR-D day-0 hardening (migration owner) | done: `SignedOnFundings` persisted (NL-494), the current funding in `GetSigningInfo` (NL-495), loopback inbound peers saved as inbound-only and their channels registered (NL-497; non-loopback residue NL-514) | e86bb0c4, a3748f4d, 84f5696c, 1304b5f6, 900810a8, a9721c49, e0b8a7a9 |
| SPR-E day-0 follow-ups | done: late half for a retired SCID ignored (NL-490), discarded splice reservations released and recorded funding spends replayed (NL-492 fixed, NL-493 partial), CLN fee cases order-independent (NL-486), `SpliceLndObserverTests` (LND 0.20 observes a spliced channel, NL-496 partial) | c51fe99a, f9c6b7f0, de620e7b, 7c4bbde5, 8566caf2, 12d9e451, a0800ac2 |

Carried: D13 (Proofs SP2 and SPR green and the LND observer check done; settle NL-477 and decide), follow-ups NL-480, NL-483, NL-488, NL-493, NL-496, NL-502, NL-507..NL-511, NL-514, NL-515.

### Wave d13 record (owner decision D13 of 2026-09-28; branch `wip/fafo-d13` from `29ce3126`)

One lane, no migration. Ledger: NL-021 and NL-042 fixed, NL-037 DF3 done (the epic stays partial for the RBF of v2 opens and NL-473/NL-474), new NL-520 (fixed).

| Task | Status | SHAs |
|---|---|---|
| D13 features | done: `FeatureOptions.OptionSplice`, `OptionQuiesce` and `DualFund` default to Optional and left `ExperimentalFeatures` (attribution_data is the only one left). BOLT 9 (09-features.md, 2026-09-28) lists no dependency for 28/29, 34/35 or 62/63, so `FeatureSet` is unchanged; D14 still requires 35 and 63 negotiated at use. Tests of legacy behaviour pin the features off explicitly (as tests pin `OptionAnchors = No`); `Day0Harness.EnableDay0Features` now only asserts the defaults with `AllowExperimentalFeatures` off. `openchannel` stays v1 unless `--dual-fund`; the daemon template writes no feature keys (defaults) | f30f3be3 |
| NL-520 splice RBF recency | done: BOLT 2's "another RBF attempt has been created recently" (SHOULD `tx_abort`, no number) is `SpliceRules.IsLastAttemptRecent`: recent until `Splice:MinRbfBlocks` (default 1) new blocks were processed since the latest attempt was created, whose height is derived from its broadcast row's `FirstBroadcastHeight` (same save as the signed session row: survives restarts, no migration). `Splice:MinRbfInterval` (was the rule, 1 min) stays a supported key, null by default; when set it replaces the block rule (the live Mutinynet nodes keep 5 s). The dual-funded RBF has no BOLT 2 recency rule (and is off by default): unchanged | f30f3be3 |
| Proofs | rules table (14 rows), harness (same block `tx_abort`, one block later accepted), config template test, `Day0FlowTests` step 9 (bump in the same block refused by B, after one empty block accepted) | f30f3be3 |

Gate (net10.0; Docker in the SDK container, SQL Server skipped): Release and Release.Native builds with the 5 baseline CS86xx, SDK 11 build of net10.0 + net11.0 (0 errors), `dotnet format` clean, non-Docker green (NL-466 flake green alone). Docker: CLN 64/70 in two full runs (NL-521, NL-522; the classes alone 12/12; `ClnQuiescenceTests.Given_OurHtlcInFlight_*`, NL-477, now passes), gossip 28/28, Day0 3/3, `SpliceLndObserverTests` 1/3 (NL-523; the pre-D13 build 2/2), LND suite 70/71 Postgres only (NL-524, the class 5/5 alone), on-chain legacy + anchors 47/47, `BackupRestoreFlowTests` 6/6, ABCD 3 x 10/10. Behaviour seen with peers: every Docker node now advertises 29/35/63; LND 0.20 keeps opening v1 channels to us and CLN's main fixture (no `--experimental-dual-fund`) too, so no v1 proof changed path; CLN now sees our nodes as splicing peers in every class.

### Lane rbf record (2026-09-28; branch `wip/fafo-rbf` from `wip/fafo` at `2b5dffd2`)

No migration. Ledger: NL-521 and NL-522 fixed, new NL-526 and NL-527.

| Task | Status | SHAs |
|---|---|---|
| NL-521 changed contribution in a dual-funded RBF | done: the driver hands `tx_ack_rbf` to the host (`IInteractiveTxHost.OnRbfAcknowledgedAsync`); either side's new `funding_output_contribution` is checked and followed (capacity, balances, reserve, in-flight limit, the signer's pending funding locked at completion, the `Initial` funding row), restored when the attempt ends unsigned; `BumpAsync` may change ours; an accepter that funded nothing may contribute in a peer's RBF. The splice ack side already read the peer's contribution (no change) | 7b8dacd6 |
| NL-522 CLN full-run failures | done: our SP-TX-05 fee reading is right (CLN's `splicein` paid its wallet part above the feerate its `splice_init` named); Proof SPR (b) derives CLN's bump feerate from its first attempt, Proof DF (c) picks feerates inside CLN's funder bounds, `ClnOfferPayTests` gives CLN's offers a path CLN introduces | f1068fce |

Gate (net10.0, Release): `dotnet format` clean, non-Docker green apart from the known flakes (NL-466, NL-472; green alone). Docker (CLN, host): `ClnSpliceRbfTests` 5/5, `ClnDualFundTests` 3/3, `ClnOfferPayTests` 4/4; one full CLN run 67/70 (+4 Explicit), the misses NL-526 (`ClnOfferReceiveTests` exact amounts vs NL-440's dummy hop, fails alone too). Dual-funded RBF stays off by default (`Node:DualFund:AllowRbf`).

### Lane dfrbf record (2026-09-28; branch `wip/fafo-dfrbf` from `wip/fafo` at `47b0ce4a`)

Owner decisions of 2026-09-28: fix NL-527; NL-526 with realistic dummy-hop fees and the records holding what we received; `Node:DualFund:AllowRbf` on by default if it tests OK. Migration `AddDualFundAttempts` (all three providers). Ledger: NL-526, NL-527, NL-528 fixed; new NL-529, NL-530, NL-531.

**What `AllowRbf` gated and why it was off.** Both directions of a dual-funded open's RBF: our `BumpAsync` as the opener and the peer's `tx_init_rbf` (either role), answered with `tx_abort` when off. It was turned off in the wave sp1 review because the channel kept the latest attempt's outpoint and peer commitment signature only and `ChannelManager.ConfirmFundingAsync` ignored which transaction confirmed: an earlier attempt confirming (any signed attempt may, BOLT 2) left the channel on an outpoint that never confirms with a first commitment signed for another funding, i.e. no unilateral close (funds dependent on the peer). Public channels were refused on top (the signer kept the first attempt's outpoint; obsolete since NL-521).

| Task | Status | SHAs |
|---|---|---|
| NL-527 our refused `tx_init_rbf` | done: `IInteractiveTxHost.OnRbfRequestEndedAsync` (peer's `tx_abort`, simultaneous `tx_init_rbf`, attempt not buildable after `tx_ack_rbf`, disconnection); the splice host keeps the no-op (its bump ends with the quiescence) | 0d15bdef |
| NL-526 dummy-hop amounts | done: no product change (invoices already record the HTLC set's amounts, `listinvoices` shows them; the final-hop checks see through our dummy hops); `ClnOfferReceiveTests` bounds the received amount by what a BOLT 4 introduction node forwards at the path's `blinded_payinfo` fee (measured exactly at the bound); in-process pin in `Bolt11BlindedInvoiceTests` | 51b40ea8 |
| NL-528 follow the confirmed attempt | done: per-attempt share and peer signature stored; `OnFundingConfirmedAsync` moves the channel (and the signer) to the attempt that confirmed and abandons a running RBF; early `channel_ready` deferred until our confirmation; restart mid-RBF restores the signed attempt from its row; the losing attempts' own inputs released at the irrevocable depth; public opens may be bumped; `AllowRbf` default true | 62a43729 |
| `bumpopen` (IPC 38) and proofs | done: CLI/IPC for the opener's bump; Proof DF extended: (c) through `bumpopen`, (d) CLN's `openchannel_bump` of its own open followed, (e) the first attempt mined after our bump (`generateblock`), both nodes follow; `Day0FlowTests` step 1 (b) bumps the public dual-funded open between two NLightning nodes | f4744680, bae1e17f |

Not done: the accepter starting an RBF (BOLT 2 MAY, NL-530); the losing attempts' watches stay pending (NL-529).

Gate (net10.0, Release): 0 errors, the 5 known CS86xx; Release.Native 0 errors; `dotnet format` clean; non-Docker 10,963 (Domain 3524, Application 2978, Integration 933, Serialization 613, Infrastructure 455, Bitcoin 1348, Bolt11 327, Daemon 785), green apart from `GossipGraphReloadTests` (NL-466, 3/3 alone). Docker: `ClnDualFundTests` 5/5, `ClnOfferReceiveTests` 4/4, `ClnOfferPayTests` + `ClnSpliceRbfTests` + `ClnCloseTests` + `ClnOfferReceiveTests` 17/17; full CLN runs: the first two 44/72 and 38/72 failed (CLN's fee floor raised by the dual-funding class's wallet sends; fixed by funding CLN at 1 sat/vB), the third 71/72 + 4 `Explicit` (the only miss `ClnCloseTests.Given_ChannelClnFunded_*` after the dual-funding class, 4/4 alone, NL-531; NL-477 passed); `Day0FlowTests` + `Day0UpgradeInPlaceTests` + `RouteBlindingFlowTests` + `Bolt11BlindedPathFlowTests` 9/9 (`scripts/run-gossip.sh`); `PostgresTests` 18/18 (the migration). SQL Server not run (standard cycle).

### Waves and lanes (for the multi-agent wave workflow)
| Wave | Lane | Files owned (exclusive) | Depends on | Proof |
|---|---|---|---|---|
| **Q** | **Q-A wire + rules** (Q1-T1, T2) | `Domain/Protocol/Messages/StfuMessage.cs`, `Domain/Channels/Quiescence/**` (not the service interface), `Infrastructure/Node/Services/PeerService.cs` (remove the stfu arm only), `ChannelManager.cs` (stfu case only); `DT/Channels/Quiescence/**`, `ST/Messages/StfuMessageTests.cs` | Q-0 | rules tables, routing test |
| Q | **Q-B service** (Q1-T3..T6) | `Application/Channels/Quiescence/**`, `Application/Channels/Services/{ChannelOperationsService,CommitScheduler}.cs` (gate only), `Domain/Node/Options/NodeOptions.cs` (quiescence keys), daemon template keys; `AT/Channels/Quiescence/**` | Q-0 | harness |
| Q | **Q-C Docker** | `IT/Docker/Interop/Cln/ClnQuiescenceTests.cs` | Q-A, Q-B (integrator runs after merge) | Proof Q |
| **IT** | **IT-A engine** (IT1) | `Domain/Protocol/InteractiveTx/**` (not `Interfaces/`), deletion of `Infrastructure/Protocol/Validators/Tx*Validator.cs`, `Infrastructure/Protocol/Interfaces/IInteractiveTransaction*.cs`, `Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs` and their tests; `DT/Protocol/InteractiveTx/**` | IT-0 | rule tables |
| IT | **IT-B Bitcoin side** (IT2) | `Infrastructure.Bitcoin/InteractiveTx/**` (+ DI line), `Application/InteractiveTx/WalletInteractiveTxContributor.cs`; `IT/BOLT3/AppendixGVectorTests.cs` + vector file; `BT/InteractiveTx/**` | IT-0 | Appendix G byte-exact |
| IT | **IT-C wire + schema** (IT3; **migration owner `AddInteractiveTxSessions`**) | the two TLVs (Domain, converters, factory, serializers' known sets), `Infrastructure.Persistence*/**`, `Infrastructure.Repositories/**`, `Tests.Utils/Mocks/CrashingUnitOfWork.cs`; their tests | IT-0 | round trips, `HasPendingModelChanges` ×3 |
| IT | **IT-D driver + handlers** (IT4) | `Application/InteractiveTx/**` (not the contributor), `Application/Channels/Handlers/Tx*MessageHandler.cs`, `ChannelManager.cs` (tx cases only); `AT/InteractiveTx/**` | IT-0 | harness |
| **SP1** | **SP1-A wire + batching** | serializers/payloads/messages for 77/80/81/127, TLV 5 and `message_type`, factories, `IMessageFactory`/`MessageFactory` (Create* only), `Application/Node/Managers/PeerManager.cs` (inbound grouping only); tests | SP1-0 | round trips, SP-OP-04 |
| SP1 | **SP1-B engine** | `Domain/Channels/Commitments/**`, `Domain/Channels/Splicing/{ChannelFunding,FundingSet}.cs` behavior, `Domain/Channels/Validators/**`, `Domain/Channels/Policies/**` (funding argument); `DT/Channels/Commitments/**` incl. simulator | SP1-0 | engine tests, simulator |
| SP1 | **SP1-C signer + schema (migration owner `AddSpliceFundings`)** | `Infrastructure.Bitcoin/Signers/LocalLightningSigner.Splicing.cs`, `ILightningSigner` splice members, `Domain/Channels/ValueObjects/ChannelSigningInfo.cs`, `Application/Channels/Services/{CommitmentSigningService,EngineCommitmentSignerPort}.cs`, `Infrastructure.Persistence*/**`, `Infrastructure.Repositories/**`, `CrashingUnitOfWork`; `BT/Signers/**Splic**`, `IT/Persistence/**Splice**`, `ChannelRoundTripTests.cs` | SP1-0 | signer tests, `HasPendingModelChanges` ×3, crash injection |
| SP1 | **SP1-D negotiation** | `Domain/Channels/Splicing/SpliceRules.cs`, `Application/Channels/Splicing/**`, `ChannelManager.cs` (splice cases only), `Application/DependencyInjection.cs` (one line); `AT/Channels/Splicing/SpliceHarnessTests.cs` | SP1-0 (fakes for B/C) | SP-T-01/02 harness |
| SP1 | **SP1-E IPC + Docker** | IPC files for `splicein`/`spliceout` (Transport.Ipc, Daemon, Client); `IT/Docker/Interop/Cln/ClnSpliceTests.cs` | SP1-0 | Proof SP1 |
| **SP2** | **SP2-A reestablish** | `Domain/Channels/Reestablish/**`, `Application/Channels/Handlers/ChannelReestablishMessageHandler.cs`, `Application/Channels/Reestablish/**`; `DT/Channels/Reestablish/**`, `AT/Channels/Splicing/SpliceConformanceTests.cs` | SP2-0 | SP-T-03..11 |
| SP2 | **SP2-B lock + gossip** | `Application/Channels/Splicing/{SpliceDepthWatcher,SpliceLockedMessageHandler,SpliceAnnouncementCoordinator}.cs`, `Application/Gossip/Announcements/**`, `Application/Gossip/Services/ChannelUpdateService.cs` (SCID switch), `IRetiredScidMap` + `HtlcSwitch.ResolveOutgoingChannel`; tests | SP2-0 | lock + SCID tests |
| SP2 | **SP2-C on-chain (migration owner if needed)** | `Application/Onchain/**`, `Domain/Onchain/**`, `Application/Channels/Safety/ChannelFailureService.cs`; `AT/Onchain/**Splice**` | SP2-0 | resolver tests |
| SP2 | **SP2-D IPC + proofs** | `listchannels` DTO/printer; `IT/Docker/Interop/Cln/ClnSpliceReestablishTests.cs`, `IT/Docker/Onchain/OnchainSpliceTests.cs` | all SP2 lanes | Proof SP2 |
| **SPR** | **SPR-A RBF protocol** | splice RBF paths in `Application/Channels/Splicing/**`, `Tx{InitRbf,AckRbf}MessageHandler.cs`, `SpliceRules.cs` (RBF rows); tests | SPR-0 | RBF harness |
| SPR | **SPR-B bump** | `bumpsplice` IPC, `Application/Onchain/Fees/SweepScheduler.cs` (splice target) | SPR-0 | IPC + scheduler tests |
| SPR | **SPR-C Docker** | `IT/Docker/Interop/Cln/ClnSpliceRbfTests.cs` | SPR-A, B | Proof SPR |

**Migration owners:** Q none; IT **IT-C** (`AddInteractiveTxSessions`); SP1 **SP1-C** (`AddSpliceFundings`); SP2 **SP2-C** only if unavoidable; SPR none. No other lane touches `Infrastructure.Persistence*` or `Infrastructure.Repositories` in that wave.

**Seams to reconcile at integration:**
- `ChannelManager.HandleChannelMessageAsync`: Q-A (stfu), IT-D (66-74), SP1-D (77/80/81) each add cases; SP1-A's batch arrives as a new `CommitmentSignedBatch` entry point.
- `ChannelOperationsService`/`CommitScheduler`: Q-B's gate, then SP1-B's batched signing through the port.
- `ICommitmentSigner`/`ICommitmentVerifier`: SP1-0 changes the signatures; SP1-B and SP1-C meet there.
- `FeatureOptions`: flipped by the integrator only (D2, D13).
- `ClientCommand`: the integrator appends (31+).
- (wave qit, accepted) `ChannelStateTransitionService.CommitAsync` → `IStfuReleaseScheduler.ScheduleRelease`: the owed `stfu` is published after the transition's replies, never inline.
- (wave qit) `IQuiescenceService.OnPeerDisconnected` has no caller; the service binds each quiescence to its connection instead. `IInteractiveTxSessionDbRepository.DeleteByChannelIdAsync` has no caller yet (NL-470).

### Effort summary
| Wave | Lanes | Estimate (wall-clock, same units as the recent waves) | Gate |
|---|---|---|---|
| Q | 3 | ~2 h | Proof Q (CLN) |
| IT | 4 | ~3 h | Appendix G + harness |
| DF (optional) | 4 | ~3 h | Proof DF (CLN v2 open) |
| SP1 | 5 | ~3 h (may spill ~1 h) | Proof SP1 (CLN) |
| SP2 | 4 | ~3 h | Proof SP2 (CLN + on-chain), then D13 |
| SPR | 3 | ~2 h | Proof SPR (CLN) |
| **Total** | | **~13 h** for Q→SPR (+3 h with DF); 5 waves | |

---

## 6. Requirements traceability matrix

Everything below is **MISSING** at `d929b879` except where noted. Prefixes: `DT/` Domain.Tests, `AT/` Application.Tests, `BT/` Infrastructure.Bitcoin.Tests, `ST/` Serialization.Tests, `IT/` Integration.Tests, `CLN/` `IT/Docker/Interop/Cln/…`, `OC/` `IT/Docker/Onchain/…`.

### 6.1 Quiescence
| ID | Level | Task | Test |
|---|---|---|---|
| Q-W-01 | MUST | Q1-T1 | `ST/Messages/StfuMessageTests` (exists; routing test added) |
| Q-S-01..04 | MUST | Q1-T2, Q1-T3 | `DT/Channels/Quiescence/QuiescenceRulesTests`; `AT/…/StfuMessageHandlerTests` |
| Q-R-01, Q-R-02 | MUST / SHOULD | Q1-T3 | `QuiescenceHarnessTests` (reply after drain) |
| Q-R-03 | MUST | Q1-T5 | `QuiescenceTimeoutMonitorTests` |
| Q-R-04 | MUST | Q1-T3 | disconnect test |
| Q-R-05 | — | Q1-T2 | simultaneous test |
| Q-R-06, SP-Q-01 | MUST | Q1-T3, SP1-D-T2 | harness: `tx_signatures` ends it; `tx_abort` ends it |

### 6.2 Interactive-tx
| ID | Level | Task | Test |
|---|---|---|---|
| IT-W-01..04 | MUST | IT3-T1 (TLVs); messages exist | `ST/Messages/Tx*MessageTests` |
| IT-S-01/02 | MUST | IT1-T1 | `DT/Protocol/InteractiveTx/InteractiveTxSessionTests` |
| IT-S-03 | MUST | IT1-T2, IT2-T2 | `CollaborativeFeeCalculatorTests`; Appendix G |
| IT-R-01 | MUST | IT1-T1, IT2-T1 | session tables; `BT/InteractiveTx/PrevTxInspectorTests` (NL-041) |
| IT-R-02/03 | MUST | IT1-T1 | session tables (4096 received, NL-219) |
| IT-R-04 | MUST | IT1-T1, IT1-T2 | `tx_complete` tables |
| IT-SIG-01..03 | MUST | IT1-T3, IT4 | `TxSignaturesOrderTests`; harness |
| IT-ABT-01 | MUST / SHOULD | IT1-T3, IT2-T3 | abort tables; reservation kept after our signatures |
| IT-RBF-01 | MUST | IT1-T4, SPR-T1 | `InteractiveTxRbfRulesTests` (#1327 rule) |

### 6.3 Splicing
| ID | Level | Task | Test |
|---|---|---|---|
| SP-W-01/02, SP-LK-01 wire, `start_batch` wire | MUST | SP1-A-T1 | `ST/Messages/{SpliceInit,SpliceAck,SpliceLocked,StartBatch}MessageTests` |
| SP-S-01/02, SP-R-01/02 | MUST | SP1-D-T1 | `DT/Channels/Splicing/SpliceRulesTests`; CLN SP1 (a)(b) |
| SP-TX-01..05 | MUST | SP1-D-T1, IT1 | rules tables; harness; CLN SP1 (f) |
| SP-CS-01/02 | MUST | SP1-B-T3 | `DT/…/SpliceCommitmentTests` |
| SP-CS-03, SP-SIG-02 | MUST | SP2-A | SP-T-03..06; CLN SP2 (a) |
| SP-SIG-01 | MUST | SP1-C-T2, SP1-D-T2 | signer tests (SP-I1); handler: missing/invalid/high-S → fail |
| SP-OP-01 | MUST | SP1-B-T4 | `UpdateValidatorTests` per funding; simulator SP-I6 |
| SP-OP-02 | MUST | done (NL-199) | existing |
| SP-OP-03..07 | MUST | SP1-A-T3, SP1-B-T2 | `AT/Node/…/BatchGroupingTests`; engine batch tests; CLN SP1 (e) |
| SP-LK-02..04 | MUST / SHOULD | SP2-B-T1 | `SpliceLockedMessageHandlerTests`; SP-T-02, SP-T-08 |
| SP-G-01/02 | MUST / SHOULD | SP2-B-T3 | announcement tests; CLN SP2 (c) |
| SP-RE-01..06 | MUST | SP2-A-T1/T2 | `DT/Channels/Reestablish/ReestablishPlannerSpliceTests`; SP-T-03..11 |
| SP-T-01..11 | — | SP1-D-T3, SP2-A-T3 | `AT/Channels/Splicing/{SpliceHarnessTests,SpliceConformanceTests}` |
| SP-I1..I8 | invariant | SP1-B-T5, SP1-C, SP2-C | simulator; signer tests; crash injection; OC splice proofs |
| B2-BATCH-01..07, B2-CS-S07, B2-CS-R04, B2-RE-13, B2-RE-26 (BOLT2 plan, now in scope) | MUST | SP1-A, SP1-B, SP2-A | as SP-OP / SP-RE rows |
| F-01..04 | — | Q-0, SP1-0, integration D13 | `FeatureOptionsTests` (35+63 required together at use; 155 never honoured) |

### 6.4 Interop proofs
| Proof | Peer | What it proves |
|---|---|---|
| Q (a)-(c) | CLN v26.06.8 | our `stfu` handling both ways, drain before reply |
| DF (optional) | CLN | IT engine against a real peer (v2 open) |
| SP1 (a)-(f) | CLN | splice-in/out both directions, batched CS while pending, lock |
| SP2 (a)-(b) | CLN | reestablish mid-splice, `my_current_funding_locked` |
| SP2 (c) | CLN + LND 0.20 observer | re-announcement, new SCID in a third node's graph |
| SP2 (d)(e) | NLightning test peer + bitcoind | force close / penalty / reorg across fundings |
| SPR | CLN | RBF both directions, batches > 2 |

---

## 7. Interop reality (as of 2026-09-27)

- **Core Lightning** (the only splicing peer in our fixtures, and the likely interop peer):
  - **Splicing is on by default since v26.04** (CHANGELOG 26.04 "Changed: Protocol: Splicing is enabled by default"; `experimental-splicing` deprecated, #9021). v26.04 added `splicein`/`spliceout` RPCs (#8857, #8856) and a `splice` feerate name (#8450). v26.06.x fixed `splicein`/`spliceout` aborting on high fees (#9109) and partial-sat balances (#9097). **Our fixture v26.06.8 therefore needs no extra flag** (verified from the changelog; the CHANGELOG on master lists up to 26.06.6, so 26.06.8's own notes are **unverified**).
  - **Spec compatibility:** CLN moved to spec message numbers in 24.11 (#7719, also funding-key rotation), became Eclair-compatible in 25.05 (#8021: "compatible with Eclair, incompatible with previous CLN versions"; a splice on a channel with a pending splice becomes an RBF), added `start_batch` in 25.09 ("support for `start_batch` in splicing makes us Eclair compatible!", #8335) and old-SCID routing (#8387, also 25.09), and made a breaking change to "`commitment_signed`s splice_info tlv's and `channel_reestablish`" in 25.12 (#8646, #8506) with "stricter conformance to Bolt spec for splice commitments" (#8463). That 25.12 change matches the final spec's `funding_txid` TLV and TLV 1/5 reestablish shape, and 26.04 turned splicing on by default after the 2026-03-23 merge, so **CLN v26.06.8 is expected to be spec-final**, but we have not run it: **(unverified until Proof SP1)**. The version-to-PR mapping above is read from the CHANGELOG section each entry sits in (verified 2026-09-27).
  - Quiescence: `option_quiesce` on by default since 24.11 (#7586); the flag was removed in 25.12 (#8523). CLN exposes `stfu_channels`/`abort_channels` (#6980) without `--developer` (verified in wave qit), but v26.06.8 refuses both with error 354 unless the peer supports splicing; `dev-quiesce` (developer only) works with any peer. As non-initiator CLN answers `stfu(0)`; on `tx_abort` it acks with `tx_abort` and restarts channeld in place (no disconnect, no reestablish); a disconnect also ends quiescence; no idle timeout. A fulfill CLN queued while quiescent is only sent after the next reestablish (NL-467).
  - Dual funding: supported, **(unverified)** whether on by default in 26.06 or still behind `--experimental-dual-fund`.
- **Eclair:** **v0.14.0** "contains the final version of channel splicing" (release notes: "support for the final version of splicing (#1160) that was recently added to the BOLTs"; APIs `splicein`, `spliceout`, `rbfsplice`), removed its prototype, and **dropped non-anchor channels**. Phoenix uses Eclair's splicing (lightning-kmp; its spec-final status **unverified**). Eclair used 8 confirmations by default since v0.13 (not scaled by amount "because it doesn't work with splicing"). Our Eclair suite is deferred by the owner, so Eclair is not a proof peer in this plan.
- **LDK:** splicing since **0.2** (2025-12-02, "Natively Asynchronous Splicing"; `ChannelManager::splice_channel`, inbound gated by `UserConfig::reject_inbound_splices`); **0.2.2** (2026-02-06) moved its prototype flag to bit 63 ("resolves a compatibility issue with eclair nodes due to the use of the same splicing feature flag (155)"); 0.2.3-0.2.6 carry splice fixes, including a **fee-inflation vulnerability when we initiate a splice** (0.2.6, #4905: "A malicious splice peer can no longer cause us to somewhat over-allocate fee when we contribute to a splice, with the over-allocation going to their output") and a reserve bypass on zero-fee-commitment channels (#4580). Both are lessons for our `tx_complete` checks (§9 risk 5). Our LDK suite is deferred.
- **LND:** **no splicing** in 0.20 (our fixture) or 0.21 (release notes list none; 0.21's production taproot channels key nonces "by funding TXID, laying the groundwork for splice support"). LND implements quiescence for dynamic commitments (0.20 adds `--htlcswitch.quiescencetimeout`, default 60 s); whether it advertises 35 and exposes a trigger is **(unverified)**. LND is a **graph observer** in Proof SP2 (c), not a splicing peer.
- **Consequence:** the Docker proofs of this plan run against **CLN v26.06.8 only**. Everything that CLN does not exercise (our own RBF of CLN-initiated splices, a peer that contributes to our splice, reorg handling) is proven in-process with `TwoNodeHarness` and against bitcoind.

---

## 8. Mapping to other plans
| This plan | Other plan | Relation |
|---|---|---|
| Q1 | BOLT2 N0-T4 (stop advertising quiesce), NL-019 | replaces the warning-and-disconnect answer |
| SP1-B | BOLT2 N4 engine, N4-T5 simulator, N5 persistence (I1-I12) | engine generalised to several fundings; invariants extended (SP-I*) |
| SP2-A | BOLT2 N7 `ReestablishPlanner` (B2-RE-*), NL-197 | TLV 1 behaviour and TLV 5 added |
| SP2-C | BOLT5 O2-O6 (watcher, classifier, resolvers, reorg), O7 anchors | per-funding classification and penalty data |
| SP2-B | BOLT7 G1 (own announcements), G2 (pruner 72 blocks), `ChannelUpdateService` | new SCID and re-announcement |
| IT, DF | NL-037 epic, BOLT2 "Channel Establishment v2" | DF closes NL-037 |
| SP1-C | rf1 R1 static channel backup (NL-426) | funding key derivation must stay restorable (D5) |

---

## 9. Risks (fund safety first)

1. **Signing commitments for several fundings** (SP-I2, SP-I3). A secret revealed while one funding's next commitment is missing, or an RAA after a partial batch, hands the peer a revoked commitment we cannot answer. *Mitigation:* `ReceiveCommit(batch)` is all-or-nothing; the RAA secret is derived only after the batch's save; the simulator runs splice steps with message loss and disconnects; crash injection at every save.
2. **Signing away the shared input too early** (SP-I1). A `shared_input_signature` sent before our signed commitment for the new funding is on disk means that if the splice confirms we hold no valid commitment for the new output. *Mitigation:* the signer refuses unless a verified remote signature for that funding is registered; the save order puts that signature on disk first; a recording-signer test asserts order.
3. **Signer invariants across fundings** (SP-I4). S1 was "one broadcast number per channel"; with fundings it must allow the same number on another funding (the splice may confirm instead of the current funding) and nothing else. *Mitigation:* SP1-C-T3 tests; the broadcast mark stores (number, funding txid) and is restored at registration.
4. **Persistence-before-send across a splice** (SP-I7). New messages that bind us: `splice_ack`, CS for the new funding, `tx_signatures`, `splice_locked`. Each has a save before the send; `InteractiveTxSessions` rows let reestablish retransmit byte-identical messages. *Mitigation:* crash injection on all three providers, SP-T-03..11, Proof SP2 (a) restarts.
5. **`tx_complete` economics.** A peer that inflates our fee share or takes our change (LDK 0.2.6 #4905), or splices out below reserve (LDK #4580), steals small amounts. *Mitigation:* our own contribution's fee is recomputed from the final tx and compared with our budget before we sign; reserve checks per D9 on both sides; RBF fee >= previous.
6. **Reorg of a splice transaction.** Before lock it is routine; after lock (deeper than `minimum_depth`) the old funding output is unspent again and old-funding commitments are valid spends. *Mitigation:* SP-I5 keeps every funding's revocation data until the splice is irrevocably deep; the old funding outpoint stays watched until then; `minimum_depth` >= 3 on mainnet (D8); a CRITICAL alert.
7. **Misclassifying the splice tx as a close** (SG6): without `FundingSpendKind.Splice`, the watcher could start resolving a live channel. *Mitigation:* SP2-C-T1 lands **before** any splice can reach the chain in a proof; until SP2, splicing stays experimental (D13) and SP1's Docker proof runs with the watcher change cherry-picked early if needed (integrator's call).
8. **Quiescence deadlocks:** two protocols needing quiescence, or a fulfill that cannot be sent while quiescent (an upstream HTLC then times out). *Mitigation:* fulfills and fails are queued, never dropped, and sent after quiescence ends; the 60 s rule and `IdleTimeout` bound it; quiescence is requested only by `SpliceService`.
9. **Batch framing** (`start_batch`): a peer that interleaves another channel's message, or a batch that crosses our `splice_locked`, must not desynchronise the inbound loop. *Mitigation:* grouping in the per-peer loop (D15), SP-OP-04/06 tests, and CLN SP1 (e).
10. **SCID change:** HTLCs in flight to the old SCID, invoices with old `r` hints, a public channel briefly "closed" in third-party graphs. *Mitigation:* D12 72-block retirement, re-issued hints, announcement at 6 blocks.
11. **Spec vs secondary sources:** lightningsplice.com links the old proposal (#863) and says "6 confirmations"; the BOLTs say "acceptable depth" and #1160 is final. We follow the BOLTs (D8) and ask Q4 in §10.
12. **CLN behavioural drift:** everything marked (unverified) in §7 is settled by Proofs Q/SP1/SP2/SPR; each proof logs CLN's version and the exact RPCs used.

### Critical files for implementation
- `src/NLightning.Domain/Channels/Commitments/ChannelCommitments.cs`, `CommitmentParams.cs`, `Channels/Validators/UpdateValidator.cs`
- `src/NLightning.Domain/Channels/Reestablish/ReestablishPlanner.cs`
- `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs` (+ new `.Splicing.cs` partial), `src/NLightning.Domain/Channels/ValueObjects/ChannelSigningInfo.cs`
- `src/NLightning.Application/Channels/Managers/ChannelManager.cs`, `Application/Node/Managers/PeerManager.cs` (inbound loop)
- `src/NLightning.Application/Onchain/OnchainChannelWatcher.cs`, `src/NLightning.Domain/Onchain/Classifiers/FundingSpendClassifier.cs`
- `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs`, `Entities/Channel/{CommitmentEntity,RevokedCommitmentEntity,ChannelEntity}.cs`
- `src/NLightning.Application/Gossip/Announcements/ChannelAnnouncementService.cs`
- `test/NLightning.Domain.Tests/Channels/Commitments/CommitmentPairSimulator.cs`, `test/NLightning.Application.Tests/Channels/Harness/TwoNodeHarness.cs`

---

## 10. Open questions for the CLN splicing lead

Concrete questions for CLN's splicing lead (via the owner), each tied to CLN **v26.06.8** as pinned in `ClnFixture`. Answers go into §7 and the proofs.

1. **(answered empirically in wave qit, see §7: yes, CLN resumes on our `tx_abort`; `stfu_channels` needs no `--developer` but needs a splicing peer)** **Ending quiescence without a splice:** after CLN and we exchange `stfu`, does CLN resume normal operation when it receives our `tx_abort` (no `splice_init` sent), and what does CLN send to end a quiescence it started with `stfu_channels` when no splice follows (`abort_channels` → `tx_abort`? a disconnect?) Is `stfu_channels` available without `--developer` in v26.06.8?
2. **Splice-out fees:** when CLN initiates a `spliceout` without wallet inputs, how is `funding_contribution_satoshis` computed: is it exactly `-amount_out` with the fee then taken from CLN's channel balance on top, or does it already include the fee (`-(amount_out + fee)`)? And as the non-initiator, what fee share does CLN expect from us when we contribute 0?
3. **Reserve after a splice of a v1 channel:** which reserve does CLN enforce at `tx_complete` and afterwards for a channel opened with v1 `channel_reserve_satoshis`: the original absolute value, 1 % of the new capacity, or the larger? Is it per active funding while the splice is pending?
4. **Depth for `splice_locked`:** which depth does CLN v26.06 use before sending `splice_locked`: the channel's `minimum_depth`, a fixed 6 (as lightningsplice.com says), or something else? And when does it send `announcement_signatures` for the spliced SCID (6 blocks)?
5. **Post-#1289 reestablish TLVs:** does v26.06.8 send TLV 1 `next_funding` as `txid ‖ retransmit_flags` (33 bytes) and TLV 5 `my_current_funding_locked` as `txid ‖ retransmit_flags`, with the `announcement_signatures` bit? Does it still accept or send the older forms (TLV 0 `next_funding_txid`, `your_last_funding_locked`) for peers that negotiated 62/63? (#1289, merged 2026-02-13, added `retransmit_flags`; CLN's 25.12 breaking change #8646/#8506 predates it, so the question is whether 26.04+ follows #1289 exactly.)
6. **Anchors across a splice:** with `option_anchors`, are the anchor outputs and the 330-sat anchor amounts simply repeated on every active commitment, and does CLN ever CPFP a splice transaction (from its wallet or the channel) instead of RBF? Does CLN splice anchor channels with any extra restriction?
7. **`start_batch` behaviour:** does CLN always send `start_batch` before multiple CSs (never the pre-spec `batch` TLV on `commitment_signed`), and how does it treat a batch from us that includes a CS for a funding it has already discarded after its own `splice_locked` (ignore per SP-OP-06, or fail)? What `batch_size` limit does it enforce (20)?
8. **Splice RBF:** when a splice is pending, does `splicein`/`spliceout` on the same channel always become a `tx_init_rbf` (#8021)? Does CLN accept a `tx_init_rbf` from the **non-initiator** of the original splice, with a different `funding_output_contribution`? What minimum interval ("created recently") does it apply, and does it implement the #1327 additive 25 sat/kw rule?
9. **Funding-key rotation:** does CLN rotate its `funding_pubkey` on every splice by default (#7719), and does it accept that we keep our old key?
10. **Test hooks:** which RPCs are the supported way to drive splices in tests on v26.06.8: `splicein`/`spliceout`, or `splice_init`/`splice_update`/`splice_signed`, or `dev-splice`? Are there dev flags to force a disconnect at a given splice step (for SP-T-03..11 against CLN), to fix the feerate, or to delay `splice_locked`?
11. **Interop matrix:** has CLN v26.06 been tested against LDK 0.2.x (bit 63, `start_batch`, reestablish), against Eclair 0.14, and against Phoenix? Are there known incompatibilities we should expect to see mirrored?
12. **`require_confirmed_inputs`:** does CLN set it by default in `splice_init`/`splice_ack`, and does it reject unconfirmed inputs from us?
13. **Gossip of spliced channels:** does CLN relay the new `channel_announcement` immediately after 6 blocks while keeping the old SCID in its graph for 72 blocks, and does it forward over the old SCID after the lock (the #8387 behaviour) for how long?
14. **Disconnect before `tx_signatures` with CS exchanged:** if we reconnect with `next_funding` for a splice whose `tx_signatures` CLN already sent but never got ours, does CLN retransmit `tx_signatures` and expect ours, exactly as SP-RE-03, or does it abort?
15. **(wave qit) Fulfill queued while quiescent:** after our `tx_abort` ends a quiescence, CLN v26.06.8 treats an `update_fulfill_htlc` it queued while quiescent as sent (`SENT_REMOVE_HTLC`) and sends it only after the next `channel_reestablish` (NL-467). Is that a known CLN bug? And should `stfu_channels`/`abort_channels` work with a peer that negotiated only `option_quiesce` (error 354 today, NL-468)?

---

## Appendix: compact lane table (for the workflow args)
| Lane | Files owned | Proof |
|---|---|---|
| Q-A | `Domain/Protocol/Messages/StfuMessage.cs`, `Domain/Channels/Quiescence/**`, `PeerService.cs` stfu arm removal, `ChannelManager.cs` stfu case; tests | rules tables, routing |
| Q-B | `Application/Channels/Quiescence/**`, gate in `ChannelOperationsService`/`CommitScheduler`, `NodeOptions` keys, template; `AT/Channels/Quiescence/**` | harness |
| Q-C | `CLN/ClnQuiescenceTests.cs` | Proof Q |
| IT-A | `Domain/Protocol/InteractiveTx/**` (not `Interfaces/`), deletion of the old validators/service; `DT/Protocol/InteractiveTx/**` | rule tables |
| IT-B | `Infrastructure.Bitcoin/InteractiveTx/**`, `Application/InteractiveTx/WalletInteractiveTxContributor.cs`, Appendix G vector + test; `BT/InteractiveTx/**` | Appendix G byte-exact |
| IT-C (migration `AddInteractiveTxSessions`) | TLVs 66/71, `Infrastructure.Persistence*/**`, `Infrastructure.Repositories/**`, `CrashingUnitOfWork` | round trips, `HasPendingModelChanges` ×3 |
| IT-D | `Application/InteractiveTx/**` (not contributor), `Channels/Handlers/Tx*MessageHandler.cs`, `ChannelManager.cs` tx cases; `AT/InteractiveTx/**` | harness |
| SP1-A | 77/80/81/127 wire, TLV 5, `message_type`, factories, `PeerManager` inbound grouping | round trips, SP-OP-04 |
| SP1-B | `Domain/Channels/Commitments/**`, `Domain/Channels/Splicing/{ChannelFunding,FundingSet}`, validators/policies; simulator | engine tests, simulator |
| SP1-C (migration `AddSpliceFundings`) | signer splice partial, `ChannelSigningInfo`, signing ports, persistence + repositories | signer SP-I1/SP-I4, `HasPendingModelChanges` ×3, crash injection |
| SP1-D | `SpliceRules`, `Application/Channels/Splicing/**`, `ChannelManager.cs` splice cases, one DI line | SP-T-01/02 harness |
| SP1-E | `splicein`/`spliceout` IPC; `CLN/ClnSpliceTests.cs` | Proof SP1 |
| SP2-A | `Domain/Channels/Reestablish/**`, reestablish handler/service; conformance tests | SP-T-03..11 |
| SP2-B | `SpliceDepthWatcher`, `SpliceLockedMessageHandler`, announcements, `ChannelUpdateService` SCID, `IRetiredScidMap` in the switch | lock + SCID + announcement tests |
| SP2-C (migration only if needed) | `Application/Onchain/**`, `Domain/Onchain/**`, `ChannelFailureService.cs` | resolver + reorg tests |
| SP2-D | `listchannels` fundings; `CLN/ClnSpliceReestablishTests.cs`, `OC/OnchainSpliceTests.cs` | Proof SP2 |
| SPR-A | splice RBF paths, `TxInitRbf/TxAckRbf` handlers, RBF rules | RBF harness |
| SPR-B | `bumpsplice` IPC, `SweepScheduler` splice target | IPC + scheduler tests |
| SPR-C | `CLN/ClnSpliceRbfTests.cs` | Proof SPR |
