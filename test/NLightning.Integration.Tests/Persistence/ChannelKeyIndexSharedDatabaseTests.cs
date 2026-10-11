using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Interfaces;
using Domain.Exceptions;
using Domain.Node.Fencing;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Repositories;

/// <summary>
/// NL-1388: the channel key index high-water mark in the database. Two processes (two service providers, each with its
/// own key manager whose index starts where an old key file left it) over one SQLite file: an index one of them handed
/// out is never handed out by the other, even when no channel row was ever saved for it, and a process the node write
/// fence refuses cannot hand out any.
/// </summary>
public sealed class ChannelKeyIndexSharedDatabaseTests : IDisposable
{
    private static readonly byte[] s_seed = Enumerable.Repeat((byte)0x5c, 32).ToArray();

    private readonly SqliteFileTestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Given_AnIndexHandedOutWithoutAChannelRow_When_AReplacementProcessOpensAChannel_Then_ItSkipsIt()
    {
        // Arrange: process A hands out index 1 for a negotiation that never saved a channel row, then goes away
        uint first;
        await using (var processA = BuildProcess(out _))
            first = processA.GetRequiredService<ILightningSigner>().CreateNewChannel(out _, out _);

        // A replacement process starts from a key file that never saw index 1
        await using var processB = BuildProcess(out var keysB);

        // Act
        var second = processB.GetRequiredService<ILightningSigner>().CreateNewChannel(out _, out _);

        // Assert
        Assert.Equal(1u, first);
        Assert.Equal(2u, second);
        Assert.Equal(2u, keysB.LastUsedChannelKeyIndex);
        Assert.Equal(2u, processB.GetRequiredService<IChannelKeyIndexStore>().GetHighWater());
    }

    [Fact]
    public async Task Given_TwoRunningProcesses_When_BothOpenChannelsAtOnce_Then_NoIndexIsHandedOutTwice()
    {
        // Arrange: both start from the same key file index
        await using var processA = BuildProcess(out _);
        await using var processB = BuildProcess(out _);
        var signers = new[]
        {
            processA.GetRequiredService<ILightningSigner>(), processB.GetRequiredService<ILightningSigner>()
        };
        var indexes = new ConcurrentBag<uint>();

        // Act
        await Task.WhenAll(Enumerable.Range(0, 2).Select(s => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
                indexes.Add(signers[s].CreateNewChannel(out _, out _));
        }, TestContext.Current.CancellationToken)));

        // Assert
        Assert.Equal(20, indexes.Count);
        Assert.Equal(20, indexes.Distinct().Count());
        Assert.Equal(indexes.Max(), processA.GetRequiredService<IChannelKeyIndexStore>().GetHighWater());
    }

    [Fact]
    public async Task Given_AFencedProcess_When_ItOpensAChannel_Then_ItIsRefusedAndClaimsNothing()
    {
        // Arrange
        await using var process = BuildProcess(out _, new FakeNodeWriteFence { Refuse = true });

        // Act & Assert
        Assert.Throws<SignerException>(() => process.GetRequiredService<ILightningSigner>()
                                                    .CreateNewChannel(out _, out _));
        Assert.Null(process.GetRequiredService<IChannelKeyIndexStore>().GetHighWater());
    }

    [Fact]
    public async Task Given_TheHighWaterMark_When_ClaimedAndRaised_Then_ItOnlyMovesUp()
    {
        // Arrange
        await using var process = BuildProcess(out _);
        var store = process.GetRequiredService<IChannelKeyIndexStore>();

        // Act & Assert
        Assert.Null(store.GetHighWater());
        store.Raise(5);
        Assert.Equal(5u, store.GetHighWater());
        store.Raise(3);
        Assert.Equal(5u, store.GetHighWater());
        Assert.False(store.TryClaim(5));
        Assert.False(store.TryClaim(4));
        Assert.True(store.TryClaim(6));
        Assert.Equal(6u, store.GetHighWater());
    }

    private ServiceProvider BuildProcess(out SecureKeyManager keyManager, INodeWriteFence? writeFence = null)
    {
        keyManager = SecureKeyManager.FromSeed(s_seed.ToArray(), BitcoinNetwork.Regtest, _ => { });
        var services = new ServiceCollection();
        if (writeFence is not null)
            services.AddSingleton(writeFence);
        services.AddLogging();
        services.Configure<NodeOptions>(o => o.BitcoinNetwork = "regtest");
        services.AddSingleton<ISecureKeyManager>(keyManager);
        services.AddBitcoinInfrastructure();
        services.AddRepositoriesInfrastructureServices();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(_database.CreateContext(),
                                                             NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>(),
                                                             writeFence: sp.GetService<INodeWriteFence>()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}