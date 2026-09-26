using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Onchain.Anchors;

using Application.Channels.Safety;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain.Anchors;
using Application.Protocol.Factories;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// BOLT 5 plan O7-T2: the fail-the-channel path hands an anchor channel's commitment to the CPFP service right after
/// the publish (outside the lock), and starts and stops it with itself; a channel without anchors is never handed over.
/// </summary>
public sealed class AnchorCpfpHookTests : IDisposable
{
    private readonly Mock<IAnchorCpfpService> _cpfp = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly InMemoryBroadcasts _store = new();
    private RealSigningCommitmentPair _pair = null!;
    private ChannelModel _channel = null!;
    private ServiceProvider _provider = null!;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Given_FailedChannel_When_CommitmentPublished_Then_AnchorChannelHandedToCpfp(bool hasAnchors,
                                                                                                 int expectedCalls)
    {
        // Arrange
        Init(hasAnchors);
        var service = _provider.GetRequiredService<ChannelFailureService>();

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("test", "failed"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
        _cpfp.Verify(c => c.OnCommitmentBroadcastAsync(_channel.ChannelId, It.IsAny<CancellationToken>()),
                     Times.Exactly(expectedCalls));
    }

    [Fact]
    public async Task Given_CpfpThrows_When_ChannelFailed_Then_FailureStillCompletes()
    {
        // Arrange
        Init(hasAnchors: true);
        _cpfp.Setup(c => c.OnCommitmentBroadcastAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("boom"));
        var service = _provider.GetRequiredService<ChannelFailureService>();

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("test", "failed"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
    }

    [Fact]
    public void Given_FailureService_When_StartedAndStopped_Then_CpfpStartedAndStopped()
    {
        // Arrange
        Init(hasAnchors: true);
        var service = _provider.GetRequiredService<ChannelFailureService>();

        // Act
        service.Start();
        service.Stop();

        // Assert
        _cpfp.Verify(c => c.Start(), Times.Once);
        _cpfp.Verify(c => c.Stop(), Times.Once);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _pair?.Dispose();
    }

    private void Init(bool hasAnchors)
    {
        _pair = new RealSigningCommitmentPair(hasAnchors);
        _channel = _pair.Alice.Channel;
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(500);
        _monitor.Setup(m => m.PublishAsync(It.IsAny<Domain.Onchain.Models.BroadcastTransactionModel>()))
                .ReturnsAsync(true);

        var watched = new Mock<IWatchedTransactionDbRepository>();
        watched.Setup(r => r.GetAllPendingAsync()).ReturnsAsync([]);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);
        unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(watched.Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_store);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        var errorSender = new Mock<IChannelErrorSender>();
        errorSender.Setup(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>())).ReturnsAsync(true);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(errorSender.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton(_cpfp.Object);
        services.AddChannelSafetyServices();
        _provider = services.BuildServiceProvider();
    }
}