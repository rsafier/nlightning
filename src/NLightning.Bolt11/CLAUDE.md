# NLightning.Bolt11 — agent guide

## Purpose
Standalone BOLT 11 invoice library: model, encode, sign, decode and validate Lightning invoices. It ships as the NuGet package `NLightning.Bolt11` (v5.0.0), or as `NLightning.Bolt11.Blazor` in `*.Wasm` configs, which define `CRYPTO_JS` and rename the assembly. `src/NLightning.Application` references it (ABCD W1-B: `Payments/Invoices/InvoiceService` encodes our invoices through the node path, `Payments/Routing/PaymentTarget.FromInvoice` reads decoded ones); besides that only `test/NLightning.Bolt11.Tests`, `test/NLightning.Integration.Tests` and `test/BlazorTests/NLightning.BlazorTestApp` do. Nothing calls those Application services on the wire yet.

## Layout
- `Models/Invoice.cs`: the public aggregate (ctors, `InSatoshis`, `Decode`, `Encode(Key)`/`Encode()`, HRP and amount parsing, sign/verify/recover).
- `Models/TaggedFieldList.cs`: internal ordered list of `ITaggedField`. Enforces `IsValid`, uniqueness (only `f` and `r` may repeat, see `IsRepeatable`) and d/h mutual exclusion. Contains the decode loop.
- `Models/TaggedFields/*TaggedField.cs`: one class per tag: p, r, 9, x, f, d, s, n, h, c, m.
- `Enums/TaggedFieldTypes.cs`: 5-bit tag values (p=1, r=3, 9=5, x=6, f=9, d=13, s=16, n=19, h=23, c=24, m=27).
- `Factories/TaggedFieldFactory.cs`: switch from tag type to `XTaggedField.FromBitReader`.
- `Services/InvoiceValidationService.cs`: `ValidateInvoice` (decode and encode) requires p and s, and exactly one of d/h. `ValidateForEncoding` (encode only) adds `ValidateFeatures`: `9` present with var_onion_optin and payment_secret, every BOLT 9 dependency set, no unknown even bit (same `FeaturesTaggedField.GetUnknownRequiredBits` test the decoder uses; unknown odd bits are allowed), no feature with both its optional and compulsory bit set, and no known feature whose BOLT 9 context excludes invoices. Deliberate spec deviation: current BOLT 9 lists var_onion_optin (8/9) and payment_secret (14/15) as ASSUMED with no context, so a strict reading forbids them in `9`; we require them as compulsory for LND 0.20 interop (the BOLT 11 examples still carry `9qrsgq`). Use `ValidateFeatures` for encoding only, not as a validity check on decoded third-party invoices. Used statically by `Invoice` (`s_invoiceValidationService`); not registered in DI.
- `Constants/TaggedFieldConstants.cs`, `Interfaces/`, `Exceptions/InvoiceSerializationException.cs`, `Models/ValidationResult.cs`.
- External pieces: `Bech32Encoder` (internal, `src/NLightning.Infrastructure.Bitcoin/Encoders/Bech32Encoder.cs`, visible here through InternalsVisibleTo), `BitReader`/`BitWriter` (`src/NLightning.Domain/Utils`), `RoutingInfo(Collection)` (`src/NLightning.Domain/Models`), `InvoiceConstants` (`src/NLightning.Domain/Constants`), `FeatureSet` (`src/NLightning.Domain/Node`).

## Add a new tagged field (step by step)
1. Add the enum value to `Enums/TaggedFieldTypes.cs`. The value is the bech32 character index.
2. Create `Models/TaggedFields/XTaggedField.cs` as `internal sealed class XTaggedField : ITaggedField` with:
   - an internal ctor(value) that precomputes `Length` in 5-bit groups (max 1023);
   - `WriteToBitWriter`, which writes exactly `Length*5` bits;
   - `IsValid()`;
   - `internal static XTaggedField? FromBitReader(BitReader, short length)`, which checks the length and throws `ArgumentException` if it is wrong.
