# NLightning.Domain — agent guide

## Purpose
This is the pure domain layer: it holds value objects, models, wire-message models, and the interfaces that the outer layers implement. It uses the BCL only. There are **no** PackageReference or ProjectReference entries in `NLightning.Domain.csproj`. It targets net10.0 via `src/Directory.Build.props`. Serialization, crypto, persistence and DI all live in the outer layers.

## Layout (folder = namespace `NLightning.Domain.<Folder>...`)
- `Protocol/`: the wire model. It contains `Constants/MessageTypes.cs` (the ushort enum), `TlvConstants.cs`, `Messages/` (one sealed class per message), `Payloads/`, `Tlv/` (BaseTlv plus typed TLVs), `Models/TLVStream.cs` (the `TlvStream` class), `Models/CommitmentNumber.cs`, `ValueObjects/` (BigSize, ChainHash, BitcoinNetwork) and `Interfaces/`. `Interfaces/` mixes the message contracts with key, secret, dust and transport services.
- `Channels/`: `Models/ChannelModel.cs` (aggregate), `Enums/ChannelState.cs`, `ValueObjects/` (ChannelId, ShortChannelId, Htlc, ChannelConfig, CommitmentKeys), `Factories/ChannelFactory.cs`, `Validators/ChannelOpenValidator.cs`, and the repository/manager interfaces.
- `Bitcoin/`: value objects (TxId, BitcoinScript, Witness), the `Transactions/` models, the `*ModelFactory` classes and the `*OutputInfo` types, plus ports (ILightningSigner, IFeeService, the Db/Memory repositories).
- `Crypto/`: CompactPubKey, PrivKey, Hash, Secret, CompactSignature, `CryptoConstants`, `ISha256` and `Interfaces/ISecp256K1Math` (EC tweak-mul/add port, implemented in Infrastructure.Bitcoin).
- `Protocol/Onion/`: BOLT 4 onion types (see the Onion routing section below).
- `Node/`: `FeatureSet.cs` (BOLT 9), `Options/FeatureOptions.cs` and `NodeOptions.cs`, and the peer interfaces.
- `Money/LightningMoney.cs`: holds the amount in **msat**. `Enums/Feature.cs`: feature bits, each stored as its ODD bit number.
- `Serialization/Interfaces`, `Transport/` (`ITransport` is internal), `Persistence/Interfaces/IUnitOfWork.cs`, `Exceptions/`, `Utils/` (BitReader/BitWriter), `Client/` (IPC DTOs and the `ClientCommand` enum), `Models/RoutingInfo*` (BOLT 11 hints).

