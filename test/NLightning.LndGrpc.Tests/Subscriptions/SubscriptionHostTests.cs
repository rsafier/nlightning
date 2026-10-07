using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;

namespace NLightning.LndGrpc.Tests.Subscriptions;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Payments.Events;
using Application.Payments.Interception;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using LndGrpc.Macaroons;
using LndGrpc.Services;
using LndGrpc.Tls;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;

/// <summary>All five passive feeds through real TLS, HTTP/2 and the generated LND client, with authorization
/// and cancellation verified against source subscriptions. These prove the RPC boundary; cluster tests prove
/// production publication points.</summary>
public sealed class SubscriptionHostTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-subscriptions-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IPeerManager> _peers = new();
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IGraphStore> _graph = new();
    private readonly HtlcEventHub _htlcs = new();
    private ServiceProvider? _services;
    private LndGrpcHost? _host;
    private int _peerReaders, _channelReaders, _transactionReaders, _graphReaders;
    private EventHandler<PeerStateChangedEventArgs>? _peerHandlers;
    private EventHandler<ChannelUpdatedEventArgs>? _channelHandlers;
    private EventHandler<ChannelUpdatedEventArgs>? _channelUpdateHandlers;
    private EventHandler<WalletTransactionEventArgs>? _transactionHandlers;
    private EventHandler<GraphChange>? _graphHandlers;

    public async ValueTask InitializeAsync()
    {
        _peers.SetupAdd(x => x.OnPeerStateChanged += It.IsAny<EventHandler<PeerStateChangedEventArgs>>())
              .Callback((EventHandler<PeerStateChangedEventArgs> handler) =>
              {
                  _peerHandlers += handler;
                  Interlocked.Increment(ref _peerReaders);
              });
        _peers.SetupRemove(x => x.OnPeerStateChanged -= It.IsAny<EventHandler<PeerStateChangedEventArgs>>())
              .Callback((EventHandler<PeerStateChangedEventArgs> handler) =>
              {
                  _peerHandlers -= handler;
                  Interlocked.Decrement(ref _peerReaders);
              });
        _channels.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        _channels.SetupAdd(x => x.OnChannelAdded += It.IsAny<EventHandler<ChannelUpdatedEventArgs>>())
                 .Callback((EventHandler<ChannelUpdatedEventArgs> handler) =>
              {
                  _channelHandlers += handler;
                  Interlocked.Increment(ref _channelReaders);
              });
        _channels.SetupRemove(x => x.OnChannelAdded -= It.IsAny<EventHandler<ChannelUpdatedEventArgs>>())
                 .Callback((EventHandler<ChannelUpdatedEventArgs> handler) =>
              {
                  _channelHandlers -= handler;
                  Interlocked.Decrement(ref _channelReaders);
              });
        _channels.SetupAdd(x => x.OnChannelUpdated += It.IsAny<EventHandler<ChannelUpdatedEventArgs>>())
                 .Callback((EventHandler<ChannelUpdatedEventArgs> handler) => _channelUpdateHandlers += handler);
        _channels.SetupRemove(x => x.OnChannelUpdated -= It.IsAny<EventHandler<ChannelUpdatedEventArgs>>())
                 .Callback((EventHandler<ChannelUpdatedEventArgs> handler) => _channelUpdateHandlers -= handler);
        _monitor.SetupGet(x => x.LastProcessedBlockHeight).Returns(150);
        _monitor.SetupAdd(x => x.OnWalletTransactionObserved += It.IsAny<EventHandler<WalletTransactionEventArgs>>())
                .Callback((EventHandler<WalletTransactionEventArgs> handler) =>
              {
                  _transactionHandlers += handler;
                  Interlocked.Increment(ref _transactionReaders);
              });
        _monitor.SetupRemove(x => x.OnWalletTransactionObserved -= It.IsAny<EventHandler<WalletTransactionEventArgs>>())
                .Callback((EventHandler<WalletTransactionEventArgs> handler) =>
              {
                  _transactionHandlers -= handler;
                  Interlocked.Decrement(ref _transactionReaders);
              });
        _graph.Setup(x => x.GetSnapshot()).Returns(new Mock<IGraphView>().Object);
        _graph.SetupAdd(x => x.GraphChanged += It.IsAny<EventHandler<GraphChange>>())
              .Callback((EventHandler<GraphChange> handler) =>
              {
                  _graphHandlers += handler;
                  Interlocked.Increment(ref _graphReaders);
              });
        _graph.SetupRemove(x => x.GraphChanged -= It.IsAny<EventHandler<GraphChange>>())
              .Callback((EventHandler<GraphChange> handler) =>
              {
                  _graphHandlers -= handler;
                  Interlocked.Decrement(ref _graphReaders);
              });
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        services.AddSingleton(new Mock<ILightningSigner>().Object);
        services.AddSingleton(new Mock<IInvoiceService>().Object);
        services.AddSingleton(new Mock<IPaymentService>().Object);
        services.AddSingleton(_peers.Object);
        services.AddSingleton(_channels.Object);
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(_graph.Object);
        services.AddSingleton<IHtlcEventSource>(_htlcs);
        services.AddSingleton(new HtlcInterceptorHub(NullLogger<HtlcInterceptorHub>.Instance));
        services.AddSingleton<LightningService>();
        services.AddSingleton<RouterService>();
        _services = services.BuildServiceProvider();
        _host = new LndGrpcHost(_services, Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }),
                                _directory, NullLogger<LndGrpcHost>.Instance);
        await _host.StartAsync(Ct);
    }

    [Fact]
    public async Task Given_ReadOnlyClient_When_PeerSessionsChange_Then_OnlineOfflineArriveAndCancellationDetaches()
    {
        using var client = Connect(LndMacaroonFiles.ReadOnlyFileName);
        using var stream = client.LightningClient.SubscribePeerEvents(new PeerEventSubscription(), cancellationToken: Ct);
        await WaitUntilAsync(() => Volatile.Read(ref _peerReaders) == 1);
        var peer = PubKey();
        _peerHandlers!(_peers.Object, new PeerStateChangedEventArgs(peer, true));
        Assert.True(await NextAsync(stream));
        Assert.Equal(peer.ToString(), stream.ResponseStream.Current.PubKey);
        Assert.Equal(PeerEvent.Types.EventType.PeerOnline, stream.ResponseStream.Current.Type);
        _peerHandlers!(_peers.Object, new PeerStateChangedEventArgs(peer, false));
        Assert.True(await NextAsync(stream));
        Assert.Equal(PeerEvent.Types.EventType.PeerOffline, stream.ResponseStream.Current.Type);
        stream.Dispose();
        await WaitUntilAsync(() => Volatile.Read(ref _peerReaders) == 0);
    }

    [Fact]
    public async Task Given_ReadOnlyClient_When_FundingIsPersisted_Then_PendingChannelArrivesAndCancellationDetaches()
    {
        using var client = Connect(LndMacaroonFiles.ReadOnlyFileName);
        using var stream = client.LightningClient.SubscribeChannelEvents(new ChannelEventSubscription(), cancellationToken: Ct);
        await WaitUntilAsync(() => Volatile.Read(ref _channelReaders) == 1);
        var key = PubKey();
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1), 483, LightningMoney.Satoshis(500_000), 144);
        var channel = new ChannelModel(new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, true,
                                                         FeatureSupport.No), new ChannelId(new byte[32]), null,
            new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), key, key, new TxId(new byte[32]), 7),
            true, null, null, LightningMoney.Satoshis(600_000),
            new ChannelKeySetModel(0, key, key, key, key, key, key), 0, 0, LightningMoney.Satoshis(400_000),
            null, 0, key, 0, ChannelState.V1FundingSigned, ChannelVersion.V1);
        _channelHandlers!(_channels.Object, new ChannelUpdatedEventArgs(channel));
        Assert.True(await NextAsync(stream));
        Assert.Equal(ChannelEventUpdate.Types.UpdateType.PendingOpenChannel, stream.ResponseStream.Current.Type);
        Assert.Equal(7U, stream.ResponseStream.Current.PendingOpenChannel.OutputIndex);
        stream.Dispose();
        await WaitUntilAsync(() => Volatile.Read(ref _channelReaders) == 0 && Volatile.Read(ref _peerReaders) == 0);
    }

    [Fact]
    public async Task Given_OpenChannel_When_StagedThenCommittedUpdate_Then_OnlyCommittedSnapshotArrives()
    {
        var key = PubKey();
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1), 483, LightningMoney.Satoshis(500_000), 144);
        var channel = new ChannelModel(new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, true,
                                                         FeatureSupport.No), new ChannelId(new byte[32]), null,
            new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), key, key, new TxId(new byte[32]), 7),
            true, null, null, LightningMoney.Satoshis(600_000),
            new ChannelKeySetModel(0, key, key, key, key, key, key), 0, 0, LightningMoney.Satoshis(400_000),
            null, 0, key, 0, ChannelState.Open, ChannelVersion.V1);
        _channels.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([channel]);
        using var client = Connect(LndMacaroonFiles.ReadOnlyFileName);
        using var stream = client.LightningClient.SubscribeChannelEvents(new ChannelEventSubscription(), cancellationToken: Ct);
        await stream.ResponseHeadersAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _channelUpdateHandlers!(_channels.Object, new ChannelUpdatedEventArgs(channel, false));
        _channelUpdateHandlers!(_channels.Object, new ChannelUpdatedEventArgs(channel));
        // The producer already captured the committed point; subsequent shared-model changes cannot alter it.
        channel.FundingOutput!.Index = 9;
        Assert.True(await NextAsync(stream));
        var update = stream.ResponseStream.Current;
        Assert.Equal(ChannelEventUpdate.Types.UpdateType.ChannelUpdate, update.Type);
        Assert.NotNull(update.UpdatedChannel);
        Assert.Equal(key.ToString(), update.UpdatedChannel.Channel.RemotePubkey);
        Assert.Equal(1_000_000L, update.UpdatedChannel.Channel.Capacity);
        Assert.Equal(new string('0', 64) + ":7", update.UpdatedChannel.Channel.ChannelPoint);
        Assert.Equal("ChanStatusDefault", update.UpdatedChannel.Channel.ChanStatusFlags);
        stream.Dispose();
        await WaitUntilAsync(() => _channelUpdateHandlers is null);
    }

    [Fact]
    public async Task Given_ReadOnlyClient_When_WalletTransactionConfirmsThenRewinds_Then_BothStatesArrive()
    {
        using var client = Connect(LndMacaroonFiles.ReadOnlyFileName);
        using var stream = client.LightningClient.SubscribeTransactions(new GetTransactionsRequest(), cancellationToken: Ct);
        await WaitUntilAsync(() => Volatile.Read(ref _transactionReaders) == 1);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new NBitcoin.OutPoint(new uint256(1), 0)));
        tx.Outputs.Add(Money.Satoshis(8_000), new Key().PubKey.WitHash.ScriptPubKey);
        _transactionHandlers!(_monitor.Object,
            new WalletTransactionEventArgs(tx.ToHex(), 8_000, 0, 150, new string('1', 64), DateTimeOffset.UnixEpoch,
                                           "deposit", [0], [], tx.GetHash().ToString()));
        Assert.True(await NextAsync(stream));
        Assert.Equal(tx.GetHash().ToString(), stream.ResponseStream.Current.TxHash);
        Assert.Equal(8_000, stream.ResponseStream.Current.Amount);
        Assert.Equal(1, stream.ResponseStream.Current.NumConfirmations);
        Assert.True(Assert.Single(stream.ResponseStream.Current.OutputDetails).IsOurAddress);
        _transactionHandlers!(_monitor.Object,
            new WalletTransactionEventArgs(tx.ToHex(), 8_000, 0, 0, "", DateTimeOffset.UnixEpoch, "deposit", [0], [], tx.GetHash().ToString()));
        Assert.True(await NextAsync(stream));
        Assert.Equal(0, stream.ResponseStream.Current.NumConfirmations);
        stream.Dispose();
        await WaitUntilAsync(() => Volatile.Read(ref _transactionReaders) == 0);
    }

    [Fact]
    public async Task Given_ReadOnlyClient_When_AnAnnouncementIsAccepted_Then_GraphUpdateArrivesAndCancellationDetaches()
    {
        using var client = Connect(LndMacaroonFiles.ReadOnlyFileName);
        using var stream = client.LightningClient.SubscribeChannelGraph(new GraphTopologySubscription(), cancellationToken: Ct);
        await WaitUntilAsync(() => Volatile.Read(ref _graphReaders) == 1);
        var alias = new byte[32];
        "subscriber-peer"u8.CopyTo(alias);
        var node = new GraphNode(PubKey(), 42, ReadOnlyMemory<byte>.Empty, alias, [1, 2, 3]);
        _graphHandlers!(_graph.Object, new GraphChange(Node: node));
        Assert.True(await NextAsync(stream));
        var update = Assert.Single(stream.ResponseStream.Current.NodeUpdates);
        Assert.Equal(node.NodeId.ToString(), update.IdentityKey);
        Assert.Equal("subscriber-peer", update.Alias);
        Assert.Equal("#010203", update.Color);
        stream.Dispose();
        await WaitUntilAsync(() => Volatile.Read(ref _graphReaders) == 0);
    }

    [Fact]
    public async Task Given_ReadOnlyClient_When_HtlcsForwardFailAndSettle_Then_OrderedEventsArriveAndCancellationDetaches()
    {
        using var client = Connect(LndMacaroonFiles.ReadOnlyFileName);
        _htlcs.Publish(new HtlcActivityEvent(HtlcActivityKind.Forward, HtlcActivityRole.Forward, 999, 9, 998, 8,
                                            DateTimeOffset.UnixEpoch));
        using var stream = client.RouterClient.SubscribeHtlcEvents(new SubscribeHtlcEventsRequest(), cancellationToken: Ct);
        await WaitUntilAsync(() => _htlcs.SubscriberCount == 1);
        Assert.True(await NextAsync(stream));
        Assert.NotNull(stream.ResponseStream.Current.SubscribedEvent);
        _htlcs.Publish(new HtlcActivityEvent(HtlcActivityKind.Forward, HtlcActivityRole.Forward, 101, 2, 102, 3,
                                            DateTimeOffset.UnixEpoch, 10_100, 500, 10_000, 460));
        _htlcs.Publish(new HtlcActivityEvent(HtlcActivityKind.ForwardFail, HtlcActivityRole.Forward, 101, 2, 102, 3,
                                            DateTimeOffset.UnixEpoch.AddSeconds(1)));
        var preimage = Enumerable.Repeat((byte)7, 32).ToArray();
        _htlcs.Publish(new HtlcActivityEvent(HtlcActivityKind.Settle, HtlcActivityRole.Receive, 101, 4, 0, 0,
                                            DateTimeOffset.UnixEpoch.AddSeconds(2), Preimage: preimage));
        Assert.True(await NextAsync(stream));
        var forward = stream.ResponseStream.Current;
        Assert.Equal(101UL, forward.IncomingChannelId);
        Assert.Equal(3UL, forward.OutgoingHtlcId);
        Assert.Equal(10_100UL, forward.ForwardEvent.Info.IncomingAmtMsat);
        Assert.True(await NextAsync(stream));
        Assert.NotNull(stream.ResponseStream.Current.ForwardFailEvent);
        Assert.True(await NextAsync(stream));
        Assert.Equal(HtlcEvent.Types.EventType.Receive, stream.ResponseStream.Current.EventType);
        Assert.Equal(preimage, stream.ResponseStream.Current.SettleEvent.Preimage.ToByteArray());
        stream.Dispose();
        await WaitUntilAsync(() => _htlcs.SubscriberCount == 0);
    }

    [Fact]
    public async Task Given_InvoiceOnlyClient_When_Subscribing_Then_OnlyOnchainFeedIsAuthorized()
    {
        using var client = Connect(LndMacaroonFiles.InvoiceFileName);
        using var peers = client.LightningClient.SubscribePeerEvents(new PeerEventSubscription(), cancellationToken: Ct);
        using var channels = client.LightningClient.SubscribeChannelEvents(new ChannelEventSubscription(), cancellationToken: Ct);
        using var graph = client.LightningClient.SubscribeChannelGraph(new GraphTopologySubscription(), cancellationToken: Ct);
        using var htlcs = client.RouterClient.SubscribeHtlcEvents(new SubscribeHtlcEventsRequest(), cancellationToken: Ct);
        foreach (var denied in new Func<Task<bool>>[]
                 { () => NextAsync(peers), () => NextAsync(channels), () => NextAsync(graph), () => NextAsync(htlcs) })
            Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(denied)).StatusCode);
        Assert.Equal(0, _peerReaders);
        Assert.Equal(0, _channelReaders);
        Assert.Equal(0, _graphReaders);
        Assert.Equal(0, _htlcs.SubscriberCount);
        using var transactions = client.LightningClient.SubscribeTransactions(new GetTransactionsRequest(), cancellationToken: Ct);
        await WaitUntilAsync(() => Volatile.Read(ref _transactionReaders) == 1);
        transactions.Dispose();
        await WaitUntilAsync(() => Volatile.Read(ref _transactionReaders) == 0);
    }

    private static CompactPubKey PubKey() => new(Convert.FromHexString(
        "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa"));

    private static async Task<bool> NextAsync<T>(AsyncServerStreamingCall<T> stream) =>
        await stream.ResponseStream.MoveNext(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private LndNodeConnection Connect(string macaroon) => LndNodeConnection.CreateWithoutNodeInfo(
        LndSettings.FromFiles($"https://127.0.0.1:{_host!.BoundPort}",
            Path.Combine(_directory, LndTlsFiles.CertificateFileName), Path.Combine(_directory, macaroon)));

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        if (_services is not null)
            await _services.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}