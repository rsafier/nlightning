using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Taproot;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Application.Gossip;
using Application.Payments;
using Application.Payments.Routing;
using Application.Payments.Switch;
using Application.Protocol.Factories;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Validators;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Onchain.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using DualFunding;
using Harness;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Protocol.Onion;
using Infrastructure.Repositories;
using Infrastructure.Serialization;
using NLightning.Tests.Utils.Mocks;
using TestUtils;

/// <summary>
/// Two in-process nodes that open a simple taproot channel with the v1 flow (NL-877 T5): each a real
/// <see cref="ChannelManager"/> with the production <c>open_channel</c>/<c>accept_channel</c>/<c>funding_created</c>/
/// <c>funding_signed</c>/<c>channel_ready</c> handlers, the channel factory and validator, the funding transaction
/// builder (the MuSig2 P2TR output, signed from a wallet output by the real signer), the normal-operation and
/// reestablish handlers, the HTLC switch and invoices, on its own SQLite database (production unit of work and
/// repositories), so a node can be stopped and started again from what it saved. Alice opens and pays.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TaprootOpenHarness : IAsyncDisposable
{
    public const uint BlockHeight = 500;
    public const uint FundingHeight = 497;
    public const long WalletSat = 3_000_000;

    private readonly string _directory;
    private readonly ConcurrentDictionary<(string From, string To), ConcurrentQueue<IChannelMessage>> _links = new();

    public TaprootOpenNode Alice { get; }
    public TaprootOpenNode Bob { get; }
    public IReadOnlyList<TaprootOpenNode> Nodes => [Alice, Bob];

    /// <summary>Every message delivered, in order.</summary>
    public List<(string From, IChannelMessage Message)> Transcript { get; } = [];

    /// <summary>Every message a node raised for its peer, delivered, lost with the link or dropped, in order.</summary>
    public List<(string From, IChannelMessage Message)> Sent { get; } = [];

    /// <summary>How many times a crashed node was restarted.</summary>
    public int Restarts { get; private set; }

    /// <summary>When set, messages are dropped (a link that is down).</summary>
    public bool LinkDown { get; set; }

    /// <summary>Replaces a message in flight (sender name, message): a test's way to send what a peer could.</summary>
    public Func<string, IChannelMessage, IChannelMessage>? Tamper { get; set; }

    /// <summary>
    /// What the nodes negotiated: anchors, <c>option_simple_taproot</c> and <c>option_simple_close</c> (LND 0.21 with
    /// <c>--protocol.simple-taproot-chans</c>, Eclair 0.14.3); no dual funding, so the open is v1.
    /// </summary>
    public FeatureOptions NegotiatedFeatures { get; set; } = new()
    {
        OptionSimpleTaproot = FeatureSupport.Optional,
        OptionSimpleClose = FeatureSupport.Optional,
        DualFund = FeatureSupport.No,
        AllowExperimentalFeatures = true
    };

    private TaprootOpenHarness(string directory)
    {
        _directory = directory;
        Alice = new TaprootOpenNode(this, "Alice", 0xA1, Path.Combine(directory, "alice.db"));
        Bob = new TaprootOpenNode(this, "Bob", 0xB0, Path.Combine(directory, "bob.db"));
    }

    public static async Task<TaprootOpenHarness> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nltg-taproot-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var harness = new TaprootOpenHarness(directory);
        foreach (var node in harness.Nodes)
            await node.StartAsync(migrate: true);

        // The peer rows the peer manager saves on connection (channels reference them)
        foreach (var node in harness.Nodes)
            await node.InScopeAsync(async unitOfWork =>
            {
                await unitOfWork.PeerDbRepository.AddOrUpdateAsync(
                    new PeerModel(harness.Other(node).NodeId, "127.0.0.1", 9735, "harness"));
                await unitOfWork.SaveChangesAsync();
                return 0;
            });
        return harness;
    }

    public TaprootOpenNode Other(TaprootOpenNode node) => node == Alice ? Bob : Alice;

    /// <summary>
    /// Delivers queued messages, one per direction in turn, until nothing is queued and no commit scheduler has a
    /// signature waiting.
    /// </summary>
    public async Task PumpAsync()
    {
        for (var steps = 0; steps < 10_000; steps++)
        {
            await RecoverAsync();
            await WhenIdleAsync();
            await RecoverAsync();
            var delivered = false;
            foreach (var key in _links.Keys.OrderBy(k => k.From).ToList())
            {
                delivered |= await DeliverNextAsync(key);
                await RecoverAsync();
            }

            if (delivered)
                continue;

            await WhenIdleAsync();
            await RecoverAsync();
            if (_links.Values.All(q => q.IsEmpty))
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>
    /// Alice opens a simple taproot channel to Bob as <c>openchannel --channel-type taproot --v1</c> does (factory,
    /// wallet lock, <c>open_channel</c> with her commitment 0 verification nonce), and the open runs up to
    /// <c>funding_signed</c>.
    /// </summary>
    /// <returns>The channel id and the funding transaction Alice published.</returns>
    public async Task<(ChannelId ChannelId, BroadcastTransactionModel Funding)> OpenAsync(
        LightningMoney fundingAmount, LightningMoney? pushAmount = null)
    {
        var request = new OpenChannelClientRequest(Bob.NodeId.ToString(), fundingAmount)
        {
            PushAmount = pushAmount,
            IsSimpleTaproot = true,
            ForceV1 = true
        };
        var factory = Alice.Services.GetRequiredService<IChannelFactory>();
        var channel = await factory.CreateChannelV1AsInitiatorAsync(request, NegotiatedFeatures, Bob.NodeId);
        Alice.Services.GetRequiredService<IUtxoMemoryRepository>()
             .LockUtxosToSpendOnChannel(fundingAmount, channel.ChannelId);

        var signer = Alice.Services.GetRequiredService<ILightningSigner>();
        var messageFactory = Alice.Services.GetRequiredService<IMessageFactory>();
        var open = messageFactory.CreateOpenChannel1Message(
            channel.ChannelId, fundingAmount, channel.LocalKeySet.FundingCompactPubKey, channel.RemoteBalance,
            channel.ChannelParams.Local, channel.ChannelParams.FeeRateAmountPerKw,
            channel.LocalKeySet.RevocationCompactBasepoint, channel.LocalKeySet.PaymentCompactBasepoint,
            channel.LocalKeySet.DelayedPaymentCompactBasepoint, channel.LocalKeySet.HtlcCompactBasepoint,
            channel.LocalKeySet.CurrentPerCommitmentCompactPoint, new ChannelFlags(ChannelFlag.None),
            new ChannelTypeTlv(channel.ChannelParams.ToChannelType()), new UpfrontShutdownScriptTlv(Array.Empty<byte>()),
            signer.GetLocalVerificationNonce(channel.LocalKeySet.KeyIndex, null, 0));
        await Alice.ChannelManager.StartOpeningChannelAsync(Bob.NodeId, channel, open);
        await PumpAsync();

        var funding = Assert.Single(Alice.Published);
        var opened = Alice.Memory.FindChannels(c => c.RemoteNodeId == Bob.NodeId).Single();
        return (opened.ChannelId, funding);
    }

    /// <summary>
    /// Restarts every node whose database "crashed" (<see cref="TaprootOpenNode.Crash"/>: a save refused, and every one
    /// after it): the link drops, the node starts again from what it saved and the link comes back (both sides send
    /// channel_reestablish). Nothing happens when no node crashed.
    /// </summary>
    public async Task RecoverAsync()
    {
        if (!Nodes.Any(n => n.Crash.Crashed))
            return;

        await DisconnectAsync();
        foreach (var node in Nodes.Where(n => n.Crash.Crashed))
        {
            Restarts++;
            await node.StopAsync();
            await node.StartAsync(migrate: false);
            await node.LoadStoredChannelsAsync();
        }

        await ReconnectAsync();
    }

    /// <summary>The link drops: queued messages are lost and both channel managers are told.</summary>
    public async Task DisconnectAsync()
    {
        LinkDown = true;
        _links.Clear();
        foreach (var node in Nodes.Where(n => n.IsRunning && !n.Crash.Crashed))
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
    public async Task RestartAsync(TaprootOpenNode node)
    {
        await DisconnectAsync();
        await node.StopAsync();
        await node.StartAsync(migrate: false);
        await node.LoadStoredChannelsAsync();
    }

    /// <summary>The funding confirms at <see cref="FundingHeight"/> for both nodes, then channel_ready is exchanged.</summary>
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

    internal void Route(TaprootOpenNode from, IChannelMessage message)
    {
        lock (Sent)
            Sent.Add((from.Name, message));
        if (LinkDown)
            return;

        _links.GetOrAdd((from.Name, Other(from).Name), _ => new ConcurrentQueue<IChannelMessage>()).Enqueue(message);
    }

    private async Task<bool> DeliverNextAsync((string From, string To) key)
    {
        if (!_links.TryGetValue(key, out var queue) || !queue.TryDequeue(out var message))
            return false;

        var from = Nodes.Single(n => n.Name == key.From);
        var to = Other(from);
        if (Tamper is not null)
            message = Tamper(from.Name, message);
        lock (Transcript)
            Transcript.Add((from.Name, message));
        to.Received.Add(message);
        try
        {
            await to.ChannelManager.HandleChannelMessageAsync(message, NegotiatedFeatures, from.NodeId);
        }
        catch (Exception) when (to.Crash.Crashed)
        {
            // The receiver died while handling it: RecoverAsync restarts it
        }

        return true;
    }

    private async Task WhenIdleAsync()
    {
        foreach (var node in Nodes.Where(n => n.IsRunning))
            await node.Scheduler.WhenIdleAsync();
    }
}

/// <summary>One side of <see cref="TaprootOpenHarness"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class TaprootOpenNode
{
    private readonly TaprootOpenHarness _harness;
    private ServiceProvider? _provider;

    public string Name { get; }
    public string DatabasePath { get; }
    public TaprootKeyManager KeyManager { get; }
    public CompactPubKey NodeId => KeyManager.NodeId;
    public NodeOptions Options { get; }
    public Mock<IBlockchainMonitor> ChainMonitor { get; private set; } = new();
    public HarnessLinkProbe Probe { get; } = new();
    public RecordingPaymentHandler PaymentHandler { get; } = new();

    /// <summary>The database's crash switch of the running process (a new one at every start).</summary>
    public CrashOnSaveInterceptor Crash { get; private set; } = new();

    /// <summary>Local commitments this node accepted: (number, txid). Kept across restarts.</summary>
    public List<(ulong Number, TxId TxId)> Verified { get; } = [];

    /// <summary>Every transaction this node handed to its chain monitor, in order (kept across restarts).</summary>
    public List<BroadcastTransactionModel> Published { get; } = [];

    /// <summary>Every message this node received, in order (kept across restarts).</summary>
    public List<IChannelMessage> Received { get; } = [];

    public bool IsRunning => _provider is not null;
    public IServiceProvider Services => _provider ?? throw new InvalidOperationException($"{Name} is stopped");
    public ChannelManager ChannelManager { get; private set; } = null!;
    public IChannelOperations Operations => Services.GetRequiredService<IChannelOperations>();
    public ICommitScheduler Scheduler => Services.GetRequiredService<ICommitScheduler>();
    public IInvoiceService Invoices => Services.GetRequiredService<IInvoiceService>();
    public IChannelMemoryRepository Memory => Services.GetRequiredService<IChannelMemoryRepository>();

    public TaprootOpenNode(TaprootOpenHarness harness, string name, byte seed, string databasePath)
    {
        _harness = harness;
        Name = name;
        DatabasePath = databasePath;
        KeyManager = new TaprootKeyManager(seed);
        Options = new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Regtest,
            EnableHtlcs = true,
            Features = new FeatureOptions
            {
                OptionSimpleTaproot = FeatureSupport.Optional,
                AllowExperimentalFeatures = true
            },
            // One wallet output funds the channel: no anchors reserve to keep in this harness
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
        ChainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(TaprootOpenHarness.BlockHeight);
        ChainMonitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                    .Callback<BroadcastTransactionModel>(b =>
                     {
                         lock (Published)
                             Published.Add(b);
                     })
                    .ReturnsAsync(true);

        Crash = new CrashOnSaveInterceptor();
        _provider = BuildProvider();
        if (migrate)
        {
            using var scope = _provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync();
        }

        // The wallet: one confirmed P2WPKH output whose key the node's key manager derives
        _provider.GetRequiredService<IUtxoMemoryRepository>()
                 .Add(new UtxoModel(new TxId(Enumerable.Repeat(KeyManager.Seed, 32).ToArray()), 0,
                                    LightningMoney.Satoshis(TaprootOpenHarness.WalletSat), 100,
                                    new WalletAddressModel(AddressType.P2Wpkh, 0, false, KeyManager.DepositAddress)));

        ChannelManager = _provider.GetRequiredService<ChannelManager>();
        ChannelManager.OnResponseMessageReady += (_, args) => _harness.Route(this, args.ResponseMessage);
    }

    public async Task StopAsync()
    {
        if (_provider is null)
            return;

        await Scheduler.WhenIdleAsync();
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
        watch.SetHeightAndIndex(TaprootOpenHarness.FundingHeight, 1);
        var before = channel.State;
        ChainMonitor.Raise(m => m.OnTransactionConfirmed += null,
                           new TransactionConfirmedEventArgs(watch, TaprootOpenHarness.BlockHeight));

        // The confirmation runs on its own task under the channel's lock: wait until it moved the channel
        for (var i = 0; i < 500 && channel.State == before; i++)
            await Task.Delay(10);
        using (await Services.GetRequiredService<IChannelLockProvider>().AcquireAsync(channelId))
        {
        }
    }

    /// <summary>A payment of <paramref name="amount"/> from this node to <paramref name="payee"/> over the channel.</summary>
    public async Task<(Hash PaymentHash, Secret Preimage)> PayAsync(TaprootOpenNode payee, ChannelId channelId,
                                                                    LightningMoney amount)
    {
        var invoice = await payee.Invoices.CreateInvoiceAsync(amount, "taproot", null, CancellationToken.None);
        var finalCltv = TaprootOpenHarness.BlockHeight + 43;
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
                  .ReturnsAsync(() => LightningMoney.Satoshis(2_500));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<ISecureKeyManager>(KeyManager);
        services.AddPersistentOnionReplayStore();
        services.AddTransient<ISha256, Sha256>();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddSingleton(ChainMonitor.Object);
        services.AddSingleton(feeService.Object);
        services.AddSingleton<IChannelIdFactory, Infrastructure.Protocol.Factories.ChannelIdFactory>();
        services.AddSingleton<IChannelOpenValidator>(new ChannelOpenValidator(Options));
        services.AddSingleton<IChannelFactory>(sp => new ChannelFactory(
                                                   sp.GetRequiredService<IChannelIdFactory>(),
                                                   sp.GetRequiredService<IChannelOpenValidator>(), feeService.Object,
                                                   sp.GetRequiredService<ILightningSigner>(), Options,
                                                   sp.GetRequiredService<ISha256>()));
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton<IFundingTransactionModelFactory, FundingTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        services.AddSingleton<ICommitmentVerifier>(sp => new RecordingVerifier(
                                                       sp.GetRequiredService<CommitmentSigningService>(),
                                                       sp.GetRequiredService<IChannelMemoryRepository>(), Verified));
        services.ConfigureDbContext<NLightningDbContext>(o => o.AddInterceptors(Crash));
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

        services.AddSingleton(sp => new ChannelManager(ChainMonitor.Object,
                                                       sp.GetRequiredService<IChannelLockProvider>(),
                                                       sp.GetRequiredService<IChannelMemoryRepository>(),
                                                       NullLogger<ChannelManager>.Instance,
                                                       sp.GetRequiredService<ILightningSigner>(), sp));
        services.AddSingleton<IChannelManager>(sp => sp.GetRequiredService<ChannelManager>());
        services.AddScoped<FundingConfirmedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<OpenChannel1Message>, OpenChannel1MessageHandler>();
        services.AddScoped<IChannelMessageHandler<AcceptChannel1Message>, AcceptChannel1MessageHandler>();
        services.AddScoped<IChannelMessageHandler<FundingCreatedMessage>, FundingCreatedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<FundingSignedMessage>, FundingSignedMessageHandler>();
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
    private sealed class LazyPublisher(TaprootOpenNode node) : IChannelMessagePublisher
    {
        public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages) =>
            node.ChannelManager.Publish(peerPubKey, messages);
    }

    /// <summary>The production verifier port, recording the txid of every commitment it accepts.</summary>
    private sealed class RecordingVerifier(CommitmentSigningService service, IChannelMemoryRepository channels,
                                           List<(ulong, TxId)> verified) : ICommitmentVerifier
    {
        private readonly EngineCommitmentVerifierPort _inner =
            new(service, channels, NullLogger<EngineCommitmentVerifierPort>.Instance);

        public bool VerifyLocalCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                          CommitmentSpec spec, CommitmentSignatures signatures)
        {
            if (!_inner.VerifyLocalCommitment(channelId, funding, number, spec, signatures))
                return false;

            channels.TryGetChannel(channelId, out var channel);
            var txId = service.VerifyLocalCommitment(channel!, funding, CommitmentTxSpec.FromCommitmentSpec(spec),
                                                     number, signatures.Signature, signatures.HtlcSignatures,
                                                     signatures.PartialSignature)
                              .CommitmentTxId;
            lock (verified)
                verified.Add((number, txId));
            return true;
        }
    }
}

/// <summary>
/// A database that dies: the <see cref="CrashAtSave"/>-th save (counted from <see cref="Arm"/>) and every save after
/// it throw <see cref="SimulatedCrashException"/> before anything is written, as a process killed before its commit.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class CrashOnSaveInterceptor : SaveChangesInterceptor
{
    private int _saves;

    /// <summary>The save (1-based, from <see cref="Arm"/>) that crashes; null never crashes.</summary>
    public int? CrashAtSave { get; private set; }

    public bool Crashed { get; private set; }

    /// <summary>Saves since <see cref="Arm"/> (or the start).</summary>
    public int Saves => _saves;

    public void Arm(int? crashAtSave)
    {
        _saves = 0;
        CrashAtSave = crashAtSave;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
                                                          InterceptionResult<int> result)
    {
        Check();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                                                                         InterceptionResult<int> result,
                                                                         CancellationToken cancellationToken = default)
    {
        Check();
        return ValueTask.FromResult(result);
    }

    private void Check()
    {
        var save = Interlocked.Increment(ref _saves);
        if (!Crashed && save != CrashAtSave)
            return;

        Crashed = true;
        throw new SimulatedCrashException(save);
    }
}

/// <summary>Node key, BIP32 channel keys and one P2WPKH deposit key of a harness node.</summary>
[ExcludeFromCodeCoverage]
internal sealed class TaprootKeyManager : ISecureKeyManager
{
    private readonly Key _nodeKey;
    private readonly ExtKey _channelRoot;
    private readonly ExtKey _depositRoot;
    private int _nextIndex;

    public TaprootKeyManager(byte seed)
    {
        Seed = seed;
        _nodeKey = new Key(Enumerable.Repeat(seed, 32).ToArray());
        _channelRoot = new ExtKey(new Key(Enumerable.Repeat((byte)(seed ^ 0x5A), 32).ToArray()), new byte[32]);
        _depositRoot = new ExtKey(new Key(Enumerable.Repeat((byte)(seed ^ 0x3C), 32).ToArray()), new byte[32]);
    }

    public byte Seed { get; }
    public CompactPubKey NodeId => new(_nodeKey.PubKey.ToBytes());

    /// <summary>The regtest address of deposit key 0 (the wallet's one output).</summary>
    public string DepositAddress => DepositKey(0, false).PubKey.WitHash.GetAddress(Network.RegTest).ToString();

    /// <summary>The regtest address of change key 1.</summary>
    public string ChangeAddress => DepositKey(1, true).PubKey.WitHash.GetAddress(Network.RegTest).ToString();

    public BitcoinKeyPath ChannelKeyPath => throw new NotSupportedException();
    public uint HeightOfBirth => 0;

    public ExtPrivKey GetNextChannelKey(out uint index)
    {
        index = (uint)Interlocked.Increment(ref _nextIndex);
        return GetChannelKeyAtIndex(index);
    }

    public ExtPrivKey GetChannelKeyAtIndex(uint index) => (ExtPrivKey)_channelRoot.Derive((int)index, true).ToBytes();

    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();

    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) =>
        (ExtPrivKey)_depositRoot.Derive(isChange ? 1u : 0u).Derive(index).ToBytes();

    public CryptoKeyPair GetNodeKeyPair() => new(new PrivKey(_nodeKey.ToBytes()), NodeId);

    public CompactPubKey GetNodePubKey() => NodeId;

    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        var sharedPoint = new PubKey(publicKey.ToArray()).GetSharedPubkey(_nodeKey);
        SHA256.HashData(sharedPoint.ToBytes(), sharedSecret);
    }

    private Key DepositKey(uint index, bool isChange) =>
        ExtKey.CreateFromBytes(GetDepositP2WpkhKeyAtIndex(index, isChange)).PrivateKey;
}