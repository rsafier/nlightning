# NLightning — agent guide (CLAUDE.md / AGENTS.md)

## Project goal

NLightning works as a fully functioning, spec-compliant BOLT Lightning node: interoperates with LND/CLN/Eclair/LDK, and can open, operate, route through and close channels safely with real funds. The BOLT specs are authoritative; every protocol piece is validated with official test vectors.

NLightning is a C# (.NET 10) Lightning Network node and library set. It is split into Domain, Application and several Infrastructure projects, plus a Daemon (`NLightning.Daemon`) and a CLI client (`NLightning.Client`) that talks to the daemon over IPC. Both usage texts call the binary `nltg` (`src/NLightning.Daemon/Utilities/DaemonUtils.cs`, `src/NLightning.Client/Utils/ClientUtils.cs`); the assemblies keep their `NLightning.*` names. What works today: BOLT 8 transport, BOLT 1 init/ping/error, BOLT 2 v1 channel **opening** (open → accept → funding_created/signed → channel_ready), BOLT 3 funding/commitment transactions and key derivation, BOLT 9 feature bits, and BOLT 11 invoices (a standalone library). BOLT 4 onion core (M1+M2: packet, Sphinx construct/peel, hop payloads, validator, replay cache; no forwarding or error onions yet). **Not implemented:** HTLC handling (update/commit/revoke), channel close and reestablish, BOLT 4 forwarding/failure onions, BOLT 7 gossip, and BOLT 5 on-chain handling.

Deeper docs: `docs/agents/REPO_MAP.md` (per-area map), `docs/agents/BOLT_COVERAGE.md` (status matrix), `docs/agents/ONION_ROUTING_PLAN.md` (BOLT 4 plan), `docs/agents/BOLT2_NORMAL_OPERATION_PLAN.md` (BOLT 2 HTLC / reestablish / close plan, milestones N0-N11), `docs/agents/LNBOLT_REVIEW.md` (legacy onion code review). Some `src/*/` and `test/` folders also have their own `CLAUDE.md`; read those when you work there.

## Issue tracking

- GitHub issues are disabled on this fork. The single issue ledger is **`docs/agents/ISSUES.md`** (IDs `NL-###`). Check it before starting work and cite the ID in commit messages.
- When you fix something, update its entry (`Status: fixed (<SHA>)`) and the summary counts **in the same commit**. When you find something new, add it with the next free `NL-###`. Never renumber and never delete entries; mark them `wontfix` or `duplicate of NL-###` instead.

## Layers & dependency rules

```
NLightning.Daemon (composition root, DI, IPC server, hosted service)
  direct refs: Application, Infrastructure, Infrastructure.Bitcoin, Infrastructure.Serialization,
               Infrastructure.Repositories, Infrastructure.Persistence.{Postgres,Sqlite,SqlServer},
               Client, Daemon.Plugins, Daemon.Contracts
  Client -> Transport.Ipc -> Daemon.Contracts          (Transport.Ipc also -> Domain; Daemon gets it only via Client)

Application -> Infrastructure.Bitcoin -> Infrastructure -> Domain     (Application also -> Infrastructure, Domain directly)
Infrastructure.Serialization -> Infrastructure, Domain
Infrastructure.Repositories  -> Infrastructure.Persistence -> Domain
Infrastructure.Persistence.{Postgres,Sqlite,SqlServer} -> Infrastructure.Persistence
NLightning.Domain   (no NuGet, no project refs)
Bolt11 -> Infrastructure + Infrastructure.Bitcoin   (only tests consume it today)
```
Simplified: see each `*.csproj` for the exact `ProjectReference` list.

