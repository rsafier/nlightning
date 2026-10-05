# CODEC_REDESIGN_PLAN — the wire codec layer redesign

Status: draft (2026-10-04). Epic NL-1100. Branch `wip/codec-redesign` from `wip/fafo`.

The BOLT wire codec layer costs ~12.9k code lines in 341 files. Eclair expresses the same
surface in ~2.0k lines in 12 files (scodec combinators, `eclair-core/.../wire/protocol`,
commit 746fcb1) — a 6.4x gap. One message (`update_fee`) costs us ~185 lines across 7 files
in 4 projects; Eclair needs ~11. The cost is structural, per message:

- a payload class plus a message class (`Domain/Protocol/Payloads` + `Messages`);
- a hand-coded payload serializer (`Infrastructure.Serialization/Payloads`, 51 files);
- a hand-coded message-type serializer (`Messages/Types`, 50 files);
- two registrations in each of two factories (`PayloadSerializerFactory`,
  `MessageTypeSerializerFactory`), each with two hand-maintained dictionaries;
- a TLV class plus a TLV converter per TLV (`Infrastructure/Protocol/Tlv/Converters`,
  26 files) and a `TlvConverterFactory` registration;
- a `Create*` method per message on `IMessageFactory`/`MessageFactory`.

Missing one registration silently turns a message into "unknown" (dropped if odd, peer
killed if even). The goal: one declarative definition per message and per TLV stream, used
for both encode and decode, keeping every behavior we have today. **No wire bytes change.**

## 1. What the new layer must preserve (the correctness contract)

These are the behaviors the equivalence tests pin; every candidate must keep all of them.

1. **Byte-exact round trips** for every message, proven by the existing round-trip and
   spec-vector tests (BOLT 1 BigSize vectors, BOLT 3/7/12 captured vectors) plus new
   old-vs-new equivalence tests.
2. **Strict TLV rules** (BOLT 1): strictly increasing types (duplicates rejected), canonical
   BigSize only, `length <= remaining` checked before allocating, and — where the message has
   a known-type set — unknown **even** types rejected (peer gets `warning` + disconnect);
   unknown **odd** types ignored. Today the ignored odd records are in fact parsed into the
   `TlvStream` and then dropped on re-encode, because message ctors rebuild `Extension` from
   the typed TLV properties in fixed ascending order. The new codec matches that (drop on
   re-encode), not a stronger "preserve unknown odd" behavior.
3. **Malformed-message framing**: payload truncation and bad lengths surface as
   `PayloadSerializationException`/`MessageSerializationException`/`InvalidCastException`
   wrapped exactly as today, so `MessageService` keeps answering with a connection `warning`
   (never an all-zero `error`).
4. **The onion is a mandatory fixed 1366 bytes** on `update_add_htlc` (constructor and
   serializer enforce it); the blinded path rides TLV 65322; `commitment_signed` carries a
   variable `u16`-counted list of 64-byte HTLC signatures.
5. **Stream contract**: deserializers get a seekable stream bounded to one message
   (`stream.Position/Length` is how trailing TLVs are found). The codec may read the
   remaining bytes into a pooled buffer and work on spans — the contract is preserved, the
   `NetworkStream` ban stays.
6. **Unknown message handling** stays in the entry point: unknown odd type → `null` (drop);
   unknown even type → `InvalidMessageException` (peer killed). `DeserializeMessageAsync<T>`
   keeps its wire-type-vs-`T` check (`InvalidMessageException` on mismatch).
7. **NativeAOT**: no reflection, no runtime `MakeGenericType`. The build must stay at
   0 warnings with `dotnet build src/NLightning.Daemon -c Release.Native -r osx-arm64
   -p:PublishAot=true`.
8. **Layering**: `NLightning.Domain` references nothing. Codec infrastructure lives in
   `NLightning.Infrastructure.Serialization` (references Domain + Infrastructure only).
   Domain keeps the message/payload/TLV *types* (handlers use them); only the codec
   mechanics move.
