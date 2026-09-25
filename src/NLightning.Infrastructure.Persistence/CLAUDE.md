# NLightning.Infrastructure.Persistence

EF Core 10 model for the node: one `NLightningDbContext`, entity classes used only for persistence, per-entity configuration, value converters, provider selection (DI) and a design-time factory for `dotnet ef`. There are **no repositories here**. Domain<->entity mapping lives in `src/NLightning.Infrastructure.Repositories` (`UnitOfWork`, `Database/**/**DbRepository.cs`). Migrations live in three sibling assemblies: `NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}/Migrations`.

## Layout
- `Contexts/NLightningDbContext.cs`: 11 DbSets (BlockchainStates, WatchedTransactions, WalletAddresses, Utxos, Channels, ChannelConfigs, ChannelKeySets, Htlcs, ChannelLocalAliases, RemoteShachains, Peers). `RemoteShachainEntity` (migration `AddRemoteShachain`): PK `(ChannelId, Bucket)`, `Index` (`long`, 48-bit), `Secret` (32 bytes), FK to `Channels` with cascade but no navigation property. `OnModelCreating` calls `modelBuilder.Configure<X>Entity(_databaseType)`.
- `Entities/{Bitcoin,Channel,Node}/`: POCOs, mostly `required` props, each with an `internal` parameterless ctor for EF.
- `EntityConfiguration/{Bitcoin,Channel,Node}/`: `public static void Configure<X>Entity(this ModelBuilder, DatabaseType)`, plus private `OptimizeConfigurationForSqlServer` helpers (varbinary sizes, online indexes). Only `UtxoEntityConfiguration` also has `OptimizeConfigurationForPostgres` (concurrent indexes).
- `ValueConverters/`: ChannelId, CompactPubKey, Hash, ShortChannelId, TxId stored as `byte[]`, using the value objects' implicit conversions.
- `DependencyInjection.cs`: `AddPersistenceInfrastructureServices(services, configuration)`. It reads `Database:Provider` (postgres|postgresql|sqlite|sqlserver|microsoftsql), `Database:ConnectionString` and `Database:EnableSensitiveQueryLogging`, and sets the MigrationsAssembly for each provider.
- `Factories/NLightningContextFactory.cs`: design-time factory driven by env vars `NLIGHTNING_POSTGRES`, then `NLIGHTNING_SQLITE`, then `NLIGHTNING_SQLSERVER` (first one set wins).
- `Enums/DatabaseType.cs`, `Providers/DatabaseTypeProvider.cs`: pass the provider into the model.
- `scripts/`: add/remove migration, start/destroy docker Postgres (port 15432) and SQL Server (1433). `README.md` has the tooling notes.

## Add an entity/table (the common task)
1. `Entities/<Area>/<Name>Entity.cs`: `required` props, `internal <Name>Entity() { }`. Use value objects plus converters for ids and keys. Enums: either a raw `byte` property (`ChannelEntity.State`, `HtlcEntity.State/Direction`) or a `: byte` enum type directly (`AddressType`). Amounts: `long` sats / `ulong` msat with the unit in the name (`AmountSats`, `AmountMsat`); note channel balances are `decimal LocalBalanceSatoshis/RemoteBalanceSatoshis`.
2. `EntityConfiguration/<Area>/<Name>EntityConfiguration.cs`: key (composite via `HasKey(x => new {...})`), `IsRequired`, `.HasConversion<XConverter>()`, relationships **explicitly** (see gotchas), SqlServer `varbinary(N)` using the Domain constants.
3. `NLightningDbContext`: add a `DbSet` and call `Configure<Name>Entity`.
4. Generate migrations for **all three** providers (below) and commit every `Migrations/*` file, including Designer and Snapshot.
5. In Repositories: add `I<Name>DbRepository` in `src/NLightning.Domain/{Bitcoin,Channels,Node}/Interfaces` (Domain uses `Channels`, plural), implement it in `src/NLightning.Infrastructure.Repositories/Database/<Area>/` on `BaseDbRepository<TEntity>`, and add a lazily created (`??=`) property to `IUnitOfWork` (`src/NLightning.Domain/Persistence/Interfaces/IUnitOfWork.cs`) and `UnitOfWork`.

## Migrations (run from this directory)
- Full script: `./scripts/start_postgres.sh && ./scripts/start_sql.sh && ./scripts/add_migration.sh MyCamelCaseName`. It builds the 3 provider projects and runs `dotnet ef database update` / `migrations add` for each one. It needs Docker and `dotnet-ef`.
- Single provider: `NLIGHTNING_SQLITE='Data Source=./nltg.db' dotnet ef migrations add X --project ../NLightning.Infrastructure.Persistence.Sqlite`. Make sure no other `NLIGHTNING_*` var is set, because Postgres is checked first.
- Remove the last one: `./scripts/remove_migration.sh`, then revert the DbContext and entity edits by hand.
- Names must be CamelCase (dotnet format). Never copy migration files between providers: the snapshots differ.
- Runtime: migrations apply only when `Database:RunMigrations=true` (`src/NLightning.Daemon/Extensions/DatabaseExtensions.cs`, `MigrateDatabaseIfConfiguredAsync`; default written config is `false`).

