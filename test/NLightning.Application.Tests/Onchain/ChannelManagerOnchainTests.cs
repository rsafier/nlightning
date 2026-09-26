using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Managers;
using Application.Channels.Services;
using Application.Onchain.Interfaces;
using Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The channel manager's BOLT 5 seams: the signer's invariant S1 restored at registration from the persisted
/// commitment broadcast (NL-297), and funding spends, resolution-output spends and new blocks routed to the on-chain
/// watcher and executor (O2-T5).
/// </summary>
public sealed class ChannelManagerOnchainTests : IDisposable
{
    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IOnchainChannelWatcher> _watcher = new();
    private readonly Mock<IOnchainResolutionExecutor> _executor = new();
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public ChannelManagerOnchainTests()
    {
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel = _pair.Alice.Channel;
        _channel.UpdateState(ChannelState.Failed);
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => predicate(_channel) ? [_channel] : []);

        var services = new ServiceCollection();
        services.AddScoped(_ => _store.CreateUnitOfWork().Object);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddSingleton(_watcher.Object);
        services.AddSingleton(_executor.Object);
        _provider = services.BuildServiceProvider();
    }

    private ChannelManager CreateChannelManager() =>
        new(_monitor.Object, new ChannelLockProvider(), _memory.Object, NullLogger<ChannelManager>.Instance,
            _pair.Alice.Signer, _provider);

    [Fact]
    public async Task Given_PersistedCommitmentBroadcast_When_ChannelRegistered_Then_SignerRefusesToRevokeIt()
    {
        // Arrange (NL-297): the failure service saved our commitment L for broadcast before a restart
        var number = _pair.Alice.State.LocalCommit.Number;
        _store.Broadcasts.Add(new BroadcastTransactionModel(
                                  new SignedTransaction(new TxId(Enumerable.Repeat((byte)0x31, 32).ToArray()),
                                                        new byte[60]), BroadcastPurpose.LocalCommitment,
                                  _channel.ChannelId, 500, commitmentNumber: number));

        // Act
        await CreateChannelManager().RegisterExistingChannelAsync(_channel);

        // Assert (S1): the mark is back before any connection; the signer never moves past L, so the secret of L is
        // never released
        Assert.True(_pair.Alice.Signer.TryGetBroadcastSignedCommitment(_channel.ChannelId, out var marked));
        Assert.Equal(number, marked);
        Assert.Throws<SignerException>(() => _pair.Alice.Signer.AdvanceLocalCommitment(_channel.ChannelId,
                                                                                      number + 1));
        Assert.Throws<SignerException>(() => _pair.Alice.Signer.RevealPerCommitmentSecret(_channel.ChannelId,
                                                                                         number));
    }

    [Fact]
    public async Task Given_NoCommitmentBroadcast_When_ChannelRegistered_Then_NoMark()
    {
        // Arrange: a funding broadcast only
        _store.Broadcasts.Add(new BroadcastTransactionModel(
                                  new SignedTransaction(new TxId(Enumerable.Repeat((byte)0x32, 32).ToArray()),
                                                        new byte[60]), BroadcastPurpose.Funding,
                                  _channel.ChannelId, 400));

        // Act
        await CreateChannelManager().RegisterExistingChannelAsync(_channel);

        // Assert: without the mark the signer may move on (the control of the test above)
        Assert.False(_pair.Alice.Signer.TryGetBroadcastSignedCommitment(_channel.ChannelId, out _));
        _pair.Alice.Signer.AdvanceLocalCommitment(_channel.ChannelId, _pair.Alice.State.LocalCommit.Number + 1);
    }

    [Fact]
    public async Task Given_FundingOutputSpent_When_Raised_Then_TheWatcherGetsIt()
    {
        // Arrange
        CreateChannelManager();
        var args = new OutpointSpentEventArgs(_channel.ChannelId, Spend(0x41), 700, 1,
                                              _channel.FundingOutput!.TransactionId!.Value,
                                              _channel.FundingOutput.Index!.Value);
        var handled = new TaskCompletionSource();
        _watcher.Setup(w => w.HandleFundingSpentAsync(args, It.IsAny<CancellationToken>()))
                .Callback(() => handled.TrySetResult())
                .ReturnsAsync((FundingSpendOutcome?)null);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, args);

        // Assert
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _executor.Verify(e => e.HandleOutputSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_ResolutionOutputSpent_When_Raised_Then_TheExecutorGetsIt()
    {
        // Arrange
        CreateChannelManager();
        var args = new OutpointSpentEventArgs(_channel.ChannelId, Spend(0x42), 700, 1,
                                              new TxId(Enumerable.Repeat((byte)0x43, 32).ToArray()), 2);
        var handled = new TaskCompletionSource();
        _executor.Setup(e => e.HandleOutputSpentAsync(args, It.IsAny<CancellationToken>()))
                 .Callback(() => handled.TrySetResult())
                 .Returns(Task.CompletedTask);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, args);

        // Assert
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Given_NewBlock_When_Raised_Then_AResolutionRoundIsScheduled()
    {
        // Arrange
        CreateChannelManager();

        // Act
        _monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(800, new byte[32]));

        // Assert
        _executor.Verify(e => e.ScheduleRound(800), Times.Once);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private static SignedTransaction Spend(byte seed) =>
        new(new TxId(Enumerable.Repeat(seed, 32).ToArray()), new byte[60]);
}