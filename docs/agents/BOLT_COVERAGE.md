# BOLT Coverage Matrix

What this file is: a BOLT-by-BOLT compliance map of NLightning (C# / .NET 10), written for agents working in this repo.
Snapshot date: 2026-09-25. It was compiled from per-area research maps. Every claim flagged "verified" was checked against the code while writing this file.
Paths are relative to the repo root.

Status legend:
- **complete**: spec behaviour is implemented and tested.
- **partial**: implemented with known gaps or bugs.
- **stub**: only names, enums, empty classes or commented-out code exist.
- **missing**: nothing exists.

A note on layers. A BOLT 2 message can be complete at the wire layer (Domain message + serializer) and still be missing at the behaviour layer (no Application handler). The matrix lists those two layers as separate rows.

---

## 0. Cross-cutting facts to know first

1. **Channel dispatch only handles channel opening.** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:88-133` has cases for OpenChannel, AcceptChannel, FundingCreated, ChannelReady and FundingSigned only (verified). Every other `IChannelMessage` falls to `default` and throws `ChannelErrorException("Unknown message type")`. `PeerManager` then disconnects the peer. So today an incoming `update_add_htlc`, `commitment_signed`, `shutdown` or `channel_reestablish` **disconnects the peer**.
2. **Some peer-level messages are silently dropped.** `src/NLightning.Infrastructure/Node/Services/PeerService.cs:100-160` dispatches only `IChannelMessage`, `ErrorMessage` and `WarningMessage` after init. `StfuMessage` derives from `BaseMessage`, not `BaseChannelMessage` (`src/NLightning.Domain/Protocol/Messages/StfuMessage.cs:15`, verified), so it is dropped. Gossip and onion_message would be dropped too.
3. **Unknown even message types throw on receive.** `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs` applies the BOLT 1 "it's OK to be odd" rule: an unknown odd type returns null, an unknown even type throws `InvalidMessageException`. The BOLT 7 types 256 and 258 are enum-only (`src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`), so they are unknown even types and **throw on receipt**.
4. **47 tests never run in CI.** `test/NLightning.Application.Tests` and `test/NLightning.Daemon.Tests` do not reference `xunit.runner.visualstudio`, so `dotnet test` discovers 0 tests in them. Run them directly: `dotnet run --project <proj>`. They pass locally (24 and 23).
5. **No onion/Sphinx code exists in the repo or its branches.** A grep for `onion`/`sphinx` finds only raw byte fields, feature bits and factory parameters. The older onion code the user mentioned is not in this checkout.

---

## BOLT 1: Base protocol

| Feature / message | Status | Implementing files | Tests | Notes |
|---|---|---|---|---|
| Message framing (u16 type) and the unknown odd/even rule | complete | `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs` | `test/NLightning.Infrastructure.Tests/Protocol/Services/MessageServiceTests.cs` | `DeserializeMessageAsync<T>` ignores the wire type and uses T's serializer. On a deserialize failure, `MessageService` sends an `error` (`src/NLightning.Infrastructure/Protocol/Services/MessageService.cs`). |
| `init` (16) plus `networks` TLV (1) | partial | `src/NLightning.Domain/Protocol/Messages/InitMessage.cs`, `.../Tlv/NetworksTLV.cs`, `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs`, `.../PeerService.cs` | `test/NLightning.Infrastructure.Serialization.Tests/Messages/InitMessageTests.cs` | Stricter than the spec: a peer is rejected if **any** of its chains is unknown to us (spec: only reject when no chain is shared). Init failures disconnect without sending error/warning. There are no unit tests for PeerService/PeerCommunicationService (`test/NLightning.Infrastructure.Tests/Node/Models/PeerTests.cs` is fully commented out). |
| `init` `remote_addr` TLV (3) | partial (buggy) | `src/NLightning.Domain/Protocol/Tlv/RemoteAddressTlv.cs`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs` | none | Our side never sends it (commented out at `src/NLightning.Domain/Node/Options/FeatureOptions.cs:241`). `TlvStreamSerializer`'s switch has no `RemoteAddressTlv` case (verified), so serializing it throws. The Tor v3 decode and the DNS (type 5) encode are wrong. |
| `error` (17) / `warning` (1) | complete | `src/NLightning.Domain/Protocol/Messages/ErrorMessage.cs`, `WarningMessage.cs`, `.../Payloads/ErrorPayload.cs`, `src/NLightning.Domain/Exceptions/` | `ErrorMessageTests.cs`, `WarningMessageTests.cs`, `test/NLightning.Domain.Tests/Protocol/Payloads/ErrorPayloadTests.cs` | Warning reuses `ErrorPayload`. The exception hierarchy decides the outcome: `ChannelErrorException` means disconnect, `ChannelWarningException` means send a warning. |
| `ping` (18) / `pong` (19) | partial | `src/NLightning.Infrastructure/Protocol/Services/PingPongService.cs`, `PeerCommunicationService.cs`, `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` | `PingMessageTests.cs`, `PongMessageTests.cs` | We always answer, even when `num_pong_bytes >= 65532`, where the spec says do not respond. There is no ping rate limit. On pong timeout, `Task.Delay` ends as Canceled, so the disconnect branch may never fire (unverified at runtime). `PingPayloadSerializer` does not consume the `ignored` bytes. |
| BigSize | partial | `src/NLightning.Domain/Protocol/ValueObjects/BigSize.cs`, `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs` | `test/NLightning.Infrastructure.Serialization.Tests/ValueObjects/BigSizeTypeSerializerTests.cs` + `Vectors/BigSize.txt` | **Non-canonical encodings are accepted** (verified: no minimum-value checks). The three non-canonical spec vectors are commented out in `BigSize.txt`. Fixing this is a BOLT 4 prerequisite. |
| TLV record / stream | partial | `src/NLightning.Domain/Protocol/Tlv/BaseTlv.cs`, `.../Models/TLVStream.cs`, `src/NLightning.Infrastructure.Serialization/Tlv/TlvSerializer.cs`, `TlvStreamSerializer.cs` | `TlvStreamSerializerTests.cs`, `TlvSerializerTests.cs` (mostly commented out), `test/NLightning.Domain.Tests/Protocol/Models/TlvStreamTests.cs` | Duplicates are rejected. Out-of-order records are **not** rejected, because `SortedDictionary` silently re-sorts them. Unknown **even** TLV types are not rejected. The serialize path is a closed type switch, so a raw `BaseTlv` or custom record cannot be written. |
| Truncated ints (tu16/tu32/tu64) | missing | (closest: `src/NLightning.Infrastructure/Converters/EndianBitConverter.cs` trim/pad flags, not spec-compliant) | none | Needed by BOLT 4 hop payloads. |
| `peer_storage` / `peer_storage_retrieval` | missing | — | — | The feature bit `Feature.OptionProvideStorage` (43) exists in `src/NLightning.Domain/Enums/Feature.cs`. No message types. |

## BOLT 2: Peer protocol for channel management

### Wire layer (Domain message + payload + serializer)

| Messages | Status | Files | Tests | Notes |
|---|---|---|---|---|
| open_channel (32), accept_channel (33), funding_created (34), funding_signed (35), channel_ready (36) | complete | `src/NLightning.Domain/Protocol/Messages/{OpenChannel1,AcceptChannel1,FundingCreated,FundingSigned,ChannelReady}Message.cs`, serializers in `src/NLightning.Infrastructure.Serialization/{Payloads,Messages/Types}/` | `ChannelReadyMessageTests.cs` only | **No serializer tests for open_channel, accept_channel, funding_created or funding_signed** (verified directory listing). The open_channel deserializer requires the channel_type TLV. |
| open_channel2 (64), accept_channel2 (65), tx_add_input … tx_abort (66-74) | complete (wire) | `src/NLightning.Domain/Protocol/Messages/{OpenChannel2,AcceptChannel2,Tx*}Message.cs` | `OpenChannel2MessageTests.cs`, `AcceptChannel2MessageTypeSerializerTests.cs`, `Tx*MessageTests.cs` | See the behaviour table below. |
| shutdown (38), closing_signed (39) + fee_range | complete (wire) | `ShutdownMessage.cs`, `ClosingSignedMessage.cs`, `.../Tlv/FeeRangeTlv.cs` | `ShutdownMessageTests.cs`, `ClosingSignedMessageTests.cs` | |
| closing_complete (40) / closing_sig (41) (option_simple_close) | missing | — | — | `Feature.OptionSimpleClose` (61) exists in the enum only. |
| update_add_htlc (128) | partial | `src/NLightning.Domain/Protocol/Payloads/UpdateAddHtlcPayload.cs`, `.../Messages/UpdateAddHtlcMessage.cs`, `src/NLightning.Infrastructure.Serialization/Payloads/UpdateAddHtlcPayloadSerializer.cs`, `.../Messages/Types/UpdateAddHtlcMessageSerializer.cs` | `UpdateAddHtlcMessageTests.cs` | The onion is an **optional** raw `ReadOnlyMemory<byte>?`. It is read only if `stream.Position + 1366 <= stream.Length` (`UpdateAddHtlcPayloadSerializer.cs:67`, verified), so a truncated message silently yields no onion. The spec makes the onion mandatory. The blinded path_key TLV (0) is supported, but `UpdateAddHtlcMessageSerializer.cs:68` looks it up with `TlvConstants.UpfrontShutdownScript` instead of `TlvConstants.BlindedPath` (it only works because both are 0; `src/NLightning.Domain/Protocol/Constants/TlvConstants.cs:44,85`). |
| update_fulfill_htlc (130), update_fail_htlc (131), update_fail_malformed_htlc (135), commitment_signed (132), revoke_and_ack (133), update_fee (134) | complete (wire) | `src/NLightning.Domain/Protocol/Messages/Update*Message.cs`, `CommitmentSignedMessage.cs`, `RevokeAndAckMessage.cs` | matching `*MessageTests.cs` | No `attribution_data` (TLV 1) or `fulfillment_payload` (TLV 3). `failure_code` is a raw ushort with no BADONION check. |
| channel_reestablish (136) + next_funding | complete (wire) | `ChannelReestablishMessage.cs`, `.../Tlv/NextFundingTlv.cs` | `TxChannelReestablishMessageTests.cs` | |
| stfu (2) | partial | `StfuMessage.cs` | `StfuMessageTests.cs` | Not a `BaseChannelMessage`, so `PeerService` drops it. |
| splicing (splice_init/ack/locked), start_batch | missing | — | — | |

### Behaviour layer (Application handlers / state machine)

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| v1 open, non-initiator (open_channel → accept_channel → funding_created → funding_signed) | complete | `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs`, `FundingCreatedMessageHandler.cs`, `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs`, `.../Validators/ChannelOpenValidator.cs` | `test/NLightning.Application.Tests/Channels/Handlers/*` (not run in CI), Docker `ChannelOpeningFlowTests.cs` | Validator bugs: the push_amount check is off by 1000× (`ChannelOpenValidator.cs:104`), and the anchor/no-anchor weight selection is inverted (`ChannelOpenValidator.cs:108-110`, `ChannelFactory.cs:183-185`). The local upfront_shutdown_script is never generated (`ChannelFactory.cs:101,235`). |
| v1 open, initiator | complete | `AcceptChannel1MessageHandler.cs`, `FundingSignedMessageHandler.cs`, `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs` | Daemon handler tests; Docker e2e against LND | The error-cleanup branch looks inverted (`AcceptChannel1MessageHandler.cs:226-242`). upfront_shutdown_script is required whenever the feature is negotiated **or** a channel_type TLV is present (`:118-120`), which in practice is almost always. The channel_type TLV alone should not trigger the requirement. The channel is not persisted before funding_signed arrives. |
| channel_ready / funding depth | partial | `ChannelReadyMessageHandler.cs`, `FundingConfirmedMessageHandler.cs`, `ChannelManager.cs` | none | `ForgetStaleChannels` (`ChannelManager.cs:~198`) selects on `FundingCreatedAtBlockHeight <= height-2016` with no state filter. That field defaults to 0 until confirmation (`ChannelModel.cs:20`, verified), so on any chain taller than 2016 blocks, unconfirmed channels are marked Stale. `ConfirmUnconfirmedChannels` re-sends channel_ready and increments the commitment number on every block while in a ReadyFor* state. The SCID is derived from a wrong tx index (see BOLT 7). |
| v2 / dual-funding / interactive-tx | stub | `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs`, `src/NLightning.Infrastructure/Protocol/Validators/Tx*Validator.cs` | none | Not in DI. The serial-id parity check looks inverted. `TxAddInputValidator.Validate` is `async void`. The factory rejects peers that require DualFund. |
| HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee) | missing (behaviour) | model only: `src/NLightning.Domain/Channels/Models/ChannelModel.cs`, `.../ValueObjects/Htlc.cs`, `.../Enums/HtlcState.cs` | none | No handlers. `ChannelModel` HTLC collections, balances, next-ids and revocation numbers are get-only with no mutators. `HtlcState` has 4 values and no commitment-dance stages. `ILightningSigner` has no HTLC-signature API. |
| Close (shutdown / closing_signed) | missing (behaviour) | `ChannelState.Closing/Closed` enum only | — | Nothing moves a channel into Closing. |
| channel_reestablish / data_loss_protect | missing (behaviour) | TODO at `ChannelManager.cs:62` | — | |
| Quiescence | missing (behaviour) | — | — | |

