# NLightning.Infrastructure.Serialization

BOLT wire (de)serialization: `IMessage` <-> bytes. Three layers: `MessageSerializer` (u16 BE type prefix) -> per-type `*MessageTypeSerializer` -> per-payload `*PayloadSerializer`, with TLV, value-object and FeatureSet serializers underneath. Interfaces live in `src/NLightning.Domain/Serialization/Interfaces` (including `IHopPayloadSerializer`; plus local `Interfaces/IFeatureSetSerializer`, `ITlvStreamSerializer`). No protocol logic here — bytes only.

## Layout
- `DependencyInjection.cs` — `AddSerializationInfrastructureServices()` registers 8 singletons (including `IHopPayloadSerializer`), plus `TryAddSingleton<ITlvConverterFactory, TlvConverterFactory>` (also TryAdded by `AddInfrastructureServices`), so this layer resolves on its own.
- `Messages/MessageSerializer.cs` — entry point; BOLT 1 unknown-odd -> `null`, unknown-even -> `InvalidMessageException`.
- `Messages/Types/` — 32 `IMessageTypeSerializer<TMessage>`; payload + TLV extension, TLVs converted via `ITlvConverterFactory`.
- `Payloads/` — 31 `IPayloadSerializer<TPayload>`; hand-coded field layout, big-endian via `NLightning.Infrastructure.Converters.EndianBitConverter`, `ArrayPool` buffers.
- `Factories/` — `MessageTypeSerializerFactory`, `PayloadSerializerFactory`, `ValueObjectSerializerFactory`: hand-written dictionaries, serializers built with `new` (not DI).
- `Tlv/` — `TlvSerializer` (BigSize type + BigSize len + value), `TlvStreamSerializer` (serialize: converter by runtime type via `ITlvConverterFactory.GetConverter(Type)`, raw `BaseTlv` verbatim; deserialize reads to end of stream, strictly increasing types, length <= remaining; `DeserializeStrictAsync(stream, knownTypes)` also rejects unknown even types).
- `Onion/HopPayloadSerializer.cs` — `IHopPayloadSerializer` (declared in Domain `Serialization/Interfaces/`): BOLT 4 hop payload TLV stream <-> `HopPayload` (see Onion section).
- `ValueObjects/` — BigSize, ChainHash, ChannelFlags, ChannelId, ShortChannelId, Witness.
- `Node/FeatureSetSerializer.cs` — BOLT 9 feature bits (init writes it twice: global + local).

## Adding a wire message (all steps required)
1. Domain: `MessageTypes` enum, `Payloads/XPayload.cs`, `Messages/XMessage.cs` (in `src/NLightning.Domain/Protocol`).
2. New TLVs: `TlvConstants`, Domain `Tlv/XTlv.cs`, converter in `src/NLightning.Infrastructure/Protocol/Tlv/Converters`, register in `TlvConverterFactory.RegisterConverters`, and add a sample to `CreateSampleTlvs` in `Tlv/TlvStreamSerializerTests.cs`.
3. `Payloads/XPayloadSerializer.cs` -> register in `PayloadSerializerFactory` in BOTH `RegisterSerializers` and `RegisterTypeDictionary`.
4. `Messages/Types/XMessageTypeSerializer.cs` -> register in `MessageTypeSerializerFactory` in BOTH `RegisterSerializers` (`_serializers`) and `RegisterTypeDictionary` (`_messageTypeDictionary`). Missing the type map => `MessageSerializer.SerializeAsync` throws `InvalidOperationException` and non-generic `DeserializeMessageAsync` treats the type as unknown.
5. Round-trip test in `test/NLightning.Infrastructure.Serialization.Tests/Messages/XMessageTests.cs` (use `Helpers/SerializerHelper.cs`).
New value object: implement `IValueObjectTypeSerializer<T>` in `ValueObjects/`, register in `ValueObjectSerializerFactory.RegisterSerializers`.

## Conventions
- File-scoped namespace `NLightning.Infrastructure.Serialization.<Folder>`, relative `using Domain...;` after the namespace line; `System`/`NLightning.*` fully-qualified usings above it.
- Implement the non-generic interface explicitly, delegating to the generic method. Non-generic `SerializeAsync` type-checks and throws `SerializationException` on mismatch.
- Payload deserializers wrap errors in `PayloadSerializationException`; message-type deserializers rethrow `SerializationException` as `MessageSerializationException` (both in `src/NLightning.Infrastructure/Exceptions`, both derive Domain `ErrorException`, NOT `SerializationException` — payload errors escape the message catch unwrapped). `MessageService.ReceiveMessage` answers any deserialization failure (malformed, truncated, unknown even type) with a `warning` (channel_id zero) and a `ConnectionException`, on which `PeerCommunicationService` closes the connection; never an all-zero `error`, which would make the peer fail every channel with us (BOLT 1).
- Return `ArrayPool` buffers in `finally`; slice rented buffers to exact length (`[..32]`) before passing to fixed-length value-object ctors.
- `dotnet format` is a CI gate (`_camelCase` fields, no unused usings — see root `.editorconfig`).

## Dependency rules
References only `NLightning.Domain` and `NLightning.Infrastructure` (csproj). Must NOT reference Application, Infrastructure.Bitcoin (no NBitcoin), Persistence/Repositories, Daemon, Client, Bolt11. Domain exposes internal ctors/init setters to this assembly via `src/NLightning.Domain/AssemblyInfo.cs` InternalsVisibleTo.

