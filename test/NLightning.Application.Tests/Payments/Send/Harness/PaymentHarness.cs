using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Payments.Send.Harness;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Application.Gossip.Interfaces;
using Application.Payments;
using Application.Payments.Send;
using Application.Payments.Send.Interfaces;
using Application.Protocol.Factories;
using Channels.Harness;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Protocol.Onion;
using Infrastructure.Serialization;

/// <summary>
/// Three in-process nodes for the W2-C send proof: Bob, Carol and David, with channels Bob–Carol and Carol–David. Each
/// node is a real <see cref="ChannelManager"/> with the production normal-operation handlers, engine ports,
/// <c>LocalLightningSigner</c>, <see cref="ChannelOperationsService"/> + <see cref="CommitScheduler"/>, the real
/// Sphinx, hop-payload and failure-onion services, the production payment core (<c>AddPaymentsServices</c>) and
/// <see cref="PaymentService"/> (<c>AddPaymentSendServices</c>). Messages travel through in-memory FIFOs per
/// direction; each unit of work stages its own writes and commits them on save.
/// </summary>
/// <remarks>
/// The HTLC switch is <see cref="HarnessForwardingSwitch"/>, a test stand-in for the W2-B switch: it forwards by
/// short channel id, accepts final HTLCs against the node's invoices, propagates fulfills and wrapped failures
/// upstream, and hands the outcomes of our own HTLCs to <see cref="IPaymentOutcomeHandler"/>.
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class PaymentHarness : IDisposable
{
    public const ulong FundingSatoshis = 2_000_000;
    public const ulong PushSatoshis = 500_000;
    public const uint FeeratePerKw = 2_500;
    public const uint BlockHeight = 500;

    public static readonly ShortChannelId ScidBobCarol = new(400, 1, 0);
    public static readonly ShortChannelId ScidCarolDavid = new(401, 2, 1);

    private static readonly ChannelId s_bobCarolId = new(Enumerable.Repeat((byte)0xBC, 32).ToArray());
    private static readonly ChannelId s_carolDavidId = new(Enumerable.Repeat((byte)0xCD, 32).ToArray());

    public PaymentHarnessNode Bob { get; }
    public PaymentHarnessNode Carol { get; }
    public PaymentHarnessNode David { get; }

    public PaymentHarness()
    {
        Bob = new PaymentHarnessNode("bob", 0xB0, new RoutingOptions
        {
            FeeBaseMsat = 1_000,
            FeeProportionalMillionths = 100,
            CltvExpiryDelta = 40
        });
        Carol = new PaymentHarnessNode("carol", 0xC0, new RoutingOptions
        {
            FeeBaseMsat = 2_000,
            FeeProportionalMillionths = 500,
            CltvExpiryDelta = 40
        });
        David = new PaymentHarnessNode("david", 0xD0, new RoutingOptions());
        foreach (var node in (PaymentHarnessNode[])[Bob, Carol, David])
            node.Network = this;

        OpenChannel(Bob, 1, Carol, 1, s_bobCarolId, ScidBobCarol, 0x71);
        OpenChannel(Carol, 2, David, 1, s_carolDavidId, ScidCarolDavid, 0x72);
    }

    public ChannelId BobCarol => s_bobCarolId;
    public ChannelId CarolDavid => s_carolDavidId;

    /// <summary>
    /// Delivers queued messages, one per direction in turn, until every queue is empty and no commit scheduler has a
    /// signature waiting.
    /// </summary>
    public async Task PumpAsync()
    {
        var nodes = new[] { Bob, Carol, David };
        for (var steps = 0; steps < 20_000; steps++)
        {
            foreach (var node in nodes)
                await node.Scheduler.WhenIdleAsync();

            var delivered = false;
            foreach (var node in nodes)
                delivered |= await node.DeliverNextAsync();
            if (delivered)
                continue;

            foreach (var node in nodes)
                await node.Scheduler.WhenIdleAsync();
            if (nodes.All(n => n.OutboxIsEmpty))
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>
    /// Runs <paramref name="operation"/> (e.g. a payment that waits for its outcome) while pumping messages.
    /// </summary>
    public async Task<T> RunAsync<T>(Task<T> operation, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!operation.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The operation did not complete");

            await PumpAsync();
            await Task.WhenAny(operation, Task.Delay(10));
        }

        await PumpAsync();
        return await operation;
    }

    public PaymentHarnessNode NodeFor(CompactPubKey nodeId) =>
        new[] { Bob, Carol, David }.Single(n => n.NodeId == nodeId);

    public void Dispose()
    {
        Bob.Dispose();
        Carol.Dispose();
        David.Dispose();
    }

    private static void OpenChannel(PaymentHarnessNode funder, uint funderKeyIndex, PaymentHarnessNode fundee,
                                    uint fundeeKeyIndex, ChannelId channelId, ShortChannelId shortChannelId,
                                    byte fundingTag)
    {
        var funderParty = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                           LightningMoney.MilliSatoshis(1_000), 30,
                                           LightningMoney.Satoshis(FundingSatoshis), 144);
        var fundeeParty = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(20_000),
                                           LightningMoney.MilliSatoshis(1_000), 30,
                                           LightningMoney.Satoshis(FundingSatoshis), 100);
        var fundingTxId = new TxId(Enumerable.Repeat(fundingTag, 32).ToArray());
        var funderBasepoints = funder.Signer.GetChannelBasepoints(funderKeyIndex);
        var fundeeBasepoints = fundee.Signer.GetChannelBasepoints(fundeeKeyIndex);
        var obscuring = new CommitmentNumber(funderBasepoints.PaymentBasepoint, fundeeBasepoints.PaymentBasepoint,
                                             new Sha256());

        var funderChannel = CreateChannel(funder, funderKeyIndex, funderBasepoints, fundee, fundeeKeyIndex,
                                          fundeeBasepoints, funderParty, fundeeParty, true, channelId, fundingTxId,
                                          obscuring);
        var fundeeChannel = CreateChannel(fundee, fundeeKeyIndex, fundeeBasepoints, funder, funderKeyIndex,
                                          funderBasepoints, fundeeParty, funderParty, false, channelId, fundingTxId,
                                          obscuring);
        funderChannel.ShortChannelId = shortChannelId;
        fundeeChannel.ShortChannelId = shortChannelId;

        funder.Open(funderChannel, fundee.Signer.GetPerCommitmentPoint(fundeeKeyIndex, 0),
                    fundee.Signer.GetPerCommitmentPoint(fundeeKeyIndex, 1));
        fundee.Open(fundeeChannel, funder.Signer.GetPerCommitmentPoint(funderKeyIndex, 0),
                    funder.Signer.GetPerCommitmentPoint(funderKeyIndex, 1));
    }

    private static ChannelModel CreateChannel(PaymentHarnessNode self, uint selfKeyIndex,
                                              ChannelBasepoints selfBasepoints, PaymentHarnessNode peer,
                                              uint peerKeyIndex, ChannelBasepoints peerBasepoints, ChannelParty local,
                                              ChannelParty remote, bool isInitiator, ChannelId channelId,
                                              TxId fundingTxId, CommitmentNumber obscuring)
    {
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(FeeratePerKw), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis),
                                                  selfBasepoints.FundingPubKey, peerBasepoints.FundingPubKey,
                                                  fundingTxId, 0);
        var localKeySet = new ChannelKeySetModel(selfKeyIndex, selfBasepoints.FundingPubKey,
                                                 selfBasepoints.RevocationBasepoint, selfBasepoints.PaymentBasepoint,
                                                 selfBasepoints.DelayedPaymentBasepoint, selfBasepoints.HtlcBasepoint,
                                                 self.Signer.GetPerCommitmentPoint(selfKeyIndex, 0));
        var remoteKeySet = new ChannelKeySetModel(0, peerBasepoints.FundingPubKey, peerBasepoints.RevocationBasepoint,
                                                  peerBasepoints.PaymentBasepoint,
                                                  peerBasepoints.DelayedPaymentBasepoint, peerBasepoints.HtlcBasepoint,
                                                  peer.Signer.GetPerCommitmentPoint(peerKeyIndex, 0));
        var localSat = isInitiator ? FundingSatoshis - PushSatoshis : PushSatoshis;
        return new ChannelModel(channelParams, channelId, obscuring, fundingOutput, isInitiator, null, null,
                                LightningMoney.Satoshis(localSat), localKeySet, 0, 0,
                                LightningMoney.Satoshis(FundingSatoshis - localSat), remoteKeySet, 0,
                                peer.NodeId, 0, ChannelState.Open, ChannelVersion.V1);
    }
}

