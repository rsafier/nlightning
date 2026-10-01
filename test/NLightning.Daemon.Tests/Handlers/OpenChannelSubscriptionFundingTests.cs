using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// NL-535: the open subscription reports every published funding transaction of a channel waiting for its funding (a
/// dual-funded open's first attempt at once, then each RBF attempt of either side) when the client asks for it, and a
/// signed funding survives the peer's disconnection and the client's Ctrl-C.
/// </summary>
public sealed class OpenChannelSubscriptionFundingTests : IDisposable
{
    private static readonly CompactPubKey s_peerId = CreatePubKey(0x02);
    private static readonly TxId s_first = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly TxId s_second = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IUtxoMemoryRepository> _utxos = new();
    private readonly Mock<IPeerService> _peerService = new();
    private readonly HashSet<TxId> _published = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;
    private readonly OpenChannelClientSubscriptionHandler _handler;

    public OpenChannelSubscriptionFundingTests()
    {
        _channel = CreateDualFundedChannel(s_first);
        var channel = _channel;
        _memory.Setup(m => m.TryGetChannel(_channel.ChannelId, out channel)).Returns(true);
        _peerService.Setup(p => p.Features).Returns(new FeatureOptions());
        var peer = new PeerModel(s_peerId, "localhost", 9735, "ipv4");
        peer.SetPeerService(_peerService.Object);
        _peerManager.Setup(m => m.GetPeer(s_peerId)).Returns(peer);

        // The broadcast rows: an attempt is published once its row exists
        var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
        broadcasts.Setup(b => b.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) =>
                   {
                       lock (_published)
                           return _published.Contains(txId)
                                      ? new BroadcastTransactionModel(new SignedTransaction(txId, [0x01]),
                                                                      BroadcastPurpose.Funding, _channel.ChannelId, 100)
                                      : null;
                   });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        _provider = services.BuildServiceProvider();

        _handler = new OpenChannelClientSubscriptionHandler(
            _memory.Object, new Mock<ILogger<OpenChannelClientSubscriptionHandler>>().Object, _peerManager.Object,
            _utxos.Object, _provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task Given_ADualFundedOpenAlreadyPublished_When_SubscribedWithNothingPrinted_Then_AnsweredAtOnce()
    {
        // Arrange: the first attempt was signed and published before the client's first subscription call
        Publish(s_first);

        // Act
        var response = await _handler.HandleAsync(Request(null), TestContext.Current.CancellationToken)
                                     .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.V1FundingSigned, response.ChannelState);
        Assert.Equal(s_first, response.TxId);
        Assert.Equal(1u, response.Index);
    }

    [Fact]
    public async Task Given_TheKnownFunding_When_AnRbfAttemptIsPublished_Then_AnsweredWithTheNewTxId()
    {
        // Arrange
        Publish(s_first);
        var handle = _handler.HandleAsync(Request(s_first), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(handle.IsCompleted);

        // Act: the RBF attempt moves the funding, is signed and published, and the channel is updated
        _channel.FundingOutput!.TransactionId = s_second;
        Publish(s_second);
        RaiseUpdated();
        var response = await handle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.V1FundingSigned, response.ChannelState);
        Assert.Equal(s_second, response.TxId);
    }

    [Fact]
    public async Task Given_AnRbfAttemptStillBeingSigned_When_TheChannelIsUpdated_Then_ItIsNotReportedUntilPublished()
    {
        // Arrange: the attempt moved the channel's funding output at its commitment step, before tx_signatures
        Publish(s_first);
        var handle = _handler.HandleAsync(Request(s_first), TestContext.Current.CancellationToken);
        _channel.FundingOutput!.TransactionId = s_second;

        // Act
        RaiseUpdated();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var reportedBeforePublish = handle.IsCompleted;
        Publish(s_second);
        RaiseUpdated();
        var response = await handle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(reportedBeforePublish);
        Assert.Equal(s_second, response.TxId);
    }

    [Fact]
    public async Task Given_TheKnownFunding_When_AnUpdateKeepsIt_Then_StillWaitingUntilReady()
    {
        // Arrange
        Publish(s_first);
        var handle = _handler.HandleAsync(Request(s_first), TestContext.Current.CancellationToken);

        // Act: an update that changes nothing about the funding, then channel_ready
        RaiseUpdated();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var answeredOnSameFunding = handle.IsCompleted;
        _channel.UpdateState(ChannelState.ReadyForUs);
        RaiseUpdated();
        var response = await handle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(answeredOnSameFunding);
        Assert.Equal(ChannelState.ReadyForUs, response.ChannelState);
        Assert.Equal(s_first, response.TxId);
    }