3. Add a case to `Factories/TaggedFieldFactory.cs`.
4. Add a typed property on `Invoice` backed by `_taggedFields.TryGet<T>(...)` / `_taggedFields.Add(...)`.
5. If the field may repeat, update the uniqueness rule in `TaggedFieldList.Add`.
6. Add tests in `test/NLightning.Bolt11.Tests/Models/TaggedFields/XTaggedFieldTests.cs` and `test/NLightning.Integration.Tests/BOLT11/TaggedFields/`.
7. Bump `<Version>`/`<AssemblyVersion>`/`<FileVersion>`/`<PackageReleaseNotes>` in the csproj and add an entry to `CHANGELOG.md` when you release.

## Conventions
- External usings (`System.*`, `NBitcoin`) go above the file-scoped `namespace NLightning.Bolt11.X;`; project usings go below it in relative form (`using Domain.Utils;`, `using Enums;`). See `Models/Invoice.cs`.
- 32-byte hashes (p, s, h) are NBitcoin `uint256` and are `Array.Reverse`d on little-endian machines.
- When `Length*5` is not a multiple of 8, `FromBitReader` reads `(len*5+7)/8` bytes and drops the last byte.
- `_invoiceString` caches the encoded string. It is invalidated by the `Changed` events on `TaggedFieldList`, `RoutingInfoCollection` and `FeatureSet`.
- Every Decode/Encode error is wrapped in `InvoiceSerializationException`.
- `dotnet format` is a CI gate. Follow `.editorconfig`: `_field` / `s_field` naming, no unused usings.

## Dependency rules
- Allowed references: `NLightning.Infrastructure.Bitcoin`, `NLightning.Infrastructure`, and Domain (transitively).
- Must NOT reference: Application, Daemon, Daemon.Contracts, Daemon.Plugins, Client, Transport.Ipc, Infrastructure.Serialization, Infrastructure.Persistence.* or Infrastructure.Repositories.
- Keep the package free of DI and hosting. `Invoice` is constructed directly, and the optional `ISecureKeyManager` is passed to its ctor.
- Code must compile under `CRYPTO_LIBSODIUM`, `CRYPTO_NATIVE` and `CRYPTO_JS` (defined in `src/NLightning.Infrastructure/NLightning.Infrastructure.csproj`; this csproj only adds `CRYPTO_JS` for `.Wasm`), because CI builds Release, Release.Native and Release.Wasm (`.github/workflows/`).