## BOLT 3: Bitcoin transaction and script formats

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Funding output (2-of-2 P2WSH) | complete | `src/NLightning.Infrastructure.Bitcoin/Outputs/FundingOutput.cs`, `Builders/FundingOutputBuilder.cs` | `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs` | |
| Funding tx build + fee/change | partial | `src/NLightning.Domain/Bitcoin/Transactions/Factories/FundingTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs` | Appendix B test at `Bolt3IntegrationTests.cs:64` has a **commented-out body** (passes vacuously) | No change-dust check. Insufficient inputs throw `ArithmeticException`. |
| Commitment tx (to_local / to_remote / HTLC outputs, obscured number, BIP69+CLTV ordering) | complete (non-anchor) | `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/CommitmentTransactionBuilder.cs`, `Comparers/TransactionOutputComparer.cs`, `src/NLightning.Domain/Protocol/Models/CommitmentNumber.cs` | Appendix C vectors in `Bolt3IntegrationTests.cs` (about 17), `CommitmentNumberTests.cs`, `CommitmentTransactionBuilderTests.cs` | Suspect items: every HTLC is subtracted from to_local, and the to_remote dust check uses the remote dust limit. |
| option_anchors | partial | `src/NLightning.Domain/Bitcoin/Transactions/Outputs/AnchorOutputInfo.cs`, `src/NLightning.Infrastructure.Bitcoin/Outputs/ToAnchorOutput.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs` | none. `test/NLightning.Tests.Utils/Vectors/Bolt3AppendixFVectors.cs` exists but is unused | The base weight works out to 1116 against the spec's 1124. Only one 330-sat anchor is deducted instead of two: `CommitmentTransactionModelFactory.cs:173` calls `AdjustForAnchorOutputs` (`:243-255`), which subtracts `anchorAmount` once. |
| Output scripts (to_local, to_remote, anchor, offered/received HTLC) | complete | `src/NLightning.Infrastructure.Bitcoin/Outputs/*.cs` | Output tests (some files commented out) + Appendix C | |
| HTLC-success / HTLC-timeout second-stage txs | stub | `src/NLightning.Infrastructure.Bitcoin/Transactions/Htlc{Success,Timeout}Transaction.cs` (commented out), `Outputs/HtlcResolutionOutput.cs` | none | `HtlcResolutionOutput` swaps the revocation and delayed keys. No Appendix C HTLC-tx vector tests. |
| Closing tx | stub | `.../Transactions/ClosingTransaction.cs` (commented out) | — | |
| Key derivation (localpubkey / revocation) | complete | `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs`, `CommitmentKeyDerivationService.cs` | Appendix E vectors (`Bolt3IntegrationTests.cs:~1098-1154`) | |
| Per-commitment secret gen + shachain storage | complete (partial API) | `KeyDerivationService.cs`, `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs` | Appendix D vectors (`Bolt3IntegrationTests.cs:~677-1065`) | `GetBasepointPrivateKey` and `LoadFromIndex` throw `NotImplementedException`. The shachain is not persisted to the DB. On reload, `ChannelDbRepository.cs:~237` rebuilds `CommitmentNumber` with the basepoints in (local, remote) order regardless of the initiator, and uses the local funding key for both parties (`:230-231`). |
| Dust limits | complete | `src/NLightning.Infrastructure.Bitcoin/Services/DustService.cs` | — | Not registered in DI. |