9. **Coexistence**: migrated and unmigrated messages sit behind the same
   `IMessageSerializer`/`IMessageTypeSerializerFactory` API. Callers and handlers do not
   change. Domain public shapes do not change.

## 2. Candidate approaches, with worked examples

All four keep the Domain types. They differ in where the *field layout knowledge* lives and
who writes it.

---

### Option 1 — hand-written C# codec combinators (scodec-style)

A small runtime: `WireReader`/`WireWriter` ref-structs over spans, primitive field codecs
(u8/u16/u32/u64 big-endian, BigSize, fixed/length-prefixed bytes, points, signatures,
truncated ints, channel_id/scid/chain_hash value objects, a strict TLV-stream reader with a
known-type set), and per-message definitions that compose them:

```csharp
// Wire/Definitions/UpdateFeeWire.cs — the whole codec, both directions, ~10 lines
internal static class UpdateFeeWire
{
    public static readonly MessageWire<UpdateFeeMessage> Def = new(MessageTypes.UpdateFee,
        (ref WireWriter w, UpdateFeeMessage m) =>
        {
            w.ChannelId(m.Payload.ChannelId);
            w.U32(m.Payload.FeeratePerKw);
        },
        (ref WireReader r) => new UpdateFeeMessage(new UpdateFeePayload(r.ChannelId(), r.U32())));
}
```

A TLV-heavy message adds a per-TLV table (type, value decoder, strict known set):

```csharp
// Wire/Definitions/ChannelReestablishWire.cs — ~35 lines for the whole message
internal static class ChannelReestablishWire
{
    private static readonly TlvDef[] Tlvs =
    [
        TlvDef.Decoded(TlvConstants.NextFunding,
                       v => new NextFundingTlv(v[..32].ToArray(), v[32])),
        TlvDecoded<MyCurrentFundingLockedTlv>(TlvConstants.MyCurrentFundingLocked, ...),
        TlvDecoded<NextLocalNoncesTlv>(TaprootTlvConstants.NextLocalNonces, ...),
        TlvDecoded<CurrentCommitNonceTlv>(TaprootTlvConstants.CurrentCommitNonce, ...)
    ];

    public static readonly MessageWire<ChannelReestablishMessage> Def = new(
        MessageTypes.ChannelReestablish,
        (ref WireWriter w, ChannelReestablishMessage m) =>
        {
            w.ChannelId(m.Payload.ChannelId);
            w.U64(m.Payload.LastLocalCommitmentNumber);
            w.U64(m.Payload.LastRemoteCommitmentNumber);
            w.Bytes(m.Payload.DataLossProtect);          // optional fixed field, as today
        },
        (ref WireReader r) => ...,
        Tlvs);
}
```

The stream rules (increasing types, canonical BigSize, length <= remaining, known-even
rejection) are written once in the TLV reader; each definition contributes only its known
set and its typed decoders — exactly what the per-serializer `s_knownExtensionTypes` sets do
today, minus the ~20 lines of `TryGetTlv`/converter plumbing per TLV per message.

- Lines: infra once (~700 including tests); per message ~10-60; deletes both serializer
  classes and all four factory registrations per message.
- Byte-exact risk: low. Same primitives, proven by equivalence tests over the existing
  fixtures; the riskiest part (TLV strictness) is code that already exists conceptually and
  is re-tested.
- Strict TLV rules: written once, shared.
- Trailing TLVs without seekable streams: the reader is span-based with an explicit
  remaining count; the seekable-stream contract is unchanged from today.
- Unknown odd/even messages: unchanged (entry point).
- Performance: spans and pooled buffers, no two-layer virtual dispatch, no per-field
  `ValueObjectSerializerFactory` lookups. Expected at parity or faster than today; measured
  (§6).
- NativeAOT: static generics only — clean.
- Adding a message afterward: Domain types (unchanged recipe) + one definition file + one
  registry line. Two touch points instead of seven; a missing registration becomes a test
  failure (`WireRegistryTests` asserts every `MessageTypes` value the node speaks is
  registered) instead of a silent unknown.
