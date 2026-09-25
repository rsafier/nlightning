# NLightning Repository Map (for agents)

> Status: generated 2026-09-25 against `main` @ `e330fcf` (release v2.0.0 merge).
> Every path is repo-relative. Claims marked **(verified)** were spot-checked directly against the source when this map was written. Everything else comes from per-area research passes that cite file/line. If a claim matters for a change you are about to make, re-check the cited line first, because line numbers drift.

NLightning is a C# / .NET 10 Lightning Network node and library set. It uses a clean-architecture layering (Domain -> Application / Infrastructure.* -> Daemon / Client), though not strictly (see [Layering reality](#23-layering-reality)). Today the node can: speak BOLT 8, exchange BOLT 1 init/ping/error/warning, open single-funded (v1) channels end to end against LND (open_channel -> funding -> channel_ready), manage an on-chain wallet via bitcoind RPC+ZMQ, and encode/decode BOLT 11 invoices. It has a standalone BOLT 4 onion library (Sphinx construct/peel, hop payloads, replay cache; M1+M2) that nothing calls yet. It **cannot** yet move HTLCs, close channels, reestablish, gossip, or forward/fail onions.

---

## Contents

1. [Quick start for agents](#1-quick-start-for-agents)
2. [Solution layout and project dependency graph](#2-solution-layout-and-project-dependency-graph)
3. [Per-project maps](#3-per-project-maps)
4. [End-to-end flow walkthrough](#4-end-to-end-flow-walkthrough)
5. [BOLT coverage matrix](#5-bolt-coverage-matrix)
6. [Onion routing (BOLT 4) readiness](#6-onion-routing-bolt-4-readiness)
7. [How-to recipes](#7-how-to-recipes)
8. [Tests map](#8-tests-map)
9. [Build, CI, tooling](#9-build-ci-tooling)
10. [Consolidated TODO / gap / bug table](#10-consolidated-todo--gap--bug-table)
11. [Cross-cutting gotchas](#11-cross-cutting-gotchas)

---

## 1. Quick start for agents

| Task | Command (run from repo root) |
|---|---|
| Build (CI-equivalent) | `dotnet build --configuration Release -p:MSBuildWarningsAsMessages=MSB4121` |
| Build native-crypto variant | `dotnet build --configuration Release.Native -p:MSBuildWarningsAsMessages=MSB4121` |
| Format gate (hard CI gate) | `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` |
| Tests (CI-equivalent) | `dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'` |
| One VSTest test | `dotnet test test/<Proj>/<Proj>.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"` (keep `-c Release`: the build above is Release-only, and stale `bin/Debug` output would otherwise be tested silently) |
| Application / Daemon tests | `dotnet run --project test/NLightning.Application.Tests` (and `...Daemon.Tests`). Selectors: `-- -class <FQN>` or `-- -method '*Name*'` |
| Run node | `dotnet run --project src/NLightning.Daemon -- --network regtest` (first run writes `~/.nltg/regtest/appsettings.json`) |
| Run CLI | `dotnet run --project src/NLightning.Client -- --network regtest info` |

Baseline observed on macOS arm64 with SDK 10.0.103:
- Debug and Release build with 0 errors. Release.Native is also OK.
- About 592 warnings, nearly all NuGet NU1902/NU1903 advisories. Only about 20 are real `warning CS86xx`.
- Format check passes.
- The CI-style test run gives **883 pass / 1 fail**. The failure is environmental: `PeerAddressTests.Given_HttpAddress_...` does live DNS on `dnstest.nlightn.ing`.
- Application.Tests 24/24 and Daemon.Tests 23/23 pass when run through the xunit v3 exe.
- `Release.Wasm` fails on macOS because `src/NLightning.Infrastructure/Crypto/Providers/JS/package.json` pins linux-x64 esbuild/rollup. It builds only on linux-x64, which is what CI uses.

**Traps to know before editing anything:**
- `dotnet test` silently finds **0 tests** in `test/NLightning.Application.Tests` and `test/NLightning.Daemon.Tests` because their csproj lacks `xunit.runner.visualstudio` **(verified)**. A green CI run says nothing about those 47 tests.
- Crypto code is compiled three times (`CRYPTO_LIBSODIUM`, `CRYPTO_NATIVE`, `CRYPTO_JS`). Build `Release` **and** `Release.Native` after touching `src/NLightning.Infrastructure/Crypto`.
- `.editorconfig` sets many IDE and naming rules to `error`, but `EnforceCodeStyleInBuild` is off. So the build can be green while `dotnet format` fails. Run the format command before finishing.
- Namespace style: file-scoped namespace **first**, then `using Domain.X;` directives written relative to `NLightning` and placed **after** it. System/Microsoft usings go above. Private fields are `_camel`, private static fields `s_camel`.
- Test naming: `Given_X_When_Y_Then_Z` (xUnit v3 + Moq).

---

## 2. Solution layout and project dependency graph

`NLightning.sln` holds 27 C# projects and 6 solution configs: Debug, Release, Debug.Native, Release.Native, Debug.Wasm, Release.Wasm. `global.json` pins SDK 10.0.0 (rollForward latestMinor). `src/Directory.Build.props` sets net10.0, Nullable, ImplicitUsings, Deterministic and package metadata **for src/ only**. Test projects re-declare these settings.

### 2.1 src/ projects

| Project | TFM | Version | Role |
|---|---|---|---|
| `src/NLightning.Domain` | net10.0 | 2.0.0 | Pure model and ports. No NuGet dependencies and no project references **(verified)** |
| `src/NLightning.Infrastructure` | net10.0 | 2.0.0 | Crypto (3 backends), BOLT 8 transport, per-peer services, TLV converters, protocol services |
| `src/NLightning.Infrastructure.Bitcoin` | net10.0 | 1.0.0 | NBitcoin: tx builders, scripts, signer, key manager, ECDH, wallet, chain monitor, fee service |
| `src/NLightning.Infrastructure.Serialization` | net10.0 | — | Wire (de)serializers: messages, payloads, TLV, BigSize, value objects |
| `src/NLightning.Infrastructure.Persistence` | net10.0 | — | EF Core 10 model (`NLightningDbContext`), entities, configs, DI provider selection |
| `src/NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}` | net10.0 | — | Per-provider migrations assemblies |
| `src/NLightning.Infrastructure.Repositories` | net10.0 | — | `UnitOfWork`, DB repositories, in-memory channel and UTXO repositories |
| `src/NLightning.Application` | net10.0 | 1.0.0 | `PeerManager`, `ChannelManager`, BOLT 2 v1 open handlers, `MessageFactory` |
| `src/NLightning.Bolt11` | net10.0 | 5.0.0 | BOLT 11 invoice library (only test projects consume it) |
| `src/NLightning.Daemon` | net10.0 | 0.0.1 | Executable host and composition root, IPC server |
| `src/NLightning.Daemon.Contracts` | **net9.0** | — | Paths, CLI parsing helpers, `IControlClient` (unused) |
| `src/NLightning.Daemon.Plugins` | **net9.0** | — | Plugin API (`IDaemonPlugin`, `IDaemonContext`), not wired up |
| `src/NLightning.Transport.Ipc` | net10.0 | — | IPC envelope, DTOs, MessagePack formatters |
| `src/NLightning.Client` | net10.0 | — | CLI (`nltg` per the usage text; the assembly is `NLightning.Client`) |

### 2.2 Project reference graph (from csproj `ProjectReference`, verified)

```mermaid
graph TD
  Domain[NLightning.Domain]
  Infra[NLightning.Infrastructure]
  Btc[NLightning.Infrastructure.Bitcoin]
  Ser[NLightning.Infrastructure.Serialization]
  Pers[NLightning.Infrastructure.Persistence]
  PPg[Persistence.Postgres]
  PSq[Persistence.Sqlite]
  PSs[Persistence.SqlServer]
  Repo[NLightning.Infrastructure.Repositories]
  App[NLightning.Application]
  B11[NLightning.Bolt11]
  Contracts["NLightning.Daemon.Contracts (net9)"]
  Plugins["NLightning.Daemon.Plugins (net9)"]
  Ipc[NLightning.Transport.Ipc]
  Client[NLightning.Client]
  Daemon[NLightning.Daemon]

  Infra --> Domain
  Btc --> Infra
  Ser --> Domain
  Ser --> Infra
  Pers --> Domain
  PPg --> Pers
  PSq --> Pers
  PSs --> Pers
  Repo --> Domain
  Repo --> Pers
  App --> Domain
  App --> Infra
  App --> Btc
  B11 --> Infra
  B11 --> Btc
  Plugins --> Contracts
  Ipc --> Contracts
  Ipc --> Domain
  Client --> Contracts
  Client --> Ipc
  Client --> Domain
  Daemon --> App
  Daemon --> Client
  Daemon --> Plugins
  Daemon --> Repo
  Daemon --> Btc
  Daemon --> PPg
  Daemon --> PSq
  Daemon --> PSs
  Daemon --> Ser
  Daemon --> Infra
  Daemon --> Contracts
```

### 2.3 Layering reality

- **Application references Infrastructure and Infrastructure.Bitcoin directly.** `IBlockchainMonitor`, `IBitcoinWalletService`, the tx builders, `ITcpService` and `PeerAddress` all come from Infrastructure namespaces. New Application code can use those, but prefer declaring new ports in Domain.
- `Transport.Ipc -> Daemon.Contracts` is declared but no source file uses it.
- Nothing in `src/` references `NLightning.Bolt11`.
- DI is spread across projects. `ITlvConverterFactory` (whose class lives in Infrastructure) is registered in `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs:41`. `IEcdh` is registered at `:35` in the same file. Signer, key manager, fee service and the Domain factories are registered only in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`, and they are **mirrored by hand** in the Docker integration tests.

### 2.4 DI entry points (the composition root is `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`)

| Extension method | File | Registers |
|---|---|---|
| `AddApplicationServices` | `src/NLightning.Application/DependencyInjection.cs` | Singletons: `IChannelManager`, `IMessageFactory`, `IPeerManager`. Every `IChannelMessageHandler<>` is registered Scoped via reflection, plus `FundingConfirmedMessageHandler` **(verified)** |
| `AddInfrastructureServices` | `src/NLightning.Infrastructure/DependencyInjection.cs` | Singletons: `IChannelIdFactory`, `IMessageServiceFactory`, `IPeerServiceFactory`, `ITcpService`, `ISha256`, `ITransportServiceFactory`. Transient: `IPingPongService` |
| `AddBitcoinInfrastructure` | `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs` | Singletons: `IBitcoinChainService`, `IBlockchainMonitor`, `ICommitmentKeyDerivationService`, `ICommitmentTransactionBuilder`, `IEcdh`, `IFundingOutputBuilder`, `IFundingTransactionBuilder`, `IKeyDerivationService`, `ITlvConverterFactory`. Scoped: `IBitcoinWalletService` |
| `AddSerializationInfrastructureServices` | `src/NLightning.Infrastructure.Serialization/DependencyInjection.cs` | Singletons: `IFeatureSetSerializer`, `IMessageSerializer`, `IMessageTypeSerializerFactory`, `IPayloadSerializerFactory`, `ITlvSerializer`, `ITlvStreamSerializer`, `IValueObjectSerializerFactory` (`DependencyInjection.cs:24-30`) |
| `AddPersistenceInfrastructureServices` | `src/NLightning.Infrastructure.Persistence/DependencyInjection.cs` | `NLightningDbContext` (provider selected from `Database:Provider`) |
| `AddRepositoriesInfrastructureServices` | `src/NLightning.Infrastructure.Repositories/DependencyInjection.cs` | `IUnitOfWork` (scoped), `IChannelMemoryRepository` and `IUtxoMemoryRepository` (singletons) |
| Daemon-only | `NodeServiceExtensions.cs` ~L96-141 | `ISecureKeyManager` instance, `FeeService` (HttpClient), `LocalLightningSigner`, `ChannelOpenValidator`, `ChannelFactory`, `CommitmentTransactionModelFactory`, `FundingTransactionModelFactory`, the IPC stack |

Not registered anywhere: `DustService`, `InteractiveTransactionService`, `PluginLoaderService`, `RevocationWatchDbRepository`. Individual `*DbRepository` classes are also not registered; reach them only through `IUnitOfWork`.

---

## 3. Per-project maps

### 3.1 NLightning.Domain (`src/NLightning.Domain`)

**Purpose:** a library-free domain model plus ports. It has no serialization logic and no NBitcoin. `AssemblyInfo.cs` grants InternalsVisibleTo to exactly: DynamicProxyGenAssembly2 (Moq), Application, Infrastructure, Infrastructure.Blazor, Infrastructure.Bitcoin, Infrastructure.Serialization, Infrastructure.Serialization.Tests, Infrastructure.Tests and Tests.Utils (`AssemblyInfo.cs:3-11`). Infrastructure.Persistence and Infrastructure.Repositories are **not** granted. **`NLightning.Domain.Tests` is NOT in that list.**

**Folders:**

| Folder | Contents / key types |
|---|---|
| `Protocol/Constants` | `MessageTypes` (ushort enum). `TlvConstants` (flat class; values **collide across messages**: 0 = UpfrontShutdownScript/BlindedPath/NextFunding/FundingOutputContribution, 1 = Networks/ChannelType/FeeRange/ShortChannelId). `ChainConstants` (main/testnet/regtest; **no signet/testnet4**). `InteractiveTransactionConstants`, `NetworkConstants` |
| `Protocol/Messages` | `BaseMessage`, `BaseChannelMessage`, plus 32 sealed messages (Init, Error, Warning, Ping, Pong, Stfu, OpenChannel1/2, AcceptChannel1/2, FundingCreated/Signed, ChannelReady, Shutdown, ClosingSigned, Tx*, UpdateAddHtlc, UpdateFulfill/Fail/FailMalformedHtlc, CommitmentSigned, RevokeAndAck, UpdateFee, ChannelReestablish) |
| `Protocol/Payloads` | One `*Payload` per message. `ErrorPayload` is shared with Warning. `PlaceholderPayload` is internal and its `ChannelId` throws |
| `Protocol/Tlv` | `BaseTlv` plus `BlindedPathTlv`, `ChannelTypeTlv`, `FeeRangeTlv`, `FundingOutputContributionTlv`, `NetworksTlv` (file `NetworksTLV.cs`), `NextFundingTlv`, `RemoteAddressTlv`, `RequireConfirmedInputsTlv` (file `RequireConfirmedInputsTLV.cs`), `ShortChannelIdTlv`, `UpfrontShutdownScriptTlv` |
| `Protocol/Models` | `TlvStream` (file `TLVStream.cs`; SortedDictionary, rejects duplicates, no even/odd rule). `CommitmentNumber` (BOLT 3 obscuring, locktime/sequence) |
| `Protocol/ValueObjects` | `BigSize`, `ChainHash`, `BitcoinNetwork` |
| `Protocol/Interfaces` | `IMessage`, `IChannelMessage`, `IMessageFactory`, `IMessageService(+Factory)`, `IPingPongService`, `ITlvConverter(+Factory)`, `ITransportServiceFactory`. The same folder also holds non-wire services: `IKeyDerivationService`, `ICommitmentKeyDerivationService`, `ISecureKeyManager`, `ISecretStorageService(+Factory)`, `IChannelKeySetFactory`, `IChannelIdFactory`, `IDustService` |
| `Protocol/Enums` | `BasepointType`, `HtlcType` (unused) |
| `Channels` | `ChannelModel` (aggregate). `ChannelState` (the numeric order **is** the state machine: None 0, V1Opening 1, V1FundingCreated 2, V1FundingSigned 3, V2Opening 10, ReadyForThem 20, ReadyForUs 21, Open 22, Closing 30, Closed 40, Stale 50). `ChannelKeySetModel`, `ChannelFactory`, `ChannelOpenValidator`, `ChannelConfig`, `ChannelId`, `ShortChannelId`, `Htlc`, `HtlcState`, `HtlcDirection`, `CommitmentKeys`. Repository ports: `IChannelMemoryRepository`, `IChannelDbRepository`, `IHtlcDbRepository`, and others |
| `Bitcoin` | Value objects (`TxId`, `BitcoinScript`, `Witness`, `SignedTransaction`, `BlockchainState`, ...). Ports: `ILightningSigner`, `IFeeService`, `IUtxoMemoryRepository`, DB repositories, `ISignatureValidator` (unused). `Transactions/`: `CommitmentTransactionModelFactory`, `FundingTransactionModelFactory`, the `*Model` classes, `*OutputInfo`, and `WeightConstants`/`TransactionConstants`. `PenaltyTransactionModel` is empty |
| `Money` | `LightningMoney` (msat, **mutable reference class**; implicit `long/ulong` means **msat**) |
| `Enums` | `Feature` (value = **odd** bit), `FeatureSupport`, `ChannelFlag`, `LightningMoneyUnit` |
| `Node` | `FeatureSet` (BOLT 9), `Options/FeatureOptions`, `Options/NodeOptions`, `IPeerManager`, `IPeerService`, `IPeerServiceFactory`, `IPeerCommunicationService`, `PeerModel`, events |
| `Crypto` | `CryptoConstants`, `ISha256` (the only crypto port in Domain), `CompactPubKey`, `PrivKey`, `Hash`, `Secret`, `CompactSignature`, `CryptoKeyPair` |
| `Serialization/Interfaces` | `IMessageSerializer`, the `IMessageTypeSerializer`/`IPayloadSerializer`/`ITlvSerializer`/`IValueObjectTypeSerializer` families and their factories |
| `Transport` | `ITransport` (**internal**, Noise CipherState pair). `ITransportService` (public) |
| `Persistence/Interfaces/IUnitOfWork.cs` | Aggregate of all DB repositories |
| `Client` | `ClientCommand` enum (0..7), `ErrorCodes`, request/response models, `ClientException`, `INamedPipeIpcService` |
| `Exceptions` | `ErrorException` -> `ConnectionException`, `ChannelErrorException` -> `SignerException`. `WarningException` -> `ChannelWarningException`. `CriticalException`. `PeerMessage` is the text that goes on the wire |
| `Models` | `RoutingInfo`, `RoutingInfoCollection` (BOLT 11 route hints, maximum 12) |
| `Utils` | `BitReader`/`BitWriter` (MSB-first; used by BOLT 11 and FeatureSet), `ByteArrayExtensions` |
| `Constants/InvoiceConstants.cs` | BOLT 11 prefixes and multipliers |

**Tests:** `test/NLightning.Domain.Tests` (about 140 active facts/theories, 227 test cases). Coverage is thin for `ChannelModel`, `ChannelFactory` and `ChannelOpenValidator`; those are exercised indirectly by Application/Daemon/Integration tests.

### 3.2 NLightning.Infrastructure (`src/NLightning.Infrastructure`)

**Purpose:** symmetric crypto, BOLT 8 transport, per-peer stack, protocol services, TLV converters. It references only Domain. The csproj uses **conditional SDK import** (it switches to Razor for `.Wasm`, where the assembly becomes `NLightning.Infrastructure.Blazor`).

| Area | Key files |
|---|---|
| Crypto backends | `Crypto/Interfaces/ICryptoProvider.cs` (internal: SHA256, AEAD ChaCha20-Poly1305 IETF/X, secure memory, Argon2, RandomBytes; **no raw ChaCha20 stream, no HMAC**). `Crypto/Factories/CryptoFactory.cs` selects the backend at compile time. `Providers/Libsodium/*` (default), `Providers/Native/*` (BouncyCastle/BCL, AOT), `Providers/JS/*` (libsodium.js via JSImport and a Vite bundle) |
| Crypto wrappers | `Crypto/Hashes/Sha256.cs` (implements `ISha256`), `Crypto/Hashes/Argon2Id.cs`, `Crypto/Ciphers/ChaCha20Poly1305.cs`, `Crypto/Ciphers/XChaCha20Poly1305.cs`, `Crypto/Functions/Hkdf.cs` (internal; its HMAC is private and asserts a 32-byte key), `Crypto/Primitives/SecureMemory.cs`, `Crypto/Interfaces/IEcdh.cs` (the port; the implementation is in Bitcoin) |
| BOLT 8 | `Transport/Handshake/States/{HandshakeState,SymmetricState,CipherState}.cs`, `Transport/Handshake/MessagePatterns/*`, `Transport/Encryption/Transport.cs` (framing, rekey handled in CipherState at 1000 nonces), `Transport/Services/{HandshakeService,TransportService,TcpService}.cs`, `Transport/Factories/TransportServiceFactory.cs` |
| Per-peer | `Node/Factories/PeerServiceFactory.cs`, `Node/Services/PeerCommunicationService.cs` (init send, ping/pong, error/warning emission), `Node/Services/PeerService.cs` (init validation, dispatch; TODO to move it to Application), `Node/ValueObjects/ConnectedPeer.cs`, `Node/Models/KeyFileData.cs` |
| Protocol | `Protocol/Services/MessageService.cs` (transport bytes to `IMessage`), `PingPongService.cs`, `SecretStorageService.cs` (BOLT 3 shachain), `DnsSeedClient.cs` (fully commented out). `Protocol/Factories/{ChannelIdFactory,MessageServiceFactory,TlvConverterFactory}.cs`. `Protocol/Tlv/Converters/*` (10 converters). `Protocol/Validators/Tx*Validator.cs` (interactive-tx, partly stubbed). `Protocol/Models/PeerAddress.cs`. `Protocol/Constants/ProtocolConstants.cs` |
| Misc | `Converters/EndianBitConverter.cs` (LE trim/pad semantics are suspect; prefer `BinaryPrimitives`), `Exceptions/*` |

**Tests:** `test/NLightning.Infrastructure.Tests` (crypto, transport, TLV converters, MessageService, PeerAddress). BOLT 8 vectors are in `test/NLightning.Integration.Tests/BOLT8`. `test/NLightning.Infrastructure.Tests/Node/Models/PeerTests.cs` is fully commented out, so PeerService, PeerCommunicationService and TcpService have no unit tests.

### 3.3 NLightning.Infrastructure.Bitcoin (`src/NLightning.Infrastructure.Bitcoin`)

**Purpose:** everything that needs NBitcoin (9.0.5) or NBitcoin.Secp256k1 (3.2.0).

| Area | Key files |
|---|---|
| Builders | `Builders/CommitmentTransactionBuilder.cs`, `Builders/FundingTransactionBuilder.cs`, `Builders/FundingOutputBuilder.cs` |
| Scripts | `Outputs/{BaseOutput,FundingOutput,ToLocalOutput,ToRemoteOutput,ToAnchorOutput,OfferedHtlcOutput,ReceivedHtlcOutput,HtlcResolutionOutput,ChangeOutput}.cs`. `Comparers/TransactionOutputComparer.cs` (BOLT 3 ordering) |
| Keys | `Services/KeyDerivationService.cs` (BOLT 3; **private** `MultiplyPubKey`/`MultiplyPrivateKey`/`AddPubKeys`/`AddPrivateKeys`). `Services/CommitmentKeyDerivationService.cs`. `Managers/SecureKeyManager.cs` (node key = master key; encrypted key file) |
| Signer | `Signers/LocalLightningSigner.cs` (`ILightningSigner`; in-memory per-channel info; `SignWalletTransaction` throws NotImplemented; no HTLC-signature API) |
| Crypto | `Crypto/Functions/Ecdh.cs` (`IEcdh` = SHA256(compressed(k*P)), which is the BOLT 4 shared-secret definition), `Crypto/Contexts/NLightningCryptoContext.cs`, `Crypto/Hashes/Ripemd160.cs` |
| Wallet/chain | `Wallet/BitcoinChainService.cs` (RPC; its constructor makes a **blocking** RPC call), `Wallet/BitcoinWalletService.cs`, `Wallet/BlockchainMonitorService.cs` (ZMQ `rawblock`) |
| Other | `Services/FeeService.cs`, `DustService.cs`, `InteractiveTransactionService.cs` (not wired). `Encoders/Bech32Encoder.cs` (internal, used by Bolt11). `Options/{BitcoinOptions,FeeEstimationOptions}.cs` |
| Dead code | `Transactions/*` (commented out; `PenaltyTransaction` is empty). `Adapters/OutputAdapters/*` (interfaces with no implementations) |

**Tests:** `test/NLightning.Infrastructure.Bitcoin.Tests` (27 active; several files are commented out). BOLT 3 vectors are in `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs`.

### 3.4 NLightning.Infrastructure.Serialization (`src/NLightning.Infrastructure.Serialization`)

**Purpose:** BOLT wire encoding. Layers: `MessageSerializer` -> `*MessageTypeSerializer` -> `*PayloadSerializer` -> value-object, TLV and FeatureSet serializers. Per-type message, payload and value-object serializers are created with `new` inside the factories; DI registers only the seven top-level singletons (see §2.4).

| Area | Key files |
|---|---|
| Top level | `Messages/MessageSerializer.cs` (u16 type; unknown odd returns `null`, unknown even throws `InvalidMessageException`; the generic `DeserializeMessageAsync<T>` **ignores the wire type**) |
| Registries | `Factories/MessageTypeSerializerFactory.cs`, `Factories/PayloadSerializerFactory.cs` (two dictionaries each), `Factories/ValueObjectSerializerFactory.cs` |
| Per-type | `Messages/Types/*` (32), `Payloads/*` (31). File-name oddities: `UpdateAddHtlcMessageSerializer.cs`, `UpdateFufillHtlc*.cs`, `FundingCreatedTypeSerializer.cs` |
| TLV | `Tlv/TlvSerializer.cs`, `Tlv/TlvStreamSerializer.cs` (a **closed type switch** on serialize; `RemoteAddressTlv` is missing from it; reads to end of stream; no ordering or unknown-even checks) |
| Value objects | `ValueObjects/{BigSize,ChainHash,ChannelFlag,ChannelId,ShortChannelId,Witness}TypeSerializer.cs` (BigSize decode is **not canonical-checked**) |
| Node | `Node/FeatureSetSerializer.cs` |

**Tests:** `test/NLightning.Infrastructure.Serialization.Tests` (round trips for 28 message types). There are no serializer tests for OpenChannel1, AcceptChannel1, FundingCreated or FundingSigned. BigSize vectors are in `Vectors/BigSize.txt`, with the non-canonical vectors commented out.

### 3.5 Persistence and Repositories

`src/NLightning.Infrastructure.Persistence`:
- `Contexts/NLightningDbContext.cs` has 9 DbSets: BlockchainStates, WatchedTransactions, WalletAddresses, Utxos, Channels, ChannelConfigs, ChannelKeySets, Htlcs, Peers.
- `Entities/{Bitcoin,Channel,Node}` hold the entities. Their constructors are internal, which is why `InternalsVisibleTo` Repositories exists.
- `EntityConfiguration/*` contains the per-provider tweaks.
- `ValueConverters/*`.
- `DependencyInjection.cs` reads `Database:Provider`/`ConnectionString`.
- `Factories/NLightningContextFactory.cs` is the design-time factory. It reads the env vars `NLIGHTNING_POSTGRES|SQLITE|SQLSERVER`.
- `scripts/add_migration.sh` generates migrations for **all 3 providers** and needs the docker DBs running.
- `RevocationWatchEntity` exists but is **not mapped**.

`src/NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}/Migrations` each hold Initial, AddBlockchaisStateAndWatchedTransaction (typo is intentional history) and AddFieldsForChannelOpen. Postgres uses snake_case naming.

`src/NLightning.Infrastructure.Repositories`:
- `UnitOfWork.cs`: lazy repositories, `GetPeersForStartupAsync`, `AddUtxo`/`TrySpendUtxo`.
- `Database/BaseDbRepository.cs`, `Database/Helpers/PrimaryKeyHelper.cs` (composite keys must be passed as a ValueTuple).
- `Database/{Bitcoin,Channel,Node}/*DbRepository.cs` contain hand-written static `Map*` methods.
- `Memory/ChannelMemoryRepository.cs`: live and temporary channels, `UpgradeChannel`, events `OnChannelUpgraded`/`OnChannelUpdated`.
- `Memory/UtxoMemoryRepository.cs`: coin selection is Branch-and-Bound with a greedy fallback. The confirmation threshold of 3 is hard-coded.

**Tests:** no direct tests. DB coverage comes only from the Docker integration tests. The Sqlite/Postgres/SqlServer smoke tests are commented out.

### 3.6 NLightning.Application (`src/NLightning.Application`)

| File | Role |
|---|---|
| `Node/Managers/PeerManager.cs` | Peer table (a plain `Dictionary`, not thread-safe). Startup reconnect. Inbound/outbound connections. Routes `OnChannelMessageReceived` to `ChannelManager`. Sends replies. `ChannelErrorException` means disconnect; `ChannelWarningException` means send a warning |
| `Channels/Managers/ChannelManager.cs` | Singleton dispatcher. Its switch handles **only** OpenChannel, AcceptChannel, FundingCreated, ChannelReady, FundingSigned; `default` throws `ChannelErrorException("Unknown message type")` **(verified)**. Also handles blockchain events: `HandleFundingConfirmationAsync`, `ForgetStaleChannels`, `ConfirmUnconfirmedChannels` |
| `Channels/Handlers/Interfaces/IChannelMessageHandler.cs` | `Task<IChannelMessage?> HandleAsync(msg, currentState, negotiatedFeatures, peerPubKey)` |
| `Channels/Handlers/OpenChannel1MessageHandler.cs` | Non-initiator: open_channel -> accept_channel |
| `Channels/Handlers/AcceptChannel1MessageHandler.cs` | Initiator: accept_channel -> funding_created |
| `Channels/Handlers/FundingCreatedMessageHandler.cs` | Non-initiator: funding_created -> funding_signed |
| `Channels/Handlers/FundingSignedMessageHandler.cs` | Initiator: funding_signed -> publish funding |
| `Channels/Handlers/FundingConfirmedMessageHandler.cs` | Internal; fires on funding depth -> channel_ready |
| `Channels/Handlers/ChannelReadyMessageHandler.cs` | Peer's channel_ready -> Open |
| `Protocol/Factories/MessageFactory.cs` | `IMessageFactory`; a `Create*` method for every message (no validation) |

**Tests:** `test/NLightning.Application.Tests` covers OpenChannel1, FundingCreated and PeerManager only. It is **invisible to `dotnet test`**.

### 3.7 NLightning.Daemon / Daemon.Contracts / Daemon.Plugins

- `src/NLightning.Daemon/Program.cs`: top-level statements. Order: config, the `--stop`/`--status`/`--help` commands, password, key create/load (`SecureKeyManager`), daemonize, then the host with Serilog, migrations (only if `Database:RunMigrations=true`), then run.
- `Extensions/NodeConfigurationExtensions.cs`: config path defaults to `~/.nltg/{network}/appsettings.json`. Precedence is JSON < env `NLTG_*` < CLI. It also holds the default config template.
- `Extensions/NodeServiceExtensions.cs`: the **composition root**.
- `Services/NltgDaemonService.cs`: the only hosted service. Start order: FeeService, PeerManager, BlockchainMonitor, IPC.
- IPC server:
  - `Services/Ipc/{NamedPipeIpcService,IpcFraming,IpcRouting,CookieFileAuthenticator}.cs`
  - `Ipc/Handlers/*IpcHandler.cs` (one per `ClientCommand`)
  - `Handlers/OpenChannelClientHandler.cs` and `OpenChannelClientSubscriptionHandler.cs` (scoped business flows)
- `Utilities/DaemonUtils.cs` handles daemonization and the PID file. `Services/PluginLoaderService.cs` is **not registered**.
- `src/NLightning.Daemon.Contracts`: `NodeConstants` (nltg.key.json, nltg.pid, nltg.ipc, nltg.cookie), `NodeUtils`, `CommandLineHelper` (shared with the client; has bugs, see §10).
- `src/NLightning.Daemon.Plugins`: API only.

**Tests:** `test/NLightning.Daemon.Tests` (open-channel client handlers, FeeService). It is **invisible to `dotnet test`**. The IPC stack has no tests.

### 3.8 NLightning.Client and NLightning.Transport.Ipc

- `src/NLightning.Client/Program.cs` dispatches on the command. Aliases (every command has a hyphenated spelling): `info|node-info`, `connect|connect-peer`, `listpeers|list-peers`, `getaddress|get-address`, `walletbalance|wallet-balance`, `openchannel|open-channel`.
- `Ipc/NamedPipeIpcClient.cs` opens one pipe per request with a 2 s connect timeout.
- `Handlers/OpenChannelMessageHandler.cs` long-polls the subscription.
- `Printers/*` write the output.
- `src/NLightning.Transport.Ipc/IpcEnvelope.cs` (MessagePack keys 0-5), `Requests/*`, `Responses/*`, `MessagePack/{NLightningMessagePackOptions,NLightningFormatterResolver}.cs`, `MessagePack/Formatters/*`.
- Wire format: a 4-byte host-endian length, then a MessagePack envelope with LZ4BlockArray compression, and a cookie token in `AuthToken`. The pipe is `<dataDir>/nltg.ipc`, which is a Unix socket on Unix.
- **Tests:** none.

### 3.9 NLightning.Bolt11 (`src/NLightning.Bolt11`)

- `Models/Invoice.cs` is the aggregate: `Decode`, `Encode(Key)` and `Encode()` via `ISecureKeyManager`.
- `Models/TaggedFieldList.cs`, `Models/TaggedFields/*` (p, r, 9, x, f, d, s, n, h, c, m), `Factories/TaggedFieldFactory.cs`, `Services/InvoiceValidationService.cs`, `Enums/TaggedFieldTypes.cs`.
- Uses `Bech32Encoder` (internal in Bitcoin, reached via IVT).
- **Tests:** `test/NLightning.Bolt11.Tests` and `test/NLightning.Integration.Tests/BOLT11` (spec vectors in `Vectors/ValidInvoices.txt`). A Blazor smoke test also exists.

---

## 4. End-to-end flow walkthrough

### 4.1 Node startup

1. `src/NLightning.Daemon/Program.cs` runs `NodeConfigurationExtensions.ReadInitialConfiguration(args)`, which resolves the network and config path and writes the default JSON if it is missing.
2. It gets the password (`--password` or `ConsoleUtils.ReadPassword`), then calls `SecureKeyManager.FromFilePath(...)` (`src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs`). A new key is created instead if `nltg.key.json` is missing; that needs bitcoind RPC for the birth height.
3. `DaemonUtils.StartDaemonIfRequested`, then `MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options`.
4. `Host.CreateDefaultBuilder().ConfigureNltg(...).ConfigureNltgServices(keyManager, configPath)` builds the DI graph described in §2.4.
5. `MigrateDatabaseIfConfiguredAsync` (`src/NLightning.Daemon/Extensions/DatabaseExtensions.cs`).
6. `NltgDaemonService.ExecuteAsync` starts:
   - `IFeeService.StartAsync`
   - `IPeerManager.StartAsync` (`src/NLightning.Application/Node/Managers/PeerManager.cs`): `uow.GetPeersForStartupAsync`, then reconnect each peer, then `ChannelManager.RegisterExistingChannelAsync` for non-Closed channels (**no channel_reestablish**), then `ITcpService.StartListeningAsync` (`src/NLightning.Infrastructure/Transport/Services/TcpService.cs`, default `127.0.0.1:9735`)
   - `IBlockchainMonitor.StartAsync(HeightOfBirth)` (`src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`): catch-up over RPC, then the ZMQ `rawblock` loop
   - `INamedPipeIpcService.StartAsync` (`src/NLightning.Daemon/Services/Ipc/NamedPipeIpcService.cs`)

### 4.2 Peer connect (BOLT 8)

Outbound (IPC `connect`, or open-channel with an unknown peer):
1. `ConnectPeerIpcHandler` calls `PeerManager.ConnectToPeerAsync(PeerAddressInfo)` (`src/NLightning.Application/Node/Managers/PeerManager.cs:133`; `PeerAddressInfo` is `src/NLightning.Domain/Node/ValueObjects/PeerAddressInfo.cs`). PeerManager builds the Infrastructure `PeerAddress` itself (`:182`) and calls `ITcpService.ConnectToPeerAsync(PeerAddress)` (`:191`).
2. `TcpService.ConnectToPeerAsync` returns a `ConnectedPeer` (`src/NLightning.Infrastructure/Node/ValueObjects/ConnectedPeer.cs`).
3. `PeerServiceFactory.CreateConnectedPeerAsync(pubkey, tcpClient)` (`src/NLightning.Infrastructure/Node/Factories/PeerServiceFactory.cs`) creates `TransportService(isInitiator: true)` via `TransportServiceFactory`, then calls `InitializeAsync`.
4. `TransportService` (`src/NLightning.Infrastructure/Transport/Services/TransportService.cs`) drives `HandshakeService` -> `HandshakeState` (`Transport/Handshake/States/HandshakeState.cs`): write Act 1 (50 B), read Act 2 (50 B), write Act 3 (66 B). ECDH goes through `IEcdh` -> `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs`. The key schedule uses `SymmetricState`/`Hkdf`. The static key is `ISecureKeyManager.GetNodeKeyPair()`.
5. `HandshakeState.Split()` produces `Encryption.Transport` (`Transport/Encryption/Transport.cs`) with two `CipherState`s (rekey every 1000 nonces).
6. `TransportService` starts the `ReadResponseAsync` loop. It reads the 18-byte header and then the body with `ReadAsync`, not `ReadExactlyAsync`, which is a known bug **(verified, lines 267/291)**. It raises `MessageReceived(MemoryStream)`.

Inbound: `TcpService` accept loop -> `OnNewPeerConnected` -> `PeerManager.HandleNewPeerConnected` -> `PeerServiceFactory.CreateConnectingPeerAsync` -> `TransportService(isInitiator: false)`: read Act 1, write Act 2, read Act 3. `RemoteStaticPublicKey` is the authenticated node id.

### 4.3 init (BOLT 1)

1. `PeerServiceFactory` builds `MessageServiceFactory.CreateMessageService(transport)` -> `MessageService` (`src/NLightning.Infrastructure/Protocol/Services/MessageService.cs`), plus a transient `PingPongService`, `PeerCommunicationService` and `PeerService`.
2. The `PeerService` constructor synchronously runs `PeerCommunicationService.InitializeAsync`. That sends `init`, built from `IMessageFactory.CreateInitMessage` with `FeatureOptions.GetNodeFeatures()` plus the networks TLV, and arms the init-receive timeout (`NodeOptions.NetworkTimeout`).
3. Receive path: `TransportService.MessageReceived` -> `MessageService.ReceiveMessage` -> `IMessageSerializer.DeserializeMessageAsync` (`src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs`, `InitMessageTypeSerializer`, `FeatureSetSerializer`) -> `PeerCommunicationService.HandleMessageReceived` -> `PeerService.HandleMessage` (`src/NLightning.Infrastructure/Node/Services/PeerService.cs`).
4. `PeerService` requires init first. It checks `FeatureSet.IsCompatible` (`src/NLightning.Domain/Node/FeatureSet.cs`), the networks TLV chain hashes (stricter than the spec: **any** unknown chain rejects), and the remote_addr TLV. On failure it disconnects without sending an error.
5. After init, ping/pong keepalive starts (`src/NLightning.Infrastructure/Protocol/Services/PingPongService.cs`: random 30-300 s). `IChannelMessage`s go to `OnChannelMessageReceived`, and error/warning to `OnAttentionMessageReceived`. **Everything else (gossip, stfu) is dropped.**

### 4.4 open_channel / accept_channel

**Initiator (us), triggered by the CLI:**
1. `nltg openchannel <node> <sats>` -> `NamedPipeIpcClient.OpenChannelAsync` -> `OpenChannelIpcHandler` (`src/NLightning.Daemon/Ipc/Handlers/OpenChannelIpcHandler.cs`) -> scoped `OpenChannelClientHandler` (`src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`).
2. It connects if needed, checks the balance (`IUtxoMemoryRepository`), then calls `ChannelFactory.CreateChannelV1AsInitiatorAsync` (`src/NLightning.Domain/Channels/Factories/ChannelFactory.cs`). This gets keys from `ILightningSigner.CreateNewChannel`, a temp id from `IChannelIdFactory.CreateTemporaryChannelId`, and returns state `V1Opening`.
3. `UtxoMemoryRepository.LockUtxosToSpendOnChannel`, then `ChannelMemoryRepository.AddTemporaryChannel`.
4. `IMessageFactory.CreateOpenChannel1Message` (with `ChannelTypeTlv` and `UpfrontShutdownScriptTlv`) -> `IPeerService.SendMessageAsync` -> ... -> `TransportService.WriteMessageAsync`.
5. It awaits `IChannelMemoryRepository.OnChannelUpgraded`, or an error/disconnect.

**Non-initiator (peer opens to us):**
1. `PeerService` -> `PeerManager.HandlePeerChannelMessage` -> `ChannelManager.HandleChannelMessageAsync` -> `OpenChannel1MessageHandler` (`src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs`).
2. `ChannelFactory.CreateChannelV1AsNonInitiatorAsync` runs `ChannelOpenValidator` (`src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs`) optional and mandatory checks, the key sets, and `CommitmentNumber(remote, local)`.
3. `AddTemporaryChannel`, then it returns `accept_channel` (`MessageFactory.CreateAcceptChannel1Message`), and `PeerManager` sends it.

**Initiator receives accept_channel:** `AcceptChannel1MessageHandler` (`src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs`):
1. Validates, then `AddRemoteKeySet`, `UpdateChannelConfig`, and `CommitmentNumber(local, remote)`.
2. Builds the funding transaction (see §4.5).
3. Derives the channel id with `ChannelIdFactory.CreateV1(txid, vout)`.
4. Calls `signer.RegisterChannel`, signs the remote commitment, and moves state to `V1FundingCreated`.
5. `UpgradeChannel` fires `OnChannelUpgraded`, which the daemon client awaits.
6. Replies with `funding_created`, which still carries the **temporary** id.

### 4.5 Funding

**Funder (inside AcceptChannel1MessageHandler):**
1. `IFundingTransactionModelFactory.Create` (`src/NLightning.Domain/Bitcoin/Transactions/Factories/FundingTransactionModelFactory.cs`: fee and change) -> `FundingTransactionBuilder.Build` (`src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs`; the funding output is at index 0; `FundingOutputBuilder` builds the 2-of-2 P2WSH).
2. `ICommitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, Remote)` (`src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs`) -> `CommitmentTransactionBuilder.Build` (`src/NLightning.Infrastructure.Bitcoin/Builders/CommitmentTransactionBuilder.cs`) -> `LocalLightningSigner.SignChannelTransaction` (`src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs`).

**Fundee receives funding_created:** `FundingCreatedMessageHandler`:
1. Sets the funding outpoint, derives the new channel id, calls `RegisterChannel`, and builds the local and remote commitments.
2. `ValidateSignature` on their signature, then signs theirs.
3. Moves to `V1FundingSigned`, then persists: `uow.ChannelDbRepository.AddAsync` + `SaveChangesAsync` (`src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`).
4. `AddChannel` and removes the temp channel.
5. `IBlockchainMonitor.WatchTransactionAsync(minimum_depth)`, then replies with `funding_signed`.

**Funder receives funding_signed:** `FundingSignedMessageHandler` requires `V1FundingCreated`. It validates the signature, calls `SignFundingTransaction` (P2WPKH/P2TR inputs), persists, calls `IBlockchainMonitor.PublishAndWatchTransactionAsync`, moves to `V1FundingSigned`, and persists again. It sends no reply.

### 4.6 channel_ready

1. `BlockchainMonitorService` (ZMQ `rawblock`) -> `ProcessBlock` -> `CheckBlockForWatchedTransactions` -> `CheckWatchedTransactionsDepth` -> `OnTransactionConfirmed`.
2. `ChannelManager.HandleFundingConfirmationAsync` sets `FundingCreatedAtBlockHeight` and `ShortChannelId(height, txIndex, vout)`. **The txIndex is wrong**; see §10.
3. It runs the scoped `FundingConfirmedMessageHandler` (`src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs`). That increments `CommitmentNumber`, derives the next per-commitment point, and creates optional SCID aliases (2-5). It moves `V1FundingSigned` to `ReadyForUs`, or `ReadyForThem` to `Open`, then persists.
4. Its `OnMessageReady(channel_ready)` goes to `ChannelManager.OnResponseMessageReady` -> `PeerManager.HandleResponseMessageReady` -> `peerService.SendMessageAsync`. One message is sent per alias.
5. The peer's `channel_ready` goes to `ChannelReadyMessageHandler` (`src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`), which stores their second per-commitment point and moves `V1FundingSigned` to `ReadyForThem`, or `ReadyForUs` to `Open`.
6. The daemon's `OpenChannelClientSubscriptionHandler` completes when it sees `ReadyForUs`/`ReadyForThem`. The CLI stops polling.

### 4.7 What happens next (not implemented)

An inbound `update_add_htlc`, `commitment_signed`, `revoke_and_ack`, `shutdown`, `channel_reestablish` and so on reaches `ChannelManager`'s `default` branch -> `ChannelErrorException` -> `PeerManager` **disconnects the peer**.

---

## 5. BOLT coverage matrix

> Full matrix: [`docs/agents/BOLT_COVERAGE.md`](BOLT_COVERAGE.md).

| BOLT | Status | Where / notes |
|---|---|---|
| 1 messaging | Mostly done | init/error/warning/ping/pong (`Domain/Protocol/Messages`, `Infrastructure/Node/Services/*`). Gaps: BigSize canonical check, TLV ordering and unknown-even checks, pong >= 65532 rule, remote_addr TLV not sent (`FeatureOptions.cs:241`), no peer_storage |
| 2 peer protocol | Partial | v1 open -> channel_ready works E2E against LND (`test/NLightning.Integration.Tests/Docker/ChannelOpeningFlowTests.cs`). Wire model and serializers exist for v2/interactive-tx, shutdown/closing_signed, HTLC updates, commitment_signed, revoke_and_ack, update_fee, reestablish and stfu, but **there are no handlers**. option_simple_close and splicing are missing |
| 3 transactions | Mostly done | Funding and commitment builders, scripts, key derivation and shachain are vector-tested (non-anchor only). Missing: HTLC-success/timeout second-stage txs, closing tx, HTLC signatures. The anchor fee path is suspect (1116 vs 1124 weight; only one anchor deducted) |
| 4 onion | **Partial (M1+M2)** | Sphinx construct/peel (`src/NLightning.Infrastructure.Bitcoin/Onion/`), hop payload model/serializer/validator, failure codes, in-memory replay cache. No error onions, forwarding or HTLC wiring. See §6 and `ONION_ROUTING_PLAN.md` |
| 5 on-chain | Missing / stub | `PenaltyTransactionModel` empty. `IRevocationWatchDbRepository` empty. Revocation watch commented out in `BlockchainMonitorService.cs` |
| 7 gossip | Missing | Only enum values 256-259. Incoming 256/258 (even) **throw** `InvalidMessageException`. Gossip feature bits are advertised anyway |
| 8 transport | Done | Vector-tested. Read-loop partial-read bug and send-ordering race (§10) |
| 9 features | Mostly done | `FeatureSet`, `FeatureOptions`. Dependency table is incomplete. `OptionRouteBlinding`/`OptionAttributionData` are advertised as Optional (`FeatureOptions.cs:55,69`) without an implementation |
| 10 DNS seed | Stub | `DnsSeedClient.cs` commented out |
| 11 invoices | Library done | `src/NLightning.Bolt11`, not wired into the node. Gaps: feature-bit check TODO (`Invoice.cs:553`), only one `r` field kept, no taproot fallback |
| 12 offers | Missing | — |

---

## 6. Onion routing (BOLT 4) readiness

> Implementation plan: [`docs/agents/ONION_ROUTING_PLAN.md`](ONION_ROUTING_PLAN.md) (authoritative for task order). Full matrix: [`docs/agents/BOLT_COVERAGE.md`](BOLT_COVERAGE.md).

> **Status (wip/fafo):** M1 and M2 of the plan are done: every primitive in §6.2 exists, and the Domain types and Sphinx construct/peel of §6.3 exist (except failure message models and error-packet wrap/unwrap, which are M3). The tables below describe the pre-M1 state and are kept for reference; see `ONION_ROUTING_PLAN.md` §3 and §5 for what was built and where.

The onion code was re-implemented from the spec; the legacy LNBolt code was only used as a reference (see `LNBOLT_REVIEW.md`). This section lists the hooks that existed before M1 and what had to be added.

### 6.1 Existing hooks

| Hook | Path | State |
|---|---|---|
| onion packet on update_add_htlc | `src/NLightning.Domain/Protocol/Payloads/UpdateAddHtlcPayload.cs` (`ReadOnlyMemory<byte>? OnionRoutingPacket`) | Optional raw bytes. The serializer reads 1366 bytes only if they remain (`src/NLightning.Infrastructure.Serialization/Payloads/UpdateAddHtlcPayloadSerializer.cs:67` **verified**) |
| blinded path_key TLV | `src/NLightning.Domain/Protocol/Tlv/BlindedPathTlv.cs` + `src/NLightning.Infrastructure/Protocol/Tlv/Converters/BlindedPathTlvConverter.cs` | Works. The message serializer looks it up with `TlvConstants.UpfrontShutdownScript` (same value 0) |
| failure payloads | `Domain/Protocol/Payloads/UpdateFailHtlcPayload.cs` (opaque `Reason`), `UpdateFailMalformedHtlcPayload.cs` (`Sha256OfOnion`, raw `ushort FailureCode`) | No failure-code enum |
| factory builders | `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` (`CreateUpdateAddHtlcMessage` ~L673, cannot attach a BlindedPathTlv) | Called nowhere |
| ECDH shared secret | `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs` via `IEcdh.SecP256K1Dh` | Matches the BOLT 4 `ss` definition |
| Node key | `ISecureKeyManager.GetNodeKeyPair()` (`src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs`) | `ILightningSigner` exposes only the pubkey |
| EC tweak-mul | `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs` (`MultiplyPubKey`, `MultiplyPrivateKey`, private) | Must be extracted |
| SHA256 | `src/NLightning.Infrastructure/Crypto/Hashes/Sha256.cs` | Ready (allocates per instance; reuse instances) |
| AEAD ChaCha20-Poly1305 with zero nonce | `src/NLightning.Infrastructure/Crypto/Ciphers/ChaCha20Poly1305.cs` (nonce 0) | Usable for route-blinding `encrypted_recipient_data` |
| HTLC storage | `Domain/Channels/ValueObjects/Htlc.cs` (holds the full AddMessage), `Infrastructure.Persistence/Entities/Channel/HtlcEntity.cs` (`AddMessageBytes`) | Onion persisted implicitly; no shared secret or forward link |
| Feature bits | `src/NLightning.Domain/Enums/Feature.cs` (VarOnionOptin 9, PaymentSecret 15, BasicMpp 17, OptionRouteBlinding 25, OptionAttributionData 37, OptionOnionMessages 39 (FeatureOptions default No), OptionPaymentMetadata 49) | Defined |
| Invoice inputs | `src/NLightning.Bolt11/Models/Invoice.cs` (PaymentHash, PaymentSecret, Amount, MinFinalCltvExpiry, RoutingInfos, PayeePubKey, Metadata) | Not referenced by any src project |

### 6.2 Missing primitives

1. **Raw ChaCha20 keystream** (96-bit zero nonce, counter 0). Add it to `ICryptoProvider` in all 3 backends: libsodium `crypto_stream_chacha20_ietf_xor`, JS sumo `crypto_stream_chacha20_ietf_xor`, and Native BouncyCastle `ChaCha7539Engine`. Do not use the AEAD, whose keystream starts at counter 1. `Providers/Native/Ciphers/ChaCha20.cs` contains only `QuarterRound`.
2. **HMAC-SHA256 with any key length.** The labels are rho/mu/um/pad/ammag/ammagext/fulfillment/blinded_node_id. The only HMAC today is the private `Hkdf.HmacHash`, which asserts a 32-byte key.
3. **Public EC scalar math** (pubkey*scalar, privkey*scalar) behind an injectable interface.
4. **Constant-time HMAC compare**: use `CryptographicOperations.FixedTimeEquals`, not value-object `Equals`.
5. **Truncated ints** (tu16/tu32/tu64) with minimal-encoding checks. They do not exist; `EndianBitConverter`'s trim/pad flags are not spec-compliant.
6. **Canonical BigSize decode** in `BigSizeTypeSerializer`.
7. **Strict TLV streams**: strictly increasing types and rejection of unknown even types. Also a generic fallback in `TlvStreamSerializer` for raw `BaseTlv` records (custom records).

### 6.3 Missing model and protocol layers (suggested placement)

- **Domain** (e.g. `Protocol/Onion/`):
  - `OnionPacket` value object (1 + 33 + 1300 + 32 = 1366 bytes), made mandatory on `UpdateAddHtlcPayload`
  - hop-payload TLVs 2/4/6/8/10/12/16/18 in a **separate constants class**, not `TlvConstants`
  - `encrypted_data_tlv` records 1-14
  - `FailureCode` enum with BADONION/PERM/NODE/UPDATE flags, plus failure message models
  - `ISphinxService` / `IOnionProcessor` / `IFailureObfuscator` ports
- **Infrastructure / Infrastructure.Bitcoin:** Sphinx construct/peel, filler generation (variable-length; the filler length is the sum of shift sizes), error packet wrap/unwrap, converters and serializers.
- **Application:**
  - `IChannelMessageHandler<>` for UpdateAddHtlc, UpdateFulfill/Fail/FailMalformed, CommitmentSigned, RevokeAndAck, UpdateFee and ChannelReestablish, each plus a `case` in `ChannelManager.HandleChannelMessageAsync`
  - `ChannelModel` mutators (it has **no** AddHtlc/fulfill/fail/balance methods today)
  - an HTLC switch or forwarding service that peels **after** the add is irrevocably committed
  - a per-channel ordering lock
- **Persistence (3 migrations each):**
  - per-incoming-HTLC shared secret and blinded flags
  - incoming->outgoing forward link
  - invoice/preimage store
  - outgoing payment attempts
  - replay (seen-HMAC) set
  - SCID -> channel mapping
- **Signer:** HTLC second-stage signatures (`htlc_signatures` in commitment_signed), plus an ECDH-with-node-key method.
- **Daemon/Client:** new `ClientCommand`s (PayInvoice, CreateInvoice, ...) following §7.3.

### 6.4 Prerequisites and blockers to fix first

- The SCID `TransactionIndex` bug in `BlockchainMonitorService.CheckBlockForWatchedTransactions` (~L473-499) and the `ShortChannelId(ulong)` mask bug (`src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs:54-58` **verified**: 0xFFFF / 0xFF should be 0xFFFFFF / 0xFFFF). Hop payloads carry the SCID as a u64.
- The `TransportService` partial-read bug (a 1366-byte onion can span TCP segments), and the encrypt-before-lock ordering race on send.
- The HTLC reload bug (`ChannelDbRepository.MapEntityToDomain`, `byte.Equals(enum)` at ~L201-222 **verified**). Offered and Fulfilled HTLCs are never restored (`h.State.Equals(HtlcState.X)` at :201/:210 is always false). Expired/Failed are restored via a working `byte[]` `Contains` (:219-220), but `htlc.Direction.Equals(HtlcDirection.Outgoing)` (:204/:213/:222) is the same bug, so every restored HTLC lands in the remote lists.
- The BOLT 2 update/commit/revoke state machine, which is a prerequisite for any forwarding.

### 6.5 Test vectors to add

`bolt04/onion-test.json`, `onion-error-test.json`, `route-blinding-test.json`, `blinded-payment-onion-test.json` and `blinded-onion-message-onion-test.json` (from lightning/bolts), plus the inline BOLT 4 "Returning Errors" / attribution traces. Put shared vector classes in `test/NLightning.Tests.Utils/Vectors/Bolt4*.cs` and spec tests in a new `test/NLightning.Integration.Tests/BOLT4/`. Primitive tests go under `test/NLightning.Infrastructure.Tests/Crypto` and must pass under both `Release` and `Release.Native`.

Suggested phases: (1) Sphinx construct/peel plus legacy errors; (2) failure codes and malformed conversion; (3) route blinding; (4) attribution_data; (5) fulfillment_payload; (6) onion messages (type 513; needs a new non-channel dispatch path in `PeerService.HandleMessage` and `IPeerService`, whose send is limited to `IChannelMessage`).

---

## 7. How-to recipes

### 7.1 Add a wire message (end to end)

1. Add the enum value in `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`.
2. Add `src/NLightning.Domain/Protocol/Payloads/XPayload.cs` (implement `IChannelMessagePayload` if it is channel-scoped).
3. Add `src/NLightning.Domain/Protocol/Messages/XMessage.cs` (`BaseChannelMessage` or `BaseMessage`; `new XPayload Payload`; build `Extension` only when TLVs are non-null).
4. For new TLVs: add a constant, a `Domain/Protocol/Tlv/XTlv.cs`, a converter in `src/NLightning.Infrastructure/Protocol/Tlv/Converters`, a registration in `TlvConverterFactory.RegisterConverters`, **and** a case in `TlvStreamSerializer.SerializeAsync`.
   - Deserialize side: `TlvStreamSerializer.DeserializeAsync` returns only raw `BaseTlv` records. In `XMessageTypeSerializer.DeserializeAsync`, extract each typed TLV with `extension.TryGetTlv(<constant>, out var b)` plus `_tlvConverterFactory.GetConverter<XTlv>().ConvertFromBase(b)`, and pass it into a message constructor parameter/property (pattern: `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs` ~66-75). Use the per-message constant, because `TlvConstants` values collide.
5. Add `src/NLightning.Infrastructure.Serialization/Payloads/XPayloadSerializer.cs` and register it in **both** dictionaries of `PayloadSerializerFactory`.
6. Add `Messages/Types/XMessageTypeSerializer.cs` and register it in **both** dictionaries of `MessageTypeSerializerFactory`.
7. Add `Create*` to `IMessageFactory` and to `MessageFactory`.
8. Route it. Channel messages need a handler plus a `case` in `ChannelManager.HandleChannelMessageAsync`. Peer-level messages need a branch in `PeerService.HandleMessage`; otherwise the message is silently dropped. Outbound non-channel messages also need a new send overload on `IPeerService`/`IPeerCommunicationService` (Domain/Node/Interfaces) and on `PeerService`/`PeerCommunicationService`, because `PeerService.SendMessageAsync` accepts only `IChannelMessage` (`src/NLightning.Infrastructure/Node/Services/PeerService.cs:87-89`).
9. Add a round-trip test in `test/NLightning.Infrastructure.Serialization.Tests/Messages`.

### 7.2 Add a channel message handler

Implement `IChannelMessageHandler<TMessage>` in `src/NLightning.Application/Channels/Handlers`; reflection registers it Scoped automatically. Then add a `case MessageTypes.X:` in `ChannelManager` (without it, the peer is disconnected). Throw `ChannelErrorException` to fail the channel or `ChannelWarningException` to warn. Return a message to reply to the sending peer. For messages to *other* peers, raise an event -> `ChannelManager.OnResponseMessageReady`. Tests go in `test/NLightning.Application.Tests` (run them via `dotnet run`).

### 7.3 Add an IPC / CLI command

1. Add the `ClientCommand` value in `src/NLightning.Domain/Client/Enums/ClientCommand.cs` (never renumber).
2. Add `[MessagePackObject]` request/response classes in `src/NLightning.Transport.Ipc/Requests|Responses` (append keys only).
3. Add a formatter for any new domain value object and register it in `NLightningFormatterResolver`.
4. Add an internal `XIpcHandler : IIpcCommandHandler` in `src/NLightning.Daemon/Ipc/Handlers` and register it with `AddSingleton<IIpcCommandHandler, X>()` in `NodeServiceExtensions`. Resolve scoped services through `CreateScope()`. (See §7.7 on mirroring registrations in the Docker E2E fixtures.)
5. Add a client method in `NamedPipeIpcClient`, a printer, `Program.cs` cases for both spellings (`xcmd` and `x-cmd`) and a `ClientUtils.ShowUsage` entry.

### 7.4 Add a feature bit

Add it to `src/NLightning.Domain/Enums/Feature.cs` (**odd** bit), then a `FeatureOptions` property wired into both `GetNodeFeatures()` and `GetNodeOptions()`, plus any dependencies in `FeatureSet.s_featureDependencies`.

### 7.5 Add a DB entity / migration

Add the entity (with an internal constructor), a `Configure<Name>Entity` extension, a DbSet and its registration in `OnModelCreating`. Prerequisite: `dotnet tool install --global dotnet-ef --version 10.0.5` (matches the EF Core packages; no `.config/dotnet-tools.json` manifest exists; `add_migration.sh` calls `dotnet ef` for each provider). `start_sql.sh` runs mssql with `--platform linux/amd64`, so it is emulated on Apple Silicon. Then `cd src/NLightning.Infrastructure.Persistence && ./scripts/start_postgres.sh && ./scripts/start_sql.sh && ./scripts/add_migration.sh CamelCaseName`. Commit all three providers' migrations. Add the domain `I*DbRepository`, the implementation (with its static `Map*` entity<->domain methods), the `IUnitOfWork` property, and the lazy (`??=`) property in `src/NLightning.Infrastructure.Repositories/UnitOfWork.cs`.

### 7.6 Add a crypto primitive

Add the method to `ICryptoProvider`, then implement it in the Libsodium (`LibsodiumWrapper` `[LibraryImport]`), JS (`LibsodiumJsWrapper` `[JSImport]`) and Native providers. Add a public wrapper under `Crypto/<Category>/` that uses `CryptoFactory.GetCryptoProvider()`. Build `Release` and `Release.Native`; `Release.Wasm` builds in CI only.

### 7.7 Register a new service / port

Prefer the layer's `DependencyInjection.cs` (`AddApplicationServices`, `AddSerializationInfrastructureServices`, etc.): the Docker E2E tests call those. If you register it only in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`, you must also mirror it by hand in `test/NLightning.Integration.Tests/Docker/ChannelOpeningFlowTests.cs` (~113-180) and `test/NLightning.Integration.Tests/Docker/AbcNetworkTests.cs` (~106-136), which build their own `ServiceCollection` (manual `ILightningSigner`, `IChannelFactory`, model factories, options); otherwise those tests throw at resolve time.

---

## 8. Tests map

| Project | Covers | Active tests* | Runner |
|---|---|---|---|
| `test/NLightning.Domain.Tests` | Money, FeatureSet, TLV stream, CommitmentNumber, value objects, BitReader/Writer, commitment model factory | 140 attrs / 227 cases | VSTest |
| `test/NLightning.Infrastructure.Tests` | Crypto providers (`#if` per backend), SHA256 NIST vectors, transport, TLV converters (not RemoteAddress), MessageService, PeerAddress (1 DNS test fails offline) | 132 / 127 | VSTest |
| `test/NLightning.Infrastructure.Bitcoin.Tests` | Commitment builder, comparer, ECDH, ToLocal output, signer, blockchain monitor | 27 | VSTest |
| `test/NLightning.Infrastructure.Serialization.Tests` | Message round trips, TLV, BigSize vectors, FeatureSet | 111 / 145 | VSTest |
| `test/NLightning.Bolt11.Tests` | Invoice, tagged fields, validation | 108 / 202 | VSTest |
| `test/NLightning.Integration.Tests` | BOLT 3 (App. B-E), BOLT 8 (App. A), BOLT 11 vectors; `Docker/` E2E with bitcoind + 3 LND | 131 (8 Docker) / 156 non-Docker | VSTest; Docker tests excluded by filter |
| `test/NLightning.Application.Tests` | OpenChannel1, FundingCreated handlers, PeerManager | 24 | **xunit exe only** |
| `test/NLightning.Daemon.Tests` | Open-channel client handlers, FeeService | 23 | **xunit exe only** |
| `test/NLightning.Tests.Utils` | Shared fakes (`FakeFixedKeyDh`, `FakeSha256` (returns zeros!), `FakeServiceProvider`, ...), `PortPoolUtil` (49100-49149), vectors (`Bolt3Appendix*`, AEAD, BOLT 8 keys) | library | — |
| `test/BlazorTests/*` | WASM crypto + Bolt11 via Playwright on :8085 | 6 | Release.Wasm only (linux) |
| `test/NLightning.Node.Tests` | **Orphan**: 3-byte BOM-only csproj, not in sln **(verified)** | 0 | — |

*Active counts are `[Fact]`/`[Theory]` attributes / executed test cases where known.

Docker E2E: `test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs` builds the image from `test/Docker/custom_lnd` (lnd v0.20.0-beta) and starts containers named miner/alice/bob/carol, **force-removing any existing containers with those names**. Run with `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker"`. Inbound tests use `HOST_ADDRESS` (default `host.docker.internal`).

Missing entirely: BOLT 4/5/7/12 tests, IPC and formatter tests, persistence round trips, anchor (Appendix F) and HTLC second-stage vectors.

---

## 9. Build, CI, tooling

- **Configurations and crypto backends** (`src/NLightning.Infrastructure/NLightning.Infrastructure.csproj`): Debug/Release use `CRYPTO_LIBSODIUM` (libsodium 1.0.21). `*.Native` or PublishAot uses `CRYPTO_NATIVE` (Portable.BouncyCastle 1.8.6.7 + Konscious Argon2). `*.Wasm` uses `CRYPTO_JS` (Microsoft.JSInterop 8.0.8 + npm/Vite bundle; assembly renamed `*.Blazor`). `src/NLightning.Bolt11` also renames itself for Wasm.
- **Workflows** (`.github/workflows/`): `dotnet.yml`/`pr.yml` (Release), `dotnet.native.yml`/`pr.native.yml` (Release.Native), `dotnet.wasm.yml`/`pr.wasm.yml` (Blazor), `combined-report.yml`/`pr.combined-report.yml` (merged coverage), `gh-pages.yml` (DocFX from `.docfx/docfx.json`). All run on ubuntu-latest with .NET 10.0.x. No pack/publish workflow, and no Docker tests in CI.
- **Sln mapping bugs:** `NLightning.Infrastructure.Serialization` maps Release.Native to Release.Wasm (`NLightning.sln` ~L283). Newer projects (Repositories, Contracts, Client, Plugins, Transport.Ipc, Application.Tests, Daemon.Tests) map every custom configuration to Debug. Edit mappings by hand when adding projects.
- **Versioning:** SemVer per package. Bump `<Version>`/`<AssemblyVersion>`/`<FileVersion>`, `PackageReleaseNotes` and `src/*/CHANGELOG.md` together. `VERSIONING.md` is stale.
- **Git:** GitHub Flow; `feature/`, `bugfix/`, `release/vX.Y.Z` branches; free-form lowercase commit subjects. `CONTRIBUTING.md` says "master" but the default branch is `main`.
- **Stale tooling:** `scripts/testwithcoverage.sh` lists nonexistent projects and exits on the first one. `.vscode/launch.json` points at `src/NLightning.NLTG/.../net8.0`.

---

## 10. Consolidated TODO / gap / bug table

Severity: **H** = blocks correctness or planned onion work; **M** = real bug or missing feature; **L** = hygiene. "Verified" means checked directly while writing this map.

### 10.1 Correctness bugs

| Sev | File (line) | Issue |
|---|---|---|
| H | `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (~473-499) | Tx index counts only *watched* txs, so every derived SCID is wrong (`ChannelManager.cs` ~317-320) |
| H | `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs:54-58` | `ulong` constructor masks tx index with 0xFFFF and output with 0xFF **(verified)** |
| H | `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs` ~201-222 | `byte.Equals(enum)` is always false (`HtlcEntity.State`/`Direction` are `byte`): Offered and Fulfilled HTLCs are never restored; Expired/Failed are restored but always classified as remote (Direction.Equals bug) **(verified)**. Same pattern in `HtlcDbRepository.cs:63,70` |
| H | `ChannelDbRepository.cs` ~231 | Remote funding pubkey is replaced with the local one on reload |
| H | `ChannelDbRepository.cs` ~237-239 | `CommitmentNumber` is rebuilt as (local, remote) regardless of initiator, giving the wrong obscuring factor for non-initiator channels |
| H | `src/NLightning.Application/Channels/Managers/ChannelManager.cs:198` | `ForgetStaleChannels` has no state filter, and the default `FundingCreatedAtBlockHeight`=0 marks unconfirmed or long-open channels Stale **(verified line)** |
| H | `src/NLightning.Infrastructure/Transport/Services/TransportService.cs:267,291` | `ReadAsync` instead of `ReadExactlyAsync`: a short read kills the connection **(verified)** |
| H | `TransportService.cs` ~201-209 | Encrypts before taking the write semaphore, so concurrent sends can desync nonces |
| M | `src/NLightning.Application/Channels/Managers/ChannelManager.cs` ~231-267 + `FundingConfirmedMessageHandler.cs` ~43-47 | Re-fires confirmation every block: repeated commitment-number increments and repeated channel_ready |
| M | `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs` ~226-242 | Error cleanup is inverted and does not release UTXOs. Lines ~118-120 reject a missing upfront_shutdown_script TLV whenever the feature is negotiated or a channel_type TLV is present (in practice almost always, including against LND) |
| M | `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs:62` | `CurrentPerCommitmentIndex == 0` check likely never true (the index counts down from 2^48-1) |
| M | `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:104` | Push-amount check is off by 1000x. Anchor weight selection is inverted here (~108-110) and in `ChannelFactory.cs:183-185` |
| M | `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs` | Anchor weight 1116 vs spec 1124; only one anchor deducted; to_remote dust uses the wrong party's limit; all HTLCs subtracted from to_local (unverified) |
| M | `src/NLightning.Infrastructure.Bitcoin/Outputs/HtlcResolutionOutput.cs:14 vs :21` | Revocation and delayed keys swapped |
| M | `src/NLightning.Infrastructure.Bitcoin/Outputs/BaseOutput.cs:24` | `Amount` setter is a no-op |
| M | `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs` | Tor v3 decode offsets and DNS (type 5) encode are broken; untested |
| M | `src/NLightning.Infrastructure/Protocol/Services/PingPongService.cs` | Pong timeout path takes `IsCanceled -> continue`, so it never disconnects |
| M | `src/NLightning.Infrastructure/Protocol/Validators/Tx*Validator.cs` | Parity check appears inverted; `TxAddInputValidator.Validate` is `async void` |
| M | `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs` ~57-65 | Output serial-id checks look in `_inputs` |
| M | `src/NLightning.Infrastructure.Repositories/Database/Bitcoin/UtxoDbRepository.cs:50` | Anonymous-object composite key throws. Mapping also drops `LockedToChannelId`/`UsedInTransactionId` |
| M | `src/NLightning.Infrastructure.Persistence/EntityConfiguration/Channel/ChannelEntityConfiguration.cs:83` | SQL Server `RemoteNodeId` is varbinary(32) for a 33-byte key |
| M | `src/NLightning.Infrastructure.Serialization/Payloads/PingPayloadSerializer.cs` / `PongPayloadSerializer.cs` | Ignored bytes are not consumed |
| M | `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs` ~29-33, ~94-98 | ~29-33: `--network=x` swallows the command (`GetCommand` skips the next arg for every option); ~95: `--cookie <path>` stores the flag itself as the path |
| M | `src/NLightning.Daemon/Ipc/Handlers/OpenChannelIpcHandler.cs:66`, `OpenChannelSubscriptionIpcHandler.cs:71` | Error code is set to the message text |
| M | `src/NLightning.Transport.Ipc/MessagePack/Formatters/SignedTransactionFormatter.cs:13` | TxId serialize/deserialize mismatch; no null check |
| M | `src/NLightning.Domain/Money/LightningMoney.cs:197` | `Bits()` == `Cents()` (the tests assert this) |
| M | `src/NLightning.Domain/Utils/BitWriter.cs:130, 26-37, 217` | `1 >> bits` mask typo (:130); `Array.Resize` on the rented buffer (26-37) means a non-pooled array is returned to ArrayPool (:217) |
| L | `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs:67-68` | Uses `TlvConstants.UpfrontShutdownScript` for the blinded path |
| L | `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` 27-31, 177, 223, 253 | Fixed Argon2 salt, zero XChaCha nonce, non-standard BIP32 master from mnemonic. Changing any of these breaks existing key files |

### 10.2 Missing features / stubs

| Sev | File (line) | Gap |
|---|---|---|
| H | `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (switch, `default` ~L133) | No handlers for HTLC add/fulfill/fail/malformed, commitment_signed, revoke_and_ack, update_fee, shutdown, closing_signed, reestablish, v2/interactive-tx, stfu |
| H | `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (~40-61) | No HTLC or balance mutators; `HtlcState` has only 4 states |
| H | BOLT 4 (everywhere) | Sphinx, hop payloads and failure codes exist (M1+M2); no error onions, forwarding, HTLC wiring or invoice store (see §6) |
| H | `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs` (52, 189, 391) | Channel info is memory-only; `SignWalletTransaction` NotImplemented; no HTLC signatures |
| M | `src/NLightning.Application/Channels/Managers/ChannelManager.cs:62,70` | TODO: reestablish on startup; Closing/Stale handling |
| M | `src/NLightning.Application/Node/Managers/PeerManager.cs:72` | TODO: failed startup reconnect skips channel registration |
| M | `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs:79`, `ChannelReadyMessageHandler.cs:104-106` | TODO: notify app / update routing tables |
| M | `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:101,235` | TODO: generate the local upfront shutdown script |
| M | `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs:160,166` | `GetBasepointPrivateKey`, `LoadFromIndex` throw NotImplemented |
| M | `src/NLightning.Infrastructure.Bitcoin/Transactions/*` | HTLC-success/timeout, closing, penalty txs commented out or empty |
| M | `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs:189-198, 248, 400` | Revocation watch and mempool (rawtx) monitoring commented out |
| M | `src/NLightning.Infrastructure.Persistence/Entities/Bitcoin/RevocationWatchEntity.cs` | Not mapped; `RevocationWatchDbRepository` and its interface are empty |
| M | `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs` ~56 | No canonical-encoding check |
| M | `src/NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs` 27-83 | Closed type switch (missing `RemoteAddressTlv`); no ordering or unknown-even rules |
| M | `src/NLightning.Infrastructure.Serialization/ValueObjects/WitnessTypeSerializer.cs:46` | Max-length check commented out |
| M | `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs:49,57`; `InteractiveTransactionService.cs:48,69` | prevTx / script validation TODOs |
| M | `src/NLightning.Domain/Node/Options/FeatureOptions.cs:241,378` | TODO BOLT 7: remote_addr TLV, network |
| M | `src/NLightning.Domain/Node/FeatureSet.cs:18` | Dependency table has only 2 entries |
| M | `src/NLightning.Domain/Protocol/Constants/ChainConstants.cs` | No Signet/testnet4 chain hash |
| M | `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs` | Gossip 256-259 enum-only (even types throw on receive); 261-265, 513, 40/41, splice types absent |
| M | `src/NLightning.Bolt11/Models/Invoice.cs:553` | TODO: feature-bit check. `TaggedFieldList.cs:32` keeps only one `r` field; `:170` swallows errors |
| M | `src/NLightning.Infrastructure/Protocol/Services/DnsSeedClient.cs` | BOLT 10 fully commented out |
| M | `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs:165` | Argon2 NotImplemented in WASM; `RandomBytes` swallows errors |
| M | `src/NLightning.Infrastructure/Crypto/Providers/Native/NativeCryptoProvider.cs:92,105` | mlock/munlock are no-ops off Windows |
| M | `src/NLightning.Domain/Client/Enums/ClientCommand.cs` | No close/list-channels/invoice/pay/disconnect commands |
| L | `src/NLightning.Infrastructure/Node/Services/PeerService.cs:17` | TODO: move to Application |
| L | `src/NLightning.Daemon/Services/PluginLoaderService.cs`, `src/NLightning.Daemon.Contracts/IControlClient.cs`, `src/NLightning.Domain/Node/Interfaces/IPeerFactory.cs`, `ISecretStorageServiceFactory.cs`, `IChannelKeySetFactory.cs`, `ISignatureValidator.cs`, `Adapters/OutputAdapters/*`, `Daemon/Helpers/AesGcmHelper.cs` | Dead or unwired code |

### 10.3 Test / tooling gaps

| Sev | File | Gap |
|---|---|---|
| H | `test/NLightning.Application.Tests/*.csproj`, `test/NLightning.Daemon.Tests/*.csproj` | Missing `xunit.runner.visualstudio`, so CI runs 0 of 47 tests **(verified)** |
| M | `test/NLightning.Node.Tests/NLightning.Node.Tests.csproj` | 3-byte orphan **(verified)** |
| M | `scripts/testwithcoverage.sh` | Stale project list |
| M | `NLightning.sln` ~283 + newer projects | Wrong configuration mappings |
| M | `test/NLightning.Infrastructure.Bitcoin.Tests/{Outputs,Transactions}/*`, `test/NLightning.Infrastructure.Tests/Node/Models/PeerTests.cs`, `test/NLightning.Integration.Tests/Docker/{Sqlite,Postgres,SqlServer}Tests.cs`, `BOLT10/DNSBootstrapTests.cs` | Fully commented out |
| M | `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs:64` | Appendix B test body commented out (passes vacuously). `Bolt3AppendixFVectors` unused |
| L | `src/NLightning.Daemon.Contracts`, `src/NLightning.Daemon.Plugins` | net9.0 and older package versions |
| L | `src/NLightning.Daemon/AssemblyInfo.cs:3` | IVT to nonexistent `NLightning.Bolts.Tests`; `Daemon.Tests` not granted |
| L | `.vscode/launch.json`, `VERSIONING.md`, `CONTRIBUTING.md`, `test/NLightning.Integration.Tests/README.md` | Stale docs/config |

---

## 11. Cross-cutting gotchas

- **Units:** `LightningMoney` stores msat. `LightningMoney x = 1000` is **1 sat**. Always use `LightningMoney.Satoshis(...)` / `.MilliSatoshis(...)`. It is a **mutable reference type** placed inside `readonly record struct`s, and subtraction throws on underflow.
- **Feature enum** values are the *odd* bit; the compulsory bit is value-1. `new FeatureSet()` always sets 5 compulsory bits.
- **`ChannelModel.UpdateState`** only allows strictly increasing numeric states. Number any new state accordingly.
- **Two commitment counters:** `CommitmentNumber.Value` counts **up** from 0, while `ChannelKeySetModel.CurrentPerCommitmentIndex` counts **down** from 2^48-1. `CommitmentNumber`'s constructor takes the *funder's* basepoint first.
- **Temp vs real channel ids:** funding_created carries the temp id. `ChannelManager`'s `currentState` lookup only searches real channels. Only the initiator gets `OnChannelUpgraded`. The initiator is not persisted until funding_signed.
- **Threading:** there is no per-channel lock, `PeerManager._peers` is a plain `Dictionary`, `MessageService` deserializes and runs handlers synchronously under a lock on the read loop, and several sync-over-async calls block.
- **Lifetimes:** managers are singletons that open a scope per message. Handlers and `IUnitOfWork` are scoped. Never inject `IUnitOfWork` into a singleton, and call `SaveChanges(Async)` explicitly.
- **Serialization streams** use `Position`/`Length` to find optional trailing TLVs and the onion. Give them a seekable, single-message `MemoryStream`.
- **Stored wire bytes:** `HtlcDbRepository` stores serialized `UpdateAddHtlcMessage`s, so changing the update_add_htlc wire format changes the meaning of stored rows.
- **Value-object equality:** `PrivKey`/`CompactSignature` use reference equality on `byte[]`. `default(TxId/Hash/Secret/...)` has null internals and throws in `GetHashCode`. `BaseTlv`/`ChainHash` hash codes are inconsistent with `Equals`.
- **Typed TLV `Value`** is not the wire bytes for FeeRange/RemoteAddress/FundingOutputContribution. Always go through the converter.
- **File/type name mismatches:** `TLVStream.cs`, `NetworksTLV.cs`, `RequireConfirmedInputsTLV.cs`, `UpfronfShutdownScriptTlvConverter.cs`, `UpdateFufillHtlc*`. Search by type name, not file name.
- **Config gotchas:** single-dash `-n`/`-c` do not work in the daemon config reader. Without `--config`, the default network is mainnet while the generated JSON says regtest. `--password` shows up in the process list.
- **Docker fixtures** force-remove containers named miner/alice/bob/carol (`test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs`). `PostgresFixture`/`SqlServerFixture` also remove `postgres`/`sqlserver` containers, but the only tests that use them are commented out.