## Tests
- `dotnet build NLightning.sln -p:MSBuildWarningsAsMessages=MSB4121`
- `dotnet test test/NLightning.Infrastructure.Serialization.Tests/NLightning.Infrastructure.Serialization.Tests.csproj --no-build` (145 pass)
- One test: add `--filter "FullyQualifiedName~PingMessageTests"`
- BOLT 1 BigSize vectors: `test/.../Vectors/BigSize.txt` (all 18 vectors active, including the 3 non-canonical-encoding failures).
- No serializer tests for FundingCreated/FundingSigned yet (OpenChannel1/AcceptChannel1 are covered by `Messages/OpenChannel1MessageTests.cs` and `MessageExtensionStrictnessTests.cs`).

## Gotchas
- Deserializers use `stream.Position/Length` for optional TLVs and the onion: they need a seekable, one-message `MemoryStream`, never a `NetworkStream`.
- `DeserializeMessageAsync<TMessage>` throws `InvalidMessageException` when the wire type is not the one registered for `TMessage` (it compares the factory's serializer for the wire type with TMessage's).
- `TlvConstants` numbers collide across messages (0 and 1 reused), so always use the semantically correct constant (e.g. `UpdateAddHtlcMessageSerializer.cs` uses `TlvConstants.BlindedPath`).
- `RemoteAddressTlv` type 5 (DNS hostname) conversion is broken: Domain length is `3 + len` (spec: `4 + len`) and the converter overwrites a hostname byte. IPv4/IPv6/Tor v3 are fine.
- Every message-type serializer that reads a TLV extension uses `DeserializeStrictAsync` with its own `s_knownExtensionTypes` set (unknown even types fail, BOLT 1). A new message with a TLV extension must do the same; the open `DeserializeAsync` is for streams whose namespace is not known. `DeserializeStrictAsync` returns an empty stream (never `null`), so test with `extension.Any()`. `BigSizeTypeSerializer` is canonical (throws `ArgumentException(NonCanonicalErrorMessage)`). `TlvSerializer` rejects length > remaining bytes before allocating.
- `OpenChannel1MessageTypeSerializer`/`AcceptChannel1MessageTypeSerializer` treat `channel_type` as optional on the wire (`ChannelTypeTlv` is nullable); BOLT 2's "fail the channel" for a missing one is enforced by `ChannelOpenValidator.PerformMandatoryChecks`.
- BOLT 7 announcements (256-259) are registered but NOT parsed: `GossipMessageTypeSerializer<TMessage>` + `GossipPayloadSerializer` keep every remaining byte as `GossipPayload.Data` (so even gossip types no longer kill the peer). Replace with real payload serializers when gossip is implemented. The gossip query messages (261-265) are parsed by their own payload/message-type serializers (`encoded_short_ids` and the query TLVs stay raw bytes; shared u16/u32 helpers in `Payloads/GossipQueryFieldSerializer.cs`); their TLV extensions are read strictly.
- Warning reuses `ErrorPayload` (`PayloadSerializerFactory` maps `MessageTypes.Warning`).
- File names differ from class names (`*MessageTypeSerializer` / `*PayloadSerializer`): `Messages/Types/UpdateAddHtlcMessageSerializer.cs`, `UpdateFailHtlcMessageSerializer.cs`, `UpdateFufillHtlcMessageSerializer.cs` (typo), `FundingCreatedTypeSerializer.cs`, `FundingSignedTypeSerializer.cs`; `Payloads/UpdateFufillHtlcSerializer.cs` (class `UpdateFulfillHtlcPayloadSerializer`).
- `HtlcDbRepository` stores serialized `UpdateAddHtlcMessage` bytes in the DB — changing that wire format affects persisted rows.

## Onion routing (BOLT 4) hooks
- `Payloads/UpdateAddHtlcPayloadSerializer.cs`: the onion is MANDATORY and exactly `OnionConstants.PacketLength` (1366) bytes, read with `ReadExactlyAsync`; a short read throws `PayloadSerializationException`, and the `UpdateAddHtlcPayload` constructor rejects any other length. The payload keeps raw bytes; `Domain/Protocol/Onion/ValueObjects/OnionPacket` parses them (version/pubkey not validated there; that is the peeler's job).
- `UpdateFailHtlcPayloadSerializer` passes `reason` (encrypted failure onion) as opaque u16-length bytes; `UpdateFailMalformedHtlcPayloadSerializer` carries sha256_of_onion + raw u16 failure_code; `UpdateFailMalformedHtlcMessageTypeSerializer` accepts any failure_code; the BOLT 2 "no BADONION bit -> fail the channel" rule needs the channel id, so `ChannelManager` enforces it (channel-scoped `warning` and close the connection, BOLT 2's other option, since we can't fail a channel yet).
- `Onion/HopPayloadSerializer.cs` (`IHopPayloadSerializer`): BOLT 4 hop payload TLV stream <-> `Domain/Protocol/Onion/Models/HopPayload`. `DeserializeAsync(ReadOnlyMemory<byte>)` takes the raw stream (no length prefix, as the Sphinx peeler returns it); `DeserializeWithLengthPrefixAsync(Stream)` reads `bigsize len || payload` and stops after it. Reads records one at a time (not via `DeserializeStrictAsync`) so every failure is `OnionException(InvalidOnionPayload)` with `bigsize type || u16 offset` (type 0/offset 0 when not attributable). Unknown odd TLVs are kept verbatim; typed TLVs re-encode canonically, so parse -> serialize is byte-identical. Semantic rules (required/forbidden fields) are in `Domain/Protocol/Onion/Validators/HopPayloadValidator`.
- onion_message (513) would be a new message following the 4-place registration above. The Sphinx core (construct/peel) is in `src/NLightning.Infrastructure.Bitcoin/Onion/` and exchanges hop payloads as raw TLV bytes, because Infrastructure.Bitcoin does not reference this project: serialize `HopPayload` here and pass the bytes to `OnionHop`, and parse `PeeledOnion.Payload` with `IHopPayloadSerializer.DeserializeAsync`.