- Reviewability: definitions are plain C# lambdas reading in field order — no generated code
  to audit, diff-friendly, and an AI agent can write one from the BOLT text directly.

The scodec-flavored refinement: rather than a `Codec<A>` monad with `combine`, we compose
with an encode lambda + a decode lambda over a shared reader/writer DSL. In Scala,
for-comprehensions make `codecA ~ codecB` elegant; in C# the two-lambda form is shorter,
faster (no intermediate tuples), and reads in field order. The property "one definition
drives both directions" is kept by construction (the equivalence tests prove it).

---

### Option 2 — codegen from the BOLT spec's CSVs (CLN-style: `tools/extract-formats.py`)

The BOLT markdown contains machine-readable message/TLV format blocks; a script extracts
them and emits C# (build-time or checked in).

```csharp
// generated: NLightning.WireGen.UpdateFee.g.cs
public static MessageWire<UpdateFeeMessage> UpdateFee { get; } = Wire.Gen<UpdateFeeMessage>(
    134, [Wire.Field<ChannelId>("channel_id"), Wire.Field<uint>("feerate_per_kw")],
    construct: (channelId, feerate) => new UpdateFeeMessage(new UpdateFeePayload(channelId, feerate)),
    deconstruct: m => (m.Payload.ChannelId, m.Payload.FeeratePerKw));
```

- The extractor is the win for *stock* messages: the definition comes from the spec text, so
  spec drift is re-syncable.
