# NLightning.Infrastructure.Repositories

This project implements the Domain repository ports: `IUnitOfWork`, the `I*DbRepository` interfaces backed by EF Core, and the two `I*MemoryRepository` singletons. It maps EF entities to Domain models and back by hand. The EF model, entities and migrations are in `../NLightning.Infrastructure.Persistence` (see persistence notes there).

## Layout
- `UnitOfWork.cs`: the scoped `IUnitOfWork` (interface: `src/NLightning.Domain/Persistence/Interfaces/IUnitOfWork.cs`). It creates each Db repo lazily over one `NLightningDbContext`, and has `GetPeersForStartupAsync`, `AddUtxo`/`TrySpendUtxo` (DB now, memory after a successful save) and `SaveChanges(Async)`.
- `Database/BaseDbRepository.cs`: generic `Get` / `GetByIdAsync(object id)` / `Insert` / `Update` / `Delete*`. Reads use AsNoTracking by default. `Update` is tracking-aware.
- `Database/Helpers/PrimaryKeyHelper.cs`: builds the PK predicate. Pass composite keys as a **ValueTuple, in key order**.
- `Database/Bitcoin/`: BlockchainState, Utxo, WalletAddresses, WatchedTransaction, RevocationWatch (an empty stub; its entity is not in the DbContext).
- `Database/Channel/`: Channel (channel row, config, key sets, aliases; attaches the commitment snapshot on reload), ChannelConfig (maps `ChannelParams`: `Local*`/`Remote*` columns per `ChannelParty` field; a default party's null amounts are written as 0), ChannelKeySet, ChannelState, RemoteShachain. `ChannelStateDbRepository` (`IUnitOfWork.ChannelStateDbRepository`, BOLT2 N5-T2) persists the engine: `InitializeAsync(snapshot)` stages everything, `ApplyAsync(next, transition, extras)` stages what `ChannelTransition` says changed (upserted and settled HTLCs are upserted by PK, settled ones stay as an archive with their final state until `PruneSettledHtlcsAsync`; dropped HTLCs are deleted; fee updates and commitment slots are synced when flagged; the channel scalars, `SentCommitDiff` (cleared when there is no unacked remote commitment), `LastSentOrder` and the shachain from `ChannelStateExtras` are written every time), `LoadAsync(channelId, CommitmentParams)` restores via `ChannelCommitments.Restore` and refuses legacy HTLC rows (NL-025). Nothing saves: the caller does one `SaveChangesAsync` per transition. `CommitmentStateEncoding` holds the blob formats. The legacy `HtlcDbRepository`/`IHtlcDbRepository` were removed. `RemoteShachainDbRepository` (`IUnitOfWork.RemoteShachainDbRepository`, NL-136) stores the peer's shachain as up to 49 `(ChannelId, Bucket)` rows: `SaveAsync(channelId, ISecretStorageService.Export())` upserts by bucket and removes absent buckets (staged, commit with the uow; calling it several times before `SaveChangesAsync` is safe because it merges `DbSet.Local`), `GetByChannelIdAsync` returns `ShachainEntry`s for `ISecretStorageService.Load`. It is not part of the `ChannelDbRepository` graph (no navigation on `ChannelEntity`); rows cascade on channel delete.
- `ChannelDbRepository` column ownership: `UpdateAsync` never writes `RemoteNextPerCommitmentPoint`, `SentCommitDiff`, `LastSentOrder`, and, once the model holds a snapshot (`ChannelModel.Commitments`), not the balances, next HTLC ids, commitment and revocation numbers either (a stale model must not roll a saved transition back). `AddAsync` also stages the snapshot when the model has one. Reads map channels one at a time (one query per channel for the state; no concurrent use of the context).
- `Database/Node/PeerDbRepository.cs`
- `Memory/ChannelMemoryRepository.cs`: live channels and temp channels (keyed by `(peerPubKey, tempChannelId)`). Raises `OnChannelUpgraded` / `OnChannelUpdated`.
- `Memory/UtxoMemoryRepository.cs`: the UTXO set, balances (confirmed means `BlockHeight + 3 <= currentBlockHeight`, hard-coded), and Branch-and-Bound coin selection with a greedy fallback.
- `DependencyInjection.cs`: `AddRepositoriesInfrastructureServices()` registers `IUnitOfWork` (scoped) and the memory repos (singleton). Individual Db repos are **not** registered in DI.

## Dependency rules
- It references only `NLightning.Domain` and `NLightning.Infrastructure.Persistence` (see the csproj). Do NOT add references to Application, Infrastructure, Infrastructure.Bitcoin, Serialization or Daemon. Get hashing (and, if ever needed, serialization) through Domain abstractions (`ISha256`, `IMessageSerializer`); UnitOfWork receives `ISha256` by injection.
- Entity constructors are `internal`. InternalsVisibleTo lives in `src/NLightning.Infrastructure.Persistence/AssemblyInfo.cs`.
- Never expose EF entities outside this project. Return Domain models only.

## Adding a Db repository (common task)
1. If the table is new, add the entity, the `Configure<Name>Entity` config, the DbSet in `NLightningDbContext`, and migrations for all 3 providers. That all happens in the Persistence project (`scripts/add_migration.sh`).
2. Add `I<Name>DbRepository` in `src/NLightning.Domain/<Area>/Interfaces/`.
3. Add `Database/<Area>/<Name>DbRepository.cs : BaseDbRepository<<Name>Entity>, I<Name>DbRepository`, with static `MapDomainToEntity` / `MapEntityToDomain`. Make a mapper `internal static` if other repos reuse it, as `ChannelDbRepository` does with the config and key set mappers.
4. Add a lazy property to `IUnitOfWork` and `UnitOfWork`.
5. Callers resolve `IUnitOfWork` from a scope (`IServiceScopeFactory.CreateScope()`) and must call `SaveChangesAsync()`. Nothing is written without it.
6. Update the `IUnitOfWork` mocks (and `test/NLightning.Tests.Utils/Mocks/CrashingUnitOfWork.cs`) if the interface changed: `test/NLightning.Application.Tests` (`Channels/Handlers/FundingCreatedMessageHandlerTests.cs`, `Node/Managers/PeerManagerTests.cs`) and `test/NLightning.Infrastructure.Bitcoin.Tests/Wallet/BlockchainMonitorServiceTests.cs`.

## Conventions
- Most files: System.*/Microsoft.* usings, then the file-scoped namespace, then relative `using Domain.X;` / `using Persistence.X;` below it. Follow the majority style in new files.
- Channel/HTLC enums (`State`, `Version`, `Direction`) are stored as `byte` on entities; `UtxoEntity.AddressType` is the enum type itself. Money types vary: `FundingAmountSatoshis` and `UtxoEntity.AmountSats` are `long`, `HtlcEntity.AmountMsat` is `ulong`, `ChannelEntity.Local/RemoteBalanceMsat` are `long` msat (NL-191). `ChannelModel.ShortChannelId` maps to a nullable column: a default (unconfirmed) scid has no bytes and is written as null. Check the entity before mapping.
- Writes build a fresh detached entity from the Domain model, then call Insert or Update. There are no explicit transactions.

## Tests
- There is no dedicated test project. Sqlite `:memory:` round-trip tests (real Sqlite migrations, `SqliteTestDatabase` helper) live in `test/NLightning.Integration.Tests/Persistence/` (BaseDbRepository paging, Utxo repository, UnitOfWork utxo sync; `ChannelDbRepositoryTests` and `ChannelStateDbRepositoryTests` via `SqliteDbTestContext`; `CommitmentStateTestKit.cs` drives two engines with fake crypto and compares snapshots structurally; crash injection with `Tests.Utils/Mocks/CrashingUnitOfWork` and a command interceptor). `Docker/PostgresTests.cs`/`SqlServerTests.cs` migrate seeded pre-`AddCommitmentState` rows and round-trip the state on real servers. Other coverage comes only from Docker e2e tests: `test/NLightning.Integration.Tests/Docker/{ChannelOpeningFlowTests,AbcNetworkTests}.cs`, run with `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker"` (needs Docker).
- Build and verify: `dotnet build NLightning.sln -p:MSBuildWarningsAsMessages=MSB4121 && dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.
- Add new repository round-trip tests next to those, reusing `SqliteDbTestContext`.

## Known bugs / gotchas (verify before relying on these paths)
- `State`/`Direction` are raw `byte` on entities: compare with `== (byte)HtlcState.X`, never `.Equals(HtlcState.X)` (a boxed enum never equals a byte). This was the NL-125 HTLC-reload bug, now fixed.
- On reload, `CommitmentNumber` must be built as (opener, accepter) payment basepoints, ordered by `IsInitiator` (BOLT 3 obscuring factor; NL-127 fixed).
- `ChannelEntity.LocalCommitmentNumber`/`RemoteCommitmentNumber` are persisted on their own (migration `PersistCommitmentNumbers`, which filled existing rows from the revocation numbers). Never rebuild them from `LocalRevocationNumber`/`RemoteRevocationNumber`: mid-dance the local commitment number runs one ahead of the revocations, and the signer's revocation guard reads it after a restart (NL-188).
- `ChannelModel.ChangeAddress` is persisted through the convention FK (`ChangeAddressIndex` + shadow `ChangeAddressIsChange`/`ChangeAddressAddressType`), which `ChannelDbRepository.SetChangeAddressForeignKey` sets via the change tracker; reads `Include(c => c.ChangeAddress)`. The referenced `WalletAddresses` row must already exist (FK). If NL-134 makes the FK explicit, that helper keeps working because it reads the FK metadata.
- `UnitOfWork.AddUtxo`/`TrySpendUtxo` only stage the DB change; `IUtxoMemoryRepository` is updated after `SaveChanges(Async)` succeeds (NL-133). Until then the utxo is not visible in memory (balances, coin selection). `TrySpendUtxo` also finds utxos added earlier in the same unit of work, and `UtxoDbRepository.Spend` cancels a still-pending insert instead of deleting.
- `ChannelMemoryRepository.TryGetChannel` returns the shared mutable model. Call `UpdateChannel` afterwards so that `OnChannelUpdated` fires.
- Do not inject `IUnitOfWork` into singletons. `UnitOfWork.Dispose` disposes the DbContext.

## Onion routing (BOLT 4) hooks
- The onion packet persists raw in `HtlcEntity.OnionRoutingPacket`; the per-HTLC shared secret in `HtlcEntity.OnionSharedSecret` (`IChannelStateDbRepository.SetOnionSharedSecretAsync`/`GetOnionSharedSecretAsync`).
- Forwarding will need new repos and tables here. Candidates: a forwarding circuit (in channel/htlc to out channel/htlc), failure reasons, an invoice/preimage store, and a lookup from scid/alias to ChannelId, likely via `IChannelMemoryRepository`.
- In-flight HTLCs round-trip through `ChannelStateDbRepository`; HTLC signatures live on the commitment rows.
