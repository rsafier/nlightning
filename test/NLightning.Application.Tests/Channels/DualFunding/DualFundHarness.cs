using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.DualFunding;
using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Application.Gossip;
using Application.InteractiveTx;
using Application.LiquidityAds;
using Application.Payments;
using Application.Payments.Routing;
using Application.Payments.Switch;
using Application.Protocol.Factories;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Validators;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Onchain.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Harness;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Protocol.Onion;
using Infrastructure.Repositories;
using Infrastructure.Serialization;
using InteractiveTx.TestDoubles;
using NLightning.Tests.Utils;
using TestUtils;

/// <summary>
/// Two in-process nodes for the dual-funded open (splicing plan wave DF): each a real <see cref="ChannelManager"/> with
/// the production <c>open_channel2</c>/<c>accept_channel2</c> handlers, <see cref="DualFundedOpenService"/>,
/// <see cref="InteractiveTxDriver"/> over the Domain engine, commitment signing, channel_ready and normal-operation
/// handlers, the HTLC switch and invoices, on its own SQLite database (the production unit of work and repositories).
/// The wallet side of the negotiation is the test contributor, builder and prevtx inspector
/// (<c>InteractiveTx/TestDoubles</c>): the funding transaction is a stand-in that is never broadcast for real.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class DualFundHarness : IAsyncDisposable
{
    public const uint BlockHeight = 500;
    public const uint FundingHeight = 497;

    private readonly string _directory;
    private readonly ConcurrentDictionary<(string From, string To), ConcurrentQueue<IChannelMessage>> _links = new();

    public DualFundNode Alice { get; }
    public DualFundNode Bob { get; }
    public IReadOnlyList<DualFundNode> Nodes => [Alice, Bob];

    /// <summary>Every message delivered, in order.</summary>
    public List<(string From, IChannelMessage Message)> Transcript { get; } = [];

    /// <summary>When set, messages sent while it returns true are dropped (a link that is down).</summary>
    public bool LinkDown { get; set; }

    /// <summary>What the nodes negotiated (<c>option_dual_fund</c> and the defaults, anchors included).</summary>
    public FeatureOptions NegotiatedFeatures { get; set; } = new() { DualFund = FeatureSupport.Optional };

    /// <summary>How long <c>OpenAsync</c>/<c>BumpAsync</c> wait (the nodes' <c>Node:DualFund:OpenTimeout</c>).</summary>
    public TimeSpan OpenTimeout { get; }

    /// <summary>The nodes' clock (stepped): tests advance it to fire the open watchdog deterministically (NL-512).
    /// </summary>
    public SteppedClockProvider Clock { get; } = new();

    /// <summary>The nodes' <c>Node:DualFund:AllowRbf</c>.</summary>
    public bool AllowRbf { get; }

    /// <summary>
    /// Whether each node has a mocked peer manager whose peer is a mocked <c>IPeerService</c>
    /// (<see cref="DualFundNode.PeerService"/>), so a test can raise the peer's error or warning.
    /// </summary>
    public bool WithPeerServices { get; }

    private DualFundHarness(string directory, long bobContributionSat, TimeSpan openTimeout, bool allowRbf,
                            bool withPeerServices)
    {
        _directory = directory;
        OpenTimeout = openTimeout;
        AllowRbf = allowRbf;
        WithPeerServices = withPeerServices;
        Alice = new DualFundNode(this, "Alice", 0xA1, Path.Combine(directory, "alice.db"), 0);
        Bob = new DualFundNode(this, "Bob", 0xB0, Path.Combine(directory, "bob.db"), bobContributionSat);
    }

    /// <summary>The nodes' fee estimate in sat/kw (default 2,500), read on every call.</summary>
    public long FeeEstimatePerKw { get; set; } = 2_500;

    /// <summary>The nodes' open timeout unless a test sets one.</summary>
    public static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromSeconds(60);

    /// <param name="bobContributionSat">What Bob (the accepter) contributes to Alice's opens.</param>
    /// <param name="openTimeout">The nodes' open timeout (default 60 s: the open watchdog runs on the wall clock, and
    /// under a loaded full run a 10 s default fired before the pump reached <c>commitment_signed</c>, failing tests
    /// that never meant to time out after about 30 s; tests of the timeout pass their own).</param>
    /// <param name="allowRbf">The nodes' <c>Node:DualFund:AllowRbf</c> (default true, as in production).</param>
    /// <param name="withPeerServices">See <see cref="WithPeerServices"/>.</param>
    public static async Task<DualFundHarness> CreateAsync(long bobContributionSat, TimeSpan? openTimeout = null,
                                                          bool allowRbf = true, bool withPeerServices = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nltg-dual-fund-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var harness = new DualFundHarness(directory, bobContributionSat, openTimeout ?? DefaultOpenTimeout,
                                          allowRbf, withPeerServices);
        foreach (var node in harness.Nodes)
            await node.StartAsync(migrate: true);
        return harness;
    }

    public DualFundNode Other(DualFundNode node) => node == Alice ? Bob : Alice;

    /// <summary>The transcript and every error the nodes raised, for assertion messages.</summary>
    public string Describe() =>
        string.Join(Environment.NewLine,
                    Transcript.Select(t => $"{t.From}: {t.Message.Type}")
                              .Concat(Nodes.SelectMany(n => n.Errors.Select(e => $"{n.Name} error: {e.Message}"))));

    /// <summary>
    /// Delivers queued messages, one per direction in turn, until nothing is queued, no commit scheduler has a
    /// signature waiting and the dual-funding service has published what it completed.
    /// </summary>
    public async Task PumpAsync(Func<string, IChannelMessage, bool>? stopBefore = null)
    {
        for (var steps = 0; steps < 10_000; steps++)
        {
            // The nodes' clock is stepped: fire whatever debounced commits became due, deterministically
            Clock.Advance(TimeSpan.FromMilliseconds(10));
            await WhenIdleAsync();
            var delivered = false;
            foreach (var key in _links.Keys.OrderBy(k => k.From).ToList())
            {
                if (_links.TryGetValue(key, out var peek) && peek.TryPeek(out var next)
                                                         && stopBefore?.Invoke(key.From, next) == true)
                    return;

                delivered |= await DeliverNextAsync(key);
            }

            if (delivered)
                continue;

            await WhenIdleAsync();
            if (_links.Values.All(q => q.IsEmpty))
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>Runs <paramref name="operation"/> while pumping, until it completes.</summary>
    public async Task<T> RunAsync<T>(Task<T> operation)
    {
        for (var rounds = 0; rounds < 1_000 && !operation.IsCompleted; rounds++)
        {
            // The nodes' clock is stepped: fire whatever debounced commits became due, and let opens whose deadline
            // the test relies on (BOLT 2 gives the initiator up) reach it deterministically
            Clock.Advance(TimeSpan.FromMilliseconds(10));
            await PumpAsync();
            if (!operation.IsCompleted)
                await Task.WhenAny(operation, Task.Delay(10));
        }

        return await operation;
    }

    /// <summary>The link drops: queued messages are lost and both channel managers are told.</summary>
    public async Task DisconnectAsync()
    {
        LinkDown = true;
        _links.Clear();
        foreach (var node in Nodes.Where(n => n.IsRunning))
        {
            node.ChannelManager.OnPeerConnectionChanged(Other(node).NodeId);
            await node.ChannelManager.OnPeerDisconnectedAsync(Other(node).NodeId);
        }
    }

    /// <summary>A new connection: each side sends its channel_reestablish first, as the peer manager does.</summary>
    public async Task ReconnectAsync()
    {
        LinkDown = false;
        foreach (var node in Nodes)
            node.ChannelManager.OnPeerConnectionChanged(Other(node).NodeId);
        foreach (var node in Nodes)
            Assert.Empty(await node.ChannelManager.OnPeerConnectedAsync(Other(node).NodeId));
    }

    /// <summary>Stops <paramref name="node"/> and starts it again from its database (link down), as a restart does.</summary>
    public async Task RestartAsync(DualFundNode node)
    {
        await DisconnectAsync();
        await node.StopAsync();
        await node.StartAsync(migrate: false);
        await node.LoadStoredChannelsAsync();
    }

    /// <summary>
    /// The funding transaction <paramref name="fundingTxId"/> confirms at <see cref="FundingHeight"/> for both nodes
    /// (their chain monitors raise the confirmation), then the channel_ready exchange is pumped.
    /// </summary>
    public async Task ConfirmFundingAsync(ChannelId channelId, TxId fundingTxId)
    {
        foreach (var node in Nodes)
            await node.ConfirmAsync(channelId, fundingTxId);
        await PumpAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in Nodes)
            await node.StopAsync();

        foreach (var node in Nodes)
            SqliteTestPools.Clear(node.DatabasePath);
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort
        }
    }

    internal void Route(DualFundNode from, IChannelMessage message)
    {
        if (LinkDown)
        {
            from.Dropped.Add(message);
            return;
        }

        _links.GetOrAdd((from.Name, Other(from).Name), _ => new ConcurrentQueue<IChannelMessage>()).Enqueue(message);
    }

    /// <summary>Takes the next message <paramref name="from"/> sent, undelivered (null when none is queued).</summary>
    public IChannelMessage? TakeNext(DualFundNode from) =>
        _links.TryGetValue((from.Name, Other(from).Name), out var queue) && queue.TryDequeue(out var message)
            ? message
            : null;

    /// <summary>Delivers <paramref name="message"/> as if <paramref name="from"/> sent it now.</summary>
    public Task DeliverAsync(DualFundNode from, IChannelMessage message) => DeliverAsync(from, Other(from), message);

    private async Task<bool> DeliverNextAsync((string From, string To) key)
    {
        if (!_links.TryGetValue(key, out var queue) || !queue.TryDequeue(out var message))
            return false;

        var from = Nodes.Single(n => n.Name == key.From);
        await DeliverAsync(from, Other(from), message);
        return true;
    }

    private async Task DeliverAsync(DualFundNode from, DualFundNode to, IChannelMessage message)
    {
        lock (Transcript)
            Transcript.Add((from.Name, message));
        to.Received.Add(message);
        try
        {
            await to.ChannelManager.HandleChannelMessageAsync(message, NegotiatedFeatures, from.NodeId);
        }
        catch (Exception e) when (e is Domain.Exceptions.ChannelErrorException
                                     or Domain.Exceptions.ChannelWarningException)
        {
            // PeerManager sends these to the peer; the harness records them
            to.Errors.Add(e);
        }
    }

    private async Task WhenIdleAsync()
    {
        foreach (var node in Nodes.Where(n => n.IsRunning))
        {
            await node.Scheduler.WhenIdleAsync();
            await node.DualFund.WhenIdleAsync();
        }
    }
}