## Dependency rules
- Do NOT reference NBitcoin, libsodium, EF Core, Microsoft.Extensions.* or any other src project. Keep the code library-free.
- Declare interfaces here and implement them outward: Infrastructure, Infrastructure.Bitcoin, Infrastructure.Serialization, Application.
- Internals are exposed through `AssemblyInfo.cs`, which lists Application, Infrastructure(.Blazor/.Bitcoin/.Serialization), Infrastructure.Serialization.Tests, Infrastructure.Tests, Tests.Utils and `DynamicProxyGenAssembly2` (Moq proxies). **`NLightning.Domain.Tests` is not in that list**, so Domain tests can only use the public API.
- Domain has no DI of its own. Registration happens in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs` and in each layer's `DependencyInjection.cs`.

## Conventions
- Namespaces are file-scoped. The prevailing style puts project `using` lines *after* the namespace line, written relative to the enclosing namespace (for example `using Money;` inside `NLightning.Domain.Channels.Models`). System usings go above the namespace line. Some files (e.g. `Protocol/Tlv/RemoteAddressTlv.cs`, `Bitcoin/Transactions/**`) instead use fully qualified `using NLightning.Domain...` above the namespace.
- Byte-backed value objects are mostly readonly structs or record structs over `byte[]` (`CompactSignature` is a `record` class). They validate length in the constructor and provide implicit conversions to and from byte[]/Span/Memory. `Hash`, `Secret` and `TxId` use `SequenceEqual` for Equals and `GetByteArrayHashCode()` (`Utils/Extensions/ByteArrayExtensions.cs`) for hashing; `ShortChannelId` hashes its parsed fields; `PrivKey`/`CompactSignature` rely on compiler-generated record equality.
- Models are classes with private setters and `UpdateX`/`AddX` mutators. `AddX` throws if the value is already set.
- Protocol failures throw `ChannelErrorException` (connection is usually closed) or `ChannelWarningException`. Only the exception's `PeerMessage` property is sent to the peer (see `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs`); the `Message` stays local.
- Build amounts with `LightningMoney.Satoshis(..)` or `MilliSatoshis(..)`. A raw `ulong`/`long` converts implicitly as **msat**.
- Mark Constants classes and exceptions `[ExcludeFromCodeCoverage]`. Name tests `Given_X_When_Y_Then_Z` (xUnit + Moq).
- `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` is a CI gate. The `.editorconfig` rules are at error severity, including `_field`/`s_field` naming.

## Adding a wire message (most common change)
1. Add the value to `Protocol/Constants/MessageTypes.cs`.
2. Add `Protocol/Payloads/XPayload.cs`, implementing `IChannelMessagePayload` (or `IMessagePayload`).
3. Add `Protocol/Messages/XMessage.cs` as a `sealed class : BaseChannelMessage` with `public new XPayload Payload => (XPayload)base.Payload;`. Build `Extension = new TlvStream(); Extension.Add(tlvs...)` only when a TLV is non-null.
4. For new TLVs: add a `TlvConstants` entry, add `Tlv/XTlv.cs : BaseTlv`, add a converter in `src/NLightning.Infrastructure/Protocol/Tlv/Converters`, and register it in `TlvConverterFactory` (`TlvStreamSerializer` looks converters up by runtime type).
5. Add the payload serializer and message-type serializer, registered in both dictionaries of both `PayloadSerializerFactory` and `MessageTypeSerializerFactory`.
6. Add `Create*` to `Protocol/Interfaces/IMessageFactory.cs` and implement it in `src/NLightning.Application/Protocol/Factories/MessageFactory.cs`.
7. Route the message: add a `case` to the `switch` in `src/NLightning.Application/Channels/Managers/ChannelManager.cs` `HandleChannelMessageAsync` (~line 88) plus an `IChannelMessageHandler<T>` (`src/NLightning.Application/Channels/Handlers/Interfaces/IChannelMessageHandler.cs`). Without these, the `default` branch throws `ChannelErrorException("Unknown message type")`. Add a round-trip test in `test/NLightning.Infrastructure.Serialization.Tests/Messages`.

## Tests
- Unit tests: `dotnet test test/NLightning.Domain.Tests/NLightning.Domain.Tests.csproj` (363 pass). Filter with `--filter "FullyQualifiedName~NLightning.Domain.Tests.ValueObjects.BigSizeTests"`.
- Wire-level coverage lives in `test/NLightning.Infrastructure.Serialization.Tests`. BOLT 3 vectors are in `test/NLightning.Integration.Tests/BOLT3` (run with `--filter 'FullyQualifiedName!~Docker'`).
- To match CI, build first: `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121`.

## Gotchas
- `TlvConstants` numbers are scoped per message: 0 and 1 are each reused several times. Use the semantically correct constant, and never mix TLVs from different namespaces in one `TlvStream`.
- For FeeRangeTlv, RemoteAddressTlv and FundingOutputContributionTlv, `Value` is not the wire bytes. Always go through the ITlvConverter.
- File name ≠ type name for `Models/TLVStream.cs`, `Tlv/NetworksTLV.cs` and `Tlv/RequireConfirmedInputsTLV.cs`.
- `ChannelModel.UpdateState` accepts only strictly increasing `ChannelState` values, and additionally forbids moving into `V2Opening` from any V1 state or leaving `V2Opening` downward. Number any new state with that in mind (current values: 0,1,2,3,10,20,21,22,30,40,50).
- `ShortChannelId(ulong)` masks the fields wrongly (tx index uses 0xFFFF instead of 0xFFFFFF, output uses 0xFF instead of 0xFFFF). Fix this before any code relies on u64 scids, such as onion hop payloads.
- `CommitmentNumber`'s constructor parameters are named (local, remote) payment basepoint, but BOLT 3 obscuring requires SHA256(opener || accepter), so callers must pass them in opener/accepter order. `Increment()` mutates the instance.
- `FeatureSet.DeserializeFromBytes` reverses the caller's array in place (on little-endian hosts). `new FeatureSet()` sets 5 compulsory bits: data_loss_protect, var_onion_optin, static_remote_key, payment_secret, channel_type.
- `LightningMoney` is a mutable reference type. `Bits()` returns the same value as `Cents()` (a known bug). The `-` operator throws on underflow.
- `default(Secret/Hash/TxId)` throws NullReferenceException in GetHashCode. `Hash`/`Secret`/`TxId` accept arrays longer than 32 bytes. `PrivKey`/`CompactSignature` compare by reference.
- The `protected internal BaseMessage(MessageTypes)` constructor installs `PlaceholderPayload`, whose `ChannelId` throws. `Stfu`, `Error` and `Warning` messages are not `IChannelMessage`.

## Onion routing (BOLT 4): M1+M2 done
Everything is under `Protocol/Onion/` (namespace `NLightning.Domain.Protocol.Onion.*`) plus `Exceptions/OnionException.cs`; tests in `test/NLightning.Domain.Tests/Protocol/Onion/`.
- `Constants/OnionConstants` (1366/1300/32 sizes, version 0, key labels `Rho`/`Mu`/`Um`/`Pad`/`Ammag`/`AmmagExt`/`BlindedNodeId`/`Fulfillment`, error-packet limits, `MaxHtlcCltv`), `OnionPayloadTlvTypes` (2..18, a separate namespace from `TlvConstants`), `EncryptedDataTlvTypes` (route-blinding namespace, M5; constants only).
- `ValueObjects/OnionPacket`: raw version(1) + pubkey(33) + hop_payloads(variable) + hmac(32). It validates only lengths, never the version byte or the pubkey, so `update_add_htlc` still parses and the peeler can return `invalid_onion_version`/`invalid_onion_key`. `default(OnionPacket)` members throw.
- `Models/OnionHop` (node id + raw payload TLV bytes **without** the bigsize length prefix), `ConstructedOnion` (packet + per-hop shared secrets), `PeeledOnion` (raw `Payload`, `SharedSecret`, `NextPacket` or null when final, `PathKeySharedSecret`), `HopPayload` (TLV stream with nullable typed accessors; unknown odd records kept).
- `Tlv/*Tlv` (AmtToForward, OutgoingCltvValue, OnionShortChannelId, PaymentData, EncryptedRecipientData, CurrentPathKey, PaymentMetadata, TotalAmountMsat) and `TruncatedIntEncoder`. Converters live in Infrastructure (`Protocol/Tlv/Converters/Onion/`).
- `Enums/FailureCode` (all BOLT 4 codes), `FailureCodeFlags`, `Extensions/FailureCodeExtensions` (`IsBadOnion()` etc.), `Enums/OnionPacketKind` (Payment: payload length >= 2; OnionMessage: >= 0).
- `Interfaces/ISphinxService` (construct/peel; implemented by `SphinxService` in Infrastructure.Bitcoin), `Interfaces/IOnionReplayCache` (implemented in Infrastructure).
- `Validators/HopPayloadValidator` (static, pure): BOLT 4 reader rules for a parsed payload given `isFinalHop` and `hasUpdateAddPathKey`. Always reports `invalid_onion_payload`; the `invalid_onion_blinding` remap for blinded routes is the caller's (M4/M5) job.
- `Factories/InvalidOnionPayloadFailureFactory`: encodes/decodes the `bigsize type || u16 offset` failure data and creates the `OnionException`.
- `Exceptions/OnionException : ErrorException`: `FailureCode`, `FailureData` (sha256_of_onion for BADONION codes, type/offset for `invalid_onion_payload`) and `SharedSecret` (set when the failure must go back encrypted in `update_fail_htlc`).
- Not yet: failure message model / error packets (M3), `FailureTlvTypes`, HtlcState values and `ChannelModel` HTLC mutators (M4), `onion_message` (513) in `MessageTypes` (M6).
- Raw wire hooks: `Payloads/UpdateAddHtlcPayload.OnionRoutingPacket` is a mandatory `ReadOnlyMemory<byte>` of exactly `OnionConstants.PacketLength` (the constructor enforces it); parse it with `OnionPacket`. `UpdateFailHtlcPayload.Reason` is opaque; `UpdateFailMalformedHtlcPayload.FailureCode` is a bare ushort. `Tlv/BlindedPathTlv` carries the update_add_htlc path_key.
- Feature bits: VarOnionOptin(9, always compulsory), PaymentSecret(15), BasicMpp(17), OptionRouteBlinding(25), OptionAttributionData(37), OptionOnionMessages(39), OptionPaymentMetadata(49). `IPeerService.SendMessageAsync` only accepts `IChannelMessage`.
- `FeatureOptions` still advertises `OptionRouteBlinding` and `OptionAttributionData` as Optional although neither is implemented (plan: default them to No until M5/M3b).
