# test/ — agent guide

The xUnit v3 + Moq suite. There is one test project per src layer, plus cross-cutting spec-vector and Docker tests. Tests must never be referenced from src/.

## Layout
- `NLightning.Domain.Tests`: value objects, Money, FeatureSet (BOLT 9), TLV/BigSize models, payloads, CommitmentNumber, CommitmentTransactionModelFactory, BitReader/Writer.
- `NLightning.Application.Tests`: channel handlers (OpenChannel1, FundingCreated) and PeerManager. Moq-heavy.
- `NLightning.Infrastructure.Tests`: crypto providers (`#if CRYPTO_LIBSODIUM` / `#if CRYPTO_NATIVE`), transport (BOLT 8 states/services), TLV converters, MessageService, PeerAddress.
- `NLightning.Infrastructure.Bitcoin.Tests`: builders, output comparer, ECDH, signer, BlockchainMonitor. Fully commented out (0 active tests): `Transactions/FundingTransactionTests.cs`, `Transactions/CommitmentTransactionTests.cs`, `Outputs/{Base,Change,Funding,ToRemote}OutputTests.cs`.
- `NLightning.Infrastructure.Serialization.Tests`: `Messages/*MessageTests.cs` (most wire messages, not all; e.g. no OpenChannel1/AcceptChannel1/FundingCreated tests, and AcceptChannel2 is `AcceptChannel2MessageTypeSerializerTests.cs`). `Helpers/SerializerHelper.cs` wires the real factories. `Vectors/BigSize.txt` holds the BOLT 1 vectors.
- `NLightning.Bolt11.Tests`: invoice model and one test file per tagged field.
- `NLightning.Daemon.Tests`: open-channel client handlers and FeeService (the `serial` collection).
- `NLightning.Integration.Tests`: `BOLT3/`, `BOLT8/` and `BOLT11/` spec vectors. `Docker/` holds live bitcoind+LND tests (LNUnit: `AbcNetworkTests`, `ChannelOpeningFlowTests`); the Postgres/Sqlite/SqlServer tests there are fully commented out. There are also `Fixtures/` and `TestCollections/`. `BOLT10/` is commented out.
- `NLightning.Tests.Utils`: a shared library, not a test project. It holds `Mocks/` (FakeFixedKeyDh, FakeHandshake*, FakeServiceProvider, FakeSha256, FakeTransport), `Vectors/` (AEAD, BOLT 3 Appendix B/C/D/F, BOLT 8 `InitiatorValidKeysVector`; the other BOLT 8 vectors live in `Integration.Tests/BOLT8/Vectors/`) and `PortPoolUtil` (ports 49100-49149).
- `BlazorTests/`: a WASM app plus Playwright tests. They build only with `-c Release.Wasm`/`Debug.Wasm`, and in practice only on linux-x64 because `src/NLightning.Infrastructure/Crypto/Providers/JS/package.json` pins linux-x64 esbuild/rollup.
- `Docker/custom_lnd`: the LND image that the Docker tests build.
- `NLightning.Node.Tests`: an orphan csproj containing only a BOM. It is not in the sln, so ignore it.

## Adding a test (the common case)
1. Put the test in the project that mirrors the src layer, using the same folder path (e.g. `src/NLightning.Infrastructure/Protocol/Tlv/Converters/X.cs` maps to `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/XTests.cs`).
2. Use a file-scoped namespace `NLightning.<Project>.Tests.<Folder>`. Relative `using Domain.X;` directives go after the namespace line.
3. Name tests `Given_X_When_Y_Then_Z`, with `// Arrange / // Act / // Assert` sections. Pass `TestContext.Current.CancellationToken` to async APIs.
4. For a new wire message, add `test/NLightning.Infrastructure.Serialization.Tests/Messages/<Name>MessageTests.cs`. Copy `UpdateAddHtlcMessageTests.cs` and build the serializer through `SerializerHelper`.
5. Spec vectors: shared C# vectors go in `NLightning.Tests.Utils/Vectors/Bolt<N>*Vectors.cs`. Spec tests go in `NLightning.Integration.Tests/BOLT<N>/`. Any text/JSON vector file needs a csproj item with `CopyToOutputDirectory="PreserveNewest"` (Serialization.Tests globs `<None Include="Vectors\*.*" .../>`; Integration.Tests uses `<Content Include="BOLT11/Vectors/ValidInvoices.txt" .../>`, so add `<Content Include="BOLT4/Vectors/*.json" .../>` there).
6. If you need internals, check that the src project's `AssemblyInfo.cs` InternalsVisibleTo lists the test assembly. Domain.Tests, Application.Tests and Daemon.Tests are NOT listed (`src/NLightning.Daemon/AssemblyInfo.cs` names a stale `NLightning.Bolts.Tests`). Internal vectors in Tests.Utils are visible only to the assemblies in `test/NLightning.Tests.Utils/AssemblyInfo.cs`.

