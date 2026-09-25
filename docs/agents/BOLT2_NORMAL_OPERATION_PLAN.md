# BOLT 2 Normal Operation, Reestablish and Close: Implementation Plan for NLightning

This is the authoritative plan for BOLT 2 "Normal Operation" (`update_add_htlc`, `update_fulfill_htlc`, `update_fail_htlc`, `update_fail_malformed_htlc`, `commitment_signed`, `revoke_and_ack`, `update_fee`), "Message Retransmission" (`channel_reestablish`, data-loss protection) and "Channel Close" (`shutdown`, legacy `closing_signed`, `option_simple_close`). It also covers the BOLT 3 pieces they need: HTLC-timeout/success transactions, commitment fee math and the closing transaction. Every repo claim cites a repo-relative path. Claims marked **(unverified)** or **(inferred)** were not proven against running code; confirm them before you rely on them.

- **Spec source:** `lightning/bolts` master, fetched 2026-09-25: `02-peer-protocol.md` (§Channel Close, §Normal Operation, §Message Retransmission) and `03-transactions.md` (HTLC txs, fee calculation, closing txs, Appendices C, D, E, F). Re-read the requirement block before you implement a handler.
- **Sources merged:** three independent drafts (safety-first, MVP-to-interop, requirements traceability). Where they disagreed about the code, the code was checked (§2.3). Design choices are recorded in §4.
- **Issue ledger:** every bug and gap here has an `NL-###` ID in [`ISSUES.md`](ISSUES.md). Tasks say "Resolves NL-…". Update the ledger entry in the same commit as the fix.
- **Status:** nothing in this plan is implemented. N0 and N1 are **hard gates**: they fix pre-existing bugs that already break the second commitment.

---

## 0. How to use this document (agents)

1. Do the milestones in order: **N0 → N1 → … → N10**. N11 is optional. N2 and N3 may run in parallel; ONION M3-T1/T2 (error-onion create) must land before N6-T5.
2. A task is done only when all of these pass:
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` (and `-c Release.Native` when crypto or signer code changes);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'` (after N0-T1 this includes Application.Tests and Daemon.Tests; before it, run them with `dotnet run --project <proj>`);
   - the task's own tests, and the **invariant suite** (N4-T5) for anything that touches the state machine. Never skip or delete an invariant test.
3. A milestone is done when its **Proof** passes. Docker proofs run locally only: `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker.NormalOperation"`. Keep `Docker` in those namespaces and nowhere else (CI filters by substring). Check `docker logs alice` on any failure.
4. Schema changes: one consolidated migration per milestone, all three providers, via `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh <Name>` (root `CLAUDE.md` › "Add an EF migration").
5. DI: new layer services go in the layer's `DependencyInjection.cs`. Anything still hand-registered in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs` must also be mirrored in the Docker test DI until N0-T8 centralizes it.
6. **Feature gate:** HTLC operation stays behind `NodeOptions.EnableHtlcs` (new, default `false` except on regtest) until N9 can fail a channel by broadcasting, and mainnet stays off until the BOLT 5 plan (NL-094) can sweep.
7. Commit shape: one task per commit, lowercase imperative subject, cite the NL IDs.

---

## 1. Spec requirements summary

### 1.1 Update lifecycle
- Updates apply first to the **other** node's commitment. Each passes five stages: pending on receiver → in receiver's latest commitment → receiver's previous commitment revoked (now pending on sender) → in sender's latest commitment → sender's previous commitment revoked.
- An add (or removal) is **irrevocably committed** once the commitment with (without) it is signed by both sides and every earlier commitment without (with) it is revoked, or once that is irreversibly confirmed on-chain.
- Forwarding: MUST NOT offer the outgoing HTLC before the incoming one is irrevocably committed; MUST NOT fail the incoming HTLC before the outgoing removal is irrevocably committed (or the HTLC-timeout is deep enough on-chain); MUST fulfill incoming as soon as the preimage is known; MUST fail incoming at `cltv_expiry` or when `cltv_expiry - height < cltv_expiry_delta(out)`.
- Deadlines: the offerer fails the channel if an offered HTLC in either commitment passes `cltv_expiry + G`; the fulfiller fails it if a fulfilled HTLC is still in a commitment `2R+G+S` (≈18) blocks before expiry. Recommended `cltv_expiry_delta ≥ 34`.
- Dust exposure (`max_dust_htlc_exposure_msat`): the receiver SHOULD fail (not reveal the preimage for) a dusty HTLC that pushes exposure over the limit; the sender SHOULD NOT send it.

### 1.2 `update_add_htlc` (128)
- **Sender:** `amount_msat > 0` and `≥` remote `htlc_minimum_msat`; `cltv_expiry < 500000000`; stays within remote `max_accepted_htlcs` and `max_htlc_value_in_flight_msat`; `id` starts at **0**, +1 per offer, never reset; the fee payer must afford the fee on both commitments above its reserve (plus both 330-sat anchors with `option_anchors`) and SHOULD keep a fee-spike buffer (2× feerate + one more HTLC); the non-payer SHOULD NOT push the payer below that; no add after `shutdown`.
- **Receiver:** reject (warning+close or error+fail) on amount 0 / below own minimum, unaffordable sender, own limits exceeded, `cltv_expiry ≥ 500000000`; MUST allow duplicate `payment_hash`; MUST ignore a repeated `id` after reconnection if its commitment was not acked; MAY fail other id violations. Onion peel (BOLT 4, `payment_hash` as associated data, `path_key` if present) happens after lock-in.

### 1.3 Removing HTLCs (130, 131, 135)
- Only HTLCs the **other** node added; only once the add is irrevocably committed.
- **Receiver:** unknown `id` (not in its current commitment), a preimage that does not hash to `payment_hash`, or `update_fail_malformed_htlc` without the BADONION bit → warning/error. A malformed failure is converted upstream into `update_fail_htlc` (`failure_code ‖ sha256_of_onion`, ONION M3-T3). `fulfillment_payload` > 32768 → error (M3b).
- TLVs `attribution_data` (1) and `fulfillment_payload` (3) are M3b; do not advertise `option_attribution_data` until then.

### 1.4 `commitment_signed` (132)
- **Sender:** MUST NOT send without changes (fee-only or dust-only changes are allowed); one `htlc_signature` per untrimmed HTLC **in commitment output order**; MUST set TLV 1 `funding_txid`; SHOULD ping/pong first if the peer was quiet. `start_batch` (127) is splice-only.
- **Receiver:** apply pending updates; `signature` valid and low-S; `num_htlcs` equals the HTLC output count; every HTLC signature valid and low-S; otherwise warning/error. MUST respond with `revoke_and_ack`. Persist before replying ("a single persistent write … for each `commitment_signed` sent or received").
- HTLC signatures use SIGHASH_ALL, or `SIGHASH_SINGLE|ANYONECANPAY` with anchors.

### 1.5 `revoke_and_ack` (133)
- Sender reveals `per_commitment_secret` for the previous commitment and `next_per_commitment_point` for the one after next.
- Receiver MUST fail if the secret is not a valid key or does not produce the previous point; MAY fail if it breaks the shachain.
- Never broadcast a revoked commitment; SHOULD NOT fully sign our own commitment except to broadcast it.

### 1.6 `update_fee` (134)
Only the funder sends it (never with `zero_fee_commitments`). The receiver fails on a non-funder sender or an unreasonable feerate, and SHOULD fail if the funder cannot afford it on the receiver's commitment (MAY defer to commit). A fee update is replaced, never removed; the last one included in a commitment applies. Non-anchor dust-exposure checks apply.

### 1.7 `channel_reestablish` (136)
- **Disconnect:** reverse every update the peer sent that was not followed by a received `commitment_signed`; a learned preimage stays known. A funder remembers the channel once the funding tx is broadcast (SHOULD NOT before); a fundee once `funding_signed` is sent.
- **Reconnect:** send `channel_reestablish` for each channel; wait for the peer's before sending anything else on that channel; a failed channel re-sends its `error` and ignores the rest.
- **Fields:** `next_commitment_number` = our local commitment number + 1; `next_revocation_number` = number of the next RAA we expect; `your_last_per_commitment_secret` = last secret received (zeros if none); `my_current_per_commitment_point`. TLV 1 `next_funding` (`sha256 txid ‖ byte retransmit_flags`) only for an unsigned interactive tx; TLV 5 `my_current_funding_locked` is splice-only.
- **Receiver:** `next_commitment_number == 0` → fail and broadcast; both 1 → retransmit `channel_ready` (otherwise MUST NOT, MAY with a new alias; ignore a redundant one); equals our last sent CS number → retransmit the same updates and CS; otherwise ≠ expected → SHOULD fail. `next_revocation_number` equals our last sent RAA number (and no `closing_signed` received) → resend it, keeping the **original relative order** with any CS; otherwise mismatch → SHOULD fail. Peer ahead **and** `your_last_per_commitment_secret` proves it → **we lost data**: MUST NOT broadcast our commitment, SHOULD send `error`. Wrong secret otherwise → SHOULD fail. Re-send `shutdown` if we sent one; closing negotiation restarts. A node that ever sent a CS must handle that commitment being broadcast at any time.

### 1.8 Close
- **`shutdown` (38):** not before `funding_created`/`funding_signed`; not while updates are pending on the receiver's commitment; once only; no `update_add_htlc` after it; no `update_*` at all once no HTLCs remain and no RAA is owed. Script: P2WPKH/P2WSH; segwit v1–16 only with `option_shutdown_anysegwit`; OP_RETURN only with `option_simple_close`; must equal a non-empty upfront script. The receiver replies with `shutdown` once it has no outstanding updates; an upfront mismatch fails the connection.
- **Legacy `closing_signed` (39):** the funder proposes first once HTLCs are cleared; `fee_range` (TLV 1) is optional; signature must be valid for either tx variant; converge by echo / strictly-between / range overlap rules (02 §closing_signed); outputs below the script's dust threshold fail the channel.
- **`option_simple_close` (40/41):** each side builds its own closing tx and pays its own fee; TLVs `closer_output_only` (1), `closee_output_only` (2), `closer_and_closee_outputs` (3); sequence 0xFFFFFFFD, locktime from the message.

### 1.9 BOLT 3
| Item | Rule |
|---|---|
| HTLC-timeout / HTLC-success | v2; locktime `cltv_expiry` / 0; one input, sequence **1 with anchors, else 0**; output `floor(amount_msat/1000) − fee` to P2WSH `OP_IF <revocationpubkey> OP_ELSE <to_self_delay> OP_CSV OP_DROP <local_delayedpubkey> OP_ENDIF OP_CHECKSIG` |
| HTLC tx fee | **0 with anchors**; else `floor(feerate×663/1000)` (timeout), `floor(feerate×703/1000)` (success) |
| Commitment fee | `floor(feerate × (724, or 1124 with anchors, + 172×untrimmed)/1000)`; funder pays it and **both** 330-sat anchors |
| Trimming | HTLC trimmed if `floor(amount_msat/1000) < holder_dust + htlc_tx_fee`; to_local **and** to_remote use the **holder's** dust limit; trimmed value goes to fee; outputs rounded down |
| Ordering | BIP69 + `cltv_expiry` tie-break (exists: `TransactionOutputComparer`) |
| Closing tx (legacy) | v2, locktime 0, sequence 0xFFFFFFFF, fee from funder, drop outputs below dust, BIP69 |
| Vectors | Appendix C (commit + HTLC txs + HTLC sigs), D (shachain), E (keys), F (anchors); fee example (feerate 5000, dust 546 → timeout 3315, success 3515, base 5340, actual 7140) |

---

## 2. Current state (verified 2026-09-25 on `wip/fafo`)

### 2.1 What exists
- **Wire:** all normal-operation messages, `channel_reestablish`, `shutdown`, `closing_signed` + serializers + `IMessageFactory.Create*` (`src/NLightning.Domain/Protocol/Interfaces/IMessageFactory.cs`). Missing: `closing_complete`/`closing_sig`, CS TLV `funding_txid`, reestablish TLV 5.
- **Commitment building:** `CommitmentTransactionModelFactory` (`src/NLightning.Domain/Bitcoin/Transactions/Factories/`), `CommitmentTransactionBuilder` (`src/NLightning.Infrastructure.Bitcoin/Builders/`), output scripts in `src/NLightning.Infrastructure.Bitcoin/Outputs/`. Appendix C commitment **signatures** pass (`test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs`), but only with hand-adjusted balances; `ExpectedCommitTx0` is used by three unit tests, `ExpectedCommitTx1..15` are unreferenced; `Bolt3AppendixFVectors.cs` is unused; there are no HTLC-tx vectors.
- **Keys:** `KeyDerivationService` (`DerivePrivateKey(basepointSecret, point)` exists), `CommitmentKeyDerivationService`, shachain `SecretStorageService` (`src/NLightning.Infrastructure/Protocol/Services/`, memory-only, Appendix D tested).
- **Signer:** `ILightningSigner` / `LocalLightningSigner` sign and verify input 0 with the funding key, SIGHASH_ALL, low-S enforced. Reusable for commitment and closing txs. No HTLC API.
- **Persistence:** `ChannelEntity`, `ChannelConfigEntity`, `ChannelKeySetEntity`, `HtlcEntity` (PK `ChannelId, HtlcId, Direction`; stores `AddMessageBytes`). `RevocationWatchEntity` is unmapped.
- **Publishing:** `IBlockchainMonitor.PublishAndWatchTransactionAsync` (used in `FundingSignedMessageHandler`).
- **Onion core:** ONION M1/M2 (Sphinx, hop payloads, validator, replay cache), not called by anything.
- **Docker:** `test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs` (bitcoind + alice/bob/carol LND 0.20.0-beta); `ChannelOpeningFlowTests` opens NLightning → alice with **no push** and checks only NLightning's state.

### 2.2 Pre-existing bugs that gate HTLC work
All verified in code unless marked. "Gate" = the milestone task that must fix it.