## Dependency rules
- The only project reference is `NLightning.Domain` (+ EF Core/Npgsql/Sqlite/SqlServer/EFCore.NamingConventions packages). Must NOT reference Application, Infrastructure, Infrastructure.Bitcoin, Serialization, Repositories or Daemon.
- No domain logic or mapping here. `InternalsVisibleTo` gives access only to `NLightning.Infrastructure.Repositories` and `DynamicProxyGenAssembly2` (Castle/Moq proxies) (`AssemblyInfo.cs`). Add a test assembly there if it has to build entities.

## Build / test
- Build: `dotnet build src/NLightning.Infrastructure.Persistence` (repo root). CI check (`.github/workflows/dotnet.yml`): `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.
- Non-Docker coverage: `test/NLightning.Integration.Tests/Persistence/` (Sqlite `:memory:` round-trips via `SqliteTestDatabase` and `SqliteDbTestContext`, provider option checks, SqlServer pending-model-changes check); this project has no test project of its own. Otherwise coverage comes only from the Docker e2e tests (`AbcNetworkTests.cs`, `ChannelOpeningFlowTests.cs`), which configure `Database:Provider=Sqlite` and run `context.Database.Migrate()`: `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker"`. The provider smoke tests in `test/NLightning.Integration.Tests/Docker/{Sqlite,Postgres,SqlServer}Tests.cs` are commented out. Add new round-trip tests next to `SqliteTestDatabase`/`SqliteDbTestContext`.

## Gotchas
- Postgres uses snake_case (`UseSnakeCaseNamingConvention`). Sqlite and SqlServer use PascalCase. Raw SQL must handle both. `EnableSensitiveDataLogging()` is on only when `Database:EnableSensitiveQueryLogging=true` (all providers; NL-135). The design-time `NLightningContextFactory` still enables it for Postgres, which only affects `dotnet ef`.
- SQL Server `varbinary(N)` sizes must use the matching Domain constant (`CryptoConstants.CompactPubkeyLen` for pubkeys, not `TxIdLength`). `ChannelEntity.RemoteNodeId` was `varbinary(32)` until migration `FixRemoteNodeIdLength` (SqlServer only; NL-129). `test/NLightning.Integration.Tests/Persistence/PersistenceConfigurationTests.cs` asserts the SqlServer snapshot has no pending model changes, so a model edit without a SqlServer migration fails the test run.
- Convention-based shadow FKs: `PeerEntity.Channels` has no configured relationship, so EF created `PeerEntityNodeId` on channels. `ChannelEntity.ChangeAddress` created `ChangeAddressIsChange`/`ChangeAddressAddressType`. Configuring these explicitly changes the schema for all 3 providers.
- `Entities/Bitcoin/RevocationWatchEntity.cs` is NOT in the DbContext, has no key, no configuration and no migration, yet `RevocationWatchDbRepository` (empty body, `BaseDbRepository<RevocationWatchEntity>`) exists in Repositories; using it would fail at runtime. Give the entity a key and configuration before adding a DbSet.
- `State`/`Direction` on `ChannelEntity`/`HtlcEntity` are raw `byte`. Compare with `== (byte)Enum.X`, not `.Equals(Enum.X)` (boxed enum never equals a byte).
- The migration name typo `AddBlockchaisStateAndWatchedTransaction` is baked into history. Do not rename it.
- Provider migration projects set `BaseOutputPath` to this project's `bin/`, so the four projects share one output folder.
- `ChannelDbRepository.UpdateAsync` detaches the child collections and synchronizes each child table by primary key (NL-192); a new child collection on `ChannelEntity` must be added to that sync too.

## Onion routing (BOLT 4) hooks
- Today the onion is persisted only inside `HtlcEntity.AddMessageBytes`, which holds the serialized `UpdateAddHtlcMessage` including the 1366-byte packet (`varbinary(max)` on SqlServer). Changing the update_add_htlc wire format changes stored rows.
- Expect to add, each as a new entity plus 3 migrations: a per-incoming-HTLC shared secret (a column on `HtlcEntity` or a table keyed by (ChannelId, HtlcId, Direction)) for failure wrapping; a forwarding-circuit table (incoming chan/htlc to outgoing chan/htlc, amounts, CLTVs, fees); failure reasons/messages; an invoice/preimage store (payment_hash, payment_secret, total_msat) for final-hop checks; an SCID to ChannelId map for hop-payload `short_channel_id` (our scid aliases already live in `ChannelLocalAliases`, keyed by the alias, and the peer's alias in `Channels.RemoteAlias`; the real scid is not persisted yet); and, later, BOLT 7 graph tables for route building.
- `ChannelEntity.ChangeAddressType` is a separate, redundant column; the real FK to `WalletAddresses` is (`ChangeAddressIndex`, shadow `ChangeAddressIsChange`, shadow `ChangeAddressAddressType`). `ChannelDbRepository` writes both.