## Mocking conventions
- Moq cannot mock `Span`/`ReadOnlySpan` parameters. Use the Fake* class + virtual `byte[]` `ITest*` interface pattern in `Tests.Utils/Mocks`.
- For HttpClient, mock `HttpMessageHandler` with `.Protected().Setup("SendAsync", ...)` (see Daemon.Tests/Services/FeeServiceTests.cs).
- `FakeSha256` returns 32 zero bytes unless its virtual `GetHashAndReset()` is overridden via Moq, so never use it bare where hash correctness matters. `FakeServiceProvider` throws KeyNotFoundException for unregistered types.
- Tests that must run serially need `[CollectionDefinition(Name, DisableParallelization = true)]` plus `[Collection(Name)]`.

## Dependency rules
- Test projects may reference src projects and `NLightning.Tests.Utils`. Nothing in src/ may reference test projects.
- Tests.Utils references only Domain, Infrastructure and Infrastructure.Bitcoin. It transitively brings in LNUnit and Docker.DotNet. Serialization.Tests and Daemon.Tests do not reference it.
- `src/Directory.Build.props` does NOT apply here. Each test csproj declares net10.0 and Nullable itself; all but Daemon.Tests also set `IsTestProject`. New test projects need `xunit.runner.visualstudio` (3.1.5) or `dotnet test` discovers nothing.

## Commands (repo root)
- All tests, as CI runs them (`.github/workflows/dotnet.yml`, minus logger/coverage flags): `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121 && dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'`
- One class: `dotnet test test/NLightning.Domain.Tests/NLightning.Domain.Tests.csproj --filter "FullyQualifiedName~NLightning.Domain.Tests.ValueObjects.BigSizeTests"`
- Application.Tests and Daemon.Tests: `dotnet test` finds 0 tests because xunit.runner.visualstudio is missing. Run `dotnet run --project test/NLightning.Application.Tests -- -class <FQN>` (or `-method '*Name*'`).
- Native crypto: repeat with `-c Release.Native`. Formatting gate: `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.
- Docker tests (need the Docker daemon and internet on first build): `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker"`. The inbound test uses `HOST_ADDRESS`.

## Gotchas
- CI silently skips all 47 tests in Application.Tests and Daemon.Tests. Run them manually whenever you touch Application or Daemon code.
- `!~Docker` is a substring filter, so keep Docker tests in `NLightning.Integration.Tests.Docker` and don't put "Docker" in other test names.
- `PeerAddressTests.Given_HttpAddress_...` needs live DNS and fails offline. That is an environment issue, not a regression.
- The Docker fixtures force-remove containers named miner/alice/bob/carol, build `../../../../Docker/custom_lnd` relative to bin, and share one regtest network across the `regtest` collection, so state carries over between tests. Always `PortPoolUtil.ReleasePort` in Dispose.
- Count active tests with `grep -E '^\s*\[(Fact|Theory)'`, because many files are commented out.

## Onion routing (BOLT 4) hooks
- There are no BOLT 4 tests yet. The existing onion-adjacent tests are `Serialization.Tests/Messages/UpdateAddHtlcMessageTests.cs` (onion is an opaque 1366-byte hex blob), `UpdateFailMalformedHtlcMessageTests.cs`, `UpdateFailHtlcMessageTests.cs` and `Infrastructure.Tests/Protocol/Tlv/Converters/BlindedPathTlvConverterTests.cs`.
- Add the spec onion/failure vectors as `Tests.Utils/Vectors/Bolt4*Vectors.cs` and put the tests in `Integration.Tests/BOLT4/` (mirroring BOLT3/BOLT8). Hop-payload TLV converter tests go under `Infrastructure.Tests/Protocol/Tlv/Converters`, and ChaCha20/HMAC primitives under `Infrastructure.Tests/Crypto` (test both Release and Release.Native). Multi-hop forwarding can reuse `LightningRegtestNetworkFixture` (alice/bob/carol) in the Docker namespace.
- Prerequisites: `Bolt3AppendixCVectors` has no second-stage HTLC-timeout/success tx vectors, and its `ExpectedCommitTx1`..`15` are unreferenced (BOLT 3 tests assert signatures only). `Bolt3AppendixFVectors` (anchors) drive a byte-exact commitment-tx Theory in `Bolt3IntegrationTests` (no HTLC second-stage vectors yet).