/// <summary>One side of <see cref="DualFundHarness"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DualFundNode
{
    private readonly DualFundHarness _harness;
    private readonly long _acceptContributionSat;
    private ServiceProvider? _provider;

    public string Name { get; }
    public string DatabasePath { get; }
    public DualFundKeyManager KeyManager { get; }
    public CompactPubKey NodeId => KeyManager.NodeId;
    public NodeOptions Options { get; }
    public Mock<IBlockchainMonitor> ChainMonitor { get; private set; } = new();
    public HarnessLinkProbe Probe { get; } = new();
    public RecordingPaymentHandler PaymentHandler { get; } = new();

    /// <summary>This node's connection to the other node, with <see cref="DualFundHarness.WithPeerServices"/>.</summary>
    public Mock<IPeerService> PeerService { get; } = new();

    /// <summary>The wallet of the interactive-tx negotiations (kept across restarts, like the node's wallet).</summary>
    public FakeInteractiveTxContributor Wallet { get; } = new();

    public FakeInteractiveTxBuilder Builder { get; } = new();
    public FakePrevTxInspector Inspector { get; } = new();

    /// <summary>Every funding (and other) transaction this node handed to its chain monitor, in order.</summary>
    public List<BroadcastTransactionModel> Published { get; } = [];

    public List<IChannelMessage> Received { get; } = [];
    public List<IChannelMessage> Dropped { get; } = [];
    public List<Exception> Errors { get; } = [];

    /// <summary>This node's <c>Node:DualFund:AllowRbf</c> instead of the harness's, from its next start.</summary>
    public bool? AllowRbfOverride { get; set; }

    public bool IsRunning => _provider is not null;
    public IServiceProvider Services => _provider ?? throw new InvalidOperationException($"{Name} is stopped");
    public ChannelManager ChannelManager { get; private set; } = null!;
    public DualFundedOpenService DualFund => Services.GetRequiredService<DualFundedOpenService>();
    public IChannelOperations Operations => Services.GetRequiredService<IChannelOperations>();
    public ICommitScheduler Scheduler => Services.GetRequiredService<ICommitScheduler>();
    public IInvoiceService Invoices => Services.GetRequiredService<IInvoiceService>();
    public IChannelMemoryRepository Memory => Services.GetRequiredService<IChannelMemoryRepository>();

    public DualFundNode(DualFundHarness harness, string name, byte seed, string databasePath,
                        long acceptContributionSat)
    {
        _harness = harness;
        _acceptContributionSat = acceptContributionSat;
        Name = name;
        DatabasePath = databasePath;
        KeyManager = new DualFundKeyManager(seed);
        Options = new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Regtest,
            EnableHtlcs = true,
            Features = new FeatureOptions { DualFund = FeatureSupport.Optional, AllowExperimentalFeatures = true },
            // The test wallet is the interactive-tx contributor's, not the node's UTXO set: no anchors reserve to keep
            Anchors = new AnchorReserveOptions { ReservePerChannel = 0 }
        };
    }

    public ChannelModel Channel(ChannelId channelId) =>
        Memory.TryGetChannel(channelId, out var channel) ? channel : throw new InvalidOperationException("No channel");

    public async Task<T> InScopeAsync<T>(Func<IUnitOfWork, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    public async Task StartAsync(bool migrate)
    {
        ChainMonitor = new Mock<IBlockchainMonitor>();
        ChainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(DualFundHarness.BlockHeight);
        ChainMonitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                    .Callback<BroadcastTransactionModel>(b =>
                     {
                         lock (Published)
                             Published.Add(b);
                     })
                    .ReturnsAsync(true);

        _provider = BuildProvider();
        if (migrate)
        {
            using var scope = _provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync();
        }

        ChannelManager = _provider.GetRequiredService<ChannelManager>();
        ChannelManager.OnResponseMessageReady += (_, args) => _harness.Route(this, args.ResponseMessage);
    }

    public async Task StopAsync()
    {
        if (_provider is null)
            return;

        await Scheduler.WhenIdleAsync();
        await DualFund.WhenIdleAsync();
        var provider = _provider;
        _provider = null;
        await provider.DisposeAsync();
        Probe.Clear();
    }

    /// <summary>Loads every stored channel and registers it with the channel manager (startup).</summary>
    public async Task LoadStoredChannelsAsync()
    {
        var channels = await InScopeAsync(async unitOfWork =>
                                              (await unitOfWork.ChannelDbRepository.GetAllAsync()).ToList());
        foreach (var channel in channels)
            await ChannelManager.RegisterExistingChannelAsync(channel);
    }

    /// <summary>The chain monitor reports <paramref name="fundingTxId"/> confirmed at the funding height.</summary>
    public async Task ConfirmAsync(ChannelId channelId, TxId fundingTxId)
    {
        var channel = Channel(channelId);
        var watch = new WatchedTransactionModel(channelId, fundingTxId, channel.ChannelParams.MinimumDepth);
        watch.SetHeightAndIndex(DualFundHarness.FundingHeight, 1);
        ChainMonitor.Raise(m => m.OnTransactionConfirmed += null,
                           new TransactionConfirmedEventArgs(watch, DualFundHarness.BlockHeight));

        // The confirmation runs on its own task under the channel's lock: wait until it moved the channel
        for (var i = 0; i < 500 && channel.State == Domain.Channels.Enums.ChannelState.V1FundingSigned; i++)
            await Task.Delay(10);
        using (await Services.GetRequiredService<IChannelLockProvider>().AcquireAsync(channelId))
        {
        }
    }

    /// <summary>A payment of <paramref name="amount"/> from this node to <paramref name="payee"/> over the channel.</summary>
    public async Task<(Hash PaymentHash, Secret Preimage)> PayAsync(DualFundNode payee, ChannelId channelId,
                                                                    LightningMoney amount)
    {
        var invoice = await payee.Invoices.CreateInvoiceAsync(amount, "dual-funded", null, CancellationToken.None);
        var finalCltv = DualFundHarness.BlockHeight + 43;
        var route = new PaymentRoute([new RouteHop(payee.NodeId, amount, finalCltv, null)], amount, finalCltv,
                                     invoice.PaymentHash, invoice.PaymentSecret);
        var onion = await Services.GetRequiredService<PaymentOnionFactory>().CreateAsync(route);
        await Operations.OfferHtlcAsync(channelId, route.FirstHopAmount, route.PaymentHash, route.FirstHopCltvExpiry,
                                        onion.Packet, null, HtlcOrigin.Local(route.PaymentHash));
        return (invoice.PaymentHash, invoice.Preimage);
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={DatabasePath}"
                           })
                           .Build();

        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(_harness.FeeEstimatePerKw));

        var services = new ServiceCollection();
        services.AddLogging();
        // Before every TryAdd(TimeProvider.System) of the service extensions below: the nodes' clock is stepped, so a
        // test owns when the open watchdog and the open deadline fire (NL-512)
        services.AddSingleton<TimeProvider>(_harness.Clock);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new DualFundingOptions
        {
            AcceptContributionSat = _acceptContributionSat,
            OpenTimeout = _harness.OpenTimeout,
            AllowRbf = AllowRbfOverride ?? _harness.AllowRbf
        }));
        if (_harness.WithPeerServices)
        {
            PeerService.SetupGet(p => p.Features).Returns(() => _harness.NegotiatedFeatures);

            // Liquidity ads (NL-850): the rates the other node sells at, as its init carries them
            PeerService.SetupGet(p => p.LiquidityRates)
                       .Returns(() => _harness.Other(this).Options.LiquidityAds.GetWillFundRates());

            // The link is up: the commit scheduler's ping before a commitment_signed is answered
            PeerService.Setup(p => p.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(true);
            var peerManager = new Mock<IPeerManager>();
            peerManager.Setup(m => m.GetPeer(It.IsAny<CompactPubKey>())).Returns((CompactPubKey nodeId) =>
            {
                var peer = new PeerModel(nodeId, "127.0.0.1", 9735, "harness");
                peer.SetPeerService(PeerService.Object);
                return peer;
            });
            services.AddSingleton(peerManager.Object);
        }
        services.AddSingleton<ISecureKeyManager>(KeyManager);
        services.AddPersistentOnionReplayStore();
        services.AddTransient<ISha256, Sha256>();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddSingleton(ChainMonitor.Object);
        services.AddSingleton(feeService.Object);
        services.AddSingleton<IChannelOpenValidator>(new ChannelOpenValidator(Options));
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IChannelMessagePublisher>(new LazyPublisher(this));
        services.AddSingleton<IPeerLivenessProbe>(Probe);
        services.AddReestablishServices();
        services.AddChannelOperationsServices();
        services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.Zero);
        services.AddGossipServices();
        services.AddPaymentsServices();
        services.AddHtlcSwitchServices();
        services.AddSingleton<ILocalPaymentHtlcHandler>(PaymentHandler);

        // The negotiation over the test wallet (kept by the node object, so it survives a restart)
        services.AddSingleton<IInteractiveTxContributor>(Wallet);
        services.AddSingleton<IInteractiveTxBuilder>(Builder);
        services.AddSingleton<IPrevTxInspector>(Inspector);
        services.AddInteractiveTxServices();
        services.AddDualFundingServices();
        services.AddSingleton<LiquidityAdsService>();

        services.AddSingleton(sp => new ChannelManager(ChainMonitor.Object,
                                                       sp.GetRequiredService<IChannelLockProvider>(),
                                                       sp.GetRequiredService<IChannelMemoryRepository>(),
                                                       NullLogger<ChannelManager>.Instance,
                                                       sp.GetRequiredService<ILightningSigner>(), sp));
        services.AddSingleton<IChannelManager>(sp => sp.GetRequiredService<ChannelManager>());
        services.AddScoped<FundingConfirmedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<OpenChannel2Message>, OpenChannel2MessageHandler>();
        services.AddScoped<IChannelMessageHandler<AcceptChannel2Message>, AcceptChannel2MessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddInputMessage>, TxAddInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddOutputMessage>, TxAddOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveInputMessage>, TxRemoveInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveOutputMessage>, TxRemoveOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxCompleteMessage>, TxCompleteMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxSignaturesMessage>, TxSignaturesMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxInitRbfMessage>, TxInitRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAckRbfMessage>, TxAckRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAbortMessage>, TxAbortMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ChannelReadyMessage>, ChannelReadyMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ChannelReestablishMessage>, ChannelReestablishMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>, UpdateAddHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFulfillHtlcMessage>, UpdateFulfillHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFailHtlcMessage>, UpdateFailHtlcMessageHandler>();
        services
           .AddScoped<IChannelMessageHandler<UpdateFailMalformedHtlcMessage>, UpdateFailMalformedHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<CommitmentSignedMessage>, CommitmentSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<RevokeAndAckMessage>, RevokeAndAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFeeMessage>, UpdateFeeMessageHandler>();
        return services.BuildServiceProvider();
    }

    /// <summary>Publishes through the node's channel manager, which is built after the provider.</summary>
    private sealed class LazyPublisher(DualFundNode node) : IChannelMessagePublisher
    {
        public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages) =>
            node.ChannelManager.Publish(peerPubKey, messages);
    }
}