| # | Bug | Evidence | NL | Gate |
|---|---|---|---|---|
| G1 | Every non-open channel message is ignored (interim: `default` throws a channel-scoped `ChannelWarningException`, so the peer gets a `warning` for that channel and stays connected; it used to fail every channel with an all-zero `error`) | `ChannelManager.HandleChannelMessageAsync` switch, `CreateNotImplementedWarning` | NL-031 | N6 (reestablish: N7, close: N10) |
| G2 | Second local per-commitment point built from **index 1** instead of 2^48−2 | `FundingConfirmedMessageHandler.cs:52-54` passes `CommitmentNumber.Value` to `GetPerCommitmentPoint`, whose `commitmentNumber` parameter is used as the raw index (`LocalLightningSigner.cs:127-139`) | NL-187 | N1-T1 |
| G3 | Commitment number mutated at confirmation, every block | `CommitmentNumber.Increment()` in the handler above; `ConfirmUnconfirmedChannels` re-runs it for `ReadyForUs`; the handler only logs a wrong state (`:43-47`) | NL-050, NL-069 | N0-T7, N1-T1 |
| G4 | One `CommitmentNumber` shared by both commitments; reload rebuilds it from `LocalRevocationNumber + 1` | `ChannelModel.CommitmentNumber`; factory `:225`; `ChannelDbRepository.cs:237-239` | NL-188, NL-127 | N1-T1, N1-T5 |
| G5 | Peer's second per-commitment point never stored | `ChannelReadyMessageHandler.cs:62` checks `CurrentPerCommitmentIndex == 0`; remote index starts at `FirstPerCommitmentIndex` (`ChannelKeySetModel.cs:29`) | NL-051 | N1-T2 |
| G6 | `LocalNextHtlcId`/`RemoteNextHtlcId` start at 1 | `ChannelFactory.cs:133,253` | NL-190 | N1-T3 |
| G7 | `ChannelConfig` is one-sided: one reserve / htlc_minimum / max_accepted / max_in_flight / to_self_delay for both directions. Non-initiator copies the opener's values (`ChannelFactory.cs:115-120`) and **echoes them back** in `accept_channel` (`OpenChannel1MessageHandler.cs:88-97`); initiator keeps its own limits but takes the peer's `to_self_delay` (`AcceptChannel1MessageHandler.cs:137-150`); the factory uses that one delay for both commitments (`:200`) | The open test pushes 0, so the peer's commitment has no to_local output and the mismatch is invisible **(inferred)** | NL-194 | N1-T4 |
| G8 | Balances persisted as whole satoshis | `ChannelEntity.cs:96,101` (`decimal`, SqlServer `bigint`), written from `LightningMoney.Satoshi` | NL-191 | N1-T5 |
| G9 | HTLCs don't reload; remote funding key replaced; HTLC signature never written | `ChannelDbRepository.cs:201-223` (`byte.Equals(enum)`), `:230-231`; `HtlcDbRepository.cs:63,70,~81` | NL-125, NL-126, NL-128 | N1-T5 |
| G10 | Channel update sends the whole graph (HTLC children) through `DbSet.Update` | `ChannelDbRepository.UpdateAsync` → `BaseDbRepository.Update:103-133`; new HTLC rows would be issued as UPDATEs **(inferred)** | NL-192 | N5-T2 |
| G11 | One channel's messages are handled concurrently; replies are fire-and-forget | `PeerManager.cs:299-301` `ContinueWith`, `:379`; no per-channel lock | NL-033 | N0-T3 |
| G12 | A handler returns one message; out-of-band sends are unordered | `IChannelMessageHandler<T>.HandleAsync` → `Task<IChannelMessage?>`; `HandleResponseMessageReady` | NL-193 | N0-T3 |
| G13 | Transport encrypts before the write lock; reads use `ReadAsync` | `TransportService.cs:201-209,267,291` | NL-105, NL-104 | N0-T2 |
| G14 | Commitment math wrong for real traffic: every HTLC debited from to_local (`:131`); to_remote trimmed with the peer's dust (`:203`); anchors: HTLC trim fee uses 666/706 instead of 0 (`:107`, `WeightConstants.cs:30-33`), base weight 1116 and one anchor deducted (`:173,243-255`); throws when both outputs are below reserve (`:193`) | `CommitmentTransactionModelFactory.cs` | NL-062, NL-061, NL-195, NL-196 | N2-T1 |
| G15 | Weight selection inverted for anchors in validator/factory | `ChannelOpenValidator.cs:108-110`, `ChannelFactory.cs:183-185` | NL-044 | N2-T1 |
| G16 | No HTLC tx builder; `HtlcResolutionOutput` swaps revocation/delayed keys; `BaseOutput.Amount` setter no-op | `Transactions/Htlc*Transaction.cs` commented out; `Outputs/HtlcResolutionOutput.cs:14-23`; `BaseOutput.cs:24` | NL-056, NL-058, NL-059 | N2-T4 |
| G17 | Signer has no HTLC API; builder returns no HTLC→vout map | `ILightningSigner.cs`; `CommitmentTransactionBuilder` returns only `SignedTransaction` | NL-057 | N2-T3, N3-T1 |
| G18 | Local key derivation computes the **current** per-commitment secret; nothing prevents releasing an unrevoked secret | `CommitmentKeyDerivationService.cs:27` calls `ReleasePerCommitmentSecret` and returns it in `CommitmentKeys` | NL-189 | N3-T2 |
| G19 | Remote shachain memory-only; `LoadFromIndex` throws | `SecretStorageService.cs:160,166`; `ChannelKeySetModel.LastRevealedPerCommitmentSecret` keeps one secret | NL-136, NL-066 | N3-T4 |
| G20 | Channels registered only after a successful connect, fire-and-forget; a failed connect skips them | `PeerManager.StartAsync:63-84` | NL-201, NL-052 | N1-T6 |
| G21 | No failed-channel state; `ChannelErrorException` always just disconnects | `PeerManager.HandleChannelMessageResponseAsync` | NL-200 | N6-T3 |
| G22 | Wire bugs: `TlvConstants.NextFunding = 0` (spec: type 1, 33 bytes with `retransmit_flags`), TLV 5 missing; `closing_signed` requires `fee_range`; CS has no `funding_txid` TLV | `TlvConstants.cs:93`, `NextFundingTlv.cs`; `ClosingSignedMessageTypeSerializer.cs:61-65`; `CommitmentSignedMessage.cs` | NL-197, NL-198, NL-199 | N0-T6 |
| G23 | Even gossip types (256/258) throw and kill the peer; LND sends a direct `channel_update` after a private channel opens **(unverified)** | `MessageSerializer.cs:51,74` | NL-100 | N0-T5 |
| G24 | Advertised but unimplemented: `option_quiesce` (stfu dropped), `option_route_blinding`, `option_attribution_data`, `option_dual_fund`, `basic_mpp`; `option_data_loss_protect` Compulsory with no reestablish | `FeatureOptions.cs:14,40,55,65,67,69` | NL-109, NL-074, NL-019, NL-042, NL-081, NL-035 | N0-T4, N7 |
| G25 | Block loop: `ForgetStaleChannels` has no state filter | `ChannelManager.cs:189-205` | NL-049 | N0-T7 |
| G26 | Application.Tests / Daemon.Tests not discovered by `dotnet test` | csproj lacks `xunit.runner.visualstudio` | NL-167 | N0-T1 |
| G27 | `LightningMoney` is a mutable reference class (aliasing hazard in commitment math) | `LightningMoney.cs:7,19-25` | NL-202 | N4-T1 (engine uses `ulong` msat) |

### 2.3 Draft disagreements resolved against the code
| Topic | Drafts said | Code says |
|---|---|---|
| Balance column type | "decimal sats" vs "long / bigint" | C# `decimal LocalBalanceSatoshis`, SqlServer `bigint`, written from `LightningMoney.Satoshi` (floor). Both lose msat. |
| Initiator `ChannelConfig` | "keeps our values except dust" vs "holds the peer's to_self_delay" | Keeps our reserve/limits, but also takes the peer's `ToSelfDelay` and dust (`AcceptChannel1MessageHandler.cs:145`). |
| Per-commitment API | "index-based" vs "conflates number and index" | Parameter is named `commitmentNumber` and is passed unchanged as the BOLT 3 index. Callers pass numbers (bug G2). |
| Startup | "failed connect skips registration" vs "connect happens before registration" | Both: connect first, then fire-and-forget `RegisterExistingChannelAsync`; a failed connect `continue`s. |
| `next_funding` / CS `funding_txid` | one draft only | Confirmed in bolts master: reestablish TLV 1 = `next_funding_txid ‖ retransmit_flags`, TLV 5 `my_current_funding_locked`; `commitment_signed` TLV 1 `funding_txid` MUST be set. |
| `ExpectedCommitTx*` "unreferenced" | one draft | `ExpectedCommitTx0` is used by 3 unit tests; 1..15 are unreferenced. |
| `HtlcDbRepository` byte/enum compare | one draft | Confirmed at `:63,70`. |
| New `ChannelState` numbers, `ClientCommand` numbers | three different proposals | Current values: states 0,1,2,3,10,20,21,22,30,40,50 (`UpdateState` strictly increasing); commands 0–7. Choice in §3.8, §3.9 and D11. |

---

## 3. Design

### 3.1 Layering
- **Domain** (`src/NLightning.Domain/Channels/Commitments/`, BCL only): the pure `Commitments` aggregate (per-HTLC state machine, `CommitmentSpec` reduce, update validation, commit/revoke transitions), `ReestablishPlanner`, closing negotiators, `ShutdownScriptValidator`, `HtlcDeadlinePolicy`. No I/O; crypto through ports (`ICommitmentSigner`, `ICommitmentVerifier`, `IPerCommitmentSecretVerifier`).
- **Infrastructure.Bitcoin:** `CommitmentFeeCalculator` consumers, HTLC and closing tx builders, signer extensions, port implementations.
- **Application** (`src/NLightning.Application/Channels/`): thin handlers, `ChannelLockProvider`, `PeerOutbox`, `ChannelOperationsService : IChannelOperations`, `CommitScheduler`, `ReestablishService`, `ChannelCloseService`, `ChannelFailureService`, `HtlcExpiryMonitor`, `FeeUpdateScheduler`, and the HTLC switch seam.
- **Repositories/Persistence:** `ChannelStateDbRepository.ApplyAsync(ChannelTransition)` with explicit per-row writes; remote shachain table.

### 3.2 Channel model changes
- **`ChannelParams { Local, Remote }`** (`Channels/ValueObjects/ChannelParty.cs`, `ChannelParams.cs`) replaces `ChannelConfig`. Direction rules (XML-documented): `Local.HtlcMinimum/MaxAcceptedHtlcs/MaxHtlcValueInFlight` are what **we announced** and bind HTLCs the peer offers us; `Remote.*` bind our offers; `Local.ChannelReserve` is what we require the peer to keep; `Local.ToSelfDelay` is the delay we impose on the **peer's** to_local, so the peer's commitment uses `Local.ToSelfDelay` and ours uses `Remote.ToSelfDelay`; `Local.DustLimit` applies to our commitment. Shared: feerate, anchors, scid alias, minimum depth, upfront scripts.
- **Commitment numbers:** `LocalCommitmentNumber`, `RemoteCommitmentNumber` (both 0 after open). `CommitmentNumber` becomes an immutable obscuring helper built from **(opener, accepter)** basepoints: `Obscure(n)`, `LockTime(n)`, `Sequence(n)`; `Increment()` is deleted. `PerCommitmentIndex.From(n) = 2^48 − 1 − n` lives in Domain; every signer API takes numbers.
- **Remote points:** `RemoteCurrentPerCommitmentPoint` (for remote commitment R) and `RemoteNextPerCommitmentPoint` (for R+1). `channel_ready` sets Next; RAA verifies `secret·G == Current`, then `Current ← Next`, `Next ← next_per_commitment_point`.
- **Money:** the engine uses `ulong` msat with `checked` arithmetic (overflow = fail the channel). `LightningMoney` stays at the edges.
- `ChannelModel` keeps identity/params/keys and holds a `Commitments` snapshot; memory-side mutation happens only by swapping in the snapshot returned by the engine **after** persistence (I2).

### 3.3 Commitment state machine (Domain, pure)
**Per-HTLC state** (authoritative, one byte, persisted in `HtlcEntity.State`): core-lightning's `htlc_state`, which maps 1:1 to the five spec stages. Transitions happen only on six events (`SendCommit`, `RecvCommit`, `SendRevoke`, `RecvRevoke`, `SendRemove`, `RecvRemove`); `HtlcStateTable` (static) answers `IsInLocalCommit`, `IsInRemoteCommit`, `Owner`, `IsFinal`, `IsRemoval`, `Next(state, event)`. An illegal transition throws `ChannelErrorException`. Values start at 10 so legacy values 0–3 stay decodable (N5 migration maps them).

| We offered | L | R | Next | | They offered | L | R | Next |
|---|---|---|---|---|---|---|---|---|
| `SentAddHtlc` 10 | – | – | send CS → 11 | | `RcvdAddHtlc` 30 | – | – | recv CS → 31 |
| `SentAddCommit` 11 | – | ✓ | recv RAA → 12 | | `RcvdAddCommit` 31 | ✓ | – | send RAA → 32 |
| `RcvdAddRevocation` 12 | – | ✓ | recv CS → 13 | | `SentAddRevocation` 32 | ✓ | – | send CS → 33 |
| `RcvdAddAckCommit` 13 | ✓ | ✓ | send RAA → 14 | | `SentAddAckCommit` 33 | ✓ | ✓ | recv RAA → 34 **lock-in** |
| `SentAddAckRevocation` 14 **locked** | ✓ | ✓ | recv remove → 15 | | `RcvdAddAckRevocation` 34 | ✓ | ✓ | send remove → 35 |
| `RcvdRemoveHtlc` 15 | ✓ | ✓ | recv CS → 16 | | `SentRemoveHtlc` 35 | ✓ | ✓ | send CS → 36 |
| `RcvdRemoveCommit` 16 | – | ✓ | send RAA → 17 | | `SentRemoveCommit` 36 | ✓ | – | recv RAA → 37 |
| `SentRemoveRevocation` 17 | – | ✓ | send CS → 18 | | `RcvdRemoveRevocation` 37 | ✓ | – | recv CS → 38 |
| `SentRemoveAckCommit` 18 | – | – | recv RAA → 19 **final** | | `RcvdRemoveAckCommit` 38 | – | – | send RAA → 39 **final** |

- **Irrevocability:** incoming add locked in at 34; outgoing removal irrevocable at 19 (only then may a failure propagate upstream); a received fulfill is acted on immediately (preimage is final knowledge).
- **Fee updates** use the same state set (owner = funder) in a `FeeUpdate` list; a commitment's feerate is the last fee update included in it.
- **Pending changes:** "changes for remote" = any HTLC or fee in a state that `SendCommit` would move (10, 17, 32, 35). A received CS that moves nothing (and changes no fee) is a protocol error. Only one outstanding CS per direction: never sign while waiting for RAA (`RemoteNextCommit` holds the unacked remote commitment).
- **`CommitmentSpec(ulong Number, uint FeeratePerKw, ulong ToLocalMsat, ulong ToRemoteMsat, IReadOnlyList<SpecHtlc>)`** is built by `Reduce(base, htlcs, fees, side)`: an HTLC present in the view is debited from **its offerer**; a removal-state fulfill moves the amount to the receiver, a fail refunds; final HTLCs fold into the base balances and are archived.
- **`Commitments` API:** `SendAdd`, `ReceiveAdd`, `SendFulfill/Fail/FailMalformed`, `ReceiveFulfill/Fail/FailMalformed`, `SendFee`, `ReceiveFee`, `CanSendCommit`, `SendCommit(ICommitmentSigner)`, `ReceiveCommit(msg, ICommitmentVerifier)`, `SendRevoke(signer)`, `ReceiveRevoke(msg, IPerCommitmentSecretVerifier)`, `RevertUncommitted()`, `IsIrrevocablyCommitted(htlc, AddOrRemove)`. Each returns `CommitmentsResult { Commitments Next; IReadOnlyList<IChannelMessage> Outbound; IReadOnlyList<IChannelDomainEvent> Events; ChannelTransition Persist }` or throws `ChannelWarningException`/`ChannelFailedException`.
- **`UpdateValidator`** evaluates §1.2/§1.3/§1.6 against the **prospective** spec of the relevant side (sender reserve, funder fee incl. anchors, fee-spike buffer when we send, max accepted, in-flight, minimum, cltv < 5e8, dust exposure). **`CommitmentFeeCalculator`** holds the BOLT 3 formulas verbatim.

