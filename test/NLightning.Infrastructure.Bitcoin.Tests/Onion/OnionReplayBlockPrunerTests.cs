using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Bitcoin.Events;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="OnionReplayBlockPruner"/> (NL-327): the onion replay set is pruned on every new block, not only when an
/// onion arrives. The SQLite proof over the real chain monitor and store is
/// <c>Integration.Tests/Persistence/OnionReplayPersistenceTests</c>.
/// </summary>
public class OnionReplayBlockPrunerTests
{
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IOnionReplayStore> _store = new();
    private readonly ConcurrentQueue<uint> _prunedHeights = new();

    public OnionReplayBlockPrunerTests()
    {
        _store.Setup(s => s.PruneAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
              .Callback<uint, CancellationToken>((height, _) => _prunedHeights.Enqueue(height))
              .ReturnsAsync(0);
    }

    [Fact]
    public async Task Given_Started_When_ABlockArrives_Then_TheStoreIsPrunedAtItsHeight()
    {
        // Arrange
        var pruner = CreatePruner();
        pruner.Start();

        // Act
        RaiseBlock(101);
        await pruner.WhenIdleAsync();
        RaiseBlock(102);
        await pruner.WhenIdleAsync();

        // Assert
        Assert.Equal([101u, 102u], _prunedHeights);
        await pruner.StopAsync();
    }

    [Fact]
    public async Task Given_NotStartedOrStopped_When_ABlockArrives_Then_NothingIsPruned()
    {
        // Arrange
        var pruner = CreatePruner();

        // Act: before Start, then after Stop
        RaiseBlock(101);
        pruner.Start();
        await pruner.StopAsync();
        RaiseBlock(102);
        await pruner.WhenIdleAsync();

        // Assert
        Assert.Empty(_prunedHeights);
        _monitor.VerifyRemove(m => m.OnNewBlockDetected -= It.IsAny<EventHandler<NewBlockEventArgs>>(), Times.Once);
    }

    [Fact]
    public async Task Given_ALowerOrRepeatedHeight_When_ABlockArrives_Then_NothingNewIsPruned()
    {
        // Arrange (a replayed block at start, or a reorg to a shorter branch)
        var pruner = CreatePruner();
        pruner.Start();
        RaiseBlock(110);
        await pruner.WhenIdleAsync();

        // Act
        RaiseBlock(110);
        RaiseBlock(105);
        await pruner.WhenIdleAsync();

        // Assert
        Assert.Equal([110u], _prunedHeights);
        await pruner.StopAsync();
    }

    [Fact]
    public async Task Given_APruneRunning_When_MoreBlocksArrive_Then_OnePruneAtTheHighestHeightFollows()
    {
        // Arrange: the first prune blocks until released
        var release = new TaskCompletionSource();
        var first = true;
        _store.Setup(s => s.PruneAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
              .Returns<uint, CancellationToken>(async (height, _) =>
               {
                   _prunedHeights.Enqueue(height);
                   if (first)
                   {
                       first = false;
                       await release.Task;
                   }

                   return 0;
               });
        var pruner = CreatePruner();
        pruner.Start();
        RaiseBlock(101);
        while (_prunedHeights.IsEmpty)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        // Act
        RaiseBlock(102);
        RaiseBlock(103);
        RaiseBlock(104);
        release.SetResult();
        await pruner.WhenIdleAsync();

        // Assert
        Assert.Equal([101u, 104u], _prunedHeights);
        await pruner.StopAsync();
    }

    [Fact]
    public async Task Given_APruneFails_When_TheNextBlockArrives_Then_ItIsPrunedAgain()
    {
        // Arrange
        var calls = 0;
        _store.Setup(s => s.PruneAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
              .Returns<uint, CancellationToken>((height, _) =>
               {
                   _prunedHeights.Enqueue(height);
                   return ++calls == 1
                              ? Task.FromException<int>(new InvalidOperationException("database is locked"))
                              : Task.FromResult(3);
               });
        var logger = new Mock<ILogger<OnionReplayBlockPruner>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var pruner = new OnionReplayBlockPruner(_monitor.Object, _store.Object, logger.Object);
        pruner.Start();

        // Act
        RaiseBlock(101);
        await pruner.WhenIdleAsync();
        RaiseBlock(102);
        await pruner.WhenIdleAsync();

        // Assert: the failure was logged, and the next block pruned
        Assert.Equal([101u, 102u], _prunedHeights);
        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                                 It.IsAny<InvalidOperationException>(),
                                 It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        await pruner.StopAsync();
    }

    [Fact]
    public void Given_TheExtension_When_Resolved_Then_OneSingletonOverTheMonitorAndStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(_store.Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Act
        services.AddOnionReplayBlockPruner();
        services.AddOnionReplayBlockPruner();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(OnionReplayBlockPruner));
        Assert.Same(provider.GetRequiredService<OnionReplayBlockPruner>(),
                    provider.GetRequiredService<OnionReplayBlockPruner>());
    }

    private OnionReplayBlockPruner CreatePruner() =>
        new(_monitor.Object, _store.Object, NullLogger<OnionReplayBlockPruner>.Instance);

    private void RaiseBlock(uint height) =>
        _monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(height, Hash.Empty));
}