# BOLT Coverage Matrix

> Individual bugs, gaps and their status are tracked in [`ISSUES.md`](ISSUES.md) (`NL-###`); that ledger is the status source for each item, this file is the per-BOLT overview.

What this file is: a BOLT-by-BOLT compliance map of NLightning (C# / .NET 10), written for agents working in this repo.
Snapshot date: 2026-09-25, refreshed the same day on `wip/fafo` @ `1a38360` after the fix swarm and again @ `3c625e1` after the four-lane integration (BOLT2 N0-N4, ONION M3). It was compiled from per-area research maps. Every claim flagged "verified" was checked against the code while writing this file.
Paths are relative to the repo root.

Status legend:
- **complete**: spec behaviour is implemented and tested.
- **partial**: implemented with known gaps or bugs.
- **stub**: only names, enums, empty classes or commented-out code exist.
- **missing**: nothing exists.

A note on layers. A BOLT 2 message can be complete at the wire layer (Domain message + serializer) and still be missing at the behaviour layer (no Application handler). The matrix lists those two layers as separate rows.

---

## 0. Cross-cutting facts to know first

1. **Channel dispatch only handles channel opening.** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` has cases for OpenChannel, AcceptChannel, FundingCreated, ChannelReady and FundingSigned, plus update_fail_malformed_htlc without BADONION (warning + close). Each peer's channel messages run one at a time in arrival order under a per-channel lock, and every send goes through one ordered `PeerOutbox` per peer (NL-033, NL-193). Every other `IChannelMessage` falls to `default` and gets a channel-scoped `warning`; the peer stays connected. A message for a channel we don't know gets an `error` for that channel_id. Channel failures always carry their channel id, never the all-zero id (NL-203).
2. **Peer-level messages.** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` handles `IChannelMessage`, `error`, `warning`, gossip (256-259 dropped; 261/263 answered with empty replies; 265 ignored) and `stfu` (channel `warning` + disconnect, since we don't do quiescence). onion_message (513) is still unknown and dropped (odd).
3. **Unknown even message types throw on receive**, as BOLT 1 requires (`src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs`). The BOLT 7 types 256-265 are all registered now (NL-100), so gossip no longer kills peers. Malformed messages (unknown even TLV, wrong wire type) get a connection `warning` and the connection is closed (NL-207).
4. **All tests run in CI.** `dotnet test --filter 'FullyQualifiedName!~Docker'` runs 3121 tests (no skips) including Application.Tests (128) and Daemon.Tests (122); `scripts/check-sln-configs.py` keeps it that way (NL-167). Docker (local only): 12 tests including the BOLT2 Proof N0/N1 against LND.
5. **Onion core (M1+M2) and error onions (M3) exist; forwarding does not.** Sphinx construct/peel, hop payload parsing/validation and a replay cache are implemented from the spec (see the BOLT 4 table). The older LNBolt onion code was not ported.

---

## BOLT 1: Base protocol

| Feature / message | Status | Implementing files | Tests | Notes |
|---|---|---|---|---|
| Message framing (u16 type) and the unknown odd/even rule | complete | `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs` | `test/NLightning.Infrastructure.Tests/Protocol/Services/MessageServiceTests.cs` | `DeserializeMessageAsync<T>` checks the wire type (NL-011). A deserialize failure sends a `warning` (not an all-zero `error`) and closes the connection (NL-023, NL-207). |
| `init` (16) plus `networks` TLV (1) | complete | `src/NLightning.Domain/Protocol/Messages/InitMessage.cs`, `.../Tlv/NetworksTLV.cs`, `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs`, `.../PeerService.cs` | `InitMessageTests.cs`, `PeerServiceTests.cs`, `PeerServiceLifecycleTests.cs`, `PeerCommunicationService*Tests.cs` | Rejects only when no chain is shared (NL-002). Feature/chain failures send a `warning` after the peer's init; nothing is sent before it (NL-003). No signet/testnet4 hashes (NL-012). |
| `init` `remote_addr` TLV (3) | partial (buggy) | `src/NLightning.Domain/Protocol/Tlv/RemoteAddressTlv.cs`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs` | none | Our side never sends it (commented out at `src/NLightning.Domain/Node/Options/FeatureOptions.cs:241`). `RemoteAddressTlvConverter` is registered in `TlvConverterFactory`, so it can be serialized, but the Tor v3 decode and the DNS (type 5) encode are wrong. |
| `error` (17) / `warning` (1) | complete | `src/NLightning.Domain/Protocol/Messages/ErrorMessage.cs`, `WarningMessage.cs`, `.../Payloads/ErrorPayload.cs`, `src/NLightning.Domain/Exceptions/` | `ErrorMessageTests.cs`, `WarningMessageTests.cs`, `test/NLightning.Domain.Tests/Protocol/Payloads/ErrorPayloadTests.cs` | Warning reuses `ErrorPayload`. `ChannelErrorException` sends `error` for that channel and disconnects (no failed-channel state yet, NL-200); `ChannelWarningException` sends a `warning`, and closes the connection when `CloseConnection` is set. |
| `ping` (18) / `pong` (19) | complete | `src/NLightning.Infrastructure/Protocol/Services/PingPongService.cs`, `PeerCommunicationService.cs`, `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` | `PingMessageTests.cs`, `PongMessageTests.cs`, `PeerCommunicationService*Tests.cs` | No pong for `num_pong_bytes >= 65532` (NL-004); ignored bytes consumed (NL-007); pinging starts after both inits and a pong timeout disconnects (NL-006). No ping rate limit (NL-005). |
| BigSize | complete | `src/NLightning.Domain/Protocol/ValueObjects/BigSize.cs`, `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs` | `test/NLightning.Infrastructure.Serialization.Tests/ValueObjects/BigSizeTypeSerializerTests.cs` + `Vectors/BigSize.txt` | Decoding is canonical: non-minimal encodings throw. All 18 spec vectors in `BigSize.txt` are active, including the 3 non-canonical failures. |
| TLV record / stream | complete | `src/NLightning.Domain/Protocol/Tlv/BaseTlv.cs`, `.../Models/TLVStream.cs`, `src/NLightning.Infrastructure.Serialization/Tlv/TlvSerializer.cs`, `TlvStreamSerializer.cs` | `TlvStreamSerializerTests.cs`, `TlvSerializerTests.cs`, `test/NLightning.Domain.Tests/Protocol/Models/TlvStreamTests.cs`, strict-TLV cases in the message tests | Deserialization rejects non-increasing types and length > remaining bytes. Every message extension uses `DeserializeStrictAsync(stream, knownTypes)`, which also rejects unknown **even** types (NL-001). Serialization picks the converter by runtime type and writes a raw `BaseTlv` verbatim. |
| Truncated ints (tu16/tu32/tu64) | complete | `src/NLightning.Domain/Protocol/Tlv/TruncatedInt.cs` (single codec, used by Domain TLV constructors and Infrastructure converters) | `TruncatedIntTests.cs`, `TruncatedIntTlvConverterTests.cs` | Minimal encoding enforced on decode. |
| `peer_storage` / `peer_storage_retrieval` | missing | — | — | The feature bit `Feature.OptionProvideStorage` (43) exists in `src/NLightning.Domain/Enums/Feature.cs`. No message types. |

## BOLT 2: Peer protocol for channel management

> Implementation plan for normal operation, reestablish and close (with the requirements traceability matrix): [`BOLT2_NORMAL_OPERATION_PLAN.md`](BOLT2_NORMAL_OPERATION_PLAN.md).

### Wire layer (Domain message + payload + serializer)

| Messages | Status | Files | Tests | Notes |
|---|---|---|---|---|
| open_channel (32), accept_channel (33), funding_created (34), funding_signed (35), channel_ready (36) | complete | `src/NLightning.Domain/Protocol/Messages/{OpenChannel1,AcceptChannel1,FundingCreated,FundingSigned,ChannelReady}Message.cs`, serializers in `src/NLightning.Infrastructure.Serialization/{Payloads,Messages/Types/}` | `OpenChannel1MessageTests.cs`, `AcceptChannel1MessageTests.cs`, `FundingCreatedMessageTests.cs`, `FundingSignedMessageTests.cs`, `ChannelReadyMessageTests.cs` | channel_type is optional on the wire (NL-027) and sent big-endian (NL-112). |
| open_channel2 (64), accept_channel2 (65), tx_add_input … tx_abort (66-74) | complete (wire) | `src/NLightning.Domain/Protocol/Messages/{OpenChannel2,AcceptChannel2,Tx*}Message.cs` | `OpenChannel2MessageTests.cs`, `AcceptChannel2MessageTypeSerializerTests.cs`, `Tx*MessageTests.cs` | See the behaviour table below. |
| shutdown (38), closing_signed (39) + fee_range | complete (wire) | `ShutdownMessage.cs`, `ClosingSignedMessage.cs`, `.../Tlv/FeeRangeTlv.cs` | `ShutdownMessageTests.cs`, `ClosingSignedMessageTests.cs` | `fee_range` is optional on receipt (NL-198). |
| closing_complete (40) / closing_sig (41) (option_simple_close) | missing | — | — | `Feature.OptionSimpleClose` (61) exists in the enum only. |
| update_add_htlc (128) | partial | `src/NLightning.Domain/Protocol/Payloads/UpdateAddHtlcPayload.cs`, `.../Messages/UpdateAddHtlcMessage.cs`, `src/NLightning.Infrastructure.Serialization/Payloads/UpdateAddHtlcPayloadSerializer.cs`, `.../Messages/Types/UpdateAddHtlcMessageSerializer.cs` | `UpdateAddHtlcMessageTests.cs` | The onion is a mandatory raw `ReadOnlyMemory<byte>` of exactly `OnionConstants.PacketLength` (1366) bytes; the payload constructor and serializer enforce it and a truncated message throws. The extension is read with `DeserializeStrictAsync` (unknown even types rejected). The blinded path_key TLV (0) is looked up with `TlvConstants.BlindedPath` and must be a 33-byte point (length/prefix only; not curve-validated). No HTLC processing uses it yet. |
| update_fulfill_htlc (130), update_fail_htlc (131), update_fail_malformed_htlc (135), commitment_signed (132), revoke_and_ack (133), update_fee (134) | complete (wire) | `src/NLightning.Domain/Protocol/Messages/Update*Message.cs`, `CommitmentSignedMessage.cs`, `RevokeAndAckMessage.cs` | matching `*MessageTests.cs` | No `attribution_data` (TLV 1) or `fulfillment_payload` (TLV 3) (NL-022). commitment_signed TLV 1 `funding_txid` is always sent and optional on receipt (NL-199). A malformed HTLC without BADONION gets warning + close (NL-023). |
| channel_reestablish (136) + next_funding | complete (wire) | `ChannelReestablishMessage.cs`, `.../Tlv/NextFundingTlv.cs` | `TxChannelReestablishMessageTests.cs` | `next_funding` is type 1 with `retransmit_flags` (NL-197). TLV 5 `my_current_funding_locked` is ignored as an unknown odd type. |
| stfu (2) | partial | `StfuMessage.cs`, `PeerService.cs` | `StfuMessageTests.cs`, `PeerServiceTests.cs` | Answered with a channel `warning` + disconnect; `option_quiesce` is not advertised (NL-019). Quiescence itself is NL-042. |
| splicing (splice_init/ack/locked), start_batch | missing | — | — | |

### Behaviour layer (Application handlers / state machine)

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| v1 open, non-initiator (open_channel → accept_channel → funding_created → funding_signed) | complete | `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs`, `FundingCreatedMessageHandler.cs`, `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs`, `.../Validators/ChannelOpenValidator.cs` | `test/NLightning.Application.Tests/Channels/Handlers/*`, `ChannelOpenValidatorTests.cs`, Docker `ChannelOpeningFlowTests.cs` | push ≤ funding and funder amount covers the fee (NL-043, NL-044); upfront_shutdown_script required only when negotiated (NL-204). accept_channel echoes the opener's channel_type and carries our own limits (NL-218, NL-194); unsupported channel_type bits are refused; the local upfront_shutdown_script is never generated (NL-045). |
| v1 open, initiator | complete | `AcceptChannel1MessageHandler.cs`, `FundingSignedMessageHandler.cs`, `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs` | Application and Daemon handler tests; Docker e2e against LND | Error cleanup removes the channel and releases UTXOs (NL-047; signer registration left behind, NL-221). The channel is persisted in funding_signed, before broadcast, as BOLT 2 wants (NL-048). open_channel carries the whole funding amount when pushing (NL-227), is built from `ChannelParams.Local`, and never sets announce_channel with scid_alias (NL-217; no public-channel option, NL-236). Docker Proof N1: a 300,000 sat push channel is Active in LND with matching balances. |
| channel_ready / funding depth | partial | `ChannelReadyMessageHandler.cs`, `FundingConfirmedMessageHandler.cs`, `ChannelManager.cs` | `ChannelReadyMessageHandlerTests.cs`, `FundingConfirmedMessageHandlerTests.cs`, `ChannelManagerTests.cs` | Stale filter covers unconfirmed opening states only (NL-049); confirmation fires once (NL-050); the peer's second point is stored (NL-051); SCID from the real block position (NL-102); aliases unique and persisted (NL-103). The second local point is at index 2^48-2 (NL-187) and local/remote commitment numbers are separate and persisted (NL-188). Docker Proof N0: a new channel stays Active and connected after 30 s idle. |
| v2 / dual-funding / interactive-tx | stub | `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs`, `src/NLightning.Infrastructure/Protocol/Validators/Tx*Validator.cs` | `InteractiveTransactionServiceTests.cs`, `TxSerialIdParityTests.cs`, `TxAddInputValidatorTests.cs` | Not in DI. serial_id parity and uniqueness fixed (NL-038, NL-040), validation awaitable (NL-039). Message caps bypassable (NL-219); prevTx/script checks TODO (NL-041). option_dual_fund is experimental-gated. |
| HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee) | partial (pure engine only, not wired) | `src/NLightning.Domain/Channels/Commitments/` (`ChannelCommitments`, `HtlcStateTable`, `Validators/UpdateValidator`), `src/NLightning.Application/Channels/Services/CommitmentSigningService.cs` | `test/NLightning.Domain.Tests/Channels/Commitments/*` (per-rule tests, scenario tests, 500-seed two-engine invariant simulator; 10k seeds explicit) | BOLT2 N4 engine: every update/commit/revoke op with the BOLT 2 sender/receiver rules (plan matrix ids), core-lightning HTLC states. No handlers (N6), no persistence of the engine state (N5), engine signer ports not implemented (NL-230). |
| Ordered processing, reconnects, startup | complete | `src/NLightning.Application/Node/Managers/PeerManager.cs`, `Node/Services/PeerOutbox.cs`, `Channels/Services/ChannelLockProvider.cs` | `PeerManagerTests`, `PeerOutboxTests`, `ChannelLockProviderTests`, `ChannelManagerConcurrencyTests` | Per-channel lock, one inbound loop + outbox per peer (NL-033, NL-193); startup registers channels before connecting and reconnects peers with active channels with backoff (NL-201). |
| Close (shutdown / closing_signed) | missing (behaviour) | `ChannelState.Closing/Closed` enum only | — | Nothing moves a channel into Closing. |
| channel_reestablish / data_loss_protect | missing (behaviour) | `ChannelManager.cs` (`default` branch) | — | An incoming channel_reestablish gets a channel-scoped `warning`. data_loss_protect is advertised Optional (ASSUMED bit) without an implementation (NL-035). |
| Quiescence | missing (behaviour) | — | — | |

## BOLT 3: Bitcoin transaction and script formats

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Funding output (2-of-2 P2WSH) | complete | `src/NLightning.Infrastructure.Bitcoin/Outputs/FundingOutput.cs`, `Builders/FundingOutputBuilder.cs` | `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs` | |
| Funding tx build + fee/change | complete | `src/NLightning.Domain/Bitcoin/Transactions/Factories/FundingTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs` | Appendix B asserted byte-for-byte in `Bolt3IntegrationTests.cs`; builder tests | Dust change folds into the fee, insufficient funds is typed (NL-063); inputs/outputs BIP 69-sorted and the funding output index returned (NL-064). |
| Commitment tx (to_local / to_remote / HTLC outputs, obscured number, BIP69+CLTV ordering) | complete | `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/CommitmentTransactionBuilder.cs`, `Comparers/TransactionOutputComparer.cs`, `src/NLightning.Domain/Protocol/Models/CommitmentNumber.cs` | Every Appendix C commitment as a full signed tx in `test/NLightning.Integration.Tests/BOLT3/Bolt3CommitmentVectorTests.cs` (verbatim spec vectors), `CommitmentNumberTests.cs`, `CommitmentTransactionBuilderTests.cs`, `CommitmentFeeCalculatorTests.cs` | Spec-driven factory over `CommitmentTxSpec` (N2-T2); `CommitmentFeeCalculator`; per-side dust and to_self_delay (NL-194); no reserve check in the factory (NL-196); `BuildWithOutputMap` returns HTLC outputs in tx order. Duplicate engine fee math: NL-231. |
| option_anchors | partial | `src/NLightning.Domain/Bitcoin/Transactions/Outputs/AnchorOutputInfo.cs`, `src/NLightning.Infrastructure.Bitcoin/Outputs/ToAnchorOutput.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs` | `Bolt3AnchorVectorTests.cs` (9 Appendix F commitments + 15 HTLC txs) | Fee, both anchors, zero-fee HTLC trimming (NL-061, NL-195) and SINGLE\|ACP HTLC sigs match Appendix F. option_anchors stays experimental-gated until BOLT 5 CPFP exists. |
| Output scripts (to_local, to_remote, anchor, offered/received HTLC) | complete | `src/NLightning.Infrastructure.Bitcoin/Outputs/*.cs` | `Outputs/*OutputTests.cs` + Appendix C | |
| HTLC-success / HTLC-timeout second-stage txs + HTLC signatures | complete | `src/NLightning.Domain/Bitcoin/Transactions/Factories/HtlcTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/HtlcTransactionBuilder.cs`, `Signers/LocalLightningSigner.cs` (HTLC APIs), `Outputs/HtlcResolutionOutput.cs` | `Bolt3HtlcTxVectorTests.cs` (33 Appendix C), `Bolt3AnchorVectorTests.cs`, `Bolt3HtlcSignerVectorTests.cs`, `CommitmentSigningServiceVectorTests.cs` | NL-056, NL-057, NL-058; signatures in commitment output order. |
| Closing tx | stub | `.../Transactions/ClosingTransaction.cs` (commented out) | — | |
| Key derivation (localpubkey / revocation) | complete | `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs`, `CommitmentKeyDerivationService.cs` | Appendix E vectors (`Bolt3IntegrationTests.cs:~1098-1154`) | |
| Per-commitment secret gen + shachain storage | complete (not wired) | `KeyDerivationService.cs`, `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs`, `src/NLightning.Infrastructure.Bitcoin/Services/PerCommitmentSecretVerifier.cs`, `src/NLightning.Infrastructure.Repositories/Database/Channel/RemoteShachainDbRepository.cs` | Appendix D vectors, `SecretStorageServiceTests.cs`, `PerCommitmentSecretVerifierTests.cs`, `RemoteShachainPersistenceTests.cs` | Numbers vs indices (NL-187); secret release guarded by the local commitment number (NL-189); the peer's shachain exports/loads and is persisted (NL-136, NL-066), but nothing saves or loads it at runtime until BOLT2 N5/N6. Only one remote per-commitment point is stored (NL-232). |
| Dust limits | complete | `src/NLightning.Infrastructure.Bitcoin/Services/DustService.cs` | — | Registered in DI (NL-068). |

## BOLT 4: Onion routing

**Overall: partial (M1+M2+M3 of the plan done).** The primitives, onion packet, Sphinx construct/peel, hop payload parsing/validation, an in-memory replay cache and the legacy error onion (failure messages, create/wrap/decrypt, malformed conversion) exist and are checked against the official `bolt04/*.json` vectors. Nothing calls them yet: there is no HTLC handling on the wire, no forwarding/final-hop logic (M4), no attribution_data (M3b) and no route-blinding payload processing (M5). The spec requirements, design and milestone plan are in [`docs/agents/ONION_ROUTING_PLAN.md`](ONION_ROUTING_PLAN.md).

| Feature | Status | Files (hooks) | Tests | Notes |
|---|---|---|---|---|
| onion_packet (1366 B: version, pubkey, 1300 hop_payloads, hmac) | complete (model + update_add_htlc framing) | `src/NLightning.Domain/Protocol/Onion/ValueObjects/OnionPacket.cs`, `.../Onion/Constants/OnionConstants.cs`; raw bytes in `UpdateAddHtlcPayload.OnionRoutingPacket` | `test/NLightning.Domain.Tests/Protocol/Onion/OnionPacketTests.cs`, `UpdateAddHtlcMessageTests.cs` | Value object validates length only (version/key are the peeler's job). `UpdateAddHtlcPayload` keeps raw bytes but requires exactly 1366; a truncated message throws. |
| Sphinx construct / peel, filler, key-gen (rho, mu, um, pad, ammag) | complete | `src/NLightning.Domain/Protocol/Onion/Interfaces/ISphinxService.cs`, `.../Models/OnionHop.cs`, `PeeledOnion.cs`; `src/NLightning.Infrastructure.Bitcoin/Onion/` (`SphinxService`, `OnionBuilder`, `OnionPeeler`, `SphinxKeyGenerator`, DI singleton) | `test/NLightning.Infrastructure.Bitcoin.Tests/Onion/`, `test/NLightning.Integration.Tests/BOLT4/OnionVectorTests.cs` | Byte-exact vs `onion-test.json`; peel applies the `path_key` tweak (checked against `blinded-payment-onion-test.json`). |
| ECDH shared secret = SHA256(compressed(k·P)) | complete (reusable) | `src/NLightning.Infrastructure/Crypto/Interfaces/IEcdh.cs`, `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs` (DI in `Infrastructure.Bitcoin/DependencyInjection.cs:35`) | `EcdhTests.cs` (BOLT 4 hop-0 shared secret from `onion-error-test.json`), all hops via `SphinxKeyGeneratorTests.cs` / `OnionVectorTests.cs` | Same construction as BOLT 8. |
| Ephemeral key blinding (point / scalar tweak-mul) | complete (reusable) | `src/NLightning.Domain/Crypto/Interfaces/ISecp256K1Math.cs`, `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Secp256K1Math.cs` (DI singleton) | `Secp256K1MathTests.cs` | Rejects off-curve points and out-of-range scalars. Callers must wipe returned `PrivKey` arrays that hold secrets. |
| HMAC-SHA256 (short keys) | complete | `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs` | `test/NLightning.Infrastructure.Tests/Crypto/Functions/HmacSha256Tests.cs` (RFC 4231) | HMAC checks in the peeler use `CryptographicOperations.FixedTimeEquals`. |
| Raw ChaCha20 keystream (zero 96-bit nonce, counter 0) | complete | `ICryptoProvider.StreamChaCha20IetfXor` in all 3 providers (libsodium, Native/BouncyCastle `ChaCha7539Engine`, JS sumo), `src/NLightning.Infrastructure/Crypto/Ciphers/ChaCha20Stream.cs` | Infrastructure.Tests crypto tests (Release + Release.Native; JS untested outside Wasm) | — |
| Hop payload TLVs (2, 4, 6, 8, 10, 12, 16, 18) | complete (parse + validate) | Domain types in `src/NLightning.Domain/Protocol/Onion/Tlv/`, type numbers in `.../Onion/Constants/OnionPayloadTlvTypes.cs`, converters in `src/NLightning.Infrastructure/Protocol/Tlv/Converters/Onion/` (registered in `TlvConverterFactory`); `HopPayload`, `HopPayloadValidator`, `InvalidOnionPayloadFailureFactory` (Domain); `src/NLightning.Infrastructure.Serialization/Onion/HopPayloadSerializer.cs`; replay cache `src/NLightning.Infrastructure/Protocol/Onion/OnionReplayCache.cs` | `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/Onion/`, `test/NLightning.Domain.Tests/Protocol/Onion/`, `test/NLightning.Infrastructure.Serialization.Tests/Onion/`, `test/NLightning.Integration.Tests/BOLT4/HopPayloadVectorTests.cs` | `current_path_key` (12) is not curve-validated (belongs to M5). The validator always reports `invalid_onion_payload`; the `invalid_onion_blinding` mapping is M5. Replay cache: see its own row. |
| Replay protection | partial | `src/NLightning.Domain/Protocol/Onion/Interfaces/IOnionReplayCache.cs`, `src/NLightning.Infrastructure/Protocol/Onion/OnionReplayCache.cs` (singleton in `AddInfrastructureServices`) | `test/NLightning.Infrastructure.Tests/Protocol/Onion/OnionReplayCacheTests.cs` | Bounded (100k) FIFO, in-memory only; record only after a successful peel. M4 needs cltv-based expiry and persistence. |
| Failure messages / codes / error packet (um, ammag) | complete (not wired) | `src/NLightning.Domain/Protocol/Onion/{Enums/FailureCode,Models/FailureMessage,Models/DecryptedFailure,Interfaces/IFailureOnionService,Interpreters/FailureInterpreter,Factories/FailureChannelUpdateFactory}.cs`, `src/NLightning.Infrastructure.Bitcoin/Onion/FailureOnionService.cs`, `src/NLightning.Infrastructure.Serialization/Onion/FailureMessageSerializer.cs` | `test/NLightning.Integration.Tests/BOLT4/FailureOnionVectorTests.cs` (onion-error-test.json + inline trace, every hop), unit tests | ONION M3 (NL-070): every code with layout validation, create/wrap/decrypt with max(27, hops) constant iterations, origin interpretation. UPDATE failures carry an empty `channel_update` (len = 0) until BOLT 7. |
| update_fail_malformed → update_fail_htlc conversion | complete (not wired) | `FailureMessage.FromMalformed`, `Validators/MalformedHtlcValidator.cs`, `IFailureOnionService.CreateErrorPacketFromMalformed` | `BOLT4/MalformedFailureConversionTests.cs`, `MalformedHtlcValidatorTests.cs` | NL-071; an all-zero sha256_of_onion is accepted. |
| Route blinding | partial (wire only) | `src/NLightning.Domain/Protocol/Tlv/BlindedPathTlv.cs`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/BlindedPathTlvConverter.cs` | `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/BlindedPathTlvConverterTests.cs` | `MessageFactory.CreateUpdateAddHtlcMessage` cannot attach it. The feature bit `OptionRouteBlinding=25` defaults to No and is experimental-gated (NL-074). |
| Basic MPP (final-hop HTLC sets) | missing | — | — | — |
| attribution_data / fulfillment_payload | missing | Feature `OptionAttributionData=37` (default No, experimental-gated) | — | ONION M3b (NL-072, NL-022). |
| onion_message (513) | missing | Feature `OptionOnionMessages=39` (default No) | — | `IPeerService.SendMessageAsync` accepts only `IChannelMessage`, so a new send path is needed. |
| Test vectors (onion-test.json, onion-error-test.json, route-blinding-test.json, blinded-payment-onion-test.json, blinded-onion-message-onion-test.json) | committed | `test/NLightning.Integration.Tests/BOLT4/Vectors/` (+ `README.md` with source), typed loader `test/NLightning.Tests.Utils/Vectors/Bolt4Vectors.cs`, csproj `Content` glob | `test/NLightning.Integration.Tests/BOLT4/Bolt4VectorLoadingTests.cs` | Used today: onion-test (construct/peel), onion-error-test (per-hop keys and the error packets, M3), the inline Returning Errors trace (`returning-errors-trace.json`), blinded-payment-onion-test (peel chain with path_key), blinded-onion-message (peel chain). Not yet used: route-blinding-test (M5). |

## BOLT 5: On-chain transaction handling

| Feature | Status | Files | Notes |
|---|---|---|---|
| Funding confirmation / watched txs | partial | `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Models/WatchedTransactionModel.cs` | ZMQ rawblock only. No reorg handling (NL-096). A failing block halts processing but no longer loops forever, and the queue is bounded (NL-097); the halt is only logged (NL-216), a failed block can be partly saved (NL-214), and the tip is not processed at startup (NL-215). |
| Penalty / justice tx, revocation watch | stub | `src/NLightning.Domain/Bitcoin/Transactions/Models/PenaltyTransactionModel.cs` (empty), `src/NLightning.Domain/Bitcoin/Interfaces/IRevocationWatchDbRepository.cs` (empty), `src/NLightning.Infrastructure.Persistence/Entities/Bitcoin/RevocationWatchEntity.cs` (not mapped), `BlockchainMonitorService.cs:189-198,400-411` (commented out) | |
| Unilateral close sweeps, HTLC on-chain resolution | missing | — | |

## BOLT 7: P2P node and channel discovery

| Feature | Status | Files | Notes |
|---|---|---|---|
| channel_announcement (256), node_announcement (257), channel_update (258), announcement_signatures (259) | stub | `src/NLightning.Domain/Protocol/Messages/{ChannelAnnouncement,NodeAnnouncement,ChannelUpdate,AnnouncementSignatures}Message.cs` (raw `GossipPayload`) | Parsed as raw bytes and dropped in `PeerService` (NL-100). No validation, no graph (NL-099). |
| Gossip queries (261-265) | partial | `Query{ChannelRange,ShortChannelIds}Message.cs`, `Reply{ChannelRange,ShortChannelIdsEnd}Message.cs`, `GossipTimestampFilterMessage.cs`, `src/NLightning.Infrastructure/Node/Services/GossipQueryResponder.cs` | Queries get one empty `reply_channel_range` (sync_complete=1) or `reply_short_channel_ids_end` with full_information=0; bad queries get a warning; the timestamp filter is ignored (NL-205). gossip_queries is advertised Optional, gossip_queries_ex is off. |
| short_channel_id encoding | complete | `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs`, `BlockchainMonitorService.cs` | Masks fixed to the BOLT 7 widths (NL-101); the tx index is the position in the block, 24-bit end to end (NL-102). The channel's SCID is persisted (NL-225). |
| scid_alias | partial | `ChannelModel.LocalAliases/RemoteAlias`, `FundingConfirmedMessageHandler.cs`, `ShortChannelIdTlv`, `ChannelLocalAliasEntity` | Aliases are unique against known SCIDs/aliases, reused on re-confirmation and persisted (NL-103, NL-209). option_scid_alias defaults to No. |
| Graph / routing table storage | missing | TODO "update routing tables" at `ChannelReadyMessageHandler.cs:~106`, `FundingConfirmedMessageHandler.cs:~79` | |
| node address descriptors | partial | `RemoteAddressTlvConverter.cs` | See BOLT 1 remote_addr. |

## BOLT 8: Encrypted and authenticated transport

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Noise_XK handshake (acts 1-3) | complete | `src/NLightning.Infrastructure/Transport/Handshake/States/{HandshakeState,SymmetricState,CipherState}.cs`, `src/NLightning.Infrastructure/Transport/Services/HandshakeService.cs` | `test/NLightning.Integration.Tests/BOLT8/{Initiator,Responder,EndToEnt}IntegrationTests.cs` (spec vectors) | |
| Framing + key rotation | complete | `src/NLightning.Infrastructure/Transport/Encryption/Transport.cs`, `CipherState.cs` | `BOLT8/MessageIntegrationTests.cs` (msgs 0/1/500/501/1000/1001), max-size in-place decrypt test | Maximum body is 65535 bytes (NL-106). |
| TCP read loop / concurrency | complete | `src/NLightning.Infrastructure/Transport/Services/TransportService.cs`, `TcpService.cs` | `TransportServiceTests.cs` | `ReadExactlyAsync` for header and body (NL-104); the write lock is held across encrypt + write and a frame is sent whole once encrypted (NL-105). IPv6 listen addresses are unsupported (NL-107). Live interop with LND is covered by Docker `AbcNetworkTests.cs`. |
| ECDH | complete | `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs` | Covered by the BOLT 8 vectors | |

## BOLT 9: Assigned feature flags

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Feature enum (odd-bit values) | complete | `src/NLightning.Domain/Enums/Feature.cs` | — | |
| FeatureSet bitfield, negotiation, serialization | complete | `src/NLightning.Domain/Node/FeatureSet.cs`, `.../Options/FeatureOptions.cs`, `src/NLightning.Infrastructure.Serialization/Node/FeatureSetSerializer.cs` | `FeatureSetTests.cs`, `FeatureOptionsTests.cs`, `FeatureSetSerializerTests.cs` | BOLT 9 dependency table and per-context filtering (NL-110, NL-111); ASSUMED bits omitted by a peer count as supported. Negotiated: Compulsory if either side requires, Optional if both support. Unimplemented features (anchors, quiesce, dual_fund, route_blinding, attribution_data, simple_close, basic_mpp, onion_messages, provide_storage) default to No and are refused unless `Features:AllowExperimentalFeatures=true` (NL-109, NL-206). Wumbo is enforced (2^24 sat) unless negotiated. `GetBytes` keeps a highest bit at a multiple of 8 (NL-226). |
| channel_type | complete | `src/NLightning.Domain/Protocol/Tlv/ChannelTypeTlv.cs`, `FeatureSet.GetWireBytes()` | `ChannelTypeTlvConverterTests.cs`, handler tests | Sent big-endian on both open paths (NL-112); the acceptor echoes the opener's bytes and refuses bits other than 12/22/46/50 (NL-218). |

## BOLT 10: DNS bootstrap (for completeness)

stub. `src/NLightning.Infrastructure/Protocol/Services/DnsSeedClient.cs` is fully commented out (NL-113); its commented-out test was deleted (NL-177).

## BOLT 11: Invoice protocol

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Bech32 envelope, HRP / network, amount multipliers, timestamp, signature sign/verify/recover | complete | `src/NLightning.Bolt11/Models/Invoice.cs`, `src/NLightning.Infrastructure.Bitcoin/Encoders/Bech32Encoder.cs` | `test/NLightning.Bolt11.Tests/`, `test/NLightning.Integration.Tests/BOLT11/` (+ `Vectors/ValidInvoices.txt`), Blazor smoke tests | High-S signatures are rejected when `n` is present (NL-213). `Bits()` fixed (NL-123). |
| p, h, s, n, d, x, m fields | complete | `src/NLightning.Bolt11/Models/TaggedFields/*` | per-field tests | `x` is a variable-length 5-bit-group field up to 63 bits (NL-121). Non-minimal `x`/`c`/`9` lengths are accepted (NL-222). |
| c (min_final_cltv_expiry) | complete | `MinFinalCltvExpiryTaggedField.cs` | per-field tests | Defaults to 18; a zero `c` is dropped (NL-115). |
| r route hints | complete | `RoutingInfoTaggedField.cs`, `src/NLightning.Domain/Models/RoutingInfo.cs` | per-field tests | Every `r` field is kept, empty ones are rejected, fees and cltv_delta are unsigned (NL-116, NL-117). |
| f fallback | partial | `FallbackAddressTaggedField.cs` | | No taproot (v1). |
| 9 features / reject unknown even bits | complete | `FeaturesTaggedField.cs`, `Invoice.cs` | `InvoiceTests.cs` | Unknown required bits rejected; required bits set on encode (NL-119). |
| Validation on encode | partial | `src/NLightning.Bolt11/Services/InvoiceValidationService.cs` | | Runs on decode only. |
| Integration with the node | missing | — | | No `src/` project references `NLightning.Bolt11`. No invoice store and no pay/invoice IPC command (`src/NLightning.Domain/Client/Enums/ClientCommand.cs`). |

## bLIPs / extensions

None implemented. No bLIP-specific code was found. `TlvStreamSerializer` can now write raw `BaseTlv` records (e.g. custom records ≥65536), but nothing uses them yet. BOLT 12 (offers) is missing.

---

## Persistence gaps that affect BOLT compliance

These are mostly in `src/NLightning.Infrastructure.Repositories/Database/Channel/`. The `RemoteNodeId` column bug is in the Persistence project.

- Fixed by the swarm: HTLC reload enum comparisons and `Signature` persistence (NL-125, NL-128), remote funding key and basepoint order on reload (NL-126, NL-127), SQL Server `RemoteNodeId` width (NL-129), change address (NL-131), child-row upserts in `UpdateAsync` (NL-192), persisted scid aliases (NL-209).
- Fixed by the four-lane work: msat balances (NL-191), real SCID (NL-225), per-side channel params (NL-194), local/remote commitment numbers (NL-188), the peer's shachain (NL-136); `ChannelRoundTripTests` reloads every `ChannelModel` field.
- Still missing: the commitment-engine state (HTLC states 10-39, fee updates, pending commitment, remote next point: NL-232) is not persisted (BOLT2 N5).
- No tables for any of: onion shared secret, forwarding circuit, invoices/preimages, payment attempts, or the channel graph.

---

## Prioritized path to a routing node

Each step lists its prerequisites. Verification for every step, matching CI (`.github/workflows/dotnet.yml:23,26,29`):

```sh
dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121 \
  && dotnet format --verify-no-changes --exclude "**/BlazorTests/**" \
  && dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'
dotnet run --project test/NLightning.Application.Tests
dotnet run --project test/NLightning.Daemon.Tests
```

The `--filter` excludes the Docker e2e tests, which need Docker. Since step 1, `dotnet test` covers Application.Tests and Daemon.Tests, so the two `dotnet run` lines are optional. For crypto-provider work (steps 7 and 8), also build and test with `-c Release.Native` (`.github/workflows/dotnet.native.yml`) and run the Release.Wasm Blazor tests (`.github/workflows/dotnet.wasm.yml`).

1. **Test and CI hygiene (small).** **Done** (NL-167, NL-175, NL-176). Add `xunit.runner.visualstudio` to `test/NLightning.Application.Tests` and `test/NLightning.Daemon.Tests`. Add serializer tests for open_channel, accept_channel, funding_created and funding_signed. Re-enable the Appendix B funding test body.
2. **Stop killing peers on unknown traffic.** **Done** (NL-100, NL-205, NL-109, NL-019, NL-104, NL-105). Order mattered here.
   - First, make BOLT 7 types 256-259 (and ideally 261-265) known types. They cannot simply be "ignored": `MessageSerializer.DeserializeMessageAsync` (`src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs:57-75`) throws on any unregistered even type, as BOLT 1 requires. Each needs a message class, a payload serializer, and registration in both dictionaries of `src/NLightning.Infrastructure.Serialization/Factories/MessageTypeSerializerFactory.cs` (`RegisterSerializers` L45+, `RegisterTypeDictionary` L113+) and of `PayloadSerializerFactory.cs` (see the Stfu entries at L64/L105). Then route them in `PeerService` to a no-op or logging handler. See the message recipe in `docs/agents/REPO_MAP.md` section 7 and the Recipes section of `CLAUDE.md`.
   - Only after that, change the `FeatureOptions` defaults (`src/NLightning.Domain/Node/Options/FeatureOptions.cs`). Stop advertising option_dual_fund, route_blinding and attribution_data until each is implemented. Keep gossip_queries advertised (or add a `gossip_timestamp_filter` reply): without it, BOLT 7 peers fall back to relaying gossip unsolicited, so dropping the flag before 256/258 are handled makes disconnects worse, not better.
   - Route `stfu`. Use `ReadExactlyAsync` in `TransportService` and hold the write lock across encrypt and write.
3. **Fix correctness bugs on the routing path.** **Done** (NL-101, NL-102, NL-049, NL-050, NL-125..NL-128, NL-043, NL-044).
   - ShortChannelId `ulong` masks.
   - `BlockchainMonitorService` tx index used for the SCID.
   - `ForgetStaleChannels` state filter and the repeated re-confirm loop.
   - `ChannelDbRepository` HTLC enum comparisons, funding pubkey, and CommitmentNumber order on reload.
   - HTLC signature persistence.
   - Validator push_amount and anchor-weight bugs.
4. **BOLT 1 strictness.** Done in M1: canonical BigSize (vectors re-enabled), tu16/tu32/tu64, strictly increasing TLV types, raw `BaseTlv` serialization, and `DeserializeStrictAsync` for update_add_htlc. Done since: every message extension uses `DeserializeStrictAsync` (NL-001).
5. **BOLT 2 normal operation (the largest block).** In progress: BOLT2 plan N0-N4 are done (ordering/lock, HTLC tx builders and vectors, HTLC signing, pure commitment engine + invariant simulator); next are N5 (persist the engine state) and N6 (handlers). Original list: add ChannelModel mutators and HTLC commitment-dance states. Add handlers plus `ChannelManager` cases for update_add_htlc, update_fulfill_htlc, update_fail_htlc, update_fail_malformed_htlc, commitment_signed, revoke_and_ack and update_fee. Add HTLC signing to `ILightningSigner`. Add HTLC-success and HTLC-timeout tx builders with the Appendix C HTLC vectors and Appendix F anchors. Add per-channel message serialization (locking). (The onion is already mandatory in `UpdateAddHtlcPayloadSerializer`.)
6. **channel_reestablish and persistence of the shachain / commitment state.** A routing node must survive restarts. The shachain is persisted (NL-136); commitment state is BOLT2 N5, reestablish N7.
7. **Crypto primitives for Sphinx.** Done (M1): `StreamChaCha20IetfXor` in all 3 backends + `ChaCha20Stream`, public `HmacSha256`, `ISecp256K1Math` extracted from `KeyDerivationService`; `ISphinxService.PeelAsLocalNode` uses `ISecureKeyManager.GetNodeKeyPair()`.
8. **BOLT 4 core.** Done (M2): OnionPacket, hop payload TLVs/serializer/validator, Sphinx construct/peel (byte-exact against `onion-test.json`), failure codes, replay cache; re-implemented from the spec, not ported. Done since (M3): failure message model, error-packet create/wrap/decrypt against `onion-error-test.json`, malformed conversion. Remaining: attribution_data (M3b).
9. **Forwarding switch.** Peel after the HTLC is irrevocably committed. Resolve SCID or alias to an outgoing channel, check fee and CLTV policy, then forward, or fail with a wrapped error. Convert update_fail_malformed into update_fail_htlc. Persist the per-HTLC shared secret, the incoming↔outgoing circuit and a replay set.
10. **Final-hop receive and sending.** Invoice store (preimage, payment_secret), with `NLightning.Bolt11` referenced from Application. Final-hop payment_data and basic_mpp checks. PayInvoice/CreateInvoice IPC commands. Routing over direct channels plus invoice route hints.
11. **Cooperative close and BOLT 5 penalty and sweeps.** Needed for safe operation with real funds.
12. **BOLT 7 gossip and graph.** channel_update inside failure messages, and pathfinding for multi-hop sends. Route blinding, attribution data and onion messages come after this.