- **Domain** references nothing. It holds most abstractions (`ITlvConverterFactory`, `IUnitOfWork`, `ILightningSigner`, `IChannelManager`, `IMessageFactory`, ...). Exceptions: `IEcdh` and `ICryptoProvider` live in `src/NLightning.Infrastructure/Crypto/Interfaces/`, `ITcpService` in `src/NLightning.Infrastructure/Transport/Interfaces/`, and `IBlockchainMonitor` in `src/NLightning.Infrastructure.Bitcoin/Wallet/Interfaces/`. Keep Domain BCL-only and free of NBitcoin.
- **Infrastructure** → Domain. **Infrastructure.Bitcoin** → Infrastructure (all secp256k1/NBitcoin code lives here). **Infrastructure.Serialization** → Domain + Infrastructure. **Persistence** → Domain. **Repositories** → Domain + Persistence.
- **Application** → Domain + Infrastructure + Infrastructure.Bitcoin. This breaks clean architecture: handlers use `IBlockchainMonitor`, tx builders and `ITcpService` directly. Don't make it worse; add new abstractions to Domain.
- `Daemon.Contracts` and `Daemon.Plugins` target **net9.0**. Everything else is net10.0 via `src/Directory.Build.props`, which does *not* apply to `test/`.
- Each layer exposes an `Add…` extension in its `DependencyInjection.cs`: `AddApplicationServices`, `AddInfrastructureServices`, `AddSerializationInfrastructureServices`, `AddPersistenceInfrastructureServices`, `AddRepositoriesInfrastructureServices`, and the exception **`AddBitcoinInfrastructure()`** (no `Services` suffix). The Daemon composes them in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`. `IEcdh` is registered in `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs`. `ITlvConverterFactory` is `TryAddSingleton`ed by both `AddInfrastructureServices` and `AddSerializationInfrastructureServices`. `IChannelOpenValidator`, `IChannelFactory`, `ICommitmentTransactionModelFactory`, `IFundingTransactionModelFactory` and `ILightningSigner` are registered by hand **only** in `NodeServiceExtensions`, not in any layer's `DependencyInjection.cs`. The Docker integration tests `test/NLightning.Integration.Tests/Docker/AbcNetworkTests.cs` and `ChannelOpeningFlowTests.cs` rebuild the same DI by hand, so mirror any new registrations there too. (`PostgresTests.cs`, `SqliteTests.cs` and `SqlServerTests.cs` in that folder are fully commented out.)

## Build / test / format (verified on SDK 10.0.103, macOS arm64)

```bash
dotnet build NLightning.sln -p:MSBuildWarningsAsMessages=MSB4121                  # Debug, ~3s warm
dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121                      # CI config
dotnet build -c Release.Native -p:MSBuildWarningsAsMessages=MSB4121               # CRYPTO_NATIVE backend
dotnet format --verify-no-changes --exclude "**/BlazorTests/**"                   # CI gate, ~17s
dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'           # CI test run
```

- **Run the node / CLI:** `dotnet run --project src/NLightning.Daemon -- --network regtest` and `dotnet run --project src/NLightning.Client -- --network regtest info`. The first daemon run writes `~/.nltg/<network>/appsettings.json` (`src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`); set the `Bitcoin` RPC/ZMQ settings there, and `Database:RunMigrations=true` (default `false`) to create the schema.
- The Release build prints about 10 warnings, all nullability (`grep 'warning CS'`). NuGet vulnerability advisories (NU1902/NU1903) are fixed or pinned (NL-170); a new one means a package needs a bump.
- **Single test (VSTest projects):** `dotnet test test/NLightning.Bolt11.Tests/NLightning.Bolt11.Tests.csproj --no-build --filter "FullyQualifiedName=<Ns.Class.Method>"`. `~` (contains), `!~` and `&` also work.
- **Application.Tests and Daemon.Tests** run under `dotnet test` like the other test projects (NL-167). The xunit v3 runner also works:
  `dotnet run --project test/NLightning.Application.Tests -- -class NLightning.Application.Tests.Node.Managers.PeerManagerTests`
  `dotnet run --project test/NLightning.Daemon.Tests -- -method '*FeeService*'` (`-namespace` also works). A filter that matches nothing exits quietly with `Total: 0`, so check the count.
- **Docker tests** (`NLightning.Integration.Tests.Docker.*`: AbcNetworkTests, ChannelOpeningFlowTests) start bitcoind + 3 LND through LNUnit and build `test/Docker/custom_lnd`. Always pass `!~Docker` unless you mean to run them. They force-remove containers named miner/alice/bob/carol.
- **Wasm** (`Release.Wasm`, used only by `test/BlazorTests/*`) runs `npm install` with linux-x64-pinned esbuild/rollup, so it **fails on macOS** (EBADPLATFORM) and builds only in CI on ubuntu. The Debug/Release sln configs don't build the Blazor projects. CI: `cd test/BlazorTests/NLightning.Blazor.Tests && dotnet build -c Release.Wasm && pwsh bin/Release.Wasm/net10.0/playwright.ps1 install chromium && dotnet test --no-build -c Release.Wasm`.
- Crypto is chosen at **compile time**: `CRYPTO_LIBSODIUM` (Debug/Release), `CRYPTO_NATIVE` (*.Native / AOT), `CRYPTO_JS` (*.Wasm), all in `src/NLightning.Infrastructure/NLightning.Infrastructure.csproj`. If you touch `src/NLightning.Infrastructure/Crypto`, build both Release and Release.Native.

## Recipes

### Add a BOLT wire message end to end
1. `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`: add the enum value.
2. `src/NLightning.Domain/Protocol/Payloads/XPayload.cs` (implements `IChannelMessagePayload` if channel-scoped) and `Messages/XMessage.cs` (`sealed : BaseChannelMessage`, `public new XPayload Payload => (XPayload)base.Payload`, and build `Extension = new TlvStream()` only if it has TLVs).
3. Payload serializer in `src/NLightning.Infrastructure.Serialization/Payloads/`. Register it in `Factories/PayloadSerializerFactory.cs` in **both** dictionaries (Type→serializer and MessageTypes→Type).
4. Message-type serializer in `Messages/Types/`. Register it in `Factories/MessageTypeSerializerFactory.cs` in **both** `RegisterSerializers` and `RegisterTypeDictionary`. If you miss the MessageTypes map, the message is treated as unknown: dropped if its type is odd, and the peer is killed if it is even.
5. `Create*` method on `src/NLightning.Domain/Protocol/Interfaces/IMessageFactory.cs` and `src/NLightning.Application/Protocol/Factories/MessageFactory.cs`.
6. Routing. For a channel message, add `IChannelMessageHandler<XMessage>` in `src/NLightning.Application/Channels/Handlers/` (auto-registered Scoped by reflection) **and** a `case` in `ChannelManager.HandleChannelMessageAsync` (`src/NLightning.Application/Channels/Managers/ChannelManager.cs`). Without the case, the message reaches `default` → `ChannelErrorException` → peer disconnected. Non-channel messages go through `src/NLightning.Infrastructure/Node/Services/PeerService.cs` `HandleMessage`, which today silently drops anything that isn't a channel/error/warning message.
7. Round-trip test in `test/NLightning.Infrastructure.Serialization.Tests/Messages/`.

### Add a TLV
1. Add a `static readonly BigSize` in `src/NLightning.Domain/Protocol/Constants/TlvConstants.cs`. Numbers are per-message and **collide** (0 and 1 are each reused). For new namespaces such as onion hop payloads, use a separate constants class.
2. `src/NLightning.Domain/Protocol/Tlv/XTlv.cs : BaseTlv`, calling `base(TlvConstants.X)`.
3. `src/NLightning.Infrastructure/Protocol/Tlv/Converters/XTlvConverter.cs : ITlvConverter<XTlv>` (validate Type/Length and throw `InvalidCastException`; mark the explicit non-generic members `[ExcludeFromCodeCoverage]`). Register it in `src/NLightning.Infrastructure/Protocol/Factories/TlvConverterFactory.cs`.
4. `TlvStreamSerializer` looks the converter up by the TLV's runtime type (`ITlvConverterFactory.GetConverter(Type)`), so registration is enough; an unregistered subtype throws "No converter found", a plain `BaseTlv` is written verbatim. `TlvStreamSerializerTests` requires a sample for every registered converter, so add one there.
5. Add a nullable property and ctor arg on the message. Read it in the message-type serializer with `TryGetTlv(TlvConstants.X, …)` plus the converter.
6. Tests go in `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/`.

### Add a channel message handler
Implement `IChannelMessageHandler<TMessage>.HandleAsync(msg, currentState, negotiatedFeatures, peerPubKey)`. The value you return is sent back to the same peer. To send elsewhere or outside a reply, raise an event that goes through `ChannelManager.OnResponseMessageReady`. Throw `ChannelErrorException` (disconnect) or `ChannelWarningException` (send a warning). Only the `PeerMessage` text goes on the wire. For temporary-channel-id messages `currentState` is `None`; look temp state up via `IChannelMemoryRepository.TryGetTemporaryChannelState`. Persist through a scoped `IUnitOfWork` and call `SaveChangesAsync`. Never inject `IUnitOfWork` into a singleton; open a scope instead. Then add the `ChannelManager` case (step 6 above).

### Add an EF migration (all 3 providers)
Edit the entity (`src/NLightning.Infrastructure.Persistence/Entities/…`, `internal` parameterless ctor), its `EntityConfiguration/…/Configure<Name>Entity`, and the DbSet/`OnModelCreating` in `Contexts/NLightningDbContext.cs`. Then:
Prerequisite: `dotnet-ef` is not installed by the repo (no `.config/dotnet-tools.json`); run `dotnet tool install --global dotnet-ef --version 10.0.5` (matches the EF packages).
```bash
cd src/NLightning.Infrastructure.Persistence
./scripts/start_postgres.sh && ./scripts/start_sql.sh      # Docker: postgres :15432, mssql :1433
./scripts/add_migration.sh MyCamelCaseName                  # builds + runs dotnet ef for Postgres, Sqlite, SqlServer
```
`add_migration.sh` runs `dotnet ef database update` before and after each `migrations add`, so the new migration is also **applied** to the Postgres/SqlServer containers and to `src/NLightning.Infrastructure.Persistence/nltg.db` (created by the script; don't commit it). `start_sql.sh` pulls mssql with `--platform linux/amd64`, which runs under emulation on Apple Silicon.
Commit the migration, Designer and Snapshot files for all three `NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}` projects together. Never copy migration files between providers: SqlServer uses varbinary sizes and Postgres uses snake_case. The design-time factory picks the provider from the first env var it finds, checking `NLIGHTNING_POSTGRES`, then `_SQLITE`, then `_SQLSERVER`. Remove a migration with `./scripts/remove_migration.sh` and revert the model changes by hand. Because the migration was already applied, EF will likely refuse to remove it until you roll each database back (`dotnet ef database update <PreviousMigration>` per provider) or recreate them with `./scripts/destroy_{postgres,sql,sqlite}.sh` then `start_*.sh` (not verified end to end). The `destroy_*.sh` scripts are also the cleanup step.

## Coding conventions (`.editorconfig`, enforced by `dotnet format` in CI)
- File-scoped namespace equal to the folder path. `System`/`Microsoft`/`NBitcoin` usings go **above** the namespace. `NLightning.*` usings go **after** it as relative names (`using Domain.Money;`).
- Private fields are `_camelCase`, private static fields are `s_camelCase`, private `const` fields are `PascalCase` (no prefix), locals and parameters are `camelCase`, and local functions are `PascalCase`. All naming rules are severity **error** in `dotnet format`. No `this.`. Prefer `var`. Unused usings, members and parameters, and double blank lines, are **errors** in format (not in build).
- LF endings. `*.cs` files have no final newline.
- Amounts are always `LightningMoney`. Implicit `ulong/long → LightningMoney` is **msat**, so use `LightningMoney.Satoshis(...)`. Byte-backed value objects (`ChannelId`, `TxId`, `CompactPubKey`, `Hash`) convert implicitly to and from `byte[]`.
- Integers on the wire are big-endian. Existing code uses `src/NLightning.Infrastructure/Converters/EndianBitConverter.cs`. Prefer `BinaryPrimitives` for new code, because the LE trim/pad helpers are suspect.
- Tests use xUnit v3 + Moq, named `Given_X_When_Y_Then_Z` with Arrange/Act/Assert comments. Pass `TestContext.Current.CancellationToken`. Moq can't mock Span parameters, so use the Fake* + virtual byte[] pattern in `test/NLightning.Tests.Utils/Mocks`.
- `InternalsVisibleTo` is declared in `src/*/AssemblyInfo.cs` where one exists: only Application, Bolt11, Daemon, Domain, Infrastructure, Infrastructure.Bitcoin and Infrastructure.Persistence have it. `NLightning.Domain.Tests`, `NLightning.Application.Tests` and `NLightning.Daemon.Tests` have **no** internals access (Daemon lists a stale `NLightning.Bolts.Tests`). When a test needs internals, add an entry, or create `AssemblyInfo.cs` if the project lacks one.

## Non-obvious gotchas
- `UpdateAddHtlcPayload.OnionRoutingPacket` is a mandatory raw `ReadOnlyMemory<byte>` of exactly `OnionConstants.PacketLength` (1366) bytes (constructor and serializer enforce it; a short read throws). Parse it with `OnionPacket`. The update_add_htlc extension is read strictly (unknown even types rejected) and the blinded path uses `TlvConstants.BlindedPath`.
- Deserializers use `stream.Position/Length` to find trailing TLVs, so they need a seekable stream bounded to one message.
- `TlvStream` (`src/NLightning.Domain/Protocol/Models/TLVStream.cs`; note the file-name casing) is a `SortedDictionary`, so it silently re-sorts records. Message TLV extensions other than update_add_htlc are still read with the open `DeserializeAsync` and are not checked for unknown even types (BOLT 1 requires it; switch them to `DeserializeStrictAsync` as they are touched). BigSize decoding is canonical.
- `TlvStreamSerializer.DeserializeAsync` enforces strictly increasing types and length <= remaining bytes but cannot reject unknown even types (no known-type set); use `DeserializeStrictAsync(stream, knownTypes)` where the namespace is known (new messages). Hop payloads use `IHopPayloadSerializer` instead, because `invalid_onion_payload` needs the offending type and offset, which the strict reader does not report.
- Some Domain TLVs (`FeeRangeTlv`, `RemoteAddressTlv`, `FundingOutputContributionTlv`) don't hold real wire bytes in `Value`. Always go through the converter.
- `ChannelModel.UpdateState` only allows strictly increasing `ChannelState` values, so number new states with that in mind. HTLC collections, balances and next-id fields have getters only; no mutators exist yet.
- `ShortChannelId(ulong)` uses the wrong masks (`src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs`). `BlockchainMonitorService` records the tx index among *watched* txs, not the index in the block, so SCIDs are wrong. Fix both before forwarding by scid.
- `ChannelDbRepository.MapEntityToDomain` compares `byte` fields with `.Equals(enum)`, which is always false, so offered/fulfilled HTLCs don't reload. It also rebuilds the funding output with the local funding key twice.
- `TransportService.WriteMessageAsync` encrypts *before* taking the write lock, so concurrent senders can desync nonces. The read loop uses `ReadAsync`, not `ReadExactlyAsync`, so large messages can drop connections.
- `ChannelManager.ForgetStaleChannels` has no state filter and relies on `FundingCreatedAtBlockHeight` defaulting to 0. Treat it as suspect.
- `NLightning.sln` config mappings are maintained by hand. When you add a project or configuration, run `python3 scripts/check-sln-configs.py` (a CI step); it also fails when a `*.Tests` project lacks `IsTestProject` or `xunit.runner.visualstudio`.
- `MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options` must be set on both IPC ends. Any IPC wire change also needs a new `ClientCommand` value (never renumber existing ones) and an `IIpcCommandHandler` registered in `NodeServiceExtensions`.

## Status & known gaps
- **BOLT 4 onion routing: M1+M2 done (core only).** Sphinx construct/peel: `ISphinxService` (Domain) → `SphinxService`/`OnionBuilder`/`OnionPeeler` in `src/NLightning.Infrastructure.Bitcoin/Onion/` (byte-exact against `onion-test.json`; peel also applies the route-blinding `path_key` tweak). Hop payloads: `HopPayload` + `HopPayloadValidator` (Domain), `IHopPayloadSerializer` (Serialization; reports `invalid_onion_payload` type/offset via `InvalidOnionPayloadFailureFactory`), `IOnionReplayCache` → in-memory `OnionReplayCache` (Infrastructure). Primitives: `HmacSha256`, `ChaCha20Stream` (`ICryptoProvider.StreamChaCha20IetfXor`, all 3 backends), `ISecp256K1Math`, strict `TruncatedInt`, canonical BigSize, strict/open `TlvStreamSerializer`. Not wired together yet: nothing calls peel → replay-check → deserialize → validate on incoming HTLCs (the intended call order is in `src/NLightning.Application/CLAUDE.md`). Missing: error onions (M3), forwarding/final-hop (M4), route-blinding payload handling (M5), persistence for per-HTLC shared secrets and replay entries. `FeatureOptions` still advertises RouteBlinding/AttributionData as Optional. Plan, as-built record, deviations and test vectors: **`docs/agents/ONION_ROUTING_PLAN.md`** (§5 "M1/M2 as built").
- The HTLC state machine is a prerequisite: there are no handlers for update_add/fulfill/fail/fail_malformed, commitment_signed, revoke_and_ack, update_fee or channel_reestablish, and `ILightningSigner` has no HTLC-signature API. Plan: **`docs/agents/BOLT2_NORMAL_OPERATION_PLAN.md`** (N0/N1 are safety gates for pre-existing bugs).
- Missing entirely: BOLT 7 gossip (types 256–259 are enum-only, and incoming even gossip messages throw), dual-funding/interactive-tx handlers (messages and serializers exist), close, BOLT 5, penalty txs (`PenaltyTransactionModel` is empty) and the plugin loader (never registered). Bolt11 isn't wired into the daemon.
- Full matrix: **`docs/agents/BOLT_COVERAGE.md`**. Area-by-area map: **`docs/agents/REPO_MAP.md`**.