- But a large share of our surface is not in merged spec text: the taproot TLVs (BOLTs PR
  #1324, still a draft), liquidity-ads TLV 1339, Eclair's odd 1111 `prevtx_details`, the
  splice/interactive-tx shapes as we negotiated them with CLN/Eclair. Those still need
  hand-maintained definitions, so we would run two mechanisms.
- It requires vendoring the BOLTs markdown (or a pinned upstream checkout) into the repo and
  keeping an extractor alive — a new maintenance surface with no tests today.
- Byte-exact risk: moderate. The generated field types must match our Domain value objects
  and the extension rules; every quirk (mandatory 1366-byte onion, `u16`-counted signature
  lists, `ExtraData`-verbatim gossip, two-BOLT-11-feature-sets in `init`) becomes a
  special case in the generator.
- AOT: fine (checked-in generated code, or a build-time generator that emits plain C#).
- Adding a message: for stock messages, paste the spec block; for extensions, hand-write.
- Reviewability: worst of the four — generated serializers are read by nobody, and the
  extractor itself is a second codec implementation to review.

Verdict: the wrong primary mechanism for this repo (extension-heavy, spec-quoting already
good). Could be revisited later as a *helper* that drafts a hand-edited definition from a
spec block.

---

### Option 3 — Roslyn incremental source generator from attributes on record types

```csharp
[WireMessage(MessageTypes.UpdateFee)]
public sealed partial record UpdateFeeMessage : BaseChannelMessage
{
    [WireField(0)] public partial ChannelId ChannelId { get; }
    [WireField(1)] public partial uint FeeratePerKw { get; }

    [Tlv(TlvConstants.NextFunding)] public partial NextFundingTlv? NextFundingTlv { get; }
}
```

The generator emits the serializer, the TLV plumbing and — the real win — the registry
(scan all attributed types), so a missing registration is *impossible to compile*.

- Lines: best long-term; per message a attributed record (~10 lines), zero serializer code.
- Byte-exact risk: moderate-low — the emitted code is per-field and auditable, but the
  generator itself is ~1-2k lines of Roslyn that must handle every quirk (fixed 1366-byte
  onion, counted lists, optional fixed fields with presence flags, value-object implicit
  conversions, records vs the existing sealed classes/BaseChannelMessage inheritance that
  handlers rely on). The migration touches every Domain message type's public shape or
  keeps dual shapes via partial members — the trickiest C# of the four options.
- Build cost: a new analyzer project, generator tests (snapshot verification), packaging
  into the Serialization project's build, IDE/live-analyzer friction. The repo has zero
  generator infrastructure today (the config-binding generator is a package).
- AOT: clean (plain C# emitted at compile time).
- Unknown-type registry: fully automatic — the best property of this option.
- Adding a message: one attributed type. Excellent.
- Reviewability: good at the definition level, poor when the generator misbehaves (the
  classic source-generator debugging problem, and AI agents write definitions but cannot
  debug the generator).

---

### Option 4 — hybrid: combinators at runtime, definitions hand-written now, generator optional later

Option 1's runtime plus a strict rule: definitions live in one folder, one per file, in a
style an emitter could produce. If the per-message boilerplate tax ever becomes the
bottleneck again (it will not for the vertical slice), a generator is added later that
*emits the definitions* (small, auditable output) rather than the serializers. The registry
stays a hand-written list guarded by a completeness test in the interim.

---

## 3. Decision

**Option 4 — hand-written combinator runtime + hand-written declarative definitions**
(option 1's runtime, option 4's staging), with these reasons:

As-built deltas from the option 1 sketch (P0 review, 2026-10-04): the decode lambda is **two-phase** — it reads the
fixed fields and returns a constructor continuation that receives the decoded `WireTlvs` (TLV records follow the body,
so the runtime parses body → extension → construct); typed-TLV lookups are **keyed by wire type**
(`tlvs.Get<NextFundingTlv>(TlvConstants.NextFunding)`), not by table index; the encode side writes **straight from the
pooled buffer to the stream** (`WireWriter.WriteTo`); the definition's TLV table doubles as the known-type set and is
built into a by-type lookup at registration (a duplicate type fails registration); and strict `TlvDef`s wrap **any**
converter failure as `SerializationException` (converters can throw `ArgumentException`, not only
`InvalidCastException`).

1. **Risk to byte-exact behavior dominates.** The whole point is that nothing on the wire
   changes; option 1's runtime is hand-written, testable C# whose every primitive can be
   equivalence-tested against the existing codecs. Options 2 and 3 add a second author of
   the wire bytes (a script or a generator) whose bugs are the hardest kind to find.
2. **The spec-codegen premise doesn't hold here** (option 2): our extension-heavy surface
   (taproot, liquidity ads, Eclair quirks) would need hand edits anyway.
3. **The generator's build-infrastructure cost is not justified by the slice** (option 3):
   the repo has no generator tooling; the option-1 definition style is already 10-60 lines
   per message, and the registry-completeness test removes the silent-unknown failure mode
   the generator was going to fix.
4. **Performance and AOT** are equal-best in options 1/4: spans, pooled buffers, static
   generics, no reflection.
5. **Reviewability for humans and agents**: definitions read in field order, next to the
   BOLT text; no generated code exists to audit; the equivalence harness is the arbiter.

What option 3 still wins — the compiler-checked registry — is captured by
`WireRegistryTests`: for every `MessageTypes` value the old factory knows, the merged
factory must return a serializer; and for every migrated message, both directions of the
old and new codecs must agree. A missing definition fails the build's test gate, not a
peer's connection.

**Scope decision.** This redesign covers *wire messages and their TLV streams*. The chosen
approach also fits (later phases, in rough order of value):
- the remaining BOLT 2 channel messages (open/accept/funding_created/signed/channel_ready/
  shutdown/closing_signed/closing_complete/stfu) — same shapes as the slice;
- the interactive-tx family (66-74) — same shapes;
- gossip queries 261-265 — plain fields, trivially fits;
- gossip 256/257/258 — fits, but needs a "raw trailing bytes kept verbatim" field kind
  (`ExtraData`) and the signature-hash layout dependency on the Domain codec; do after the
  plain messages;
- onion hop payloads — fits (`TlvDef` is namespace-scoped already), but
  `invalid_onion_payload` needs the offending type/offset in errors, which the strict reader
  deliberately does not report; needs an error-detailing variant of the TLV reader;
- BOLT 12 TLVs — already pure Domain codecs (`Bolt12TlvStream`), no reason to move them onto
  this runtime; leave them;
- onion messages (513) — small, fits, low value;
- liquidity-ads TLV 1339 — a single TLV with sub-records; fits as a `TlvDef` with a
  structured decoder.

## 4. The new "add a wire message" recipe (after the migration)

1. Domain: `MessageTypes` value, `XPayload`, `XMessage` — **unchanged** (steps 1-2 of the
   old recipe).
2. New file `src/NLightning.Infrastructure.Serialization/Wire/Definitions/XWire.cs`: one
   `MessageWire<XMessage>` definition (encode lambda, decode lambda, TLV table if any).
3. Register the definition in `Wire/Definitions/WireRegistry.cs` (one line).
4. `Create*` on `IMessageFactory`/`MessageFactory` — unchanged (still the send-side seam).
5. Round-trip test — unchanged.

Not needed anymore: the payload serializer, the message-type serializer, both
`PayloadSerializerFactory` registrations, both `MessageTypeSerializerFactory` registrations,
and the TLV converter (for TLVs whose Domain `Value` holds wire bytes). A missing
registration fails `WireRegistryTests` at build time instead of killing peers at run time.

## 5. Migration phases

Phase 0 (this branch) is the vertical slice below; each later phase is one reviewable PR,
same gates, same equivalence harness. **Status: the migration is COMPLETE on `wip/codec-redesign` (NL-1101, NL-1102) — all 50
`MessageTypes` messages the node speaks are on the Wire codec. P3 moved the gossip family over
(256/257/258 frame the Domain codecs with `ExtraData` verbatim; 259 uses the strict-empty + keep-raw
extension options; the five queries carry `TlvDef.RawKnown` records); `announcement_signatures` 259
moved from P1 to P3 with its family as planned. P4 resolved: `onion_message` 513 migrated in P2;
`HopPayloadSerializer` and `FailureMessageSerializer` stay dedicated — `invalid_onion_payload` needs
the offending record's type+offset (wire-visible error data the strict reader deliberately does not
carry), the failure serializer is synchronous inside the crypto loop with its own lenient framing, and
neither is a `MessageTypes`-keyed peer message. Revisit hop payloads only if the runtime grows an
error-detailing TLV reader.**

- **P0 (this PR): infrastructure + slice.** Wire runtime (reader/writer, primitives, strict
  TLV stream, `MessageWire<T>`, `WireRegistry`, factory merge), then migrate:
  - BOLT 1: `init`, `error`, `warning`, `ping`, `pong`;
  - BOLT 2 HTLC/commitment set: `update_add_htlc`, `update_fulfill_htlc`, `update_fail_htlc`,
    `update_fail_malformed_htlc`, `commitment_signed`, `revoke_and_ack`, `update_fee`;
  - `channel_reestablish` (TLVs 1, 5, 22, 24);
  - `tx_add_input` (TLVs 0, 2/1111).
  Checklist per message: definition file; delete payload+message serializer; remove both
  factory registrations; equivalence test (fixture + property); round-trip test still green
  unchanged.
- **P1: the rest of BOLT 2** (open/accept both versions, funding_created/signed,
  channel_ready, shutdown, closing_signed/closing_complete/closing_sig, stfu,
  announcement_signatures 259) and their TLVs. Watch: `channel_type` optional-on-wire rule,
  taproot TLV known sets, `option_simple_close` 98/32-byte nonces.
- **P2: interactive-tx (66-74) + splice TLVs**, liquidity-ads TLV 1339.
- **P3: gossip** (256/257/258 with `ExtraData`-verbatim + signature-hash coupling, 261-265).
- **P4: onion** (513; hop payloads only with the error-detailing reader variant).
- **Not planned**: BOLT 12 codecs (already pure Domain, different consumers); the onion hop payloads and
  the failure-message serializer (see the P4 resolution above).

## 6. Measurements

Before (code lines = non-blank, non-comment; measured on the branch point `2df63cb2`):

| area | files | code lines |
|---|---|---|
| `Infrastructure.Serialization` (whole project) | 125 | 7,201 |
| `Domain/Protocol` (whole tree) | 307 | 8,257 |
| `Infrastructure/Protocol/Tlv` (converters) | 37 | 1,407 |
| **codec-layer total (approx., minus non-codec Domain/Protocol files)** | **341** | **~12,900** |

Per-message cost today (code lines in the codec layer): `update_fee` 165 across 7 files;
`channel_reestablish` 219 across 3 main files + 4 TLV classes + 4 converters + registrations;
`tx_add_input` 397 across 5 files.

After the P0 slice (measured on `wip/codec-redesign`, commit `81e90dd4`):

| P0 slice | files | code lines |
|---|---|---|
| deleted hand-written serializers (14 message-type + 13 payload) | 27 | 1,610 |
| added Wire runtime (reader, writer, MessageWire, registry) | 6 | 718 (one-time) |
| added definitions (14 messages) | 5 | 384 (~27 per message) |
| **net for the slice** | | **−508** (excl. tests) |

Remaining unmigrated boilerplate: 38 payload serializers (2,138 lines) + 36 message-type
serializers (2,157 lines) = ~4,300 lines over ~40 messages; replacing them at ~27 lines per
message plus removing the per-TLV converters (26 files, ~1,000 lines) puts the projected
full-layer net at roughly **−3,500 to −4,000 code lines**, with the structural wins larger
than the count: 7→3 touch points per message, one shared strict-TLV implementation, and the
registry-completeness test replacing the silent-unknown failure mode.

Performance (Stopwatch micro-benchmark `WirePerfBenchmark`, `Explicit`/`Category=Benchmark`,
Release net10.0, Apple M-series, 20k iterations after warmup; the same file was run against
the branch point in a throwaway worktree for the old numbers):

| op | old codec | new codec |
|---|---|---|
| `update_add_htlc` decode (1366-byte onion) | 1.5 µs/op | 1.0 µs/op |
| `update_add_htlc` encode | 0.8 µs/op | 0.7 µs/op |
| `commitment_signed` decode (8 signatures) | 2.7 µs/op | 1.6 µs/op |
| `commitment_signed` encode | 1.4 µs/op | 1.1 µs/op |

The new codec is faster on every measured hot path (fewer virtual layers, span-based
primitives, no per-field factory lookups), comfortably inside the 1.5x acceptance bound.

## 7. Risks

1. **Byte drift on a migrated message** — mitigated by the equivalence harness (every
   migrated message: old codec and new codec must agree byte-for-byte on encode, and decode
   to equal values, over existing fixtures plus randomized property tests, including strict
   rejection cases) and by the unchanged round-trip/vector tests.
2. **TLV `Value`-is-wire-bytes invariant** — some Domain TLVs do not hold wire bytes in
   `Value` (e.g. `FeeRangeTlv`); those keep the converter path until their phase. Each
   migrated TLV's definition asserts it with an encode-side equivalence test.
3. **Async/stream contract regressions** — the codec reads the remaining bytes of the
   bounded seekable stream into a pooled buffer; the seekable-stream-only gotcha is
   unchanged and re-documented.
4. **Behavior drift on rejection paths** — exception types and wrap points are pinned by
   the existing malformed-message tests; the equivalence harness includes the
   strict-TLV rejection cases.
5. **Coexistence bugs at the factory seam** — the merged factory prefers `WireRegistry` and
   falls back to legacy; `WireRegistryTests` plus the full Serialization suite cover both
   paths; migrated legacy registrations are deleted, not dead-coded.
6. **AOT** — ref structs and static generics only; the Release.Native build gate stays at
   0 warnings.
7. **Scope creep into handlers/Domain shapes** — prohibited by the contract in §1; the
   equivalence and round-trip tests fail if public shapes move.

## 8. Ledger

Reserved range NL-1100..NL-1129 in `docs/agents/ISSUES.md`:
- NL-1100: epic, the codec redesign (this plan);
- P0 slice entries: NL-1101 (infrastructure), NL-1102.. per migrated message group, plus any
  bugs found during the equivalence work.
