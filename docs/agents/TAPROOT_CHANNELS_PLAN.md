# Simple Taproot Channels: Plan for NLightning

This is the plan for the simple taproot channel type (`option_simple_taproot`, feature bits 80/81) and, later, taproot gossip (public taproot channels). The epic is NL-877; taproot gossip is NL-878 ([`ISSUES.md`](ISSUES.md)).

- **Status (2026-10-04, after wave t03, branch `wip/taproot-t03`):** T0-T5 are done (T5 except liquidity ads in a taproot splice), T6 is proven against LND 0.21.4 and Eclair 0.14.3 (force closes and a penalty included), T7 waits for BOLTs #1059. The channel type stays experimental (`FeatureOptions.ExperimentalFeatures`, advertised `No` by default) until the owner decides on D-T2. See "Wave t03 record". (After wave t02: T0-T3 done, T4 a safety floor, T6 LND cooperative flows only.)
- **Spec source:** [`bolt-simple-taproot.md`](https://github.com/lightning/bolts/blob/master/bolt-simple-taproot.md), the extension BOLT merged with [BOLTs #995](https://github.com/lightning/bolts/pull/995) on 2026-05-04. It changes BOLT 2, 3 and 5 and depends on BIP 340, 341, 342, 86 and BIP 327 (MuSig2 v1.0.0). Taproot gossip is [BOLTs #1059](https://github.com/lightning/bolts/pull/1059) (`channel_announcement_2`, `channel_update_2`, `announcement_signatures_2`, `node_announcement_2`; bits 70-75). It was still a draft on 2026-10-03.
- **Interop landscape (2026-10-03):**
  - **LND 0.21** (June 2026): taproot channels are production-ready with the final scripts and bits 80/81, but only private, and only on explicit request (`--channel_type=taproot`; lnd #10820 stopped implicit selection for public opens). LND keys nonces by funding txid to prepare for splicing, and has gossip v2 wire work open (lnd #8044 series, lnd #11164, approved 2026-09-28, not merged).
  - **Eclair 0.14** (May 2026): final taproot channels, enabled by default, private only.
  - **CLN, LDK:** status not checked. Check before T6.
  - **Public taproot channels:** none anywhere until #1059 is merged and implemented.
- **What the spec says that shapes this plan.** The facts below were read from the spec on 2026-10-03; T0 re-reads them against the merged text.
  - `option_simple_taproot` **depends on `option_channel_type` and `option_simple_close`**. Our `OptionSimpleClose` defaulted to `No` (`FeatureOptions`); D-T1 (wave t01, lane SC) made it Optional by default on every network, so taproot only needs it negotiated with the peer.
  - Taproot channels **MUST NOT set `announce_channel`**: they are private only until taproot gossip exists.
  - **Funding output:** a BIP 327 MuSig2 aggregate key with a BIP-86 style tweak, spent by key path (commitment and cooperative close).
  - **Commitment outputs** are all P2TR:
    - `to_local`: the NUMS point `simple_taproot_nums` (`02dca094751109d0bd055d03565874e8276dd53e926b44e3bd1bb6bf4bc130a279`) as the internal key and two leaves: the delay leaf (`<local_delayedpubkey> OP_CHECKSIGVERIFY <to_self_delay> OP_CSV`) and the revocation leaf (`<local_delayedpubkey> OP_DROP <revocation_pubkey> OP_CHECKSIG`). Both spends, the penalty included, are script-path spends.
    - `to_remote`: the same NUMS point as the internal key and one leaf (`<remotepubkey> OP_CHECKSIGVERIFY 1 OP_CSV`), so it can only be spent by script.
    - **Spec errata (checked against the spec's own vectors, 2026-10-03):** §To Remote Outputs names a second NUMS point `0245b181...` and a control block with `combined_funding_key`. The vectors use `02dca094...` as the internal key of both `to_local` and `to_remote`, so the vectors win, and the control block's internal key is the NUMS point.
    - anchors: the owner's key as the internal key, plus a 16-block leaf.
    - HTLC outputs and second-level HTLC transactions: tapscript trees.
  - **New TLVs:**
    - `next_local_nonce` (66 bytes): on `open_channel` and `accept_channel` (type 4) and on `channel_ready` (type 4).
    - `partial_signature_with_nonce` (98 bytes = 32 + 66): on `funding_created`, `funding_signed` and `commitment_signed` (type 2).
    - `next_local_nonces` (type 22, entries keyed by funding txid): on `revoke_and_ack` and `channel_reestablish`.
    - `shutdown_nonce` (type 8): on `shutdown`.
    - Partial signatures with JIT nonces: on `closing_complete`/`closing_sig` (types 5/6/7, and `next_closee_nonce` type 22).
  - **Nonces:**
    - Verification nonces may be derived deterministically from a MuSig2 shachain, `hmac("taproot-rev-root" || funding_txid, sha256(shachain_root))`, so nothing has to be stored.
    - Signing nonces are JIT and must never sign two different messages.
    - Any nonce **not** derived from the counter scheme **MUST be persisted**.
  - HTLC second-level signatures stay plain BIP-340 Schnorr (not MuSig2), with SIGHASH_SINGLE|ANYONECANPAY semantics as for anchors.
  - The spec has inline test vectors: scripts and leaf hashes for every output type, plus three transaction scenarios (no HTLCs, five HTLCs, HTLCs trimmed at a 2,500 sat dust limit).

---

## 0. How to use this document (agents)

1. **Order:** T0 → T1 → T2 → T3 → T4 → T5 → T6. T7 (gossip) waits for #1059 to be merged. T1 and T2 may run in parallel once T0's API is fixed; T4 needs T1 and T3.
2. **A task is done** when all of these pass:
   - `dotnet build NLightning.sln -c Release -p:MSBuildWarningsAsMessages=MSB4121` with 0 CS warnings, and `-c Release.Native` (this is crypto code);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release -f net10.0 --filter 'FullyQualifiedName!~Docker'`;
   - the ledger updated in the same commit;
   - the task's own tests. Every builder is checked **byte-exact against the spec vectors** and by **script execution** (NBitcoin `TransactionBuilder.Verify` / taproot sighash against the spent outputs), as BOLT 3 and BOLT 5 were.
3. **Proofs:** T0-T5 are proven with spec vectors and the in-process harnesses (`Application.Tests/Channels/Harness/TwoNodeHarness`, `ThreeNodeHarness`; SQLite restarts and crash injection as in N5). No new Docker tests: the interop proofs of T6 are cluster suites (`scripts/run-cluster.sh`, root `CLAUDE.md`), run on the owner's machine.
4. **Feature gate:** `OptionSimpleTaproot` goes into `FeatureOptions.ExperimentalFeatures` the day the enum value lands. It leaves that set only after T6's interop proofs pass against LND 0.21.4 and Eclair 0.14.3, and only by an owner decision (as O7-T4 and D13 did). The staging bits 180/181 are never advertised.
5. **Schema:** one migration owner per wave, all three providers, through `add_migration.sh` with the compiled models regenerated (root `CLAUDE.md`).
6. **Commits:** `taproot: <what> (NL-877 / T#)`.
7. **Ledger IDs:** new taproot entries use only the reserved ranges **NL-950..NL-979** (waves t01/t02) and **NL-1050..NL-1079** (wave t03) (orchestrator allocation 2026-10-03; the other local lanes use NL-900..NL-910). Wave t01's entries were renumbered at the `wip/fafo` integration: NL-911 (MuSig2 zeroing), NL-912 (NBitcoin MuSig2), NL-913 (simple-close cluster proof), NL-914 (spec errata), NL-915 (anchor key name).

---

## 1. Milestones

### T0: MuSig2 and Schnorr primitives (Infrastructure.Bitcoin)
- **Check the library first.** `NBitcoin.Secp256k1` 3.2.0 (referenced) has a `Musig` namespace. Check whether it follows BIP 327 v1.0.0 (key aggregation with the x-only/plain key rules, `NonceGen` with the `rand'` input, `NonceAgg`, `Sign`, `PartialSigVerify`, `PartialSigAgg`, taproot tweaks). If it does not, write our own small BIP 327 module.
- **Tests:** byte-exact against the official BIP 327 vectors (`key_agg_vectors.json`, `nonce_gen_vectors.json`, `nonce_agg_vectors.json`, `sign_verify_vectors.json`, `tweak_vectors.json`, `sig_agg_vectors.json`, `det_sign_vectors.json`).
- **Domain ports:** add BCL-only value objects for `MusigPubNonce` (66 bytes) and `PartialSignature` (32 bytes), plus an `IMusigSession`-style port. NBitcoin types stay out of Domain.
- **Secret nonces:** they never leave the signer, are single-use by construction (consumed on sign), and are zeroed after use (`SECURITY_REVIEW.md` rules for key material).

### T1: Scripts and transactions (BOLT 3 extension)
- **Domain models:** funding script, `to_local`/`to_remote`/anchor tapscript trees, offered/accepted HTLC trees and second-level HTLC trees. Factories mirror the existing `CommitmentTransactionModelFactory`/`HtlcTransactionModelFactory`.
- **Infrastructure.Bitcoin builders** next to the anchors builders, with the control blocks for each script path.
- **Shared fee code:** the weights go in the existing `CommitmentFeeCalculator`, as a channel-type branch (one calculator, NL-231).
- **Tests:** the spec's script vectors (leaf hashes, internal and output keys) and its three commitment transaction scenarios, byte-exact.

### T2: Wire
- **The TLVs** listed above, each through the "Add a TLV" recipe (`TlvConstants`, converter, `TlvConverterFactory`, a `TlvStreamSerializerTests` sample).
- **Known-type sets:** add each TLV to the message serializer's known-type set (they are even, so a missing entry rejects the peer, NL-001).
- **`commitment_signed` batches:** for taproot, the partial signature replaces the ECDSA `signature` field (a zero signature on the wire). Check the spec's exact rule at T0.
- **`channel_type`:** add bit 80 plus the anchors-equivalent semantics to `ChannelTypeTlv` negotiation.
- **Tests:** round-trip tests per message, and LND-captured bytes once T6 has a peer.

### T3: Channel state, signer and persistence
- **Read NL-904 first:** the t01 review's T3 obligations (fee methods by format, the anchors switch, verification nonces bound to the funding txid, the mirrored remote-commitment test).
- **Signer:**
  - `ILightningSigner` gains MuSig2 commitment and close signing (partial sign, partial verify, aggregate), plus Schnorr HTLC signatures, behind the existing per-channel registration.
  - The revocation guard (NL-189) and S1 (NL-271) extend to taproot unchanged.
- **Nonces:**
  - Use the spec's counter scheme for verification nonces (derived from our shachain and the funding txid, so nothing is stored and a restart re-derives them).
  - Signing nonces are JIT. If any nonce is not counter-derived, it is persisted in the same save as the transition before it is sent (persist, then memory, then send, as the interactive-tx driver does).
  - **No nonce is ever reused across a crash.** A crash-injection test on all three providers proves it, as N5's did.
- **Snapshot:** `ChannelCommitments` carries the peer's next nonces per funding (splices give several fundings, which is why `next_local_nonces` is keyed by funding txid).
- **Persistence:** a migration for the peer's nonces and the channel type, then extend `ChannelRoundTripTests`.
- **Reestablish:** `ReestablishPlanner` sends and expects `next_local_nonces`; a retransmitted `commitment_signed` is signed again with fresh nonces, never replayed byte for byte (unlike ECDSA retransmission).

### T4: BOLT 5 on chain
- **Read NL-904 first:** taproot HTLC transactions need wallet fee inputs, signed after them with every spent output in the `SIGHASH_DEFAULT` sighash.
- **Classification:** `OnchainChannelWatcher` classifies taproot commitments.
- **Resolvers:** `LocalCommitResolver`, `RemoteCommitResolver` and `RevokedCommitResolver` gain taproot output descriptors: script-path spends with control blocks (the `to_local` penalty included: its revocation leaf), and key-path penalties of HTLC outputs and second-level outputs, whose internal key is the revocation key.
- **Fees and reorgs:** CPFP through the taproot anchor (`Onchain/Anchors/`), the anchor reserve, `SweepScheduler` RBF and the mempool reactor all work unchanged on the new descriptors.
- **Tests:** script-execution tests for every spend, and the cluster proofs in T6.

### T5: Interactions
- **Opens:** v1 and v2 (dual-funded) taproot opens.
- **Cooperative close:** `option_simple_close` is required, and the legacy `closing_signed` is never used for a taproot channel (lnd #9669 was LND's pre-final behavior).
- **Splicing:** splice and splice RBF of a taproot channel. Eclair supports it; `SpliceService` signs the shared input with MuSig2.
- **Backups:** static channel backups and peer storage carry the channel type; restore recovery sweeps `to_remote` by script path.
- **Funding watches:** the dual-fund RBF attempts' watches (NL-529) are unchanged.

### T6: Interop proofs (cluster harness)
- **Against LND 0.21.4** (`lnd` suite; give the LND nodes the taproot flag through `LndNodeOptions` if 0.21 still needs one, and ask for `--channel_type=taproot`): open both ways, payments, a restart and reestablish, cooperative close, our force close and theirs with HTLCs, and a penalty (the `onchain` suite's helpers, `LndChannelDbRollback`).
- **Against Eclair 0.14.3** (`eclair`/`eclair2` suites): the same set, plus a splice.
- **Location and run:** new classes next to the anchors classes (`Docker/Onchain/Taproot/`) and in `Docker/Interop/Eclair/` (the namespaces keep the `Docker` name; the backend is the cluster), run with `scripts/run-cluster.sh --suite <s>`.
- **Exit:** an owner decision on advertising `OptionSimpleTaproot` Optional.

### T7: Taproot gossip (NL-878, after BOLTs #1059 is merged)
- **What it adds:** typed `channel_announcement_2`/`channel_update_2`/`announcement_signatures_2`/`node_announcement_2`, MuSig2-signed announcements, block-height timestamps, and graph and sync support for the v2 messages next to v1.
- **Not written yet:** the detailed plan is written when the spec is final, against the LND gossip v2 implementation as the interop peer.
- **Until then:** taproot channels stay private (spec rule), and `openchannel --public` refuses the taproot channel type.

---

## 2. Wave t01 (started 2026-10-03, branch `wip/taproot-plan`)

Three lanes in parallel worktrees, merged with `--no-ff` by the integrator; new ledger entries are proposed by the lanes and numbered by the integrator.

| Lane | Scope | Proof |
|---|---|---|
| T0 | BIP 327 MuSig2 behind a Domain port (`KeySort`, `KeyAgg` with the BIP-86 tweak, nonce generation with caller randomness, `NonceAgg`, partial sign/verify, aggregation; the 66-byte public nonce, 97-byte secret nonce and 32-byte partial signature as Domain value objects); D-T3 | every BIP 327 vector file, byte-exact; the spec's funding `combined_key` from its funding keys |
| T1 | Tapscript trees, control blocks and the taproot output scripts of §Funding/§Commitment/§HTLC; the unsigned taproot commitment transaction (354 sat dust, zero-fee HTLC outputs, taproot anchors) and both second-level HTLC transactions; BIP-340 HTLC signatures | the spec's script vectors (leaf scripts, leaf hashes, roots, internal/output keys, pkScripts) and its three transaction cases (txid and outputs; the HTLC resolution transactions with their witnesses byte-exact) |
| SC | D-T1: `OptionSimpleClose` Optional by default | feature tests, config template, docs |

After the merge the integrator replays the spec's signed commitment vectors end to end (T0 nonces and partial signatures over T1's sighash).

**Record.** T0 merged at `795ae14b`: `IMusig2Service` (Domain port; `AggregateTaprootKeyPath` gives the funding key), the value objects `MusigPublicNonce`/`MusigAggregateNonce`/`MusigPartialSignature`/`MusigPartialSignatureWithNonce`/`MusigTweak` and the single-use `MusigSecretNonce` (no byte export, consumed atomically by `Sign`); every BIP 327 vector byte-exact and, over NBitcoin's key-path sighash of `expected_commitment_tx_hex`, the spec's `remote_partial_sig` and aggregated witness signature reproduced for all three transaction cases. SC merged at `a2e7d6bc`: D-T1 done, legacy close proofs pinned to `OptionSimpleClose = No`; the cluster re-run is NL-913. T1 merged at `c7da7b8b`: Domain `Bitcoin/Transactions/Enums/CommitmentFormat` (`StaticRemoteKey`, `Anchors`, `SimpleTaproot`; `HasAnchorOutputs()`, `IsTaproot()`), the one fee calculator branched by format (`WeightConstants.CommitmentWeightSimpleTaproot` 968 + 172 per HTLC, zero-fee HTLC transactions, `TransactionConstants.SimpleTaprootDustLimit` 354 sat, the funder pays both 330 sat anchors), `Infrastructure.Bitcoin/Taproot/` (`SimpleTaprootScripts`, `TapscriptTree`, `TaprootSignatures` with `ComputeFundingKeySpendSigHash`), the taproot outputs (`TaprootFundingOutput`, `TaprootToLocalOutput`, `TaprootToRemoteOutput`, `TaprootAnchorOutput`, `TaprootHtlcOutput`, `TaprootHtlcResolutionOutput`), the commitment and HTLC builders and the signer's HTLC paths on BIP 340; every script vector, the three commitment transactions (signed, with the vector's MuSig2 witness) and all 9 HTLC resolution transactions byte-exact. Spec discrepancies beyond the To Remote errata: `scripts.funding.combined_key` is the BIP 86-tweaked key, the `accepted_htlc_*` script vectors swap the local and remote HTLC keys, and the `*_local_commit`/`*_remote_commit` entries are identical (NL-914). Testing in the integration followed the owner's split (2026-10-03: another agent runs the suites): Release build 0 warnings, format clean, the lanes' own new tests green; the full suites ran only on the T0+SC tree (green but the known NL-434 canary).

## Wave t01 hand-off (2026-10-03, `wip/taproot-plan`)

**State.** T0 and T1 are library code with vector proofs; nothing is wired into channels. There is no feature bit 80/81, no TLV, no handler, no persistence, and `ChannelParams.CommitmentFormat` never returns `SimpleTaproot` yet. `option_simple_close` is Optional by default (D-T1). The branch is `wip/fafo` plus this work, kept in sync by merges (no rebase: the branch is pushed and force pushes are denied).

**For the testing agent.**
- Non-Docker: `dotnet test --no-build -c Release -f net10.0 --filter 'FullyQualifiedName!~Docker'`; new classes: `Infrastructure.Bitcoin.Tests` `~Crypto.Musig2` (BIP 327 vectors, spec replay) and `~Taproot.SimpleTaproot` (scripts, commitment, signer, spend paths, HTLC builder), `Domain.Tests` `~Musig` and `~SimpleTaproot`; `Integration.Tests ~BOLT3` must stay byte-exact (248/248 in lane T1).
- Cluster (NL-913, the simple-close default): `scripts/run-cluster.sh --matrix cln,eclair,lnd,day0`.

**Next wave (t02): T2 and T3 can start in parallel.**
- T2: `Feature.OptionSimpleTaproot` 80/81 in `FeatureOptions.ExperimentalFeatures`; the channel type; TLVs `next_local_nonce` (open/accept/channel_ready, type 4), `partial_signature_with_nonce` (funding_created/signed, commitment_signed, type 2), `next_local_nonces` (revoke_and_ack and channel_reestablish, type 22, keyed by funding txid), `shutdown_nonce` (type 8), and the `closing_complete`/`closing_sig` partial signatures (types 5/6/7, `next_closee_nonce` 22); read §New TLV Types and the per-message Requirements in the spec before coding (`Infrastructure.Bitcoin.Tests/Taproot/Vectors/README.md` names the source).
- T3: a persisted taproot flag on `ChannelParams` so `CommitmentFormat` can return `SimpleTaproot`; format overloads for the `CommitmentSpec` region of `CommitmentFeeCalculator` and the commitment engine; the format overload of `CreateCommitmentTransactionModel` onto `ICommitmentTransactionModelFactory`; the signer's MuSig2 commitment signing over `IMusig2Service` with D-T4 nonces (`GenerateNonce` takes the shachain leaf as randomness; `MusigSecretNonce` has no byte export on purpose, NL-911); the crash-injection proof that no nonce is reused.
- T4 inputs left by T1: `HtlcTransactionBuilder.EstimateAnchorBaseWeight`/`AddFeeInputs` and `BaseTaprootOutput.ToCoin()` throw `NotSupportedException` for taproot; `CommitmentOutputMapper` and the resolvers have no taproot descriptors.
- Cleanup: the spec vectors are kept once since the t01 integration (`Taproot/Vectors/`); `AnchorOutputInfo.FundingPubKey` holds the taproot anchor's internal key and wants a rename (NL-915).

**t01 integration (2026-10-03, merged into `wip/fafo`).** Review of MuSig2, scripts/signatures and transactions/fees: no spec deviation and no legacy/anchors regression; robustness fixes NL-903; the T3/T4 obligations it found (fee methods by format, the anchors switch, verification nonces bound to the funding, taproot HTLC fee inputs, a mirrored remote-commitment test, LND's staging scripts) are NL-904, read it before starting T3. NL-913 proven on the cluster; CLN v26.06.8 does not signal simple close.

## Wave t02 record (2026-10-03, branch `wip/taproot-t02` from `wip/fafo` at `eb166f13`)

**Lanes** (parallel worktrees, merged with `--no-ff`, each lane's tests fail without its change; reports kept by the integrator): phase A SIG (signer, `LocalLightningSigner.Taproot.cs`), WIRE (bit 81 and every TLV), STATE (format, fees, engine, migration `AddSimpleTaprootChannels`), REVA (adversarial review of phase A); phase B OPS (v1 open, normal operation, reestablish, crash proof), CLOSE (simple close, force-close floor, splice refusal, backups), V2 (dual-funded open), V2INT (V2 onto OPS/CLOSE, the v2 seam lifted), REVB and REVC (adversarial reviews of OPS/CLOSE and V2/V2INT), LND (T6 on the cluster).

**What works.**
- **Gate (D-T2):** `Feature.OptionSimpleTaproot = 81`, in `FeatureOptions.ExperimentalFeatures`, advertised `No` by default; honoured only with `Features:AllowExperimentalFeatures=true` and `Features:OptionSimpleTaproot=Optional`. Our default open type stays anchors. BOLT 9 dependencies `option_channel_type` and `option_simple_close`; a peer's 81 without them is ignored while we do not support taproot (NL-973). The staging bits 180/181 are never advertised or accepted.
- **Opening:** `openchannel --channel-type taproot` (client option, `OpenChannelIpcRequest` key 11 `ChannelType`, "taproot" or "anchors"). It needs our gate, the peer's 80/81 and `option_simple_close`, and refuses `--public` (taproot channels stay private until T7) and `--request-inbound` (NL-971). v1 or dual-funded (v2) by the NL-551 rules (`--v1` forces v1; LND has no dual funding, so v1 with LND). channel_type is exactly {80} (+46/+50 per the scid_alias/zeroconf choice), as LND requires. An inbound taproot open is accepted only when the option is negotiated and `announce_channel` is unset.
- **Nonces (D-T4):** verification nonces from the counter scheme, `HMAC-SHA256(key = "taproot-rev-root" || funding_txid, sha256(shachain seed))`, leaf `2^48-1-n`, `NonceGen(rand' = leaf, pk = that funding's key)`; commitment 0 of a v1 open uses the key without a txid (sent before the txid exists), a dual-funded channel's commitment 0 binds the attempt's txid (NL-972). Signing and closer nonces are JIT (fresh randomness, the secret key and the message mixed in), never stored; closee nonces live in signer memory, single use, forgotten on every connection. A verification nonce signs one broadcast session only.
- **Wire:** open/accept/channel_ready `next_local_nonce` (4), funding_created/signed and commitment_signed `partial_signature_with_nonce` (2) with a zero 64-byte signature field, revoke_and_ack and channel_reestablish `next_local_nonces` (22; txid in internal byte order, sorted, at most 16), shutdown `shutdown_nonce` (8), closing_complete 5/6/7 and closing_sig 5/6/7 + 22, and Eclair's/BOLTs #1324's `tx_complete` `commit_nonces` (4) and `funding_nonce` (6), `tx_signatures` (2), `channel_reestablish` `current_commit_nonce` (24).
- **State:** `ChannelParams.OptionSimpleTaproot` (with `OptionAnchorOutputs` true: anchors semantics), `CommitmentFormat.SimpleTaproot`, fees by format everywhere (NL-904 items 1-2), the engine's `RemoteNextNonces` per funding (consumed when we sign, replaced by revoke_and_ack and channel_reestablish), `CommitmentSignatures.PartialSignature`; the peer's partial signature and nonces persisted (`AddSimpleTaprootChannels`, all three providers, regenerated after `AddTrampolineRelayAttempts` at the integration).
- **Operation:** payments with BIP 340 HTLC signatures (0x83 on the peer's), reestablish after a restart with nonces exchanged first, a retransmitted `commitment_signed` re-signed with fresh nonces (never replayed byte for byte); the D-T4 crash proof on SQLite (NL-956); a forward through taproot channels in the three-node harness.
- **Close:** `option_simple_close` only, with the shutdown closee nonce, JIT closer nonces and the `next_closee_nonce` rotation for RBF rounds; `closing_signed` is never sent and a peer's gets a channel warning; `closechannel` is refused if the peer's connection did not negotiate simple close (NL-967).
- **Backups:** backup codec version 2 with the taproot flag (version 1 unchanged without taproot channels), peer storage flag bit 2; a restored taproot channel's data-loss `channel_reestablish` carries nonces (NL-974).

**Refused (clean errors):** splicing a taproot channel (NL-965), RBF of a dual-funded taproot open (NL-970), liquidity ads with taproot (NL-971), public taproot channels.

**T4 (safety floor only, NL-966):** a force close (`forceclosechannel`, fail-the-channel) broadcasts our latest commitment with the aggregated MuSig2 key-path signature from the stored peer partial signature; the watcher classifies taproot spends; our `to_local` (delay leaf), our `to_remote` on their commitment (1-CSV leaf) and the revoked `to_local` (revocation leaf) are swept by script path (BIP 342 sighash over every spent output, RBF re-sign). Not resolved, alerted once per output (`[NL-966]`): HTLC outputs on either commitment (our HTLC transactions need wallet fee inputs, NL-904 item 4), second-level outputs, HTLC penalties, preimages revealed on chain, anchor CPFP. Funds in HTLC outputs of a force-closed taproot channel wait for T4: keep HTLCs small on the experimental gate.

**T6 (LND 0.21.4):** LND needs `--protocol.simple-taproot-chans` (no other flag); it then advertises 81 and 181 and forces its RBF close (61/161). Suite `taproot` (`Docker/Taproot/LndTaprootFlowTests`, its own network with one LND `tara`, 1 namespace): LND opens with `CommitmentType = SIMPLE_TAPROOT_FINAL` (7) and we open with `--channel-type taproot`; payments both ways; our restart with an HTLC in flight and LND's restart, reestablish with the type-22 maps; cooperative closes both ways (LND always runs `closing_complete`/`closing_sig`, both transactions MuSig2 key-path spends); commitment fee identical to LND's in both roles. Green 3 runs in a row (lane LND) and again in the integration matrix `tap2-mx1` (`scripts/run-cluster.sh --matrix lnd,cln,eclair,day0,onchain,anchors,taproot`, 12 namespaces, 1,014 s: lnd 58/58, cln 90 + 1 rerun-green + 4 `Explicit`, eclair 25 + 2 `Explicit`, day0 5 + 1 rerun-green (NL-983), onchain 33 + 2 `Explicit`, anchors 18/18, taproot 2/2). Not covered: force closes, on-chain HTLCs (NL-978); Eclair (stretch, not run; NL-957 `prevtx_details` would be needed for Eclair's taproot wallet inputs).

**Ledger:** NL-953..NL-961, NL-965..NL-979 (NL-950..NL-952, NL-962..NL-964 unused); NL-904 items 1-3 and 5-7 done, item 4 stays with T4.

**Next (t03):** T4 in full (NL-966, NL-904 item 4), the force-close interop (NL-978), Eclair T6 (with NL-957), splicing (NL-965), dual-funded RBF (NL-970, needs a column), then the owner decision on advertising `option_simple_taproot` Optional. (Done in wave t03, below, except the decision.)

## Wave t03 record (2026-10-04, branch `wip/taproot-t03` from `wip/fafo` at `6b0d9e12`)

**Lanes** (parallel worktrees `tap3-*`, merged with `--no-ff`; each lane's tests fail without its change): phase 1 T4L (our commitment on chain), T4R (the peer's and revoked commitments), SPL (splicing), RBF (dual-funded RBF, the wave's only migration), ECL (NL-957 and the Eclair open/pay/close proofs), SMALL (NL-987, NL-960); phase 2 on the merged tree FC (LND force-close interop), ECL2 (Eclair splices, RBF and force closes), and the adversarial reviews RV4 (T4L/T4R), RVS (SPL and its merge with RBF) and RVR (RBF and ECL).

**What works.**
- **T4, BOLT 5 in full (NL-966, NL-904 item 4).**
  - Our commitment (lane T4L): the zero-fee HTLC-timeout/success transactions go the anchors path, funded by wallet fee inputs (`HtlcTransactionBuilder.EstimateAnchorBaseWeight`/`AddFeeInputs` accept taproot); our BIP 340 signature is made after the fee inputs, `SIGHASH_DEFAULT` over every spent output (`TaprootSignatures.ComputeHtlcSigHash(..., allowFeeInputs)`), the peer's 0x83 signature stays valid (HTLC input and output at index 0); P2TR second-level outputs (`TaprootHtlcResolutionOutput`) swept by the delay leaf; upstream timeout as anchors; CPFP of our and the peer's commitment through our P2TR anchor by key path (`ILightningSigner.SignTaprootAnchorInput`, `AnchorCpfpService.Taproot.cs`) and the 16-block sweep of any taproot anchor by its `OP_16 OP_CSV` leaf.
  - The peer's commitments (lane T4R): HTLC claims by script path (our offered HTLC by the timeout leaf at `cltv_expiry`, the peer's by the success leaf with an allowed preimage, final-hop and relay claims included, nSequence 1); revoked HTLC outputs and the cheater's second-level outputs penalised by the revocation key path tweaked with the tree's merkle root (`Domain/Onchain/Taproot/TapscriptMerkleRoot`, `SweepInputFactory.TaprootSecondLevelPenalty`); `HtlcWitnessParser` reads the taproot stacks (control block last, annex dropped), so a preimage the peer reveals on either commitment, confirmed or in the mempool, fulfills upstream; `SweepScheduler` re-signs every taproot spend on RBF; rows recorded by a t02 build take the rebuilt commitment's leaf (RV4's NL-1051 for revoked rows).
  - `UnsupportedTaprootOutputs` remains only for a peer-commitment HTLC row no mapped leaf matches.
- **Splicing (NL-965, lane SPL):** splice in, out and RBF in both roles; the shared input spent by MuSig2 key path with BOLTs PR #1324 as Eclair 0.14.3 speaks it (`tx_complete` `commit_nonces` 4 and `funding_nonce` 6, `tx_signatures` 2); the shared input's partial signature made at the commitment step and saved in the interactive-tx row before our `commitment_signed` (format 2 witness blob, no schema change); a taproot splice always rotates the funding key; reestablish names the pending splice in type 22 and carries type 24; force close on a pending or locked splice funding. Review fixes NL-1078 and NL-1060 (the 16-entry nonce map caps splice RBF siblings and dual-funded open attempts).
- **Dual-funded RBF (NL-970, lane RBF):** both directions, `InteractiveTxSessions.TheirCommitmentPartialSignature` (migration `AddDualFundTaprootAttempts`), the confirmed attempt followed and force-closable from its stored partial signature; a pending open's `next_local_nonces` covers every signed attempt (the peer's may cover a subset once it saw one confirm, RVR's NL-1079, high); a `next_funding` without a usable type 24 gets `tx_abort` before our `tx_signatures` (NL-969). Liquidity ads with a taproot open, both roles and its RBF (NL-971); a taproot splice still refuses them. `bumpopen` hint valid (NL-986).
- **Wire:** tx_add_input `prevtx_details` read as type 2 and Eclair's 1111 (NL-957); an input without `prevtx` is accepted only when every input is P2TR. We keep sending `prevtx`.
- **Operator:** `listchannels` prints the channel type (IPC key 27, NL-987).
- **D-T4 on Postgres (NL-960):** the crash-at-every-save proof runs in the cluster `postgres` suite (`Docker/TaprootPostgresCrashTests`).

**T6 against LND 0.21.4 (lane FC, NL-978 fixed):** `Docker/Taproot/LndTaprootForceCloseTests` in the same suite (one node per test, an HTLC each way on the commitment): our `forceclosechannel` (key-path commitment, HTLC-success with the preimage and HTLC-timeout at `cltv_expiry` by script path with LND's `SIGHASH_SINGLE|ANYONECANPAY` signature and a wallet fee input, P2TR second-level outputs and `to_local` swept by the delay leaf after the 144-block CSV, Closed, LND `RemoteForceClose`), LND's force close (preimage and timeout claims and `to_remote` by script path, LND `LocalForceClose`) and the penalty of a revoked commitment that holds an HTLC (LND's `channel.db` rolled back; HTLC by the revocation key path, `to_local` by the revocation leaf; the wallet gets the whole channel less the fees); `LndTaprootFlowTests` adds a crash after our `commitment_signed` and an LND restart with an HTLC in flight. The suite takes about 220 s (6 tests). The run found NL-1055 (our unspent anchor kept the channel resolving; fixed in the executor, any format).

**T6 against Eclair 0.14.3 (lanes ECL, ECL2):** Eclair needs no configuration (it advertises `option_simple_taproot` and prefers it for private channels). `EclairTaprootTests` (suite `eclair`): Eclair's dual-funded taproot open to us and ours to Eclair from a P2TR-only wallet, payments both ways, restarts of either side, simple close both ways, and RBF of either side's open. `EclairTaprootSpliceTests` (suite `eclair2`, 5): splices in and out by either side, our `bumpsplice`, Eclair's `rbfsplice`, a restart of either side while our splice is pending. `EclairTaprootOnchainTests` (suite `eclair`, 2): both force closes with an HTLC each way, resolved without an `[NL-966]` alert. Each green twice; full `eclair` 31/31 and `eclair2` 12/12. Interop fixes: NL-1065 (we charged Eclair's splice-out the segwit marker and flag), NL-1058 (a splice `commitment_signed` crossing our `tx_abort`).

**Open:** NL-1050 (the peer's anchor on a revoked or future commitment is not swept), NL-1059 (restore does not follow taproot splices), NL-1061 (Eclair fails the channel when we lost an attempt it is signing), NL-1062 (cosmetic), liquidity ads in a taproot splice (NL-971 note). T7 waits for BOLTs #1059.

**Next:** the owner decision D-T2 (advertise `option_simple_taproot` Optional); T7 once BOLTs #1059 is merged.

## 3. Decisions

- **D-T1 (owner decision 2026-10-03): `OptionSimpleClose` Optional by default on every network**, independent of taproot (proven against LND, CLN and Eclair since ABCD wave 6 and batch10). Done in wave t01, lane SC.
- **D-T2 (owner decision 2026-10-03):** once T6 passes, advertise `option_simple_taproot` Optional and accept taproot opens, but keep anchors as the channel type of our own opens until the owner flips the preference after field experience.
- **D-T3 (owner decision 2026-10-03):** use `NBitcoin.Secp256k1`'s MuSig2 (`Musig.MusigContext`, already referenced) if it passes every BIP 327 vector, otherwise write a small BIP 327 module in `Infrastructure.Bitcoin`. Decided by T0's vector run. **Outcome (wave t01, lane T0): our own module** (`Infrastructure.Bitcoin/Crypto/Musig2/Bip327`, a line-by-line port of BIP 327's `reference.py` over NBitcoin.Secp256k1's group and scalar primitives, secret multiplications through the blinded generator context). NBitcoin.Secp256k1 3.2.0's MuSig2 passed 51 of the 56 vector cases but cannot take an aggregate nonce or a given secret nonce in `Sign` (sign_error 2-5 cannot run) and its `MusigPubNonce` accepts the point at infinity in a signer's nonce (det_sign error 3), which BIP 327 refuses; it stays in the tests as a cross-check (NL-912).
- **D-T4 (owner decision 2026-10-03):** verification nonces use the spec's counter scheme (re-derived, never stored); signing nonces are JIT and persisted before any message that uses them is sent; T3's crash-injection proof that no nonce is ever reused is a gate.
