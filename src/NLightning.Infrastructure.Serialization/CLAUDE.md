# NLightning.Infrastructure.Serialization

BOLT wire (de)serialization: `IMessage` <-> bytes. `MessageSerializer` handles the u16 big-endian type prefix and resolves the body codec directly through `WireRegistry`. Every supported peer message has one declarative `MessageWire<T>` definition; its TLV table composes value encode/decode pairs (NL-1100). No payload/message serializer factories, TLV converters or converter factory remain.

## Layout
- `DependencyInjection.cs` — `AddSerializationInfrastructureServices()` registers `WireRegistry`, `IMessageSerializer`, the TLV/value-object/feature serializers, and the dedicated `IHopPayloadSerializer`/`IFailureMessageSerializer`. This layer resolves independently of other infrastructure registrations.
- `Messages/MessageSerializer.cs` — entry point; unknown odd message -> `null`, unknown even -> `InvalidMessageException`. Generic deserialization checks the wire type against the registered message type.
- `Wire/MessageWire.cs` — body encode lambda and decode lambda returning a constructor continuation receiving `WireTlvs`; extension framing and strict record validation are shared.
- `Wire/Definitions/` — definitions for every supported peer message, registered once in `Wire/WireRegistry.cs`. The registry-completeness tests cover every supported `MessageTypes` value and both lookup directions.
- `Wire/TlvDef.cs` — a known wire tag and explicit value encode/decode pair, with a typed `TlvDef<TTlv>` view. `Raw(type, exactLength)` handles closing signatures; `RawKnown(type)` retains raw gossip-query values. `.AsLenient()` tolerates invalid advisory values; `.WithType(type)` reuses a value codec under an alternate tag (prevtx_details 2/1111).
- `Wire/TlvDefs.cs` — small value codecs reused across peer messages; single-message value codecs live beside their owning definition. `Wire/HopTlvDefs.cs` — hop value codecs and the explicit `All` table consumed by the dedicated onion reader.
- `Wire/WireReader.cs`, `WireWriter.cs` — span-based big-endian fields, canonical BigSize, pooled encoding buffer returned in `finally`.
- `Tlv/` — standalone record and stream serializers, retained for hop payloads and other raw streams. The stream writer obtains typed value definitions from `WireRegistry`'s index derived from message tables and hop definitions; raw `BaseTlv` records are written verbatim. A missing typed definition throws. Records remain in insertion order, never silently sorted.
- `Factories/ValueObjectSerializerFactory.cs`, `ValueObjects/`, `Node/FeatureSetSerializer.cs` — value objects and feature bits for the dedicated serializers.
- `Onion/HopPayloadSerializer.cs`, `FailureMessageSerializer.cs` — dedicated BOLT 4 codecs (see below).

## Adding a wire message or TLV
Follow the root `CLAUDE.md` recipes. Domain keeps constants, payload/message/TLV types and the send-side `IMessageFactory` seam. Add one `MessageWire<XMessage>` in `Wire/Definitions/`, its `TlvDef` table and one `WireRegistry` registration. Put reused value codecs in `TlvDefs`; there is no second converter registration. Add byte-exact round trips and rejection cases in `Tests/Messages/` and value tests in `Tests/Wire/Tlvs/`. Add a typed sample to `TlvStreamSerializerTests.CreateSampleTlvs`; it covers every value type indexed by the registry. Use `Helpers/SerializerHelper.WireRegistry` for message and value lookups.

## Conventions and invariants
- File-scoped namespace; relative Domain usings after it, System/Microsoft/fully-qualified NLightning usings above it. No unused usings; root `.editorconfig` is enforced by `dotnet format`.
- This project references only Domain and Infrastructure; never Application, Infrastructure.Bitcoin, NBitcoin, Persistence/Repositories, Daemon or Client. Domain exposes internal ctors/init setters to this assembly.
- Body/constructor failures become `PayloadSerializationException`; extension/value decode failures become `MessageSerializationException`. The closing pair requests `wrapBodyErrors` to preserve its legacy wide catch. These errors derive Domain `ErrorException`; `MessageService` answers malformed messages with a connection `warning` and disconnect, never an all-zero `error`.
- Strict message TLVs: types must increase (no duplicates), BigSize must be canonical, length must not exceed the remaining bytes before allocation, unknown even tags fail, unknown odd tags are ignored. Normal message constructors rebuild extensions from typed properties, dropping ignored odd records on re-encode. `WireTlvs` also supplies raw records where explicitly requested.
- Init's invalid advisory remote_addr/liquidity rates remain available via `RawValue` and the Domain message's undecodable properties. Its value definitions decode leniently; standalone value decoding still validates them.
- Gossip 256/257/258 frames the Domain codecs (`GetBytes`/`Parse`), preserving every signed unknown trailing byte in `ExtraData`. 259 validates the trailing TLVs with an empty known set while retaining its raw extension. Gossip query definitions retain known raw values with `RawKnown`. Taproot gossip frames the pure Domain TLV codecs.
- Seekable, bounded one-message streams are required; never pass a `NetworkStream` to the deserializers.
- `channel_type` is optional on receipt; channel validation enforces negotiation rules. `fee_range` and `funding_txid` are also nullable on receipt. Some Domain `Value`s do not hold the wire bytes: use the definition's encode function (not a blanket `tlv.Value` shortcut).
- `update_add_htlc` carries a mandatory 1366-byte onion. `update_fail_htlc.reason` stays opaque. A failure_code without BADONION is a channel-level rule, enforced by `ChannelManager`.
- `onion_message` 513 validates `point path_key || u16 len || packet` (minimum packet length 66) and an empty known trailing TLV set. The packet is raw; its interior is decoded by Domain `OnionMessageTlvsCodec`.
- Commitment/onion bytes are persisted by `ChannelStateDbRepository`; preserving these formats also preserves stored channel state.

## Dedicated onion serializers
`HopPayloadSerializer` reads records one at a time so `invalid_onion_payload` can identify the offending type and byte offset, including the BigSize length prefix. It uses `HopTlvDefs` for value codecs, preserves unknown odd records, and keeps custom records >=65536 for the validator to decide based on hop role. Required/forbidden fields remain in Domain `HopPayloadValidator`. Do not move its reader onto `MessageWire` without an error-detailing reader (plan §5).

`FailureMessageSerializer` is synchronous inside the crypto loop: `u16 code || data || tlv_stream`. Known truncated data fails; malformed extra bytes are ignored (`Extension = null`) as BOLT 4 requires. Its error payload framing is `failure_len || failuremsg || pad_len || zero pad`, with at least 256 bytes of failure+pad and a total authenticated packet <=32768. Keep this lenient framing dedicated. BOLT 12 codecs stay in Domain.

## Gates
Use the root commands with `-f net10.0`, `--blame-hang-timeout 5m` on every `dotnet test`, and the requested non-Docker/non-SqlServer filter. Serialization tests include exact-hex message round trips, BOLT 1 BigSize vectors, strictness/property tests, all typed value definitions, and registry completeness. `WirePerfBenchmark` is explicit. Run Release with zero warnings, the format verification gate and `scripts/check-sln-configs.py`; run Release.Native if Infrastructure/Crypto changes.