## Tests
- Unit tests: `dotnet test test/NLightning.Bolt11.Tests/NLightning.Bolt11.Tests.csproj` (272 tests). `Models/InvoiceNodeEncodingTests.cs` covers the node encode path (key manager, features, `c`/`x`/`s`, several `r`, NL-120 rejections); `Models/LndInvoiceFixtureTests.cs` holds LND 0.20.0-beta regtest fixtures (plain, custom route hints, hold) with LND's `decodepayreq` values, plus one NLightning-encoded string that LND decoded.
- Spec vectors: `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~BOLT11"` (vectors in `BOLT11/Vectors/ValidInvoices.txt`; invalid cases are InlineData in `InvoiceIntegrationTests.cs`).
- Run a single test with `--filter "FullyQualifiedName~InvoiceValidationServiceTests"`.
- WASM smoke tests are in `test/BlazorTests/NLightning.Blazor.Tests/Bolt11`. They need `-c Release.Wasm`, which builds on linux-x64 only because of the npm pins.
- Before opening a PR, run `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121 && dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.

## Gotchas
- `ITaggedField.Length` counts 5-bit groups. `TaggedFieldList.CalculateSizeInBits()` actually returns groups, and `Encode` multiplies by 5.
- Property setters go through `TaggedFieldList.Replace`, so setting a property again replaces the old value (`RoutingInfos` and `FallbackAddresses` replace every `r`/`f` field). Setting `Description` while `DescriptionHash` is set (or the reverse) still throws.
- A decoded invoice without `n` exposes the recovered key through `PayeePubKey` but does not add an `n` field, so re-encoding it does not add one. `Encode(Key)` replaces that key with the signing key. With an `n` field, `Decode` rejects high-S signatures (BOLT 11 low-S rule); without one, recovery accepts both.
- Several `r` fields are allowed. `RoutingInfos` is only the first (most preferred) one; read all of them with `RouteHints` and append with `AddRouteHint`.
- `TaggedFieldList.FromBitReader` gives each field parser its own `BitReader` over exactly `data_length*5` bits, skips unknown types, `f` fields with an unknown version and invalid-point `n` fields (parser returns null), keeps the first of a duplicated non-repeatable field, and throws on everything else: truncated fields, malformed known fields (wrong `p`/`h`/`s`/`n` length, bad `r` length), `d`+`h`, and dangling groups. `Invoice.Decode` passes the exact field-bit count, computed from the string length. The spec example "fields which must be ignored" is therefore rejected (bolts#1243 made wrong fixed lengths a MUST-fail).
- `9` field: decode rejects unknown even bits (unknown = no `Domain.Enums.Feature` pair) and ignores unknown odd bits; `Encode` adds var_onion_optin (8) and payment_secret (14) as compulsory when neither bit of the pair is set (it mutates the caller's `FeatureSet`). Decode does not check BOLT 9 dependencies (e.g. basic_mpp -> payment_secret); encode does, through `FeatureSet.GetMissingDependencies`. `FeaturesTaggedField` reads/writes without the old `shouldPad` shift, which read 15-bit fields (`9qrsgq`) one bit too high (8/14 as 9/15).
- `x` is a `long` read/written as big-endian 5-bit groups (no 32-bit cap; values over 63 bits are rejected). `x`=0 is kept (written as an empty field) and makes the invoice already expired. A decoded `c` of 0 is dropped, so the default 18 applies. An `r` field must hold at least one entry: `AddRouteHint`/`RoutingInfos` reject an empty collection, and `Encode` throws if a route hint collection was emptied after it was added.
- `MinFinalCltvExpiry` is a non-nullable `ushort` that returns the spec default 18 (`InvoiceConstants.DefaultMinFinalCltvExpiryDelta`) when `c` is absent, so it cannot tell you whether `c` was present.
- `FallbackAddressTaggedField` has no taproot (witness v1) support. Unknown versions are skipped.
- `Encode(Key)` first adds var_onion_optin (bit 8) and payment_secret (bit 14) as compulsory when missing, then runs `ValidateForEncoding` (NL-120) and signs nothing if it fails (`InvoiceSerializationException` wrapping an `InvalidOperationException` that lists the errors). An invoice needs p, s and d or h to encode. It never adds basic_mpp (17), so a default invoice's `9` field is exactly bits 8 and 14 (what LND 0.20 marks required; LND also sets 17 and 25 optional).
- Node use: build the invoice with the public ctor (amount, description or hash, payment hash, payment secret, network, `ISecureKeyManager`), set `MinFinalCltvExpiry`, `ExpiryDate` and `AddRouteHint` for each hint, then `Encode()`. It signs with `GetNodeKeyPair()`, copying the private key first and zeroing only its own copy (the `ISecureKeyManager` contract does not say the returned buffer is caller-owned). There is no `n` field, so payers recover the node id from the signature. `Encode()`, and `ToString()` with no cached string, throw `InvalidOperationException` when no `ISecureKeyManager` was given; use `ToString(Key)` or `Encode(Key)`.
- If assembly names change, update the InternalsVisibleTo lists in `src/NLightning.Infrastructure.Bitcoin/AssemblyInfo.cs` (Bech32Encoder) and `./AssemblyInfo.cs`.

## Onion-routing (BOLT 4) hooks
This project has no onion code. A future Application-layer payment service would take its sender inputs from here:
- `PaymentHash` becomes `update_add_htlc.payment_hash`.
- `PaymentSecret` + `Amount` become the final-hop `payment_data` TLV (type 8).
- `MinFinalCltvExpiry` sets the final `outgoing_cltv_value` (already defaults to 18 when `c` is absent).
- `RoutingInfos` supply the last private hops (scid, fees, cltv_delta).
- `PayeePubKey` is the final hop's node id for the Sphinx ECDH.
- `Metadata` becomes the `payment_metadata` TLV (type 16).
- `Features` must be checked for basic_mpp before splitting a payment (unknown required bits are already rejected on decode).

On the receive side, the node must persist (payment_hash, payment_secret, amount, min_final_cltv) so it can validate final-hop payloads. There is no invoice table yet.