### 3.4 Persistence ordering and crash consistency
Invariants (each has a test; N4-T5 harness checks all after every step):

| ID | Invariant |
|---|---|
| I1 Persist-before-send | No committing message (`update_*`, CS, RAA, `shutdown`, `closing_*`) reaches the wire before the transition that produced it is committed in **one** `SaveChangesAsync`. |
| I2 Memory-after-disk | The in-memory snapshot is replaced only after the save succeeds; a failed save leaves the old state and sends nothing. |
| I3 No live secret | `per_commitment_secret(n)` is released only if local commitment `n+1` with verified remote signatures is persisted. |
| I4 No revoked broadcast | Only the latest local commitment is ever signed for broadcast; never after data loss. |
| I5 Broadcastable | The persisted state always holds the latest local commitment + remote sig + HTLC sigs. |
| I6 Conservation | `to_local + to_remote + Σ htlc == funding_msat` for every spec (before fees/anchors). |
| I7 Agreement | In the two-engine harness both sides build byte-identical txs for every commitment both signed. |
| I8 Irrevocability gates | Lock-in events only after persist; remove only after lock-in; upstream failure only after removal is irrevocable. |
| I9 Monotonic | HTLC ids, commitment and revocation numbers never decrease or repeat, across restarts. |
| I10 Preimage durability | A received preimage is persisted before any use. |
| I11 Reestablish soundness | After any crash point, reload + reestablish converge without `error` (except the data-loss test). |
| I12 Data-loss safety | After proven data loss we never sign or broadcast our commitment. |

| Event | Persist atomically | Send after persist | Crash window |
|---|---|---|---|
| Local add/fulfill/fail/fee | update row (state 10/35 or fee), next id | `update_*` | Peer never saw it; re-sent with the same id before the next CS on reestablish |
| Local sign | `RemoteNextCommit` (spec, txid, point), HTLC states via `SendCommit`, **`SentCommitDiff`** = wire bytes of the updates since the last CS + the CS, `LastSentOrder` | stored diff | Peer's `next_commitment_number == R+1` → re-send the stored bytes verbatim |
| Remote `update_*` | nothing (memory, lost on disconnect per spec) — **except** `update_fulfill_htlc`: persist the preimage (I10), then raise `OutgoingHtlcFulfilled` | — | Spec reverses unsigned peer updates |
| Remote CS | new local commitment (spec, sig, HTLC sigs, txid), pending peer updates, `LocalCommitmentNumber = L+1`, `LastSentOrder` | RAA(secret(L), point(L+2)) — secret derived **after** the save | RAA is deterministic from the seed; resend when `next_revocation_number == L` |
| Remote RAA | shachain bucket, remote commitment rotation, revoked-commitment record (for BOLT 5), clear `SentCommitDiff`, HTLC states | nothing; then events `IncomingHtlcLockedIn` / `OutgoingHtlcFailed` / settled | Events are re-derived from persisted states on startup (idempotent) |

Batching: `CommitScheduler` debounces signing (~10 ms, configurable) after local updates or a received RAA with pending changes; it never signs while `RemoteNextCommit` exists. SQLite: verify EF's per-`SaveChanges` transaction and set `PRAGMA synchronous=FULL` in `src/NLightning.Infrastructure.Persistence/DependencyInjection.cs` (default durability **unverified**).

### 3.5 Data model and migrations (all three providers)
| Migration (milestone) | Changes |
|---|---|
| `SplitChannelParamsAndMsatBalances` (N1-T5) | `ChannelConfigEntity` → `Local*`/`Remote*` columns (reserve, htlc_min, max_accepted, max_in_flight, to_self_delay, dust) with a data step copying old values; `ChannelEntity.LocalBalanceMsat/RemoteBalanceMsat` (`long`, `= sats × 1000`), drop `*Satoshis`; `LocalCommitmentNumber`, `RemoteCommitmentNumber` (map from revocation numbers); `ChannelKeySetEntity` remote current + next point; fix SqlServer `RemoteNodeId varbinary(33)` (NL-129). |
| `AddRemoteShachain` (N3-T4) | `RemoteShachainEntity` (PK `ChannelId, Bucket 0..48`; `Index`, `Secret`). |
| `AddCommitmentState` (N5-T1) | `ChannelEntity`: `RemoteNextCommitmentNumber?`, `SentCommitDiff?` (blob), `LastSentOrder`, `FeeratePerKw`, `ErrorSent?`, `DataLossDetected`. `CommitmentEntity` (PK `ChannelId, Side, Number`; feerate, to_local/to_remote msat, txid, point, counterparty sig, HTLC sigs blob in output order, status Current/PendingAck). `HtlcEntity`: new `State` values (10–39), `RemovalKind`, `FailReason?`, `FailureCode?`, `Sha256OfOnion?`, `PaymentPreimage`, `OnionSharedSecret?` (M4), drop `Signature` (sigs live on `CommitmentEntity`). `FeeUpdateEntity` (PK `ChannelId, Seq`; feerate, owner, state). Map `RevocationWatchEntity` (commitment number, txid, HTLC outputs blob; `PenaltyTransactionBytes` nullable). Refuse to start if HTLC rows hold unknown legacy states. |
| `AddInvoicesAndPayments` (N8) | `InvoiceEntity` (PK hash; preimage, secret, amount?, bolt11, expiry, state), `PaymentEntity` (PK hash; amount, channel, htlc id, status, preimage?, failure code?, hop shared secret, created). |
| `AddShutdownState` (N10) | `LocalShutdownScript?`, `RemoteShutdownScript?`, `ShutdownSentAt?`, closing negotiation state, `ClosingTxId?`. |

Repositories: `ChannelStateDbRepository.ApplyAsync` writes channel scalars, commitments, HTLC upserts by PK, fee updates, shachain buckets and revocation watch rows explicitly — never `DbSet.Update(graph)`. `ChannelDbRepository.UpdateAsync` stops touching HTLCs. `ISecretStorageService.Load(IEnumerable<(bucket, index, secret)>)` / `Export()` replace `LoadFromIndex`.

### 3.6 Signer API (`ILightningSigner`, `LocalLightningSigner`)
```csharp
CompactPubKey GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber);      // index = 2^48-1-n internally
Secret RevealPerCommitmentSecret(ChannelId channelId, ulong commitmentNumber);         // throws unless n < LocalCommitted
void AdvanceLocalCommitment(ChannelId channelId, ulong newLocalCommitted);             // only after persist
IReadOnlyList<CompactSignature> SignRemoteHtlcTransactions(ChannelId channelId,
    IReadOnlyList<HtlcSigningContext> htlcTxs);                                        // ctx: tx, witness script, amount,
                                                                                       // remote point, anchors → sighash
void ValidateLocalHtlcSignatures(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTxs,
    IReadOnlyList<CompactSignature> signatures);                                       // count, low-S, order
// SignChannelTransaction / ValidateSignature stay (commitment + closing: input 0, funding key, SIGHASH_ALL)
```
- HTLC key: `channelKey.Derive(4', true)` then `IKeyDerivationService.DerivePrivateKey(htlcBaseSecret, point)`; SIGHASH_ALL, or `SINGLE|ANYONECANPAY` with anchors; `MakeCanonical()`.
- `ReleasePerCommitmentSecret` becomes internal; `CommitmentKeys.PerCommitmentSecret` is removed; local keys are derived from the **point**.
- `LocalCommitted` is loaded at `RegisterChannel` from the channel row. Remote secret verification (`secret·G == expected`, shachain insert) is a Domain port `IPerCommitmentSecretVerifier` implemented in Infrastructure.Bitcoin: it needs no private key.

### 3.7 Concurrency, handlers and routing (Application)
- **Inbound order:** `PeerManager` processes each peer's channel messages with one consumer loop that `await`s `ChannelManager.HandleChannelMessageAsync` and enqueues every reply before taking the next message.
- **Per-channel lock:** `ChannelLockProvider` (singleton, `SemaphoreSlim` per `ChannelId`) is held around every mutation: peer messages, IPC operations, block events, reestablish.
- **Outbound order:** `PeerOutbox` (per peer, `System.Threading.Channels`) is the only send path for replies **and** `OnResponseMessageReady`; the lock is held until the transition's messages are enqueued, so wire order equals persist order.
- **Contract:** `IChannelMessageHandler<T>.HandleAsync` returns `Task<IReadOnlyList<IChannelMessage>>`; the five open handlers return a single-element list.
- **Handlers** (`src/NLightning.Application/Channels/Handlers/`, auto-registered) + `ChannelManager` cases: `UpdateAddHtlc`, `UpdateFulfillHtlc`, `UpdateFailHtlc`, `UpdateFailMalformedHtlc`, `CommitmentSigned`, `RevokeAndAck`, `UpdateFee`, `ChannelReestablish`, `Shutdown`, `ClosingSigned` (+ `ClosingComplete`, `ClosingSig` in N11). Each: lock → load snapshot → `Commitments.X` → `ApplyAsync` + `SaveChangesAsync` → `signer.AdvanceLocalCommitment` if applicable → swap snapshot → enqueue outbound → raise events.
- **Lifecycle:** `IChannelManager.OnPeerConnectedAsync(peer)` (after `init`) and `OnPeerDisconnectedAsync(peer)` drive reestablish and `RevertUncommitted()`; updates are gated until the peer's reestablish is processed.
- **Failure:** `ChannelFailedException(channelId, peerMessage)` → persist `State = Failed` + `ErrorSent`, send `error`, refuse updates, re-send the error on reconnect. `ChannelWarningException` keeps today's semantics.

### 3.8 Channel states
`ChannelState` additions (strictly increasing, below `Closed = 40`): `ShuttingDown = 23`, `Negotiating = 25`, keep `Closing = 30` = mutual-close tx broadcast awaiting confirmation, `Failed = 35`. Data loss is a persisted flag (`DataLossDetected`) plus `Failed`, not a state.

### 3.9 IPC commands (append-only `ClientCommand`)
| Value | Command | Milestone |
|---|---|---|
| 8 | `ListChannels` → id, peer, state, local/remote msat, local/remote commitment numbers, pending HTLCs, `DataLossDetected` | N0-T8 |
| 9 | `CreateInvoice(amountMsat?, description, expirySecs)` → bolt11, hash | N8-T2 |
| 10 | `PayInvoice(bolt11, amountMsat?, timeoutSecs)` → status, preimage?, failure code? (direct peer only) | N8-T3 |
| 11 | `CloseChannel(channelId, feeRatePerKw?)` → closing txid | N10-T3 |

Each needs Domain request/response, `[MessagePackObject]` DTOs in `src/NLightning.Transport.Ipc`, a Daemon IPC handler, a scoped client handler (Docker tests call these directly), and CLI output (Daemon `CLAUDE.md` recipe). Assign the next free value at implementation time if the order changes; never renumber.

### 3.10 Onion seam (ONION M3/M4)
- **`IChannelOperations`** (Domain, implemented by `ChannelOperationsService`): `OfferHtlcAsync(channelId, amountMsat, hash, cltv, onion, BlindedPathTlv?, circuitRef)` (persists add + circuit reference atomically, returns id; completes on resolution), `FulfillHtlcAsync`, `FailHtlcAsync(reasonBytes)`, `FailMalformedHtlcAsync(code, sha256OfOnion)`, `UpdateFeeAsync`, `ShutdownAsync`, `RecordOnionSecretAsync`. Each enforces §3.3 preconditions (I8).
- **Events** (`IChannelDomainEvent`, raised after persist, replayed on startup): `IncomingHtlcLockedIn(channel, htlcId, add)`, `OutgoingHtlcFulfilled(channel, htlcId, preimage)` (immediate), `OutgoingHtlcFailed(channel, htlcId, reason | malformed)` (only when the removal is irrevocable), `OutgoingHtlcSettled` (pruning).
- **`IHtlcSwitch`** subscribes to those events. N6 ships `LocalOnlyHtlcSwitch` (fail every locked-in HTLC with an encrypted `temporary_node_failure`, needs ONION M3-T1/T2 create); N8 ships `FinalHopHtlcSwitch` (peel → replay record → deserialize → validate → invoice checks; non-final → `unknown_next_peer`); ONION M4-T4/T5 later replace the non-final branch by offering an outgoing HTLC through `IChannelOperations` — no channel-layer change.
- The ONION M4 circuit table references `HtlcEntity (ChannelId, HtlcId, Direction)`; `OnionSharedSecret` is stored in N5 (or recomputed from `AddMessageBytes`).

### 3.11 Reestablish algorithm (`ReestablishPlanner`, pure)
**Send** after `init`, per channel: `next_commitment_number = L + 1`; `next_revocation_number = R` (the number of the remote commitment the peer must revoke next); `your_last_per_commitment_secret = R == 0 ? zeros : shachain(R − 1)`; `my_current_per_commitment_point = point(L)`. The channel is `AwaitingReestablish` (outbound queued, inbound updates → warning) until the peer's message is processed.

**Receive** (X = their `next_commitment_number`, Y = their `next_revocation_number`, S = their secret), in this order:
1. `X == 0` → `ChannelFailed(mustBroadcast: true)`.
2. **Data loss first:** `Y > L` and `S == secret(Y − 1)` from our seed → persist `DataLossDetected`, send `error`, never sign or broadcast our commitment (I12), alert the operator.
3. `L > 0` → `S` must equal `secret(L − 1)`; `L == 0` → zeros. Otherwise fail.
4. **RAA:** `Y == L − 1` → resend RAA (regenerated); `Y == L` → none; else fail.
5. **CS:** `RemoteNextCommit != null && X == R + 1` → resend `SentCommitDiff` verbatim; `X == R + 1` with nothing pending, or `X == R + 2` with a pending CS the peer already has (it will resend its RAA) → OK; else fail.
6. Emit 4 and 5 in `LastSentOrder`; then persisted-but-unsigned local updates (states 10/35, fee) with their original ids; then `shutdown` if sent.
7. `X == 1` on both sides → retransmit `channel_ready`.

