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

    [Theory]
    [InlineData(ChannelState.Failed, true)]
    [InlineData(ChannelState.OnchainResolving, true)]
    [InlineData(ChannelState.Open, false)]
    public async Task Given_ASpliceSpendOfTheFundingOutput_When_Raised_Then_TheWatcherGetsItOnlyWhenTheChannelFailed(
        ChannelState state, bool expectWatcher)
    {
        // Arrange (SP2-C-T2): a pending splice of the channel confirms. An open channel stays on its fundings; a failed
        // one (or one whose recorded close the splice replaced after a reorg) needs our commitment on the splice funding
        var spliceTx = Spend(0x44);
        var current = Domain.Channels.Splicing.ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!;
        var splice = current with
        {
            FundingTxId = spliceTx.TxId,
            Kind = Domain.Channels.Splicing.Enums.ChannelFundingKind.Splice,
            Status = Domain.Channels.Splicing.Enums.ChannelFundingStatus.Pending
        };
        var port = new Mock<Application.Channels.Splicing.Interfaces.ISpliceStatePort>();
        port.Setup(p => p.GetFundings(It.IsAny<ChannelModel>()))
            .Returns(new Domain.Channels.Splicing.FundingSet(current, [splice]));
        using var openPair = new RealSigningCommitmentPair(hasAnchors: false);
        var channel = state == ChannelState.Open ? openPair.Alice.Channel : _channel;
        Assert.Equal(state == ChannelState.Open, channel.State == ChannelState.Open);
        if (state == ChannelState.OnchainResolving)
            _channel.UpdateState(ChannelState.OnchainResolving);
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? found) =>
                {
                    found = channel;
                    return id == channel.ChannelId;
                }));
        var services = new ServiceCollection();
        services.AddScoped(_ => _store.CreateUnitOfWork().Object);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddSingleton(_watcher.Object);
        services.AddSingleton(_executor.Object);
        services.AddSingleton(port.Object);
        await using var provider = services.BuildServiceProvider();
        _ = new ChannelManager(_monitor.Object, new ChannelLockProvider(), _memory.Object,
                               NullLogger<ChannelManager>.Instance, _pair.Alice.Signer, provider);
        var args = new OutpointSpentEventArgs(channel.ChannelId, spliceTx, 700, 1,
                                              channel.FundingOutput!.TransactionId!.Value,
                                              channel.FundingOutput.Index!.Value);
        var handled = new TaskCompletionSource();
        _watcher.Setup(w => w.HandleFundingSpentAsync(args, It.IsAny<CancellationToken>()))
                .Callback(() => handled.TrySetResult())
                .ReturnsAsync((FundingSpendOutcome?)null);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, args);

        // Assert
        if (expectWatcher)
        {
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        else
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                           It.IsAny<CancellationToken>()), Times.Never);
        }

        _executor.Verify(e => e.HandleOutputSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_ASpliceAttemptConfirms_When_Raised_Then_ItsRbfSiblingsAreNoLongerRebroadcast()
    {
        // Arrange (NL-534): an open channel with a splice and its RBF sibling pending, both broadcast; the sibling
        // confirms first, so the original attempt double-spends it and bitcoind refuses it after every block
        using var openPair = new RealSigningCommitmentPair(hasAnchors: false);
        var channel = openPair.Alice.Channel;
        var winner = Spend(0x45);
        var loserTxId = new TxId(Enumerable.Repeat((byte)0x46, 32).ToArray());
        var current = Domain.Channels.Splicing.ChannelFunding.FromFundingOutput(channel.FundingOutput!)!;
        var loser = current with
        {
            FundingTxId = loserTxId,
            Kind = Domain.Channels.Splicing.Enums.ChannelFundingKind.Splice,
            Status = Domain.Channels.Splicing.Enums.ChannelFundingStatus.Pending
        };
        var sibling = loser with
        {
            FundingTxId = winner.TxId,
            Kind = Domain.Channels.Splicing.Enums.ChannelFundingKind.SpliceRbf,
            RbfOf = loserTxId
        };
        var port = new Mock<Application.Channels.Splicing.Interfaces.ISpliceStatePort>();
        port.Setup(p => p.GetFundings(It.IsAny<ChannelModel>()))
            .Returns(new Domain.Channels.Splicing.FundingSet(current, [loser, sibling]));
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? found) =>
                {
                    found = channel;
                    return id == channel.ChannelId;
                }));
        foreach (var txId in new[] { loserTxId, winner.TxId })
            _store.Broadcasts.Add(new BroadcastTransactionModel(new SignedTransaction(txId, new byte[60]),
                                                                BroadcastPurpose.Funding, channel.ChannelId, 600));
        var services = new ServiceCollection();
        services.AddScoped(_ => _store.CreateUnitOfWork().Object);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddSingleton(_watcher.Object);
        services.AddSingleton(_executor.Object);
        services.AddSingleton(port.Object);
        await using var provider = services.BuildServiceProvider();
        _ = new ChannelManager(_monitor.Object, new ChannelLockProvider(), _memory.Object,
                               NullLogger<ChannelManager>.Instance, _pair.Alice.Signer, provider);
        var args = new OutpointSpentEventArgs(channel.ChannelId, winner, 700, 1,
                                              channel.FundingOutput!.TransactionId!.Value,
                                              channel.FundingOutput.Index!.Value);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, args);

        // Assert: the losing attempt abandoned at the winner's first confirmation, the winner still pending (a reorg
        // sends it again), nothing handed to the watcher
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_store.Broadcasts.Single(b => b.TransactionId == loserTxId).State == BroadcastState.Pending
            && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Abandoned, _store.Broadcasts.Single(b => b.TransactionId == loserTxId).State);
        Assert.Equal(BroadcastState.Pending, _store.Broadcasts.Single(b => b.TransactionId == winner.TxId).State);
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
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