/// <summary>One node of <see cref="PaymentHarness"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class PaymentHarnessNode : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryChannelRepository _channels = new();
    private readonly ConcurrentQueue<(CompactPubKey To, IChannelMessage Message)> _outbox = new();
    private readonly ChannelLockProvider _lockProvider = new();

    public string Name { get; }
    public CompactPubKey NodeId { get; }
    public HarnessKeyManager KeyManager { get; }
    public NodeOptions Options { get; }
    public ILightningSigner Signer { get; }
    public ChannelManager ChannelManager { get; }
    public IChannelOperations Operations { get; }
    public ICommitScheduler Scheduler { get; }
    public HarnessStateStore Store { get; } = new();
    public InMemoryPaymentDbRepository Payments { get; } = new();
    public InMemoryInvoiceDbRepository Invoices { get; } = new();
    public HarnessForwardingSwitch Switch { get; }
    public IPaymentService PaymentService => _provider.GetRequiredService<IPaymentService>();
    public IInvoiceService InvoiceService => _provider.GetRequiredService<IInvoiceService>();

    /// <summary>The peers' <c>channel_update</c>s the invoice service reads (route hints).</summary>
    public Mock<IChannelUpdateService> ChannelUpdates { get; } = new();

    public PaymentHarness Network { get; set; } = null!;
    public bool OutboxIsEmpty => _outbox.IsEmpty;

    public PaymentHarnessNode(string name, byte seed, RoutingOptions routing)
    {
        Name = name;
        KeyManager = new HarnessKeyManager(seed);
        NodeId = KeyManager.NodeId;
        Options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest, EnableHtlcs = true, Routing = routing };

        var blockchainMonitor = new Mock<IBlockchainMonitor>();
        blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(PaymentHarness.BlockHeight);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<ISecureKeyManager>(KeyManager);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton<IChannelMemoryRepository>(_channels);
        services.AddSingleton<IOnionReplayCache>(new OnionReplayCache());
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddSingleton(blockchainMonitor.Object);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton<IChannelLockProvider>(_lockProvider);
        services.AddSingleton<IChannelMessagePublisher>(new LazyPublisher(this));
        services.AddSingleton<IPeerLivenessProbe>(new MarkedLinkProbe());
        services.AddChannelOperationsServices();
        services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.Zero);
        services.AddSingleton(ChannelUpdates.Object);
        services.AddPaymentsServices();
        services.AddPaymentSendServices();
        services.AddSingleton<HarnessForwardingSwitch>();
        services.AddSingleton<IHtlcSwitch>(sp => sp.GetRequiredService<HarnessForwardingSwitch>());
        services.AddScoped(_ => CreateUnitOfWork());
        services.AddScoped<IPaymentDbRepository>(_ => Payments);
        services.AddScoped<IInvoiceDbRepository>(_ => Invoices);
        services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>, UpdateAddHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFulfillHtlcMessage>, UpdateFulfillHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFailHtlcMessage>, UpdateFailHtlcMessageHandler>();
        services
           .AddScoped<IChannelMessageHandler<UpdateFailMalformedHtlcMessage>, UpdateFailMalformedHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<CommitmentSignedMessage>, CommitmentSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<RevokeAndAckMessage>, RevokeAndAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFeeMessage>, UpdateFeeMessageHandler>();
        _provider = services.BuildServiceProvider();

        Signer = _provider.GetRequiredService<ILightningSigner>();
        ChannelManager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, _lockProvider, _channels,
                                            NullLogger<ChannelManager>.Instance, Signer, _provider);
        ChannelManager.OnResponseMessageReady += (_, args) => _outbox.Enqueue((args.PeerPubKey, args.ResponseMessage));
        Operations = _provider.GetRequiredService<IChannelOperations>();
        Scheduler = _provider.GetRequiredService<ICommitScheduler>();
        Switch = _provider.GetRequiredService<HarnessForwardingSwitch>();
    }

    public ChannelModel Channel(ChannelId channelId) =>
        _channels.TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{Name} has no channel {channelId}");

    /// <summary>Registers an opened channel with its first commitment state (as channel_ready would).</summary>
    public void Open(ChannelModel channel, CompactPubKey peerPoint0, CompactPubKey peerPoint1)
    {
        Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        channel.UpdateCommitments(ChannelStateTransitionService.CreateInitialCommitments(channel, peerPoint0,
                                                                                         peerPoint1));
        _channels.AddChannel(channel);
        Store.Seed(channel.Commitments!);
        _provider.GetRequiredService<IPeerLivenessProbe>().MarkLinkUp(channel.ChannelId, channel.RemoteNodeId);
    }

    /// <summary>A <c>channel_update</c> payload as <paramref name="peer"/> would send it for its side of a channel.
    /// </summary>
    public static ChannelUpdatePayload PeerUpdate(PaymentHarnessNode peer, ShortChannelId shortChannelId)
    {
        var routing = peer.Options.Routing;
        return new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest, shortChannelId, 1,
                                        ChannelUpdatePayload.MessageFlagMustBeOne, 0, routing.CltvExpiryDelta,
                                        routing.HtlcMinimumMsat, routing.FeeBaseMsat,
                                        routing.FeeProportionalMillionths,
                                        PaymentHarness.FundingSatoshis * 1_000);
    }

    /// <summary>Hands the oldest queued message to its recipient's channel manager.</summary>
    /// <returns>False when nothing was queued.</returns>
    public async Task<bool> DeliverNextAsync()
    {
        if (!_outbox.TryDequeue(out var item))
            return false;

        var recipient = Network.NodeFor(item.To);
        await recipient.ChannelManager.HandleChannelMessageAsync(item.Message, new FeatureOptions(), NodeId);
        return true;
    }

    public void Dispose() => _provider.Dispose();

    private IUnitOfWork CreateUnitOfWork()
    {
        var staged = new StagedStateStore(Store);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(staged);
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(staged);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);
        unitOfWork.SetupGet(u => u.PaymentDbRepository).Returns(Payments);
        unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(Invoices);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            staged.Commit();
            return Task.CompletedTask;
        });
        return unitOfWork.Object;
    }

    /// <summary>Publishes through the node's channel manager, which is built after the provider.</summary>
    private sealed class LazyPublisher(PaymentHarnessNode node) : IChannelMessagePublisher
    {
        public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages) =>
            node.ChannelManager.Publish(peerPubKey, messages);
    }

    /// <summary>A channel's link is up once marked (every peer stays connected in this harness).</summary>
    private sealed class MarkedLinkProbe : IPeerLivenessProbe
    {
        private readonly ConcurrentDictionary<ChannelId, byte> _links = new();

        public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                       CancellationToken cancellationToken = default) =>
            Task.FromResult(_links.ContainsKey(channelId));

        public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey) => _links[channelId] = 0;
    }
}

