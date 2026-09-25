# NLightning.Bolt11 — agent guide

## Purpose
Standalone BOLT 11 invoice library: model, encode, sign, decode and validate Lightning invoices. It ships as the NuGet package `NLightning.Bolt11` (v5.0.0), or as `NLightning.Bolt11.Blazor` in `*.Wasm` configs, which define `CRYPTO_JS` and rename the assembly. No `src/` project references it yet; only `test/NLightning.Bolt11.Tests`, `test/NLightning.Integration.Tests` and `test/BlazorTests/NLightning.BlazorTestApp` do. It is not wired into any payment/HTLC/onion flow.

## Layout
- `Models/Invoice.cs`: the public aggregate (ctors, `InSatoshis`, `Decode`, `Encode(Key)`/`Encode()`, HRP and amount parsing, sign/verify/recover).
- `Models/TaggedFieldList.cs`: internal ordered list of `ITaggedField`. Enforces `IsValid`, uniqueness (only `f` and `r` may repeat, see `IsRepeatable`) and d/h mutual exclusion. Contains the decode loop.
- `Models/TaggedFields/*TaggedField.cs`: one class per tag: p, r, 9, x, f, d, s, n, h, c, m.
- `Enums/TaggedFieldTypes.cs`: 5-bit tag values (p=1, r=3, 9=5, x=6, f=9, d=13, s=16, n=19, h=23, c=24, m=27).
- `Factories/TaggedFieldFactory.cs`: switch from tag type to `XTaggedField.FromBitReader`.
- `Services/InvoiceValidationService.cs`: requires p and s, and exactly one of d/h. Used statically by `Invoice` (`s_invoiceValidationService`); not registered in DI.
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
- Unit tests: `dotnet test test/NLightning.Bolt11.Tests/NLightning.Bolt11.Tests.csproj` (about 202 tests).
- Spec vectors: `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~BOLT11"` (vectors in `BOLT11/Vectors/ValidInvoices.txt`; invalid cases are InlineData in `InvoiceIntegrationTests.cs`).
- Run a single test with `--filter "FullyQualifiedName~InvoiceValidationServiceTests"`.
- WASM smoke tests are in `test/BlazorTests/NLightning.Blazor.Tests/Bolt11`. They need `-c Release.Wasm`, which builds on linux-x64 only because of the npm pins.
- Before opening a PR, run `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121 && dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.

## Gotchas
- `ITaggedField.Length` counts 5-bit groups. `TaggedFieldList.CalculateSizeInBits()` actually returns groups, and `Encode` multiplies by 5.
- Property setters call `Add`, so setting Features, RoutingInfos, ExpiryDate, PayeePubKey or MinFinalCltvExpiry twice throws. There is no replace.
- Several `r` fields are allowed. `RoutingInfos` is only the first (most preferred) one; read all of them with `RouteHints` and append with `AddRouteHint`.
- `TaggedFieldList.FromBitReader` gives each field parser its own `BitReader` over exactly `data_length*5` bits, skips unknown types, `f` fields with an unknown version and invalid-point `n` fields (parser returns null), keeps the first of a duplicated non-repeatable field, and throws on everything else: truncated fields, malformed known fields (wrong `p`/`h`/`s`/`n` length, bad `r` length), `d`+`h`, and dangling groups. `Invoice.Decode` passes the exact field-bit count, computed from the string length. The spec example "fields which must be ignored" is therefore rejected (bolts#1243 made wrong fixed lengths a MUST-fail).
- Feature-bit validation is a TODO (`Invoice.cs:553`). Unknown even features are NOT rejected, and the writer does not force the payment_secret or var_onion_optin bits.
- `MinFinalCltvExpiry` is a non-nullable `ushort` that returns the spec default 18 (`InvoiceConstants.DefaultMinFinalCltvExpiryDelta`) when `c` is absent, so it cannot tell you whether `c` was present.
- `FallbackAddressTaggedField` has no taproot (witness v1) support. Unknown versions are skipped.
- `Encode()` never runs `InvoiceValidationService`. `ToString()` with no cached string and no `ISecureKeyManager` throws NullReferenceException (explicitly, from `Encode()`); use `ToString(Key)` or `Encode(Key)`.
- If assembly names change, update the InternalsVisibleTo lists in `src/NLightning.Infrastructure.Bitcoin/AssemblyInfo.cs` (Bech32Encoder) and `./AssemblyInfo.cs`.

## Onion-routing (BOLT 4) hooks
This project has no onion code. A future Application-layer payment service would take its sender inputs from here:
- `PaymentHash` becomes `update_add_htlc.payment_hash`.
- `PaymentSecret` + `Amount` become the final-hop `payment_data` TLV (type 8).
- `MinFinalCltvExpiry` sets the final `outgoing_cltv_value` (already defaults to 18 when `c` is absent).
- `RoutingInfos` supply the last private hops (scid, fees, cltv_delta).
- `PayeePubKey` is the final hop's node id for the Sphinx ECDH.
- `Metadata` becomes the `payment_metadata` TLV (type 16).
- `Features` must be checked for var_onion_optin, payment_secret and basic_mpp. Fix the TODO at `Invoice.cs:553` first.

On the receive side, the node must persist (payment_hash, payment_secret, amount, min_final_cltv) so it can validate final-hop payloads. There is no invoice table yet.
