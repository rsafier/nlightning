# Simple Taproot Channels: Plan for NLightning

This is the plan for the simple taproot channel type (`option_simple_taproot`, feature bits 80/81) and, later, taproot gossip (public taproot channels). The epic is NL-877; taproot gossip is NL-878 ([`ISSUES.md`](ISSUES.md)).

- **Status (2026-10-03, branch `wip/taproot-plan` from `wip/fafo` @ `fedb876`, when written):** nothing exists; this is a plan only, no wave has started. There is no MuSig2, no taproot channel type and no taproot commitment, HTLC or sweep scripts. We never offer or accept the channel type, so Eclair 0.14.3 (which prefers taproot) and LND 0.21.4 (taproot only when asked) open anchors channels with us; both are the interop peers of the cluster harness. The only taproot code is in the wallet: `LocalLightningSigner.SignWalletTransaction` signs P2TR wallet inputs (BIP-86 key path), and BOLT 11 decodes and encodes v1 fallback addresses (NL-118).
- **Spec source:** [`bolt-simple-taproot.md`](https://github.com/lightning/bolts/blob/master/bolt-simple-taproot.md), the extension BOLT merged with [BOLTs #995](https://github.com/lightning/bolts/pull/995) on 2026-05-04. It changes BOLT 2, 3 and 5 and depends on BIP 340, 341, 342, 86 and BIP 327 (MuSig2 v1.0.0). Taproot gossip is [BOLTs #1059](https://github.com/lightning/bolts/pull/1059) (`channel_announcement_2`, `channel_update_2`, `announcement_signatures_2`, `node_announcement_2`; bits 70-75). It was still a draft on 2026-10-03.
- **Interop landscape (2026-10-03):**
  - **LND 0.21** (June 2026): taproot channels are production-ready with the final scripts and bits 80/81, but only private, and only on explicit request (`--channel_type=taproot`; lnd #10820 stopped implicit selection for public opens). LND keys nonces by funding txid to prepare for splicing, and has gossip v2 wire work open (lnd #8044 series, lnd #11164, approved 2026-09-28, not merged).
  - **Eclair 0.14** (May 2026): final taproot channels, enabled by default, private only.
  - **CLN, LDK:** status not checked. Check before T6.
  - **Public taproot channels:** none anywhere until #1059 is merged and implemented.
- **What the spec says that shapes this plan.** The facts below were read from the spec on 2026-10-03; T0 re-reads them against the merged text.
  - `option_simple_taproot` **depends on `option_channel_type` and `option_simple_close`**. Our `OptionSimpleClose` defaults to `No` (`FeatureOptions`), so taproot needs it on, or at least negotiated per peer.
  - Taproot channels **MUST NOT set `announce_channel`**: they are private only until taproot gossip exists.
  - **Funding output:** a BIP 327 MuSig2 aggregate key with a BIP-86 style tweak, spent by key path (commitment and cooperative close).
  - **Commitment outputs** are all P2TR:
    - `to_local`: the revocation key as the internal key, plus a delayed CSV leaf.
    - `to_remote`: the NUMS point `simple_taproot_nums` (`02dca094751109d0bd055d03565874e8276dd53e926b44e3bd1bb6bf4bc130a279`) as the internal key, so it can only be spent by script.
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
- **Classification:** `OnchainChannelWatcher` classifies taproot commitments.
- **Resolvers:** `LocalCommitResolver`, `RemoteCommitResolver` and `RevokedCommitResolver` gain taproot output descriptors: script-path spends with control blocks, and the key-path revocation spend of `to_local`.
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

## 2. Decisions to take

- **D-T1 (owner):** turn `OptionSimpleClose` on by default, or only when taproot is negotiated. Taproot cannot close cooperatively without it.
- **D-T2 (owner):** whether to offer taproot by default once T6 passes (Eclair does; LND only on request), and whether to prefer it over anchors for private opens.
- **D-T3:** NBitcoin.Secp256k1 MuSig or our own BIP 327 module (decided by T0's vector run).