    [Fact]
    public async Task Given_ASignedFunding_When_ThePeerDisconnects_Then_TheWaitGoesOn()
    {
        // Arrange
        Publish(s_first);
        var handle = _handler.HandleAsync(Request(s_first), TestContext.Current.CancellationToken);

        // Act
        _peerService.Raise(p => p.OnDisconnect += null, this, new PeerDisconnectedEventArgs(s_peerId));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var faulted = handle.IsCompleted;
        _channel.UpdateState(ChannelState.ReadyForThem);
        RaiseUpdated();
        var response = await handle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: the reconnection reestablishes the channel; the open was never failed
        Assert.False(faulted);
        Assert.Equal(ChannelState.ReadyForUs, response.ChannelState);
    }

    [Fact]
    public async Task Given_ASignedFundingAndNoConnectedPeer_When_Subscribed_Then_AnsweredWithoutThePeer()
    {
        // Arrange
        _peerManager.Setup(m => m.GetPeer(s_peerId)).Returns((PeerModel?)null);
        Publish(s_first);

        // Act
        var response = await _handler.HandleAsync(Request(null), TestContext.Current.CancellationToken)
                                     .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_first, response.TxId);
    }

    [Fact]
    public async Task Given_TheClientStopsWaiting_When_Cancelled_Then_TheOpenKeepsItsWalletOutputs()
    {
        // Arrange: a v1 open still negotiating (UTXOs locked), the client presses Ctrl-C
        var opening = CreateDualFundedChannel(s_first, ChannelState.V1Opening);
        _memory.Setup(m => m.TryGetChannel(opening.ChannelId, out opening)).Returns(true);
        _utxos.Setup(u => u.GetLockedUtxosForChannel(opening.ChannelId))
              .Returns([
                   new Domain.Bitcoin.Wallet.Models.UtxoModel(new TxId(new byte[32]), 0,
                                                              LightningMoney.Satoshis(1_000_000), 100, 0, false,
                                                              Domain.Bitcoin.Enums.AddressType.P2Wpkh)
               ]);
        using var cancellation = new CancellationTokenSource();

        // Act
        var handle = _handler.HandleAsync(new OpenChannelClientSubscriptionRequest(opening.ChannelId)
        {
            ReportFundingChanges = true
        }, cancellation.Token);
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle);
        _utxos.Verify(u => u.ReturnUtxosNotSpentOnChannel(It.IsAny<ChannelId>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnOlderClient_When_TheFundingWasSignedBeforeSubscribing_Then_AnsweredAtOnce()
    {
        // Arrange (NL-295): no ReportFundingChanges, and the funding was already signed and published before the
        // call: the current state answers, without waiting for the channel's next update
        Publish(s_first);

        // Act
        var response = await _handler.HandleAsync(new OpenChannelClientSubscriptionRequest(_channel.ChannelId),
                                                  TestContext.Current.CancellationToken)
                                     .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.V1FundingSigned, response.ChannelState);
        Assert.Equal(s_first, response.TxId);
    }

    public void Dispose() => _provider.Dispose();

    private OpenChannelClientSubscriptionRequest Request(TxId? known) =>
        new(_channel.ChannelId) { ReportFundingChanges = true, KnownFundingTxId = known };

    private void Publish(TxId txId)
    {
        lock (_published)
            _published.Add(txId);
    }

    private void RaiseUpdated() =>
        _memory.Raise(m => m.OnChannelUpdated += null, this, new ChannelUpdatedEventArgs(_channel));

    private static ChannelModel CreateDualFundedChannel(TxId fundingTxId,
                                                        ChannelState state = ChannelState.V1FundingSigned)
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        var key = CreatePubKey(0x03);
        var funding = new FundingOutputInfo(LightningMoney.Satoshis(500_000), key, s_peerId, fundingTxId, 1);
        return new ChannelModel(new ChannelParams(), new ChannelId(bytes), null, funding, true, null, null,
                                LightningMoney.Satoshis(250_000),
                                new ChannelKeySetModel(0, key, key, key, key, key, key), 0, 0,
                                LightningMoney.Satoshis(250_000), null, 0, s_peerId, 0, state, ChannelVersion.V2);
    }

    private static CompactPubKey CreatePubKey(byte prefix)
    {
        var bytes = new byte[33];
        bytes[0] = prefix;
        for (var i = 1; i < 33; i++)
            bytes[i] = (byte)(i + prefix);
        return new CompactPubKey(bytes);
    }
}