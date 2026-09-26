using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Application.Payments;
using Application.Payments.Onion;
using Docker.Mock;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure;
using Infrastructure.Bitcoin;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Protocol.Onion;
using Infrastructure.Repositories;
using Infrastructure.Serialization;
using static OnionReplaySchemaRoundTrip;

/// <summary>
/// The persistent onion replay set (NL-078) on a SQLite file with the node's own layer registrations: a replayed onion
/// is detected across a node restart, the recording HTLC may be processed again, entries are pruned by block height
/// (explicitly and from the chain monitor's tip), and concurrent HTLCs with one onion never both pass. The migration
/// itself is proven by <see cref="Given_RowsFromBeforeAddOnionReplaySet_When_Migrated_Then_TheyMoveForwardAndTheReplaySetWorks"/>
/// here and by the Docker Postgres/SQL Server tests.
/// </summary>
public sealed class OnionReplayPersistenceTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x77, 32).ToArray();
    private static readonly Secret s_paymentSecret = Enumerable.Repeat((byte)0x78, 32).ToArray();
    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-onion-replay-{Guid.NewGuid():N}.db");

    private readonly FakeSecureKeyManager _keyManager = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Given_RowsFromBeforeAddOnionReplaySet_When_Migrated_Then_TheyMoveForwardAndTheReplaySetWorks()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (the same rows and assertions as the Docker Postgres/SQL Server tests)
        await OnionReplaySchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_OnionProcessedForOneHtlc_When_TheNodeRestartsAndAnotherHtlcCarriesIt_Then_ItIsFailedAsAReplay()
    {
        // Arrange: a real payment onion to this node, processed for HTLC 0 of channel A before a restart
        byte[] onion;
        IncomingOnionResult first;
        await using (var node = await StartNodeAsync())
        {
            onion = await BuildOnionAsync(node);
            first = await node.GetRequiredService<IncomingOnionProcessor>()
                              .ProcessAsync(onion, s_paymentHash, replayOwner: new OnionReplayOwner(s_channelA, 0, 700));
        }

        // Act: the restarted node sees the same onion on another HTLC, then its own HTLC again (link-up replay)
        IncomingOnionResult replayed, reprocessed;
        await using (var node = await StartNodeAsync())
        {
            var processor = node.GetRequiredService<IncomingOnionProcessor>();
            replayed = await processor.ProcessAsync(onion, s_paymentHash,
                                                    replayOwner: new OnionReplayOwner(s_channelB, 3, 800));
            reprocessed = await processor.ProcessAsync(onion, s_paymentHash,
                                                       replayOwner: new OnionReplayOwner(s_channelA, 0, 700));
        }

        // Assert
        Assert.IsType<IncomingOnionFinal>(first);
        var failed = Assert.IsType<IncomingOnionFailed>(replayed);
        Assert.Equal(FailureCode.TemporaryNodeFailure, failed.Failure.Code);
        Assert.IsType<IncomingOnionFinal>(reprocessed);
    }

    [Fact]
    public async Task Given_EntriesWithDifferentExpiries_When_PrunedByHeightAndRestarted_Then_OnlyThePassedOnesAreForgotten()
    {
        // Arrange
        await using (var node = await StartNodeAsync())
        {
            var store = node.GetRequiredService<IOnionReplayStore>();
            Assert.True(await store.TryAddAsync(Hmac(0x01), s_channelA, 0, 100, TestContext.Current.CancellationToken));
            Assert.True(await store.TryAddAsync(Hmac(0x02), s_channelA, 1, 200, TestContext.Current.CancellationToken));

            // Act: the chain is at 200, past 100 but not past 200
            var removed = await store.PruneAsync(200, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, removed);
        }

        await using (var node = await StartNodeAsync())
        {
            var store = node.GetRequiredService<IOnionReplayStore>();
            Assert.True(await store.TryAddAsync(Hmac(0x01), s_channelB, 9, 900, TestContext.Current.CancellationToken));
            Assert.False(await store.TryAddAsync(Hmac(0x02), s_channelB, 9, 900, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Given_ChainTipPastAnEntry_When_AnotherOnionIsRecorded_Then_TheExpiredEntryIsPrunedFirst()
    {
        // Arrange: an entry expiring at 100, then the chain monitor processes block 150
        await using var node = await StartNodeAsync();
        var store = node.GetRequiredService<IOnionReplayStore>();
        Assert.True(await store.TryAddAsync(Hmac(0x11), s_channelA, 0, 100, TestContext.Current.CancellationToken));
        await InScopeAsync(node, async unitOfWork =>
        {
            unitOfWork.BlockchainStateDbRepository.Add(new BlockchainState(150, new Hash(new byte[32]),
                                                                           DateTime.UtcNow));
            await unitOfWork.SaveChangesAsync();
        });

        // Act
        var added = await store.TryAddAsync(Hmac(0x12), s_channelA, 1, 300, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(added);
        await InScopeAsync(node, async unitOfWork =>
        {
            Assert.Null(await unitOfWork.OnionReplayDbRepository.GetByHmacAsync(Hmac(0x11)));
            Assert.NotNull(await unitOfWork.OnionReplayDbRepository.GetByHmacAsync(Hmac(0x12)));
            Assert.Equal(1, await unitOfWork.OnionReplayDbRepository.CountAsync());
        });
    }

    [Fact]
    public async Task Given_ManyHtlcsWithTheSameOnion_When_RecordedConcurrently_Then_ExactlyOneIsAccepted()
    {
        // Arrange
        await using var node = await StartNodeAsync();
        var store = node.GetRequiredService<IOnionReplayStore>();

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(
                                                                             () => store.TryAddAsync(
                                                                                 Hmac(0x21), s_channelA, (ulong)i,
                                                                                 500,
                                                                                 TestContext.Current
                                                                                    .CancellationToken),
                                                                             TestContext.Current.CancellationToken)));

        // Assert
        Assert.Single(results, r => r);
    }

    /// <summary>The node's layers on the SQLite file (the daemon's registrations), migrated.</summary>
    private async Task<ServiceProvider> StartNodeAsync()
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={_databasePath}"
                           })
                           .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(_keyManager);
        services.AddInfrastructureServices();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddPaymentsServices();
        var provider = services.BuildServiceProvider();

        // The daemon's store is the persistent one
        Assert.IsType<PersistentOnionReplayStore>(provider.GetRequiredService<IOnionReplayStore>());

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private async Task<byte[]> BuildOnionAsync(IServiceProvider node)
    {
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
                                     new OutgoingCltvValueTlv(500),
                                     new PaymentDataTlv(s_paymentSecret, LightningMoney.MilliSatoshis(1_000)));
        using var stream = new MemoryStream();
        await node.GetRequiredService<IHopPayloadSerializer>().SerializeAsync(payload, stream);

        var sessionKey = new PrivKey(Enumerable.Repeat((byte)0x05, 32).ToArray());
        return node.GetRequiredService<ISphinxService>()
                   .Construct([new OnionHop(_keyManager.GetNodePubKey(), stream.ToArray())], sessionKey,
                              s_paymentHash)
                   .ToBytes();
    }

    private static async Task InScopeAsync(IServiceProvider node, Func<IUnitOfWork, Task> action)
    {
        using var scope = node.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }
}