**Disconnect** (`RevertUncommitted`): drop `RcvdAddHtlc` rows and unsigned peer fee updates; `RcvdRemoveHtlc` → back to `SentAddAckRevocation` (a fulfill's preimage stays persisted); our unsigned updates stay and are re-sent. Startup registers every non-Closed channel as `AwaitingReestablish` (N1-T6).

### 3.12 Close
`ChannelCloseService` + `ShutdownMessageHandler`: persist script and `ShuttingDown`, then send; validate script form and upfront script; refuse adds both ways after shutdown; sign pending changes before replying. `LegacyClosingNegotiator` (pure) runs once both shutdowns are exchanged and nothing is pending; `ClosingTransactionBuilder` (`BuildLegacy`, later `BuildSimple`); persist the fully signed tx before broadcast via `PublishAndWatchTransactionAsync`; confirmation → `Closed`. Do not revive `Transactions/ClosingTransaction.cs` (delete it).

---

## 4. Decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | **Per-HTLC state = core-lightning `htlc_state` enum** (table-driven, 1 byte, persisted), inside a pure eclair-style `Commitments` aggregate that returns `{Next, Outbound, Events, Persist}` | One byte per HTLC maps 1:1 to the spec's stages, is trivially persisted and queried, and every transition is a table row that can be tested exhaustively. The aggregate API keeps handlers thin and the engine pure. | LND commit heights (four nullable heights per update: more columns, derived state easy to get wrong); eclair message buckets (natural in Scala with blobs, awkward relationally). |
| D2 | **Per-channel lock + per-peer ordered inbound loop + per-peer `PeerOutbox`** | Gives the same guarantees as an actor with a much smaller change to `ChannelManager`/`PeerManager` and the scoped-handler DI pattern. | A `ChannelActor` per channel (safer-sounding but restructures dispatch, DI scopes and the open flow at once). |
| D3 | **Persist-before-send, one `SaveChangesAsync` per transition, memory swapped after save; RAA secret derived after the save** | The spec's own durability rule; makes every crash window recoverable by reestablish (§3.4). | Send-then-persist (loses revocation secrets / signed commitments on crash). |
| D4 | **Store the sent CS diff as wire bytes** for retransmission; regenerate RAA from the seed | Retransmission must be byte-identical; stored bytes survive logic changes and serializer refactors. RAA is deterministic and needs no storage. | Re-deriving updates from the log on reestablish (any logic change breaks retransmission). |
| D5 | **MVP channel type: `option_static_remotekey` only, no anchors.** Anchor math is fixed and vector-tested (N2-T5, N3-T1) but `OptionAnchors` stays No until BOLT 5 CPFP exists | Matches what we negotiate today (`FeatureOptions.OptionAnchors = No`, `FeatureSet.NewBasicChannelType()`); HTLC txs are plain SIGHASH_ALL. | Anchors first (zero-fee HTLC txs and anchors are useless without CPFP). |
| D6 | **Signer API takes commitment numbers, not indices**, with a secret-release guard inside the signer | Removes the G2 bug class by construction and keeps the guard valid for a future remote signer. | Keeping index-based API with helper overloads (callers can still pass the wrong unit). |
| D7 | **One outstanding CS per direction** | Simpler state; LND does the same. | Pipelining several CS. |
| D8 | **Milestone order: safety gates (N0, N1) → BOLT 3/signer → pure engine → persistence → wiring → reestablish → payments → fees/deadlines/fail → close**, each ending with a proof and, where an LND peer is involved, a Docker test | Keeps the fund-safety gates first (safety draft) while every behaviour milestone ends with an LND interop proof (interop draft). Reestablish precedes real payments because LND reestablishes on every reconnect and restart tests need it. | Interop-first (payments before reestablish, no gates) or engine-only (no interop until the end). |
| D9 | **`update_fee` in the engine from N4, handler in N6, scheduler in N9.** MVP Docker tests use NLightning-funded channels (we never *receive* `update_fee`); LND-funded channels are proven in N9 | Receive-side rules are pure and cheap to test early; LND funder behaviour needs the scheduler anyway. | Deferring all `update_fee` work (LND-funded channels would disconnect). |
| D10 | **Fail-the-channel:** until N9, send `error` + persist `Failed` + CRITICAL log (regtest only via `EnableHtlcs`); N9 broadcasts the latest local commitment through a single `ChannelFailureService` (the only broadcast path, which enforces I4/I12). Sweeps stay in BOLT 5 (NL-094) | Real funds need a broadcast; one path makes "never broadcast revoked" auditable. | Broadcasting from handlers ad hoc. |
| D11 | **New states** `ShuttingDown 23`, `Negotiating 25`, `Failed 35`; data loss as a flag | Fits the strictly increasing `UpdateState`; data loss can coexist with Failed. | `DataLossDetected` as a state (can't combine with Failed); repurposing `Closing` for negotiation. |
| D12 | **Legacy `closing_signed` first, `option_simple_close` optional (N11)** | What LND runs by default; simple close is feature-gated (`OptionSimpleClose = No`). | Simple close only. |
| D13 | **`long` msat balance columns** | Fits any real channel; SQL-friendly across providers. | `ulong`/`decimal(20,0)`. |
| D14 | **Payments over a direct channel are in scope (N8)** as the minimal ONION M4 slice (single-hop final receive and send) | An end-to-end interop proof needs a settled payment; forwarding stays in ONION M4-T4/T5. | Leaving all payments to the ONION plan (no settle-path proof for the channel layer). |

---

## 5. Milestones

### N0: Safety gates I — plumbing, wire and feature hygiene (no HTLC behaviour)
| Task | Files | Acceptance |
|---|---|---|
| **N0-T1** Test discovery. Resolves NL-167 | `test/NLightning.Application.Tests/*.csproj`, `test/NLightning.Daemon.Tests/*.csproj` (`xunit.runner.visualstudio` 3.1.5, `IsTestProject`) | `dotnet test` discovers the 24 + 23 tests |
| **N0-T2** Transport. Resolves NL-104, NL-105 | `src/NLightning.Infrastructure/Transport/Services/TransportService.cs` (hold `_networkWriteSemaphore` across encrypt+write; `ReadExactlyAsync` for header and body) | `test/NLightning.Infrastructure.Tests/Transport/Services/TransportServiceTests`: 1000 concurrent sends decrypt in order; a 40 KB message fed in 1-byte chunks is received |
| **N0-T3** Ordered processing, multi-message replies, outbox, lock. Resolves NL-033, NL-193; partial NL-108 | `PeerManager.cs` (per-peer consumer loop, `ConcurrentDictionary` peers), `IChannelMessageHandler.cs` (list return) + 5 open handlers, `IChannelManager`/`ChannelManager.cs`, new `Application/Node/Services/PeerOutbox.cs`, `Application/Channels/Services/ChannelLockProvider.cs` | `AT/Node/Managers/PeerManagerTests.Given_HandlerReturnsThree_Then_SentInOrder`, `…Given_EventAndReplyInterleave_Then_FifoPreserved`; `AT/Channels/Managers/ChannelManagerConcurrencyTests.Given_TwoMessagesSameChannel_Then_Serialized`; open-flow tests unchanged |
| **N0-T4** Feature hygiene. Resolves NL-074; partial NL-109, NL-019, NL-042, NL-081 | `src/NLightning.Domain/Node/Options/FeatureOptions.cs`: Quiesce, RouteBlinding, AttributionData, DualFund, BasicMpp → No (keep gossip_queries until NL-100; keep data_loss_protect, fixed by N7) | `DT/Node/FeatureOptionsTests.Given_Defaults_Then_UnimplementedNotAdvertised` |
| **N0-T5** Gossip parse-and-drop. Resolves NL-100 | message/payload types for 256–259 capturing raw bodies; serializers in both factory dictionaries; `PeerService.HandleMessage` drops them with a debug log | serializer round trips; receiving 258 raises nothing |
| **N0-T6** Wire fixes. Resolves NL-197, NL-198, NL-199; partial NL-001 | `CommitmentSignedMessage` + `FundingTxIdTlv` (type 1) + converter + `CommitmentSignedMessageTypeSerializer` (strict {1}) + `MessageFactory`; `TlvConstants.NextFunding = 1`, `NextFundingTlv` = txid ‖ `retransmit_flags`, `MyCurrentFundingLocked = 5` (parse, ignore), reestablish serializer strict {1,5}; `ClosingSignedMessage` nullable `FeeRangeTlv`, serializer strict {1} | `ST/Messages/CommitmentSignedMessageTests` (with/without TLV; unknown even rejected); `ST/Messages/TxChannelReestablishMessageTests` (fixture fixed to type 1, TLV 5 ignored); `ST/Messages/ClosingSignedMessageTests.Given_NoFeeRange_Then_Ok` |
| **N0-T7** Block-loop fixes. Resolves NL-049, NL-050 | `FundingConfirmedMessageHandler.cs` (return on wrong state, idempotent), `ChannelManager.ConfirmUnconfirmedChannels`, `ForgetStaleChannels` (unconfirmed opening states only, explicit creation height) | `AT/…/FundingConfirmedMessageHandlerTests.Given_ReadyForUs_When_NewBlock_Then_NoResendNoIncrement`; `…ForgetStaleChannels_Given_OpenChannel_Then_NotStale` |
| **N0-T8** Docker harness + `ListChannels`. Resolves NL-156; partial NL-152 | `test/NLightning.Integration.Tests/Docker/Utils/NLightningTestNode.cs` (DI from `(dbPath, ISecureKeyManager, port)`, Start/Stop/Dispose, reuses the same key manager and SQLite file on restart); migrate `ChannelOpeningFlowTests`/`AbcNetworkTests`; move layer registrations into each `DependencyInjection.cs`; `ClientCommand.ListChannels = 8` (§3.9) | Docker tests compile and pass; `DaemonTests` cover the IPC handler |

**Proof N0:** unit tests above + Docker `NormalOperationFlowTests.Given_NewChannel_When_Idle_Then_PeerStaysConnected`: NLightning opens 1,000,000 sat (no push) to alice, mine 6, wait 30 s; LND lists the channel `Active` for our node id (select by channel point), the peer is still connected, NLightning is `Open`, and `channel_ready` was sent once.

### N1: Safety gates II — channel-state correctness on the open path
| Task | Files | Acceptance |
|---|---|---|
| **N1-T1** Commitment numbers and indices. Resolves NL-187, NL-188, NL-069 | `PerCommitmentIndex` (Domain), `CommitmentNumber` (immutable opener/accepter helper, no `Increment`), `ChannelModel` (`LocalCommitmentNumber`, `RemoteCommitmentNumber`), `ILightningSigner`/`LocalLightningSigner.GetPerCommitmentPoint(channelId, n)`, `FundingConfirmedMessageHandler`, `AcceptChannel1`/`FundingCreated`/`FundingSigned` handlers, factory takes the explicit number | `channel_ready.second_per_commitment_point == point(index 2^48−2)`; numbers never mutate at confirmation; `BT/Signers/LocalLightningSignerTests.Given_Number1_Then_PointAtIndexFirstMinus1`; Appendix C still green |
| **N1-T2** Remote next point. Resolves NL-051 | `ChannelKeySetModel` (`CurrentPerCommitmentPoint`, `NextPerCommitmentPoint`), `ChannelReadyMessageHandler.cs` | `AT/…/ChannelReadyMessageHandlerTests.Given_ChannelReady_Then_RemoteNextPointStored` |
| **N1-T3** HTLC ids start at 0. Resolves NL-190 | `ChannelFactory.cs:133,253`, `Bolt3IntegrationTests.GetTestChannelModel` | `DT/Channels/Factories/ChannelFactoryTests.Given_NewChannel_Then_NextHtlcIdsZero` |
| **N1-T4** `ChannelParams` split. Resolves NL-194 | `ChannelParty`/`ChannelParams` (§3.2) replacing `ChannelConfig` in `ChannelFactory`, `OpenChannel1MessageHandler` (accept_channel carries **our** `NodeOptions` values), `AcceptChannel1MessageHandler` (store remote values), `ChannelOpenValidator`, `CommitmentTransactionModelFactory` (holder's delay per side) | `AT/…/OpenChannel1MessageHandlerTests.Given_RemoteParams_Then_AcceptCarriesOurValues`; `DT/…/ChannelFactoryTests.Given_Open_Then_LocalAndRemoteParamsMapped` (distinct values per side); `DT/…/CommitmentTransactionModelFactoryTests.Given_RemoteSide_Then_UsesLocalToSelfDelay` |
| **N1-T5** Reload + msat + migration `SplitChannelParamsAndMsatBalances`. Resolves NL-125, NL-126, NL-127, NL-128, NL-129, NL-191 | `ChannelDbRepository.cs` (`== (byte)…`, remote funding key, opener/accepter order from `IsInitiator`), `HtlcDbRepository.cs:63,70,~81`, entities/configs, 3 migrations (§3.5) | New `IT/Persistence/ChannelRoundTripTests` (SQLite `:memory:`, Sqlite migrations assembly): initiator and non-initiator channels with HTLCs in every legacy state reload equal; same obscuring factor; remote funding key kept; balance 1 msat remainder survives |
| **N1-T6** Startup order. Resolves NL-201, NL-052; partial NL-067 | `PeerManager.StartAsync` (load and **await** registration of every non-Closed channel, incl. `LocalLightningSigner.RegisterChannel`, then connect with retry/backoff) | `AT/Node/Managers/PeerManagerTests.Given_PeerUnreachable_When_Start_Then_ChannelsRegistered`; `…Given_Start_Then_RegisteredBeforeConnect` |

**Proof N1:** round-trip tests + Docker `Given_ChannelWithPush_When_Idle_Then_LndActiveAndBalancesAgree`: NLightning opens 1,000,000 sat with a 300,000 sat push; LND `Active` with `local_balance` 300,000 sat (± fee), NLightning `Open`, both balances agree. Stretch: alice opens to NLightning (non-initiator path) and it goes `Active` (if LND sends `update_fee` here, defer to N9).

### N2: BOLT 3 correctness (runs in parallel with N3)
| Task | Files | Acceptance / vectors |
|---|---|---|
| **N2-T1** Fee calculator + factory fixes. Resolves NL-061, NL-062, NL-195, NL-196, NL-044 | `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentFeeCalculator.cs`; `WeightConstants` (`CommitmentWeightNoAnchors = 724`, `CommitmentWeightAnchors = 1124`, `HtlcOutputWeight = 172`, HTLC fee 0 with anchors); `CommitmentTransactionModelFactory.cs` (offerer-debit via spec, holder dust for both balances, 2×330 anchors, no reserve throw); `ChannelOpenValidator.cs:108-110`, `ChannelFactory.cs:183-185` | `DT/Bitcoin/Transactions/CommitmentFeeCalculatorTests.Given_BoltExample_Feerate5000_Then_3315_3515_Base5340_Actual7140`; `…Given_RemoteSideTx_Then_ToRemoteTrimmedWithHolderDust`; Appendix C "fee greater than funder amount" builds |
| **N2-T2** Spec-driven factory. Resolves NL-176 (commit part) | factory input `(ChannelStatic, CommitmentSpec, side, number, perCommitmentPoint)`; delete the balance hacks in `GetTestChannelModel` | Every Appendix C commitment case built from `to_local_msat`/`to_remote_msat`/feerate/htlcs; **full tx hex** equals `ExpectedCommitTx0..15` after inserting both sigs |
| **N2-T3** HTLC output map | `ICommitmentTransactionBuilder`, `CommitmentTransactionBuilder.cs` → `CommitmentTransactionBuildResult { Tx, IReadOnlyList<(HtlcOutputInfo, uint Vout)> HtlcOutputsInTxOrder }` (old method kept as wrapper) | `BT/Builders/CommitmentTransactionBuilderTests.Given_SameAmountAndHash_Then_HtlcOrderByCltv` (Appendix C "2 offered with the same amount and preimage") |
| **N2-T4** HTLC txs. Resolves NL-056, NL-058, NL-059 | Domain `Bitcoin/Transactions/Models/HtlcTransactionModel.cs`, `Factories/HtlcTransactionModelFactory.cs`; `src/NLightning.Infrastructure.Bitcoin/Builders/{Interfaces/IHtlcTransactionBuilder,HtlcTransactionBuilder}.cs` (returns unsigned tx + spent witness script + amount); fix `Outputs/HtlcResolutionOutput.cs` and `BaseOutput.Amount`; delete commented `Transactions/Htlc*`; register in `AddBitcoinInfrastructure` | New `IT/BOLT3/Bolt3HtlcTxVectorTests.cs`: every Appendix C `htlc_timeout_tx`/`htlc_success_tx` byte-exact after inserting `remote_htlc_signature`, `local_htlc_signature` and the preimage; vectors added to `Bolt3AppendixCVectors.cs` (record the spec commit in a header comment) |
| **N2-T5** Anchors vectors. Resolves NL-061 (vectors), NL-176 (Appendix F) | `Bolt3AppendixFVectors.cs` (+ HTLC txs/sigs), `IT/BOLT3/Bolt3AnchorVectorTests.cs` | All Appendix F commitment txs byte-exact; HTLC txs sequence 1, fee 0 |

**Proof N2:** all Appendix C/F commitment and HTLC tx vectors byte-exact; the fee example passes.

### N3: Signer and revocation store
| Task | Files | Acceptance / vectors |
|---|---|---|
| **N3-T1** HTLC signing/verification. Resolves NL-057 | `ILightningSigner.cs`, `LocalLightningSigner.cs`, `HtlcSigningContext`, `test/NLightning.Integration.Tests/BOLT3/Mocks/Bolt3TestLightningSigner.cs` | Appendix C: our `local_htlc_signature` byte-equal (RFC 6979); `remote_htlc_signature` validates; tampered or high-S rejected; Appendix F remote sigs validate only as `SINGLE\|ACP`; order equals output order |
| **N3-T2** Number-based API + revocation guard. Resolves NL-189 | `ILightningSigner` (§3.6), `LocalLightningSigner`, `CommitmentKeyDerivationService` (derive from point), `CommitmentKeys` | `BT/Signers/LocalLightningSignerTests.Given_UnrevokedCommitment_When_Reveal_Then_Throws`; Appendix E key vectors still pass |
| **N3-T3** Secret verifier | Domain `Protocol/Interfaces/IPerCommitmentSecretVerifier.cs`, Bitcoin impl | Appendix E `per_commitment_secret → point`; wrong secret → false |
| **N3-T4** Remote shachain persistence + migration `AddRemoteShachain`. Resolves NL-136, NL-066 | `SecretStorageService` (`Load`/`Export`, reject out-of-order), `RemoteShachainEntity` + repo, 3 migrations | Appendix D insert vectors survive export/reload; SQLite round trip; "wrong sequence" cases still rejected |
| **N3-T5** `CommitmentSigningService` | `src/NLightning.Application/Channels/Services/CommitmentSigningService.cs` (`ICommitmentSigner`, `ICommitmentVerifier`) | `AT/…/CommitmentSigningServiceTests` with Appendix C data: remote commitment sig + ordered HTLC sigs equal the vectors |

**Proof N3:** Appendix C/D/E/F signer tests green in Release and Release.Native.

### N4: Pure commitment state machine + invariant harness
| Task | Files | Acceptance |
|---|---|---|
| **N4-T1** Types + reduce. Resolves NL-032, NL-202 (engine side) | `src/NLightning.Domain/Channels/Commitments/{HtlcStateTable,HtlcEvent,CommitmentSpec,SpecHtlc,FeeUpdate,LocalCommit,RemoteCommit,RemoteNextCommit,CommitmentsResult,ChannelTransition}.cs`, `Channels/Enums/HtlcState.cs` (values 10–39) | `DT/Channels/Commitments/HtlcStateTableTests` (every legal transition, every illegal one throws); `ReduceTests` (offerer debit, fulfill vs fail, last fee wins) |
| **N4-T2** Update ops + validator. Resolves NL-023 | `Commitments.cs`, `Channels/Validators/UpdateValidator.cs` | One test per ADD/DEL/FEE row in §6 (names there) |
| **N4-T3** Commit/revoke ops | `Commitments.SendCommit/ReceiveCommit/SendRevoke/ReceiveRevoke` with fake ports | `CommitmentsScenarioTests.Given_SpecDiagram_When_Steps1To9_Then_EachUpdateTraversesFiveStates`; crossed CS; fee-only CS; waiting-for-RAA blocks signing |
| **N4-T4** Events | `Channels/Commitments/Events/*` | `…Given_IncomingAdd_When_BothRevoked_Then_IncomingHtlcLockedInOnce`; `…Given_DownstreamFail_Then_OutgoingHtlcFailedOnlyWhenIrrevocable` |
| **N4-T5** Two-engine simulator + invariants I1–I12 | `test/NLightning.Domain.Tests/Channels/Commitments/CommitmentPairSimulator.cs` | 10k seeded random runs (add/fulfill/fail/malformed/fee/commit/revoke, message loss, disconnect + `RevertUncommitted`); all invariants after every step; 483-HTLC limit; seeds logged |

Domain.Tests has no internals access: keep the engine public or add an IVT entry in `src/NLightning.Domain/AssemblyInfo.cs`.
**Proof N4:** Domain tests + invariant suite green (runs in CI).

### N5: Commitment-state persistence
| Task | Files | Acceptance |
|---|---|---|
| **N5-T1** Migration `AddCommitmentState` (§3.5). Resolves NL-025 (legacy rows rejected at startup); partial NL-095, NL-137 | entities, configs, `NLightningDbContext`, 3 migrations | migrations apply on Postgres, SQL Server, SQLite |
| **N5-T2** `ChannelStateDbRepository.ApplyAsync`. Resolves NL-192, NL-138 | `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs`, `IUnitOfWork`, Domain interface; `ChannelDbRepository.UpdateAsync` stops writing HTLCs; memory repo returns snapshots | SQLite: apply 50 random transitions from the simulator, reload, equal model; mid-dance (`RemoteNextCommit` present) reload identical |
| **N5-T3** Crash injection + durability | `test/NLightning.Tests.Utils/Mocks/CrashingUnitOfWork.cs`; SQLite `synchronous=FULL` | throw at the k-th save → no partial rows (rollback verified on SQLite); Postgres/SQL Server via existing fixtures locally |

**Proof N5:** round-trip and crash tests green.

### N6: Application wiring — HTLC dance with LND (fail-back)
Prerequisite: ONION M3-T1 + create half of M3-T2 (NL-070 partial).

| Task | Files | Acceptance |
|---|---|---|
| **N6-T1** Handlers + cases for 128–135. Resolves NL-031 (core); covers ONION M4-T1 | `UpdateAddHtlc`, `UpdateFulfillHtlc`, `UpdateFailHtlc`, `UpdateFailMalformedHtlc`, `CommitmentSigned`, `RevokeAndAck`, `UpdateFee` handlers; `ChannelManager.cs` cases | `AT/Channels/Handlers/*Tests`: state guard (`Open` + reestablished), violation → exception type, `Given_PersistFails_Then_NoRevokeSent` |
| **N6-T2** Operations, scheduler, events, switch | Domain `IChannelOperations`, `IHtlcSwitch`, `IChannelDomainEvent`; `ChannelOperationsService`, `CommitScheduler` (debounce, ping-before-commit via `IPeerService.LastMessageReceivedAt`), `LocalOnlyHtlcSwitch`, `NodeOptions.EnableHtlcs` | `AT/Channels/Services/*Tests`; event replay on startup (crash after RAA persist → event once on restart) |
| **N6-T3** Failed-channel path. Resolves NL-200 | `ChannelFailedException`, `ChannelState.Failed = 35`, `PeerManager` (send `error`, persist `ErrorSent`) | error persisted and sent; updates refused afterwards |
| **N6-T4** In-process two-node harness | `AT/Channels/Harness/TwoNodeHarness.cs` (two `ChannelManager`s joined by in-memory outboxes, real builders/signer, fixed keys) | add → CS → RAA → CS → RAA; fulfill; fail; fee round; 30 HTLCs each way; txids identical at every step (I7) |
| **N6-T5** Docker `Given_LndPaysUs_When_LockedIn_Then_FailedBackAndChannelActive` | `NormalOperationFlowTests.cs` | alice pays a test invoice to us over the N1 channel; we lock in and fail back with `temporary_node_failure`; LND reports the failure; the channel stays `Active`; `ListChannels` shows local/remote commitment numbers = 2 |

**Proof N6:** harness + Docker fail-back test.

### N7: Reestablish and disconnect
| Task | Files | Acceptance |
|---|---|---|
| **N7-T1** `ReestablishPlanner` (pure) | `src/NLightning.Domain/Channels/Reestablish/{ReestablishPlanner,ReestablishPlan}.cs` | `DT/Channels/Reestablish/ReestablishPlannerTests`: exhaustive table over (L, R, waiting?, last order) × (their next_commitment, next_revocation, secret), both RAA/CS orders, data loss, `next_funding` on v1 → `tx_abort` |
| **N7-T2** Lifecycle hooks + gating + revert. Resolves NL-035 (core) | `PeerManager` (after `init` → `OnPeerConnectedAsync`; disconnect → `OnPeerDisconnectedAsync`), `IChannelManager`, `ReestablishService`, `ChannelReestablishMessageHandler`, outbox gate | `AT/Channels/Managers/ChannelManagerReconnectTests` (two channels → two reestablish; local add queued until synced; unacked peer add dropped, same id accepted once) |
| **N7-T3** Retransmission | stored `SentCommitDiff`, RAA regeneration, `LastSentOrder`, unsigned local updates, `shutdown` re-send, `channel_ready` when both numbers are 1, `error` re-send for Failed channels | harness: disconnect at every message boundary of the dance and crash at every persist point → both sides converge with identical txids (I11) |
| **N7-T4** Data loss. Resolves NL-035 (data_loss_protect) | `DataLossDetected` flag, signer refuses broadcast signing, `ListChannels` field | `…Given_PeerAhead_WithValidSecret_Then_NoBroadcast_And_ErrorSent` |
| **N7-T5** Startup for non-Open states. Partial NL-036 | `ChannelManager.RegisterExistingChannelAsync` | Failed/ShuttingDown/Negotiating channels resume their state on reconnect |
| **N7-T6** Funder remember rule. Resolves NL-048 (spec: funder SHOULD NOT remember before broadcast; current persist-at-funding_signed-then-broadcast is correct — close NL-048 as wontfix with this test) | `FundingSignedMessageHandler` | `AT/…/FundingSignedMessageHandlerTests.Given_FundingSigned_Then_PersistedBeforeBroadcast` |

**Proof N7:** planner + harness + Docker `Given_Restart_Then_Reestablish`: (a) idle restart of NLightning (same DB and key manager) → reestablish both ways, LND `PendingChannels` empty, N6 fail-back flow works again; (b) LND restart likewise; (c) test hook kills us after the CS persist but before the RAA arrives → after restart LND retransmits and the dance completes.

### N8: Payments over a direct channel (ONION M4 slice)
| Task | Files | Acceptance |
|---|---|---|
| **N8-T1** Move `IHopPayloadSerializer`. Resolves NL-075 | to `src/NLightning.Domain/Serialization/Interfaces/` | build + existing hop payload tests |
| **N8-T2** Final-hop receive + invoices + `CreateInvoice`. Partial NL-114, NL-137; covers ONION M4-T2, M4-T3 (single part) | `FinalHopHtlcSwitch` (peel with `PeelAsLocalNode(packet, hash, null)`, record replay after peel, deserialize, `HopPayloadValidator`, amount/cltv/secret/`total_msat` checks, non-final → `unknown_next_peer`, BADONION → malformed, `invalid_onion_payload` → `update_fail_htlc` with `OnionException.SharedSecret`); `InvoiceEntity` + repo (migration `AddInvoicesAndPayments`); Bolt11 reference from Application/Daemon; invoices set var_onion_optin + payment_secret, no basic_mpp (NL-119 encode side); `ClientCommand.CreateInvoice` | unit: unknown hash / wrong secret → 0x400F with (htlc_msat, height); amount/cltv → 0x0013/0x0012; valid → fulfill; invoice settled when the removal is irrevocable |
| **N8-T3** Single-hop send + `PayInvoice`. Resolves NL-115; covers ONION M4-T6 (direct), M3-T2 decrypt | `PaymentService` (bolt11 decode, payee must be a direct peer, `ConstructWithSharedSecrets` one hop, `outgoing_cltv = height + min_final(default 18) + 3`, CSPRNG session key), `PaymentEntity` + repo, `FailureOnionService` decrypt, `ClientCommand.PayInvoice` | `AT/Payments/PaymentServiceTests`; create→decrypt round trip |

**Proof N8:** Docker `Given_LndPaysOurInvoice_Then_Settled` (50,000 sat: preimage matches, balances agree to the msat, no pending HTLCs; a 5,000 sat HTLC at 10,000 sat/kw is trimmed and still settles; unknown hash → `INCORRECT_OR_UNKNOWN_PAYMENT_DETAILS`, channel stays Active), `Given_LndInvoice_When_WePay_Then_PreimageReturned` (20,000 sat; LND `SETTLED`), `Given_ConcurrentPaymentsBothWays_Then_AllSucceed` (10 each way, crossed CS), `Given_InFlightRestart_Then_FulfillArrivesAfterReestablish` (alice hold invoice, restart us, settle).

### N9: Fees, deadlines, dust exposure, fail-the-channel
| Task | Files | Acceptance |
|---|---|---|
| **N9-T1** `FeeUpdateScheduler` + `FeeUpdatePolicy` | funder only, ±20% hysteresis, floor 253 sat/kw, bounds vs `IFeeService` (receive: < 0.5× or > 10× estimate) | `AT/Channels/Services/FeeUpdateSchedulerTests.Given_EstimateUp20Pct_Then_UpdateFeeQueued`; harness fee/add race |
| **N9-T2** Deadlines | Domain `HtlcDeadlinePolicy` (G=2, fulfill deadline 18), `NodeOptions.CltvExpiryDelta` (default 34), `HtlcExpiryMonitor` on `OnNewBlockDetected` | table tests at heights N−1, N, N+G |
| **N9-T3** Dust exposure | `UpdateValidator` + `NodeOptions.MaxDustHtlcExposureMsat` | DUST-01..05 rows |
| **N9-T4** `ChannelFailureService`. Partial NL-094, NL-095 | error + sign latest local commitment with both sigs + broadcast + `Failed`; refuses under `DataLossDetected`; the only broadcast path | `AT/…/ChannelFailureServiceTests.Given_Revoked_Then_OnlyLatestBroadcast`, `…Given_DataLoss_Then_Refused` |

**Proof N9:** Docker `Given_LndFundedChannel_Then_PaymentsAndFeeUpdatesWork` (alice opens to us with push; payments both ways; our `update_fee` on an N1 channel accepted by LND; LND `update_fee` accepted if it sends one **(unverified trigger in regtest)**), and `Given_OfferedHtlcPastDeadline_Then_ChannelFailedAndCommitmentConfirmed` (alice hold invoice never settled; mine past deadline; our latest commitment confirms).

### N10: Cooperative close
| Task | Files | Acceptance |
|---|---|---|
| **N10-T1** Scripts and dust. Resolves NL-045, NL-068 | `Channels/Validators/ShutdownScriptValidator.cs`; `DustService` in `AddBitcoinInfrastructure` (+ P2A/OP_RETURN thresholds); local upfront script from the wallet | table tests (every form, push lengths 2/40, OP_RETURN 6..80, invalid ones) |
| **N10-T2** Closing tx. Resolves NL-065 | Domain `ClosingTransactionModel`, `ClosingTransactionBuilder.BuildLegacy`; delete dead `Transactions/{ClosingTransaction,BaseTransaction,FundingTransaction}.cs` | `BT/Builders/ClosingTransactionBuilderTests` (both variants, floor, dust drop, BIP69, witness `0 <sig1> <sig2> <script>` in funding-key order) |
| **N10-T3** Shutdown + negotiation + `CloseChannel`. Resolves NL-034 (legacy); partial NL-036, NL-152 | `ShutdownMessageHandler`, `ClosingSignedMessageHandler`, `ChannelCloseService`, `LegacyClosingNegotiator`, states 23/25/30, migration `AddShutdownState`, `ClientCommand.CloseChannel` | negotiator property tests (convergence ≤ 64 rounds, strictly-between, range overlap); handler tests per §6 SHUT/CLS rows |

**Proof N10:** Docker `Given_OpenChannel_When_CooperativeClose_Then_FundsReturnToWallets`: (a) we close: LND lists `COOPERATIVE_CLOSE`, after 6 blocks we are `Closed` and our wallet rises by `to_local − fee`; (b) alice closes a second channel: we reply `shutdown`, negotiate, same checks; (c) with and without `fee_range`; stretch: close with a hold-invoice HTLC in flight completes only after it settles.

### N11 (optional): `option_simple_close` and anchors
- **N11-T1** Messages 40/41 + TLVs 1/2/3 (4-place registration). Resolves NL-020.
- **N11-T2** `SimpleCloseNegotiator`, handlers, `BuildSimple`; set `OptionSimpleClose = Optional` only after a Docker close against a peer that supports it **(unverified LND version)**.
- **N11-T3** Enable `OptionAnchors` only after N2-T5, N3-T1 and BOLT 5 CPFP.

### After N10 (separate plan, gates mainnet)
BOLT 5 minimal (NL-094): broadcast HTLC txs with stored sigs, sweep to_local after CSV, time out offered HTLCs, claim received HTLCs with preimages, penalty via `RevocationWatchEntity` + shachain. `EnableHtlcs` defaults to true on mainnet only then.

---

## 6. Requirements traceability matrix

Status: **DONE**, **WIRE** (message only), **PARTIAL**, **BUG**, **MISSING**, **N/A** (splice / zero_fee_commitments; kept for traceability). Flags: [M3]/[M3b]/[M4]/[M5] co-owned with that ONION milestone; [B5] needs BOLT 5. Test prefixes: `DT/` Domain.Tests, `AT/` Application.Tests, `BT/` Infrastructure.Bitcoin.Tests, `ST/` Infrastructure.Serialization.Tests, `IT/` Integration.Tests, `DK/` `IT/Docker/NormalOperationFlowTests`. `…` = the `Commitments` test class of that area.

### 6.1 General and forwarding
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-NO-01 | Five-stage update lifecycle | MISSING | N4-T3 | `DT/…/CommitmentsScenarioTests.Given_SpecDiagram_When_Steps1To9_Then_EachUpdateTraversesFiveStates` |
| B2-NO-02 | HTLCs only after both `channel_ready` | MISSING | N6-T1 | `AT/…/UpdateAddHtlcMessageHandlerTests.Given_ChannelNotOpen_When_Add_Then_ChannelError` |
| B2-NO-03 | Irrevocably-committed definition | MISSING | N4-T4 | `DT/…/CommitmentsIrrevocabilityTests` [M4] |
| B2-FWD-01 | No outgoing offer before incoming lock-in | MISSING | N4-T4 / N6-T2 | `AT/Payments/HtlcSwitchTests.Given_IncomingNotLockedIn_Then_NoOutgoingAdd` [M4] |
| B2-FWD-02 | No incoming fail before outgoing removal irrevocable | MISSING | N4-T4 | `…Given_DownstreamFailNotLockedIn_Then_UpstreamNotFailed` [M4][B5] |
| B2-FWD-03 | Fail incoming at expiry / delta | MISSING | N9-T2 | `AT/…/HtlcExpiryMonitorTests.Given_IncomingAtExpiry_When_NewBlock_Then_FailQueued` [M4] |
| B2-FWD-04 | `expiry_too_far` | MISSING | ONION M4-T4 | M4 tests [M4] |
| B2-FWD-05 | Fulfill incoming on outgoing fulfill / on-chain preimage | MISSING | N4-T4 | `…Given_DownstreamFulfill_Then_UpstreamFulfillImmediately` [M4][B5] |

### 6.2 `cltv_expiry_delta` and dust exposure
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-CLTV-01 | Offerer deadline `cltv_expiry + G` | MISSING | N9-T2 | `DT/Channels/Policies/HtlcDeadlinePolicyTests` |
| B2-CLTV-02 | Don't offer already-expired HTLCs | MISSING | N4-T2 | `DT/…/SendAddTests.Given_ExpiryInPast_Then_Rejected` |
| B2-CLTV-03 | Offered past deadline → fail channel | MISSING | N9-T2/T4 | `AT/…/HtlcExpiryMonitorTests.Given_OfferedPastDeadline_Then_ChannelFailed` [B5] |
| B2-CLTV-04 | Fulfiller deadline (18 blocks) | MISSING | N9-T2 | `HtlcDeadlinePolicyTests` |
| B2-CLTV-05 | Fail (not forward) past fulfillment deadline | MISSING | N9-T2 | `AT/Payments/…` [M4] |
| B2-CLTV-06 | Fulfilled past deadline → fail channel | MISSING | N9-T2/T4 | `…Given_FulfilledPastDeadline_Then_ChannelFailed` [B5] |
| B2-CLTV-07 | `cltv_expiry_delta ≥ 34` | MISSING | N9-T2 | `DT/Node/NodeOptionsTests` [M4] |
| B2-DUST-01/02 | Receiving: remote/local dust over limit → fail once committed, no preimage | MISSING | N9-T3 | `DT/…/DustExposureTests.Given_RemoteDustOverLimit_When_ReceiveAdd_Then_MarkedFailOnLockIn` (+ local) [M4] |
| B2-DUST-03/04 | Offering over limit → don't send | MISSING | N9-T3 | `…Given_OfferOverRemoteDust_Then_Refused` (+ local) |
| B2-DUST-05 | Non-anchor fee increase over dust limit | MISSING | N9-T1 | `DT/…/FeeUpdatePolicyTests` |

### 6.3 `update_add_htlc`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-ADD-W01 | Onion exactly 1366 B | DONE | — | `ST/Messages/UpdateAddHtlcMessageTests` |
| B2-ADD-W02 | TLV 0 `blinded_path`; unknown even rejected | DONE | — | same [M5] |
| B2-ADD-S01 | Payer affords fee on both commitments above reserve | MISSING | N4-T2 | `DT/Channels/Validators/UpdateValidatorTests.Given_FunderAtReserve_When_SendAdd_Then_Rejected` |
| B2-ADD-S02 | Anchors: both anchors affordable | MISSING | N4-T2 | `…Given_AnchorsFunder_When_OnlyOneAnchorAffordable_Then_Rejected` |
| B2-ADD-S03 | Fee-spike buffer | MISSING | N4-T2 | `…Given_SpikeBufferViolated_Then_Refused` |
| B2-ADD-S04 | Non-payer doesn't starve payer | MISSING | N4-T2 | `…Given_NonFunderAdd_When_FunderCantPayFee_Then_Refused` |
| B2-ADD-S05 | `amount_msat > 0` | MISSING | N4-T2 | `DT/…/SendAddTests.Given_ZeroAmount_Then_Rejected` |
| B2-ADD-S06 | ≥ remote minimum | BUG (NL-194) | N1-T4, N4-T2 | `…Given_BelowRemoteMinimum_Then_Rejected` |
| B2-ADD-S07 | `cltv_expiry < 500000000` | MISSING | N4-T2 | `…Given_TimestampCltv_Then_Rejected` |
| B2-ADD-S08 | ≤ remote `max_accepted_htlcs` | BUG (NL-194) | N1-T4, N4-T2 | `…Given_RemoteMaxAcceptedReached_Then_Rejected` |
| B2-ADD-S09 | ≤ remote in-flight | BUG (NL-194) | N1-T4, N4-T2 | `…Given_InFlightExceeded_Then_Rejected` |
| B2-ADD-S10 | ids from 0, +1, never reset | BUG (NL-190) | N1-T3, N4-T2 | `…Given_ThreeAddsAcrossTwoCommits_Then_Ids012` |
| B2-ADD-S11 | Blinded relay sets `path_key` | MISSING (NL-026) | ONION M5 | M5 [M5] |
| B2-ADD-S12 | Splice rules | N/A | — | — |
| B2-ADD-S13 | No add after `shutdown` | MISSING | N10-T3 | `DT/…/ShutdownTests.Given_ShutdownSent_When_SendAdd_Then_Rejected` |
| B2-ADD-R01 | 0 / below own minimum → fail | MISSING | N4-T2, N6-T1 | `DT/…/ReceiveAddTests.Given_BelowOwnMinimum_Then_Violation`; `AT/…/UpdateAddHtlcMessageHandlerTests` |
| B2-ADD-R02 | Sender can't afford → fail | MISSING | N4-T2 | `…Given_SenderBelowReserveAfterAdd_Then_Violation` |
| B2-ADD-R03 | Own max accepted / in-flight | MISSING | N4-T2 | `…Given_OwnMaxAcceptedExceeded_Then_Violation` |
| B2-ADD-R04 | `cltv_expiry ≥ 5e8` → fail | MISSING | N4-T2 | `…Given_TimestampCltv_Then_Violation` |
| B2-ADD-R05 | Duplicate `payment_hash` allowed | MISSING | N4-T2 | `…Given_DuplicateHash_Then_Accepted` |
| B2-ADD-R06 | Repeated id after reconnect ignored | MISSING | N7-T2 | `DT/…/ReestablishTests.Given_UnackedAdd_When_Reconnect_Then_SameIdAcceptedOnce` |
| B2-ADD-R07 | Other id violations → MAY fail | MISSING | N4-T2 | `…Given_IdGap_Then_Violation` |
| B2-ADD-R08 | Decrypt onion (path_key, hash as AD) | PARTIAL (Sphinx done, not called) | N8-T2 | ONION M4-T2 tests [M4] |
| B2-ADD-R09 | Onion failure → failure message | MISSING | N6-T2, N8-T2 | `AT/Payments/HtlcSwitchTests.Given_BadOnion_Then_FailMalformedAfterLockIn` [M3][M4] |
| B2-ADD-R10 | BOLT 4 payload reader rules | PARTIAL (validator not wired) | N8-T2 | `AT/Payments/FinalHopHtlcSwitchTests` [M4] |

### 6.4 Removing HTLCs
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-DEL-W01 | fulfill TLVs 1/3, fail TLV 1 | MISSING (NL-022) | ONION M3b | `ST/Messages/UpdateFulfillHtlcMessageTests` [M3b] |
| B2-DEL-00 | Only remove the other node's HTLCs | MISSING | N4-T2 | `DT/…/RemoveTests.Given_RemoveOwnOfferedHtlc_Then_Rejected` |
| B2-DEL-01 | Remove as soon as possible | MISSING | N6-T2 | `AT/Channels/Services/CommitSchedulerTests` [M4] |
| B2-DEL-02 | Fail timed-out HTLCs | MISSING | N9-T2 | see B2-CLTV-03 |
| B2-DEL-03 | No remove before lock-in | MISSING | N4-T2 | `…Given_AddNotLockedIn_When_SendFulfill_Then_Rejected` |
| B2-DEL-04 | Blinded non-final → `invalid_onion_blinding` | MISSING | ONION M5 | M5 [M5] |
| B2-DEL-05 | `path_key` in add → malformed `invalid_onion_blinding` | MISSING | ONION M5 | M5 [M5] |
| B2-DEL-06 | `attribution_data` when negotiated | MISSING (advertised, NL-074) | N0-T4 (default No), M3b | `DT/Node/FeatureOptionsTests.Given_Defaults_Then_UnimplementedNotAdvertised` [M3b] |
| B2-DEL-07 | Relay `fulfillment_payload` | MISSING | ONION M3b | M3b [M3b] |
| B2-DEL-R01 | Unknown id → fail | MISSING | N4-T2 | `…Given_UnknownId_Then_Violation` |
| B2-DEL-R02 | Wrong preimage → fail | MISSING | N4-T2 | `…Given_WrongPreimage_Then_Violation` |
| B2-DEL-R03 | `fulfillment_payload` > 32768 → error | MISSING | ONION M3b | `AT/…/UpdateFulfillHtlcMessageHandlerTests.Given_OversizeFulfillmentPayload_Then_Error` [M3b] |
| B2-DEL-R04 | Malformed without BADONION → fail (NL-023) | MISSING | N4-T2 | `…Given_MalformedWithoutBadOnion_Then_Violation` [M3] |
| B2-DEL-R05 | `sha256_of_onion` mismatch → MAY retry | MISSING | ONION M4-T5 | M4 [M4] |
| B2-DEL-R06 | Convert malformed upstream (NL-071) | MISSING | ONION M3-T3/M4-T5 | M3-T3 tests [M3][M4] |
| B2-DEL-R07 | Double removal is a violation | MISSING | N4-T2 | `…Given_DoubleFulfill_Then_Violation` |

### 6.5 `start_batch`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-BATCH-01..07 | Batch rules (`message_type` 132) | N/A (splice only; 127 is odd and dropped) | — | — |

### 6.6 `commitment_signed`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-CS-W01 | TLV 1 `funding_txid`, MUST set | BUG (NL-199) | N0-T6 | `ST/Messages/CommitmentSignedMessageTests.Given_FundingTxidTlv_When_RoundTrip_Then_Equal` |
| B2-CS-S01 | No CS without changes | MISSING | N4-T3 | `DT/…/SendCommitTests.Given_NoChanges_Then_CannotSign` |
| B2-CS-S02 | Fee-only CS allowed | MISSING | N4-T3 | `…Given_OnlyFeeUpdate_Then_CanSign` |
| B2-CS-S03 | Revocation-number-only CS allowed | MISSING | N4-T3 | `…Given_DustOnlyAdd_Then_CanSign_And_NumHtlcs0` |
| B2-CS-S04 | HTLC sigs in output order | MISSING | N2-T3, N3-T1 | `IT/BOLT3/Bolt3HtlcTxVectorTests.Given_AppendixC_When_SigningRemoteHtlcTxs_Then_SignaturesInVectorOrder` |
| B2-CS-S05 | Ping before CS if idle | MISSING | N6-T2 | `AT/…/CommitSchedulerTests.Given_IdlePeer_When_Commit_Then_PingFirst` |
| B2-CS-S06 | One un-revoked CS; needs remote next point | BUG (NL-051) | N1-T2, N4-T3 | `…Given_WaitingForRevocation_Then_CannotSign` |
| B2-CS-S07 | Splice batch | N/A | — | — |
| B2-CS-R01 | Invalid / high-S sig → fail | PARTIAL (validator exists) | N3-T5, N6-T1 | `AT/…/CommitmentSignedMessageHandlerTests.Given_BadSig_Then_ChannelError` |
| B2-CS-R02 | `num_htlcs` mismatch → fail | MISSING | N4-T3 | `DT/…/ReceiveCommitTests.Given_WrongNumHtlcs_Then_Violation` |
| B2-CS-R03 | Invalid HTLC sig → fail | MISSING | N3-T1 | `IT/BOLT3/Bolt3HtlcTxVectorTests.Given_AppendixC_RemoteHtlcSig_Then_Valid` (+ tampered, Appendix F) |
| B2-CS-R04 | Splice receive rules | N/A | — | — |
| B2-CS-R05 | Respond with RAA | MISSING | N6-T1 | `…Given_ValidCommit_Then_RevokeAndAckQueued` |
| B2-CS-R06 | Persist before reply | MISSING | N5-T2, N6-T1 | `…Given_PersistFails_Then_NoRevokeSent` |

### 6.7 `revoke_and_ack`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-RAA-S01 | Secret of previous local commitment | BUG (NL-187) | N1-T1, N3-T2 | `DT/…/RevokeTests.Given_LocalCommitN_When_Revoke_Then_SecretIndexIsFirstMinus(N-1)` |
| B2-RAA-S02 | Point for the next local commitment | BUG (NL-187) | N1-T1 | same |
| B2-RAA-S03 | One RAA per batch | N/A (no batches) | — | — |
| B2-RAA-R01 | Invalid secret → error + fail | MISSING | N3-T3, N6-T1 | `BT/…/PerCommitmentSecretVerifierTests`; `AT/…/RevokeAndAckMessageHandlerTests.Given_WrongSecret_Then_ErrorAndFail` |
| B2-RAA-R02 | Shachain violation → MAY fail | PARTIAL (not persisted) | N3-T4 | Appendix D "wrong sequence"; `AT/…Given_ShachainInconsistent_Then_Fail` [B5] |
| B2-RAA-R03 | RAA without outstanding CS → violation | MISSING | N4-T3 | `…Given_NoPendingCommit_Then_Violation` |
| B2-RAA-R04 | Store remote secrets for penalties | PARTIAL (last only, NL-136) | N3-T4 | `IT/Persistence/RemoteShachainPersistenceTests` [B5] |
| B2-RAA-N01 | Never broadcast revoked commitments | MISSING | N9-T4 | `AT/…/ChannelFailureServiceTests.Given_Revoked_Then_OnlyLatestBroadcast` [B5] |
| B2-RAA-N02 | Don't sign own commitment unless broadcasting | DONE in effect | N9-T4 | same (code review) |

### 6.8 `update_fee`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-FEE-Z01 | `zero_fee_commitments` rules | N/A | — | — |
| B2-FEE-S01 | Payer keeps feerate sufficient | MISSING | N9-T1 | `AT/…/FeeUpdateSchedulerTests.Given_EstimateUp20Pct_Then_UpdateFeeQueued` |
| B2-FEE-S02 | Non-payer MUST NOT send | MISSING | N4-T2 | `DT/…/FeeTests.Given_NotFunder_When_SendFee_Then_Rejected` |
| B2-FEE-S03 | Non-anchor dust on increase → MAY skip/fail | MISSING | N9-T1 | see B2-DUST-05 |
| B2-FEE-R01 | Unreasonable feerate → fail | MISSING | N4-T2, N9-T1 | `…Given_FeeAboveMax_Then_Violation` |
| B2-FEE-R02 | Non-payer sender → fail | MISSING | N4-T2 | `…Given_FromNonFunder_Then_Violation` |
| B2-FEE-R03 | Funder can't afford → SHOULD fail | MISSING | N4-T2 | `…Given_FunderCantAffordNewFee_Then_Violation` |
| B2-FEE-R04 | Non-anchor dust after increase → MAY fail | MISSING | N9-T1 | see B2-DUST-05 |
| B2-FEE-X01 | Replaced, last applies per commitment | MISSING | N4-T1 | `DT/…/ReduceTests.Given_TwoFeeUpdates_Then_LastApplies` |

### 6.9 `channel_reestablish`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-RE-W01 | `next_funding` = TLV 1 (txid ‖ flags) | BUG (NL-197) | N0-T6 | `ST/Messages/TxChannelReestablishMessageTests.Given_NextFundingType1_Then_Parsed` |
| B2-RE-W02 | TLV 5 `my_current_funding_locked` | MISSING (NL-197) | N0-T6 | `…Given_Tlv5_Then_Ignored` |
| B2-RE-01 | Funder remembers after broadcast only | PARTIAL (NL-048) | N7-T6 | `AT/…/FundingSignedMessageHandlerTests.Given_FundingSigned_Then_PersistedBeforeBroadcast` |
| B2-RE-02 | Fundee remembers after `funding_signed` | DONE | — | `FundingCreatedMessageHandlerTests` |
| B2-RE-03 | Continue over a new transport | MISSING | N7-T2 | `AT/Channels/Managers/ChannelManagerReconnectTests` |
| B2-RE-04 | Revert uncommitted peer updates; keep preimages | MISSING | N4-T3, N7-T2 | `DT/…/ReestablishTests.Given_ProposedRemoteAdds_When_Disconnect_Then_Dropped_And_PreimageKept` |
| B2-RE-05 | Failed channel re-sends error | MISSING (NL-200) | N6-T3, N7-T3 | `AT/…Given_FailedChannel_When_Reconnect_Then_ErrorResent` |
| B2-RE-06 | Send reestablish per channel | MISSING | N7-T2 | `…Given_TwoOpenChannels_When_PeerInit_Then_TwoReestablishSent` |
| B2-RE-07 | Wait for peer's reestablish | MISSING | N7-T2 | `…Given_NotReestablished_When_LocalAdd_Then_Queued` |
| B2-RE-08 | `next_commitment_number` = L+1 | MISSING | N7-T1 | `DT/Channels/Reestablish/ReestablishPlannerTests` |
| B2-RE-09 | `next_revocation_number` | MISSING | N7-T1 | same |
| B2-RE-10 | Valid `my_current_per_commitment_point` | MISSING | N7-T1 | same |
| B2-RE-11 | `your_last_per_commitment_secret` (zeros if none) | MISSING | N7-T1, N3-T4 | same |
| B2-RE-12 | No `next_funding` on v1 | DONE by omission | N7-T1 | `…Given_V1Channel_Then_NoNextFunding` |
| B2-RE-13 | `my_current_funding_locked` send rules | N/A | — | — |
| B2-RE-14 | Received 0 → fail and broadcast | MISSING | N7-T1, N9-T4 | `…Given_Zero_Then_FailAndBroadcast` [B5] |
| B2-RE-15 | Both 1 → resend `channel_ready` | MISSING | N7-T3 | `…Given_BothOne_Then_ResendChannelReady` |
| B2-RE-16 | Otherwise no `channel_ready` resend | BUG (NL-050) | N0-T7, N7-T1 | `…Given_Two_Then_NoChannelReady` |
| B2-RE-17 | Ignore redundant `channel_ready` | PARTIAL | N7-T3 | `AT/…/ChannelReadyMessageHandlerTests.Given_Open_When_Duplicate_Then_NoStateChange` |
| B2-RE-18 | Resend last CS + updates, same number | MISSING | N7-T3 | `…Given_LastCsLost_Then_UpdatesAndCsResent_SameNumber` |
| B2-RE-19 | Otherwise mismatch → fail | MISSING | N7-T1 | `…Given_NumberAhead_Then_Error` |
| B2-RE-20 | Resend last RAA, keep order with CS | MISSING | N7-T3 | `…Given_RaaLost_And_CsLost_Then_OriginalOrderPreserved` (both orders) |
| B2-RE-21 | Revocation mismatch → fail | MISSING | N7-T1 | `…Given_RevocationMismatch_Then_Error` |
| B2-RE-22 | Ignore `my_current_per_commitment_point` | MISSING | N7-T1 | `…Given_InvalidPoint_Then_Ignored` |
| B2-RE-23 | Data loss → no broadcast, send error | MISSING (Compulsory feature, NL-035) | N7-T4 | `…Given_PeerAhead_WithValidSecret_Then_NoBroadcast_And_ErrorSent` [B5] |
| B2-RE-24 | Wrong secret → fail | MISSING | N7-T1 | `…Given_BadSecret_Then_Fail` |
| B2-RE-25 | Unexpected `next_funding` on v1 → `tx_abort` | MISSING | N7-T1 | `…Given_NextFundingOnV1_Then_TxAbort` |
| B2-RE-26 | `my_current_funding_locked` processing | N/A | — | — |
| B2-RE-27 | Handle broadcast of any sent commitment | MISSING | BOLT 5 | BOLT 5 plan [B5] |
| B2-RE-28 | Re-send `shutdown` | MISSING | N7-T3, N10-T3 | `…Given_ShutdownSent_When_Reconnect_Then_ShutdownResent` |
| B2-RE-29 | Closing negotiation restarts | MISSING | N10-T3 | `DT/Channels/Closing/LegacyClosingNegotiatorTests.Given_Reconnect_Then_Restart` |

### 6.10 `shutdown`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-SHUT-W01 | Wire | WIRE | — | `ShutdownMessageTests` |
| B2-SHUT-S01 | Not before funding_created/signed | MISSING | N10-T3 | `AT/…/ChannelCloseServiceTests.Given_V1Opening_Then_Refused` |
| B2-SHUT-S02 | MAY send before `channel_ready` | MISSING | N10-T3 | `…Given_ReadyForUs_Then_Allowed` |
| B2-SHUT-S03 | Not with pending updates on receiver | MISSING | N10-T3 | `DT/…/ShutdownTests.Given_ProposedLocalChanges_Then_ShutdownDeferredUntilSigned` |
| B2-SHUT-S04 | Only once | MISSING | N10-T3 | `…Given_ShutdownSent_Then_SecondRefused` |
| B2-SHUT-S05 | Unlocked splice | N/A | — | — |
| B2-SHUT-S06 | No add after shutdown | MISSING | N10-T3 | see B2-ADD-S13 |
| B2-SHUT-S07 | Cleared → no `update_*` | MISSING | N10-T3 | `…Given_Cleared_When_SendFee_Then_Rejected` |
| B2-SHUT-S08 | Fail routing of HTLCs added after our shutdown | MISSING | N10-T3 | `AT/Payments/HtlcSwitchTests.Given_LocalShutdown_When_IncomingAdd_Then_FailedBack` [M4] |
| B2-SHUT-S09 | Reuse upfront script | BUG (NL-045) | N10-T1 | `…Given_UpfrontScript_Then_SameScriptSent` |
| B2-SHUT-S10 | Allowed script forms | MISSING | N10-T1 | `DT/Channels/Validators/ShutdownScriptValidatorTests` |
| B2-SHUT-R01 | Before funding → error | MISSING | N10-T3 | `AT/…/ShutdownMessageHandlerTests.Given_V1Opening_Then_Error` |
| B2-SHUT-R02 | Bad script → warning | MISSING | N10-T3 | `…Given_P2PkhScript_Then_Warning` |
| B2-SHUT-R03 | Pre-ready → MAY reply | MISSING | N10-T3 | `…Given_PreReady_Then_ShutdownReply` |
| B2-SHUT-R04 | Reply once no outstanding updates | MISSING | N10-T3 | `…Given_PendingLocalChanges_Then_CsThenShutdown` |
| B2-SHUT-R05 | Upfront mismatch → fail connection | MISSING | N10-T3 | `…Given_UpfrontMismatch_Then_Disconnect` |
| B2-SHUT-R06 | Peer add after its shutdown → violation | MISSING | N10-T3 | `DT/…/ShutdownTests.Given_RemoteShutdown_When_ReceiveAdd_Then_Violation` |

### 6.11 Legacy `closing_signed`
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-CLS-W01 | `fee_range` optional | BUG (NL-198) | N0-T6 | `ST/Messages/ClosingSignedMessageTests.Given_NoFeeRange_When_Deserialize_Then_Ok` |
| B2-CLS-01 | Funder proposes once cleared | MISSING | N10-T3 | `DT/…/LegacyClosingNegotiatorTests.Given_Funder_Cleared_Then_Proposes` |
| B2-CLS-02 | Initial fee from estimate; set `fee_range` | MISSING | N10-T3 | `…Given_Estimate_Then_FeeAndRangeSet` |
| B2-CLS-03 | No response in time → fail | MISSING | N10-T3, N9-T4 | `AT/…Given_NoReplyTimeout_Then_ChannelFailed` [B5] |
| B2-CLS-04 | Non-funder range rules | MISSING | N10-T3 | `…Given_NonFunder_Then_MaxAtLeastReceivedMax` |
| B2-CLS-05 | Sign BOLT 3 closing tx | MISSING (NL-065) | N10-T2 | `BT/Builders/ClosingTransactionBuilderTests` + DK close |
| B2-CLS-R01 | Sig valid for either variant | MISSING | N10-T3 | `AT/…/ClosingSignedMessageHandlerTests.Given_SigOverVariantWithoutOurOutput_Then_Accepted` |
| B2-CLS-R02 | Equal fee → sign and broadcast | MISSING | N10-T3 | `…Given_EqualFee_Then_Agree` |
| B2-CLS-R03 | Fee in our range → broadcast, echo | MISSING | N10-T3 | `…Given_FeeInOurRange_Then_EchoAndBroadcast` |
| B2-CLS-R04 | No overlap → warn / fail | MISSING | N10-T3 | `…Given_NoOverlap_Then_Warning` |
| B2-CLS-R05 | Funder: outside overlap → fail, else echo | MISSING | N10-T3 | `…Given_FunderFeeOutsideOverlap_Then_Fail` |
| B2-CLS-R06 | Non-funder mismatch rules | MISSING | N10-T3 | `…Given_NonFunderMismatch_Then_Fail` |
| B2-CLS-R07 | No range: strictly between | MISSING | N10-T3 | `…Given_NotStrictlyBetween_Then_Warning` |
| B2-CLS-R08 | Agree → echo | MISSING | N10-T3 | `…Given_Agree_Then_Echo` |
| B2-CLS-R09 | Otherwise propose strictly between | MISSING | N10-T3 | `…Given_Disagree_Then_StrictlyBetween` (property) |
| B2-CLS-R10 | Output below script dust → fail | MISSING (NL-068) | N10-T1/T3 | `…Given_P2PkhOutputBelow546_Then_Fail` |

### 6.12 `closing_complete` / `closing_sig` (optional)
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B2-SC-W01 | Messages 40/41 + TLVs 1/2/3 | MISSING (NL-020) | N11-T1 | `ST/Messages/ClosingCompleteMessageTests`, `ClosingSigMessageTests` |
| B2-SC-01 | Send `closing_complete` once cleared | MISSING | N11-T2 | `DT/Channels/Closing/SimpleCloseNegotiatorTests` |
| B2-SC-C01 | Fee ≤ own balance | MISSING | N11-T2 | `…Given_FeeAboveBalance_Then_Refused` |
| B2-SC-C02 | At least one non-dust output | MISSING | N11-T2 | `…Given_BothDust_Then_Refused` |
| B2-SC-C03/04/05 | Scripts and locktime | MISSING | N11-T2 | `…Given_Proposal_Then_ScriptsAndLocktime` |
| B2-SC-C06/C07 | TLV selection by balance/dust | MISSING | N11-T2 | table test |
| B2-SC-C08 | BOLT 3 simple closing tx + sigs | MISSING | N11-T2 | `BT/Builders/ClosingTransactionBuilderTests.Given_SimpleClose_Then_Seq0xFFFFFFFD_LocktimeFromMsg_Bip69` |
| B2-SC-C09 | Wait for `closing_sig` | MISSING | N11-T2 | `…Given_Outstanding_Then_SecondRefused` |
| B2-SC-E01 | Fee > closer balance → fail | MISSING | N11-T2 | `AT/…/ClosingCompleteMessageHandlerTests` |
| B2-SC-E02/E03 | Script mismatch / invalid | MISSING | N11-T2 | same |
| B2-SC-E04/E05 | OP_RETURN zero; build closer's tx | MISSING | N11-T2 | builder tests |
| B2-SC-E06 | Signature selection rules | MISSING | N11-T2 | table test |
| B2-SC-E07/E08 | Missing / invalid sig → fail | MISSING | N11-T2 | handler tests |
| B2-SC-E09 | Sign, broadcast, reply `closing_sig` | MISSING | N11-T2 | handler tests |
| B2-SC-E10 | Use `closer_scriptpubkey` later | MISSING | N11-T2 | negotiator tests |
| B2-SC-G01..G06 | `closing_sig` receiver rules | MISSING | N11-T2 | `AT/…/ClosingSigMessageHandlerTests` |

### 6.13 BOLT 3
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B3-ORD-01 | BIP69 + cltv tie-break | DONE | N2-T3 | Appendix C "same amount and preimage" full-tx check |
| B3-CTX-01 | Version, obscured locktime/sequence | DONE | — | `DT/Protocol/Models/CommitmentNumberTests` |
| B3-CTX-02 | Obscuring factor (opener ‖ accepter) | BUG on reload (NL-127) | N1-T5 | `IT/Persistence/ChannelRoundTripTests.Given_NonInitiatorChannel_When_Reload_Then_SameObscuringFactor` |
| B3-OUT-01..04 | to_local, to_remote, anchors, HTLC scripts | DONE (NL-059 setter) | N2-T4 | Appendix C + F |
| B3-OUT-05 | Amounts rounded down | PARTIAL (NL-191) | N1-T5, N2-T1 | Appendix C tx 15 (`to_local_msat` 6_999_999_999) |
| B3-TRIM-01 | Base fee from funder before outputs | PARTIAL | N2-T1 | Appendix C |
| B3-TRIM-02 | Two anchors deducted | BUG (NL-061) | N2-T1 | Appendix F |
| B3-TRIM-03 | to_local below holder dust omitted | DONE | — | Appendix C |
| B3-TRIM-04 | to_remote uses holder dust | BUG (NL-062) | N2-T1 | `DT/…/CommitmentFeeCalculatorTests.Given_RemoteSideTx_Then_ToRemoteTrimmedWithHolderDust` |
| B3-TRIM-05/06 | HTLC trim thresholds (0 fee with anchors) | BUG (NL-195) | N2-T1 | Appendix C min/max feerate + F |
| B3-FEE-01/02 | HTLC tx fees 663/703, 0 with anchors | PARTIAL/BUG | N2-T1 | `…Given_BoltExample_Feerate5000_Then_3315_3515` |
| B3-FEE-03 | Base fee 724/1124 + 172n | BUG anchors (NL-061) | N2-T1 | `…Base5340_Actual7140` + Appendix F |
| B3-FEE-04/06 | Funder pays; funder output may be 0 | BUG (NL-196) | N2-T1 | Appendix C "fee greater than funder amount" |
| B3-FEE-05 | Feerate too low → MAY fail | MISSING | N9-T1 | see B2-FEE-R01 |
| B3-BAL-01 | HTLC debited from its offerer | BUG (NL-062) | N2-T2, N4-T1 | `DT/…/ReduceTests.Given_IncomingHtlc_Then_DebitedFromRemote` |
| B3-HTX-01 | HTLC-timeout/success structure | MISSING (NL-056) | N2-T4 | `IT/BOLT3/Bolt3HtlcTxVectorTests` |
| B3-HTX-02 | HTLC tx output script | BUG (NL-058) | N2-T4 | same + `BT/Outputs/HtlcResolutionOutputTests` |
| B3-HTX-03 | Anchors: SINGLE\|ACP remote sig | MISSING | N3-T1 | Appendix F HTLC txs [B5] |
| B3-LCTX-01 | Legacy closing tx | MISSING (NL-065) | N10-T2 | `BT/Builders/ClosingTransactionBuilderTests` + DK close |
| B3-CLTX-01 | Simple closing tx | MISSING | N11-T2 | same |
| B3-DUST-01 | Script dust thresholds | PARTIAL (NL-068) | N10-T1 | `BT/Services/DustServiceTests` |
| B3-KEY-01 | Per-commitment key derivation | DONE | — | Appendix E |
| B3-KEY-02 | Secret generation + shachain storage | PARTIAL (NL-136) | N3-T4 | Appendix D + persistence round trip [B5] |
| B3-VEC-C | Appendix C commit + HTLC txs byte-exact | PARTIAL (NL-176) | N2-T2, N2-T4 | `IT/BOLT3/Bolt3IntegrationTests`, `Bolt3HtlcTxVectorTests` |
| B3-VEC-F | Appendix F commit + HTLC txs | MISSING (NL-176) | N2-T5, N3-T1 | `IT/BOLT3/Bolt3AnchorVectorTests` |

### 6.14 Cross-cutting prerequisites
| ID | What | Status | Task | Test |
|---|---|---|---|---|
| X-01 | Serialized per-channel processing, ordered outbound, N replies | BUG (NL-033, NL-193) | N0-T3 | `AT/Node/Managers/PeerManagerTests` |
| X-02 | Transport lock-before-encrypt, `ReadExactlyAsync` | BUG (NL-104, NL-105) | N0-T2 | `TransportServiceTests` |
| X-03 | Local/remote channel params | BUG (NL-194) | N1-T4 | `ChannelFactoryTests` |
| X-04 | msat balances, local/remote numbers, remote next point | BUG (NL-191, NL-188, NL-051) | N1-T1/T2/T5 | `ChannelRoundTripTests` |
| X-05 | HTLC reload bugs | BUG (NL-125..128) | N1-T5 | `ChannelRoundTripTests` |
| X-06 | Fail-the-channel service | MISSING (NL-200) | N6-T3, N9-T4 | `ChannelFailureServiceTests` |
| X-07 | Feature hygiene | BUG (NL-109, NL-074) | N0-T4 | `FeatureOptionsTests` |
| X-08 | Application.Tests discovered | BUG (NL-167) | N0-T1 | `dotnet test` count |
| X-09 | Strict TLV reads on touched messages | PARTIAL (NL-001) | N0-T6, N11-T1 | serializer tests |
| X-10 | Persist-before-send / crash consistency (I1–I12) | MISSING | N4-T5, N5-T3, N7-T3 | invariant suite, crash tests |
| X-11 | Startup registration before connect | BUG (NL-201, NL-052) | N1-T6 | `PeerManagerTests` |
| X-12 | Gossip types don't kill peers | BUG (NL-100) | N0-T5 | serializer tests + DK idle test |

### 6.15 Test wiring summary
| Test kind | Location | Notes |
|---|---|---|
| Appendix C/D/E/F vectors | `test/NLightning.Integration.Tests/BOLT3/`, data in `test/NLightning.Tests.Utils/Vectors/` | byte-exact hex, spec commit recorded |
| Engine, planner, negotiators, validators, invariant simulator | `test/NLightning.Domain.Tests/Channels/{Commitments,Reestablish,Closing,Validators,Policies}/` | runs in CI |
| Handlers, services, two-node harness | `test/NLightning.Application.Tests/Channels/` | after N0-T1 |
| Signer, builders, verifier | `test/NLightning.Infrastructure.Bitcoin.Tests/{Signers,Builders,Services}/` | Release + Release.Native |
| Serializers | `test/NLightning.Infrastructure.Serialization.Tests/Messages/` | strict TLV cases |
| Persistence round trips, crash injection | `test/NLightning.Integration.Tests/Persistence/` | SQLite `:memory:`; Postgres/SQL Server locally via fixtures |
| LND interop | `test/NLightning.Integration.Tests/Docker/NormalOperationFlowTests.cs` (+ `Utils/NLightningTestNode.cs`) | Docker only, never in CI |

---

## 7. Mapping to ONION plan milestones

| This plan | ONION task | Scope delivered here |
|---|---|---|
| N0-T4 | §9 risk 7 (feature defaults) | RouteBlinding / AttributionData → No |
| N6-T1 `UpdateAddHtlcMessageHandler` | M4-T1 | Full BOLT 2 validation + persistence; no peel on receive. M4-T1 reduces to subscribing the switch to `IncomingHtlcLockedIn` |
| N6-T2 `LocalOnlyHtlcSwitch` | needs M3-T1 + M3-T2 (create) | Fail back with `temporary_node_failure` |
| N5-T1 `HtlcEntity.OnionSharedSecret` | M4-T7 (part) | Per-HTLC shared secret column |
| N8-T1 | M4 prerequisite (NL-075) | `IHopPayloadSerializer` in Domain |
| N8-T2 `FinalHopHtlcSwitch` | M4-T2, M4-T3 (single part) | Peel after lock-in, replay record, validation, final-hop checks; non-final → `unknown_next_peer` |
| N8-T3 `PaymentService` | M4-T6 (direct channel), M3-T2 (decrypt) | Single-hop send, payment table |
| N8 migration | M4-T7 (part) | Invoice + payment tables |
| Deferred | M3-T3, M4-T4, M4-T5, M4-T7 (circuits, persistent replay set), M3b, M5 | Plug into `IHtlcSwitch` / `IChannelOperations` / events without channel-layer changes. Replay cache stays in-memory (NL-078); SCID fixes (NL-101, NL-102) remain M4-T4 prerequisites |

ONION §7 prerequisites map as: handlers → N6; `ChannelModel` mutators / `HtlcState` → N4; HTLC signatures → N2/N3; HTLC reload → N1-T5; per-channel ordering → N0-T3; transport → N0-T2.

---

## 8. Risks

1. **Coupling to `CommitmentNumber.Increment()`** (G3): the open handlers mutate it today. Keep `ChannelOpeningFlowTests` green before and after N1-T1.
2. **Parameter-direction bugs** (NL-194) poison every limit check and the remote commitment's `to_self_delay`. Every ADD/FEE test uses distinct local/remote values. The existing open test hid this by never pushing funds (N1 proof covers it).
3. **HTLC signature order drift:** one mis-ordered signature makes LND force-close. The build result carries the order (N2-T3); Appendix C same-amount case + Appendix F.
4. **Persisted-data migration:** dev/regtest DBs hold sat balances, legacy `HtlcState` values and old-framing HTLC rows (NL-025). Migrations convert (`msat = sat × 1000`) and startup refuses unknown HTLC states.
5. **Signer vs DB state split:** `AdvanceLocalCommitment` before persist voids I3. Only the handler pipeline calls it, after the save; a recording-signer test asserts order.
6. **EF atomicity/durability** (G10, SQLite pragma) are inferred/unverified. N5-T2/T3 must prove them on all three providers.
7. **Retransmission determinism:** stored CS diffs are wire bytes; never re-derive them.
8. **Fail-the-channel without sweeps:** until BOLT 5, a failed channel's funds depend on CSV timeouts nobody sweeps. `EnableHtlcs` gate, CRITICAL alerts, regtest only.
9. **`option_data_loss_protect` is Compulsory** while reestablish is missing; peers assume RE-23 semantics. N7 is mandatory before any peer relies on it; N9-T4 checks `DataLossDetected`.
10. **Feature advertisement:** keep simple_close, anchors, quiesce, route_blinding, attribution_data, dual_fund, basic_mpp off until their milestones pass.
11. **LND 0.20 specifics (unverified):** direct `channel_update` after private opens; odd TLVs on add/fail/fulfill; absence of `fee_range`; gossip-query stalls; `update_fee` triggers in regtest; simple-close support. Log the LND version in Docker tests; odd TLVs are tolerated, unknown even ones rejected.
12. **Docker fixture state:** alice accumulates channels across tests; select by channel point. `FakeSecureKeyManager` is random per instance: restarts must reuse the same instance and SQLite file.
13. **Fee and dust rounding:** all BOLT 3 math is floor(msat → sat); trimmed sets differ per side. N8 proof includes a trimmed HTLC on purpose.
14. **Schema churn:** five migrations × three providers; never copy migration files between providers.
15. **Test discovery:** Application tests are invisible to CI until N0-T1; do it first.
16. **Spec drift:** `funding_txid` in CS and reestablish TLV 5 are recent. They are odd TLVs, so older peers ignore them.