/// <summary>The send side's link: up once the channel manager marked it (channel opened or reestablished).</summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessLinkProbe : IPeerLivenessProbe
{
    private readonly ConcurrentDictionary<ChannelId, byte> _links = new();

    public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                   CancellationToken cancellationToken = default) =>
        Task.FromResult(_links.ContainsKey(channelId));

    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey)
    {
        _links[channelId] = 0;
        LinkUp?.Invoke(this, new ChannelLinkUpEventArgs(channelId, peerPubKey));
    }

    public event EventHandler<ChannelLinkUpEventArgs>? LinkUp;

    public void Clear() => _links.Clear();
}

/// <summary>Node key and BIP32 channel keys of a harness node, with a channel key counter.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DualFundKeyManager : ISecureKeyManager
{
    private readonly Key _nodeKey;
    private readonly ExtKey _channelRoot;
    private int _nextIndex;

    public DualFundKeyManager(byte seed)
    {
        _nodeKey = new Key(Enumerable.Repeat(seed, 32).ToArray());
        _channelRoot = new ExtKey(new Key(Enumerable.Repeat((byte)(seed ^ 0x5A), 32).ToArray()), new byte[32]);
    }

    public CompactPubKey NodeId => new(_nodeKey.PubKey.ToBytes());

    public BitcoinKeyPath ChannelKeyPath => throw new NotSupportedException();
    public uint HeightOfBirth => 0;

    public ExtPrivKey GetNextChannelKey(out uint index)
    {
        index = (uint)Interlocked.Increment(ref _nextIndex);
        return GetChannelKeyAtIndex(index);
    }

    public ExtPrivKey GetChannelKeyAtIndex(uint index) => (ExtPrivKey)_channelRoot.Derive((int)index, true).ToBytes();

    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();

    public CryptoKeyPair GetNodeKeyPair() => new(new PrivKey(_nodeKey.ToBytes()), NodeId);

    public CompactPubKey GetNodePubKey() => NodeId;

    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        var sharedPoint = new PubKey(publicKey.ToArray()).GetSharedPubkey(_nodeKey);
        SHA256.HashData(sharedPoint.ToBytes(), sharedSecret);
    }
}