/// <summary>
/// A node key (Sphinx ECDH, invoice signing) and channel keys from a fixed seed byte. Hand-written because Moq cannot
/// set up methods with span parameters.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessKeyManager : ISecureKeyManager
{
    private readonly Key _nodeKey;
    private readonly ExtKey _rootKey;

    public HarnessKeyManager(byte seed)
    {
        _nodeKey = new Key(Enumerable.Repeat(seed, 32).ToArray());
        _rootKey = new ExtKey(new Key(Enumerable.Repeat((byte)(seed ^ 0x5A), 32).ToArray()), new byte[32]);
    }

    public CompactPubKey NodeId => new(_nodeKey.PubKey.ToBytes());

    public BitcoinKeyPath ChannelKeyPath => throw new NotSupportedException();
    public uint HeightOfBirth => 0;

    public ExtPrivKey GetNextChannelKey(out uint index) => throw new NotSupportedException();

    public ExtPrivKey GetChannelKeyAtIndex(uint index) => (ExtPrivKey)_rootKey.Derive((int)index, true).ToBytes();

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

/// <summary>
/// The committed commitment state of one node's channels (per channel), their onion secrets and HTLC origins.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessStateStore
{
    public Lock Sync { get; } = new();
    public Dictionary<ChannelId, ChannelCommitments> Commitments { get; } = [];
    public Dictionary<ChannelId, IReadOnlyList<ShachainEntry>> Shachains { get; } = [];
    public Dictionary<(ChannelId, HtlcKey), Secret> OnionSecrets { get; } = [];
    public Dictionary<(ChannelId, HtlcKey), HtlcOrigin> Origins { get; } = [];
    public List<(ChannelId, HtlcKey)> Pruned { get; } = [];

    /// <summary>When set, the next <c>ApplyAsync</c> throws (a transition whose save fails); then it clears.</summary>
    public bool FailNextApply { get; set; }

    public void Seed(ChannelCommitments commitments)
    {
        lock (Sync)
            Commitments[commitments.ChannelId] = commitments;
    }
}

/// <summary>
/// One unit of work's view of <see cref="HarnessStateStore"/>: writes are staged and applied by <see cref="Commit"/>
/// (the unit of work's save); reads see the committed state plus this unit of work's staged writes.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class StagedStateStore(HarnessStateStore store) : IChannelStateDbRepository, IRemoteShachainDbRepository
{
    private readonly Dictionary<ChannelId, ChannelCommitments> _commitments = [];
    private readonly Dictionary<ChannelId, IReadOnlyList<ShachainEntry>> _shachains = [];
    private readonly Dictionary<(ChannelId, HtlcKey), Secret> _onionSecrets = [];
    private readonly Dictionary<(ChannelId, HtlcKey), HtlcOrigin> _origins = [];
    private readonly List<(ChannelId, HtlcKey)> _prunes = [];

    public void Commit()
    {
        lock (store.Sync)
        {
            foreach (var (id, commitments) in _commitments)
                store.Commitments[id] = commitments;
            foreach (var (id, shachain) in _shachains)
                store.Shachains[id] = shachain;
            foreach (var (key, secret) in _onionSecrets)
                store.OnionSecrets[key] = secret;
            foreach (var (key, origin) in _origins)
                store.Origins[key] = origin;
            store.Pruned.AddRange(_prunes);
        }

        _commitments.Clear();
        _shachains.Clear();
        _onionSecrets.Clear();
        _origins.Clear();
        _prunes.Clear();
    }

    public Task InitializeAsync(ChannelCommitments snapshot, ChannelStateExtras? extras = null)
    {
        _commitments[snapshot.ChannelId] = snapshot;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(ChannelCommitments next, ChannelTransition transition, ChannelStateExtras? extras = null)
    {
        lock (store.Sync)
        {
            if (store.FailNextApply)
            {
                store.FailNextApply = false;
                throw new InvalidOperationException("Injected channel state save failure");
            }
        }

        _commitments[next.ChannelId] = next;
        if (extras?.RemoteShachain is { } shachain)
            _shachains[next.ChannelId] = shachain;
        return Task.CompletedTask;
    }

    public Task<PersistedChannelState?> LoadAsync(ChannelId channelId, CommitmentParams @params) =>
        throw new NotSupportedException();

    public Task SetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc, Secret sharedSecret)
    {
        _onionSecrets[(channelId, htlc)] = sharedSecret;
        return Task.CompletedTask;
    }

    public Task<Secret?> GetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc)
    {
        if (_onionSecrets.TryGetValue((channelId, htlc), out var staged))
            return Task.FromResult<Secret?>(staged);
        lock (store.Sync)
            return Task.FromResult(store.OnionSecrets.TryGetValue((channelId, htlc), out var secret)
                                       ? secret
                                       : (Secret?)null);
    }

    public Task SetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc, HtlcOrigin origin)
    {
        _origins[(channelId, htlc)] = origin;
        return Task.CompletedTask;
    }

    public Task<HtlcOrigin?> GetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc)
    {
        if (_origins.TryGetValue((channelId, htlc), out var staged))
            return Task.FromResult<HtlcOrigin?>(staged);
        lock (store.Sync)
            return Task.FromResult(store.Origins.TryGetValue((channelId, htlc), out var origin)
                                       ? origin
                                       : (HtlcOrigin?)null);
    }

    public Task<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>> FindHtlcsByOriginAsync(HtlcOrigin origin)
    {
        lock (store.Sync)
            return Task.FromResult<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>>(
                store.Origins.Where(pair => pair.Value.Equals(origin)).Select(pair => pair.Key).ToList());
    }

    public Task PruneSettledHtlcsAsync(ChannelId channelId, IEnumerable<HtlcKey> htlcs)
    {
        _prunes.AddRange(htlcs.Select(h => (channelId, h)));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShachainEntry>> GetByChannelIdAsync(ChannelId channelId)
    {
        if (_shachains.TryGetValue(channelId, out var staged))
            return Task.FromResult(staged);
        lock (store.Sync)
            return Task.FromResult(store.Shachains.TryGetValue(channelId, out var shachain)
                                       ? shachain
                                       : (IReadOnlyList<ShachainEntry>)[]);
    }

    public Task SaveAsync(ChannelId channelId, IReadOnlyList<ShachainEntry> entries)
    {
        _shachains[channelId] = entries;
        return Task.CompletedTask;
    }
}