## BOLT 4: Onion routing

**Overall: missing.** No code exists beyond raw wire fields. The spec requirements, design and milestone plan are in [`docs/agents/ONION_ROUTING_PLAN.md`](ONION_ROUTING_PLAN.md). The table below lists repo hooks and the prerequisites that do exist.

| Feature | Status | Files (hooks) | Tests | Notes |
|---|---|---|---|---|
| onion_packet (1366 B: version, pubkey, 1300 hop_payloads, hmac) | missing | raw bytes: `UpdateAddHtlcPayload.OnionRoutingPacket` | `UpdateAddHtlcMessageTests.cs` (opaque blob) | No value object. The literal 1366 is hard-coded in the serializer. |
| Sphinx construct / peel, filler, key-gen (rho, mu, um, pad, ammag) | missing | — | — | — |
| ECDH shared secret = SHA256(compressed(k·P)) | complete (reusable) | `src/NLightning.Infrastructure/Crypto/Interfaces/IEcdh.cs`, `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs` (DI in `Infrastructure.Bitcoin/DependencyInjection.cs:32`) | `EcdhTests.cs` (key length only) | Same construction as BOLT 8. |
| Ephemeral key blinding (point / scalar tweak-mul) | partial | private `MultiplyPubKey` / `MultiplyPrivateKey` at `KeyDerivationService.cs:167,209` (verified private) | — | Needs to be extracted into a public service. |
| HMAC-SHA256 (short keys) | missing | private `Hkdf.HmacHash` in `src/NLightning.Infrastructure/Crypto/Functions/Hkdf.cs` asserts a 32-byte key | — | — |
| Raw ChaCha20 keystream (zero 96-bit nonce, counter 0) | missing | `ICryptoProvider` has AEAD only. `Providers/Native/Ciphers/ChaCha20.cs` is just a QuarterRound helper | — | Must be implemented in all 3 providers (libsodium, Native/BouncyCastle `ChaCha7539Engine`, JS sumo). |
| Hop payload TLVs (2, 4, 6, 8, 10, 12, 16, 18) | missing | — | — | Blocked on tu64/tu32 support, strict TLV rules and canonical BigSize. Keep the type numbers out of the flat `TlvConstants`, because its values collide by design. |
| Failure messages / codes / error packet (um, ammag) | missing | raw: `UpdateFailHtlcPayload.Reason`, `UpdateFailMalformedHtlcPayload.FailureCode` (ushort) | — | No `FailureCode` enum. |
| update_fail_malformed → update_fail_htlc conversion | missing | — | — | — |
| Route blinding | partial (wire only) | `src/NLightning.Domain/Protocol/Tlv/BlindedPathTlv.cs`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/BlindedPathTlvConverter.cs` | `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/BlindedPathTlvConverterTests.cs` | `MessageFactory.CreateUpdateAddHtlcMessage` cannot attach it. The feature bit `OptionRouteBlinding=25` is **advertised as Optional by default** despite having no implementation. |
| Basic MPP (final-hop HTLC sets) | missing | — | — | — |
| attribution_data / fulfillment_payload | missing | Feature `OptionAttributionData=37` (default Optional, also advertised) | — | — |
| onion_message (513) | missing | Feature `OptionOnionMessages=39` (default No) | — | `IPeerService.SendMessageAsync` accepts only `IChannelMessage`, so a new send path is needed. |
| Test vectors (onion-test.json, onion-error-test.json, route-blinding-test.json, …) | missing | — | — | Not committed. Fetch them from lightning/bolts `bolt04/*.json` (canonical list in `ONION_ROUTING_PLAN.md` §1.8) and commit them under `test/NLightning.Integration.Tests/BOLT4/Vectors/`, adding a `<Content Include="BOLT4/Vectors/*.json" CopyToOutputDirectory="PreserveNewest"/>` entry to the Integration.Tests csproj (`ONION_ROUTING_PLAN.md` §5 M2 "Test location" and §8). |

## BOLT 5: On-chain transaction handling

| Feature | Status | Files | Notes |
|---|---|---|---|
| Funding confirmation / watched txs | partial | `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Models/WatchedTransactionModel.cs` | ZMQ rawblock only. No reorg handling. A block whose processing throws is never removed from the queue. |
| Penalty / justice tx, revocation watch | stub | `src/NLightning.Domain/Bitcoin/Transactions/Models/PenaltyTransactionModel.cs` (empty), `src/NLightning.Domain/Bitcoin/Interfaces/IRevocationWatchDbRepository.cs` (empty), `src/NLightning.Infrastructure.Persistence/Entities/Bitcoin/RevocationWatchEntity.cs` (not mapped), `BlockchainMonitorService.cs:189-198,400-411` (commented out) | |
| Unilateral close sweeps, HTLC on-chain resolution | missing | — | |

## BOLT 7: P2P node and channel discovery

| Feature | Status | Files | Notes |
|---|---|---|---|
| channel_announcement (256), node_announcement (257), channel_update (258), announcement_signatures (259) | stub | `MessageTypes.cs` enum only | 256 and 258 **throw** on receive (unknown even type). Yet `FeatureOptions` advertises GossipQueries and GossipQueriesEx as Optional. |
| Gossip queries (261-265) | missing | — | Receiving 262 or 264 throws. |
| short_channel_id encoding | partial (buggy) | `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs` | The `ulong` constructor masks tx index with `0xFFFF` and output with `0xFF` (verified lines 55-57). They should be `0xFFFFFF` and `0xFFFF`. `BlockchainMonitorService.CheckBlockForWatchedTransactions` (~473-499) stores the index among *watched* txs, not the index in the block, so SCIDs are wrong: the counter is only incremented in `finally` (`:496-499`), after the `continue` for unwatched txs (`:478-479`). It is also declared `ushort` (`:473`), but the BOLT 7 tx index is 24-bit. The fix is to count every tx in the block (increment before the `continue`) and widen the index to `uint` end to end, including `WatchedTransactionModel.SetHeightAndIndex(uint, ushort)` (`src/NLightning.Domain/Bitcoin/Transactions/Models/WatchedTransactionModel.cs:22`), `WatchedTransactionDbRepository.cs:67` and the persisted `TransactionIndex` column. |
| scid_alias | partial | `ChannelModel.LocalAliases/RemoteAlias`, `FundingConfirmedMessageHandler.cs`, `ShortChannelIdTlv` | Random aliases with no uniqueness check. |
| Graph / routing table storage | missing | TODO "update routing tables" at `ChannelReadyMessageHandler.cs:~106`, `FundingConfirmedMessageHandler.cs:~79` | |
| node address descriptors | partial | `RemoteAddressTlvConverter.cs` | See BOLT 1 remote_addr. |

## BOLT 8: Encrypted and authenticated transport

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Noise_XK handshake (acts 1-3) | complete | `src/NLightning.Infrastructure/Transport/Handshake/States/{HandshakeState,SymmetricState,CipherState}.cs`, `src/NLightning.Infrastructure/Transport/Services/HandshakeService.cs` | `test/NLightning.Integration.Tests/BOLT8/{Initiator,Responder,EndToEnt}IntegrationTests.cs` (spec vectors) | |
| Framing + key rotation | complete | `src/NLightning.Infrastructure/Transport/Encryption/Transport.cs`, `CipherState.cs` | `BOLT8/MessageIntegrationTests.cs` (msgs 0/1/500/501/1000/1001) | Maximum body is 65519 bytes, not 65535. |
| TCP read loop / concurrency | partial | `src/NLightning.Infrastructure/Transport/Services/TransportService.cs`, `TcpService.cs` | `TransportServiceTests.cs` | Headers and bodies are read with `ReadAsync`, not `ReadExactlyAsync` (~267/291), so a short TCP read kills the connection. Encryption happens **before** the write semaphore (~201-209), so concurrent sends can reorder nonces. IPv6 listen addresses are unsupported. Live interop with LND is covered by Docker `AbcNetworkTests.cs`. |
| ECDH | complete | `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs` | Covered by the BOLT 8 vectors | |

## BOLT 9: Assigned feature flags

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Feature enum (odd-bit values) | complete | `src/NLightning.Domain/Enums/Feature.cs` | — | |
| FeatureSet bitfield, negotiation, serialization | partial | `src/NLightning.Domain/Node/FeatureSet.cs`, `.../Options/FeatureOptions.cs`, `src/NLightning.Infrastructure.Serialization/Node/FeatureSetSerializer.cs` | `test/NLightning.Domain.Tests/Node/FeatureSetTests.cs`, `FeatureSetSerializerTests.cs` | `s_featureDependencies` holds only 2 entries. Missing examples include basic_mpp→payment_secret. Dependencies are checked only on the remote set. There is no per-context filtering. Features are advertised (default Optional in `FeatureOptions.cs`) that are not implemented: gossip_queries, gossip_queries_ex, option_dual_fund, route_blinding, attribution_data. `ChannelFactory.cs:52,147` rejects only a *Compulsory* DualFund from the peer, so advertising option_dual_fund as Optional invites v2 opens we cannot handle. |
| channel_type | complete | `src/NLightning.Domain/Protocol/Tlv/ChannelTypeTlv.cs` | `ChannelTypeTlvConverterTests.cs` | |

## BOLT 10: DNS bootstrap (for completeness)

stub. `src/NLightning.Infrastructure/Protocol/Services/DnsSeedClient.cs` is fully commented out, and so is `test/NLightning.Integration.Tests/BOLT10/DNSBootstrapTests.cs`.

## BOLT 11: Invoice protocol

| Feature | Status | Files | Tests | Notes |
|---|---|---|---|---|
| Bech32 envelope, HRP / network, amount multipliers, timestamp, signature sign/verify/recover | complete | `src/NLightning.Bolt11/Models/Invoice.cs`, `src/NLightning.Infrastructure.Bitcoin/Encoders/Bech32Encoder.cs` | `test/NLightning.Bolt11.Tests/`, `test/NLightning.Integration.Tests/BOLT11/` (+ `Vectors/ValidInvoices.txt`), Blazor smoke tests | |
| p, h, s, n, d, x, m fields | complete | `src/NLightning.Bolt11/Models/TaggedFields/*` | per-field tests | |
| c (min_final_cltv_expiry) | partial | `MinFinalCltvExpiryTaggedField.cs` | | Returns null when absent. The spec default is 18. |
| r route hints | partial | `RoutingInfoTaggedField.cs`, `src/NLightning.Domain/Models/RoutingInfo.cs` | | Only the first `r` field survives, even though the spec allows several. Fees and cltv_delta are signed types. |
| f fallback | partial | `FallbackAddressTaggedField.cs` | | No taproot (v1). |
| 9 features / reject unknown even bits | partial | `FeaturesTaggedField.cs`, TODO at `Invoice.cs:553` | | |
| Validation on encode | partial | `src/NLightning.Bolt11/Services/InvoiceValidationService.cs` | | Runs on decode only. |
| Integration with the node | missing | — | | No `src/` project references `NLightning.Bolt11`. No invoice store and no pay/invoice IPC command (`src/NLightning.Domain/Client/Enums/ClientCommand.cs`). |

## bLIPs / extensions

None implemented. No bLIP-specific code was found. Keysend and custom records (≥65536) cannot be serialized either, because of the closed `TlvStreamSerializer` switch. BOLT 12 (offers) is missing.

---

## Persistence gaps that affect BOLT compliance

These are mostly in `src/NLightning.Infrastructure.Repositories/Database/Channel/`. The `RemoteNodeId` column bug is in the Persistence project.

- `ChannelDbRepository.MapEntityToDomain` compares `byte` with an enum using `.Equals` (~lines 201-223). Offered and fulfilled HTLCs therefore never reload, and every HTLC is classed as remote. `HtlcDbRepository.cs:63,70` has the same pattern.
- `HtlcDbRepository` never writes `Signature`.
- SQL Server maps `RemoteNodeId` as `varbinary(32)`, but pubkeys are 33 bytes. `src/NLightning.Infrastructure.Persistence/EntityConfiguration/Channel/ChannelEntityConfiguration.cs:83` uses `TransactionConstants.TxIdLength` for it.
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

The `--filter` excludes the Docker e2e tests, which need Docker. The last two commands are needed until step 1 lands (see cross-cutting fact 4). For crypto-provider work (steps 7 and 8), also build and test with `-c Release.Native` (`.github/workflows/dotnet.native.yml`) and run the Release.Wasm Blazor tests (`.github/workflows/dotnet.wasm.yml`).

1. **Test and CI hygiene (small).** Add `xunit.runner.visualstudio` to `test/NLightning.Application.Tests` and `test/NLightning.Daemon.Tests`. Add serializer tests for open_channel, accept_channel, funding_created and funding_signed. Re-enable the Appendix B funding test body.
2. **Stop killing peers on unknown traffic.** Order matters here.
   - First, make BOLT 7 types 256-259 (and ideally 261-265) known types. They cannot simply be "ignored": `MessageSerializer.DeserializeMessageAsync` (`src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs:57-75`) throws on any unregistered even type, as BOLT 1 requires. Each needs a message class, a payload serializer, and registration in both dictionaries of `src/NLightning.Infrastructure.Serialization/Factories/MessageTypeSerializerFactory.cs` (`RegisterSerializers` L45+, `RegisterTypeDictionary` L113+) and of `PayloadSerializerFactory.cs` (see the Stfu entries at L64/L105). Then route them in `PeerService` to a no-op or logging handler. See the message recipe in `docs/agents/REPO_MAP.md` section 7 and the Recipes section of `CLAUDE.md`.
   - Only after that, change the `FeatureOptions` defaults (`src/NLightning.Domain/Node/Options/FeatureOptions.cs`). Stop advertising option_dual_fund, route_blinding and attribution_data until each is implemented. Keep gossip_queries advertised (or add a `gossip_timestamp_filter` reply): without it, BOLT 7 peers fall back to relaying gossip unsolicited, so dropping the flag before 256/258 are handled makes disconnects worse, not better.
   - Route `stfu`. Use `ReadExactlyAsync` in `TransportService` and hold the write lock across encrypt and write.
3. **Fix correctness bugs on the routing path.**
   - ShortChannelId `ulong` masks.
   - `BlockchainMonitorService` tx index used for the SCID.
   - `ForgetStaleChannels` state filter and the repeated re-confirm loop.
   - `ChannelDbRepository` HTLC enum comparisons, funding pubkey, and CommitmentNumber order on reload.
   - HTLC signature persistence.
   - Validator push_amount and anchor-weight bugs.
4. **BOLT 1 strictness.** Canonical BigSize decoding: re-enable the vectors. Add tu16/tu32/tu64 types. Make TLV stream decoding strict: increasing types, reject unknown even types. Open up the `TlvStreamSerializer` switch to raw `BaseTlv` and add `RemoteAddressTlv`.
5. **BOLT 2 normal operation (the largest block).** Add ChannelModel mutators and HTLC commitment-dance states. Add handlers plus `ChannelManager` cases for update_add_htlc, update_fulfill_htlc, update_fail_htlc, update_fail_malformed_htlc, commitment_signed, revoke_and_ack and update_fee. Add HTLC signing to `ILightningSigner`. Add HTLC-success and HTLC-timeout tx builders with the Appendix C HTLC vectors and Appendix F anchors. Add per-channel message serialization (locking). Make the onion mandatory in `UpdateAddHtlcPayloadSerializer`.
6. **channel_reestablish and persistence of the shachain / commitment state.** A routing node must survive restarts.
7. **Crypto primitives for Sphinx.** Raw ChaCha20 stream in all 3 `ICryptoProvider` backends. Public HMAC-SHA256 that accepts any key length. Public secp256k1 tweak-mul service extracted from `KeyDerivationService`. An ECDH-with-node-key hook via `ISecureKeyManager.GetNodeKeyPair()`.
8. **BOLT 4 core.** OnionPacket value object, hop payload TLVs, a pure deterministic Sphinx construct/peel implementation, and failure codes plus error-packet wrap/unwrap. Validate each piece byte-for-byte against `bolt04/onion-test.json` and `onion-error-test.json`. Port the user's older onion code into this layer.
9. **Forwarding switch.** Peel after the HTLC is irrevocably committed. Resolve SCID or alias to an outgoing channel, check fee and CLTV policy, then forward, or fail with a wrapped error. Convert update_fail_malformed into update_fail_htlc. Persist the per-HTLC shared secret, the incoming↔outgoing circuit and a replay set.
10. **Final-hop receive and sending.** Invoice store (preimage, payment_secret), with `NLightning.Bolt11` referenced from Application. Final-hop payment_data and basic_mpp checks. PayInvoice/CreateInvoice IPC commands. Routing over direct channels plus invoice route hints.
11. **Cooperative close and BOLT 5 penalty and sweeps.** Needed for safe operation with real funds.
12. **BOLT 7 gossip and graph.** channel_update inside failure messages, and pathfinding for multi-hop sends. Route blinding, attribution data and onion messages come after this.
