using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Harness;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Application.Channels.Switch;
using Application.Protocol.Factories;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// Two in-process nodes (BOLT2 plan N6-T4): each a real <see cref="ChannelManager"/> with the production
/// normal-operation handlers, engine ports, <c>LocalLightningSigner</c>, commitment/HTLC builders and shachain,
/// joined by in-memory outboxes (each node's <see cref="ChannelManager.OnResponseMessageReady"/> feeds a FIFO the peer
/// drains). Persistence is an in-memory store that commits one transition per save. Every commitment a node signs and
/// every commitment a node verifies is recorded with its txid (invariant I7).
/// </summary>
/// <remarks>
/// Each node's send side is the production <see cref="ChannelOperationsService"/> and <see cref="CommitScheduler"/>
/// (N6-T2, no debounce), publishing through its <see cref="ChannelManager"/>. The HTLC switch records every event and,
/// with <c>localOnlySwitch</c>, hands it to the production <see cref="LocalOnlyHtlcSwitch"/> (with a fake Sphinx peel
/// and a fake error onion, since this project has no serializer for failure messages).
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class TwoNodeHarness : IDisposable
{
    public const ulong FundingSatoshis = 2_000_000;
    public const ulong PushSatoshis = 800_000;
    public const uint InitialFeeratePerKw = 2_500;
    public const uint BlockHeight = 500;

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x6B, 32).ToArray());
    public static readonly byte[] Onion = new byte[1366];

    public HarnessNode Alice { get; }
    public HarnessNode Bob { get; }

    public TwoNodeHarness(bool hasAnchors = false, bool localOnlySwitch = false)
    {
        Alice = new HarnessNode("Alice", 0xA1, localOnlySwitch);
        Bob = new HarnessNode("Bob", 0xB0, localOnlySwitch);
        Alice.Peer = Bob;
        Bob.Peer = Alice;

        // Per-side values differ on purpose (dust limit, to_self_delay) so a direction mix-up changes the txid
        var aliceParty = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                          LightningMoney.MilliSatoshis(1_000), 30,
                                          LightningMoney.Satoshis(FundingSatoshis), 144);
        var bobParty = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(20_000),
                                        LightningMoney.MilliSatoshis(1_000), 30,
                                        LightningMoney.Satoshis(FundingSatoshis), 100);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var obscuring = new CommitmentNumber(Alice.Basepoints.PaymentBasepoint, Bob.Basepoints.PaymentBasepoint,
                                             new Sha256());

        Alice.Open(CreateChannel(Alice, Bob, aliceParty, bobParty, true, fundingTxId, obscuring, hasAnchors));
        Bob.Open(CreateChannel(Bob, Alice, bobParty, aliceParty, false, fundingTxId, obscuring, hasAnchors));
    }

    /// <summary>
    /// Delivers queued messages, one per side in turn (so messages cross like on a real link), until both outboxes are
    /// empty and neither commit scheduler has a signature waiting.
    /// </summary>
    public async Task PumpAsync()
    {
        for (var steps = 0; steps < 10_000; steps++)
        {
            await Alice.Scheduler.WhenIdleAsync();
            await Bob.Scheduler.WhenIdleAsync();
            var aliceSent = await Alice.DeliverNextAsync();
            var bobSent = await Bob.DeliverNextAsync();
            if (aliceSent || bobSent)
                continue;

            await Alice.Scheduler.WhenIdleAsync();
            await Bob.Scheduler.WhenIdleAsync();
            if (Alice.OutboxIsEmpty && Bob.OutboxIsEmpty)
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>The <c>reason</c> the fake error onion returns: a marker, the failure code and its data.</summary>
    public static byte[] FakeErrorPacket(FailureMessage message) =>
        [0xEE, (byte)((ushort)message.Code >> 8), (byte)message.Code, .. message.Data.ToArray()];

    public static Secret Preimage(int tag)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(tag + 1).CopyTo(bytes, 0);
        return new Secret(bytes);
    }

    public static Hash Hash(Secret preimage)
    {
        using var sha256 = new Sha256();
        var hash = new byte[32];
        sha256.AppendData(preimage);
        sha256.GetHashAndReset(hash);
        return new Hash(hash);
    }

    public void Dispose()
    {
        Alice.Dispose();
        Bob.Dispose();
    }

    private static ChannelModel CreateChannel(HarnessNode self, HarnessNode peer, ChannelParty local,
                                              ChannelParty remote, bool isInitiator, TxId fundingTxId,
                                              CommitmentNumber obscuring, bool hasAnchors)
    {
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(InitialFeeratePerKw), 3,
                                              hasAnchors, FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis),
                                                  self.Basepoints.FundingPubKey, peer.Basepoints.FundingPubKey,
                                                  fundingTxId, 0);
        var localKeySet = new ChannelKeySetModel(self.KeyIndex, self.Basepoints.FundingPubKey,
                                                 self.Basepoints.RevocationBasepoint, self.Basepoints.PaymentBasepoint,
                                                 self.Basepoints.DelayedPaymentBasepoint,
                                                 self.Basepoints.HtlcBasepoint, self.Point(0));
        var remoteKeySet = new ChannelKeySetModel(0, peer.Basepoints.FundingPubKey, peer.Basepoints.RevocationBasepoint,
                                                  peer.Basepoints.PaymentBasepoint,
                                                  peer.Basepoints.DelayedPaymentBasepoint,
                                                  peer.Basepoints.HtlcBasepoint, peer.Point(0));
        var localSat = isInitiator ? FundingSatoshis - PushSatoshis : PushSatoshis;
        return new ChannelModel(channelParams, ChannelId, obscuring, fundingOutput, isInitiator, null, null,
                                LightningMoney.Satoshis(localSat), localKeySet, 0, 0,
                                LightningMoney.Satoshis(FundingSatoshis - localSat), remoteKeySet, 0,
                                peer.NodeId, 0, ChannelState.Open, ChannelVersion.V1);
    }
}

/// <summary>One side of <see cref="TwoNodeHarness"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessNode : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryChannelRepository _channels = new();
    private readonly ConcurrentQueue<IChannelMessage> _outbox = new();
    private readonly ChannelLockProvider _lockProvider = new();

    public string Name { get; }
    public uint KeyIndex { get; }
    public CompactPubKey NodeId { get; }
    public ChannelBasepoints Basepoints { get; }
    public ILightningSigner Signer { get; }
    public ChannelManager ChannelManager { get; }
    public IChannelOperations Operations { get; }
    public ICommitScheduler Scheduler { get; }
    public InMemoryChannelStateStore Store { get; } = new();

    /// <summary>What the liveness probe answers for the peer (the peer is "connected").</summary>
    public bool PeerAlive { get; set; } = true;

    public bool OutboxIsEmpty => _outbox.IsEmpty;
    public HarnessNode Peer { get; set; } = null!;

    /// <summary>Remote commitments this node signed: (number, txid).</summary>
    public List<(ulong Number, TxId TxId)> Signed { get; } = [];

    /// <summary>Local commitments this node verified: (number, txid).</summary>
    public List<(ulong Number, TxId TxId)> Verified { get; } = [];

    /// <summary>Every domain event handed to this node's HTLC switch, in order.</summary>
    public List<IChannelDomainEvent> Events { get; } = [];

    /// <summary>Every message this node received, in order (type only).</summary>
    public List<IChannelMessage> Received { get; } = [];

    public ChannelModel Channel => _channels.TryGetChannel(TwoNodeHarness.ChannelId, out var channel)
                                       ? channel
                                       : throw new InvalidOperationException("No channel");

    public ChannelCommitments State => Channel.Commitments!;

    public HarnessNode(string name, byte seedTag, bool localOnlySwitch = false)
    {
        Name = name;
        KeyIndex = seedTag;

        var rootKey = new ExtKey(new Key(Enumerable.Repeat(seedTag, 32).ToArray()), new byte[32]);
        var secureKeyManager = new Mock<ISecureKeyManager>();
        secureKeyManager.Setup(x => x.GetChannelKeyAtIndex(It.IsAny<uint>()))
                        .Returns((uint index) => (ExtPrivKey)rootKey.Derive((int)index, true).ToBytes());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(Store);
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(Store);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            Store.Commit();
            return Task.CompletedTask;
        });

        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                  .Callback((IMessage message, Stream stream) => stream.Write([(byte)((ushort)message.Type >> 8),
                                                                                (byte)message.Type]))
                  .Returns(Task.CompletedTask);

        var blockchainMonitor = new Mock<IBlockchainMonitor>();
        blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(TwoNodeHarness.BlockHeight);

        // A final-hop peel with a per-node secret; the error onion only marks the failure it was given
        var failureOnion = new Mock<IFailureOnionService>();
        failureOnion.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                    .Returns((Secret _, FailureMessage message, int _) => TwoNodeHarness.FakeErrorPacket(message));

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions { EnableHtlcs = true }));
        services.AddSingleton(secureKeyManager.Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton<IChannelMemoryRepository>(_channels);
        services.AddBitcoinInfrastructure();
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddSingleton<ICommitmentSigner>(sp => new RecordingSigner(
                                                     sp.GetRequiredService<CommitmentSigningService>(), _channels,
                                                     Signed));
        services.AddSingleton<ICommitmentVerifier>(sp => new RecordingVerifier(
                                                       sp.GetRequiredService<CommitmentSigningService>(), _channels,
                                                       Verified));
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton(serializer.Object);
        services.AddSingleton(blockchainMonitor.Object);
        services.AddSingleton<ISphinxService>(new FinalHopSphinx(new Secret(Enumerable.Repeat(seedTag, 32).ToArray())));
        services.AddSingleton(failureOnion.Object);
        services.AddSingleton<IChannelLockProvider>(_lockProvider);
        services.AddSingleton<IChannelMessagePublisher>(new LazyPublisher(this));
        services.AddSingleton<IPeerLivenessProbe>(new FlagProbe(this));
        services.AddChannelOperationsServices();
        services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.Zero);
        services.AddSingleton<LocalOnlyHtlcSwitch>();
        services.AddSingleton<IHtlcSwitch>(sp => new RecordingSwitch(
                                               Events,
                                               localOnlySwitch ? sp.GetRequiredService<LocalOnlyHtlcSwitch>() : null));
        services.AddScoped(_ => unitOfWork.Object);
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
        Basepoints = Signer.GetChannelBasepoints(KeyIndex);
        NodeId = new Key(Enumerable.Repeat(seedTag, 32).ToArray()).PubKey.ToBytes();
        ChannelManager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, _lockProvider, _channels,
                                            NullLogger<ChannelManager>.Instance, Signer, _provider);
        ChannelManager.OnResponseMessageReady += (_, args) => _outbox.Enqueue(args.ResponseMessage);
        Operations = _provider.GetRequiredService<IChannelOperations>();
        Scheduler = _provider.GetRequiredService<ICommitScheduler>();
    }

    public CompactPubKey Point(ulong commitmentNumber) => Signer.GetPerCommitmentPoint(KeyIndex, commitmentNumber);

    /// <summary>Registers the opened channel and gives it its first commitment state (as channel_ready would).</summary>
    public void Open(ChannelModel channel)
    {
        Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        channel.UpdateCommitments(ChannelStateTransitionService.CreateInitialCommitments(channel, Peer.Point(0),
                                                                                         Peer.Point(1)));
        _channels.AddChannel(channel);
        Store.Seed(channel.Commitments!);
    }

    /// <summary>Hands the oldest queued message to the peer's channel manager.</summary>
    /// <returns>False when nothing was queued.</returns>
    public async Task<bool> DeliverNextAsync()
    {
        if (!_outbox.TryDequeue(out var message))
            return false;

        Peer.Received.Add(message);
        await Peer.ChannelManager.HandleChannelMessageAsync(message, new FeatureOptions(), NodeId);
        return true;
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Publishes through the node's channel manager, which is built after the provider.</summary>
    private sealed class LazyPublisher(HarnessNode node) : IChannelMessagePublisher
    {
        public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages) =>
            node.ChannelManager.Publish(peerPubKey, messages);
    }

    /// <summary>"Ping before commit" answered by <see cref="PeerAlive"/>.</summary>
    private sealed class FlagProbe(HarnessNode node) : IPeerLivenessProbe
    {
        public Task<bool> IsAliveAsync(CompactPubKey peerPubKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(node.PeerAlive);
    }

    /// <summary>Records every event, then hands it to the production switch when there is one.</summary>
    private sealed class RecordingSwitch(List<IChannelDomainEvent> events, IHtlcSwitch? inner) : IHtlcSwitch
    {
        public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
        {
            lock (events)
                events.Add(channelEvent);
            if (inner is not null)
                await inner.HandleAsync(channelEvent, cancellationToken);
        }
    }

    /// <summary>Every onion peels as a final hop with this node's fixed shared secret (Moq can't mock span arguments).
    /// </summary>
    private sealed class FinalHopSphinx(Secret sharedSecret) : ISphinxService
    {
        public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                     ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                     OnionPacketKind packetKind) => throw new NotSupportedException();

        public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                           ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                           OnionPacketKind packetKind) =>
            throw new NotSupportedException();

        public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey) =>
            throw new NotSupportedException();

        public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData,
                                           CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            new(new byte[] { 2, 0 }, sharedSecret, null);

        public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                                CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            throw new NotSupportedException();
    }

    /// <summary>The production signer port, recording the txid of every commitment it signs.</summary>
    private sealed class RecordingSigner(CommitmentSigningService service, IChannelMemoryRepository channels,
                                         List<(ulong, TxId)> signed) : ICommitmentSigner
    {
        private readonly EngineCommitmentSignerPort _inner = new(service, channels);

        public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                                         CompactPubKey remotePerCommitmentPoint)
        {
            channels.TryGetChannel(channelId, out var channel);
            var txId = service.SignRemoteCommitment(channel!, CommitmentTxSpec.FromCommitmentSpec(spec), number,
                                                    remotePerCommitmentPoint).CommitmentTxId;
            signed.Add((number, txId));
            return _inner.SignRemoteCommitment(channelId, number, spec, remotePerCommitmentPoint);
        }
    }

    /// <summary>The production verifier port, recording the txid of every commitment it accepts.</summary>
    private sealed class RecordingVerifier(CommitmentSigningService service, IChannelMemoryRepository channels,
                                           List<(ulong, TxId)> verified) : ICommitmentVerifier
    {
        private readonly EngineCommitmentVerifierPort _inner =
            new(service, channels, NullLogger<EngineCommitmentVerifierPort>.Instance);

        public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                          CommitmentSignatures signatures)
        {
            if (!_inner.VerifyLocalCommitment(channelId, number, spec, signatures))
                return false;

            channels.TryGetChannel(channelId, out var channel);
            var txId = service.VerifyLocalCommitment(channel!, CommitmentTxSpec.FromCommitmentSpec(spec), number,
                                                     signatures.Signature, signatures.HtlcSignatures).CommitmentTxId;
            verified.Add((number, txId));
            return true;
        }
    }
}

/// <summary>
/// The commitment state and the peer's shachain of one node, staged by <c>ApplyAsync</c>/<c>SaveAsync</c> and committed
/// by the unit of work's save (one transition per save).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryChannelStateStore : IChannelStateDbRepository, IRemoteShachainDbRepository
{
    private ChannelCommitments? _staged;
    private IReadOnlyList<ShachainEntry>? _stagedShachain;

    private readonly Dictionary<HtlcKey, Secret> _onionSecrets = [];
    private readonly List<HtlcKey> _stagedPrunes = [];

    public ChannelCommitments? Committed { get; private set; }

    /// <summary>The archived HTLCs whose pruning was saved.</summary>
    public List<HtlcKey> Pruned { get; } = [];
    public IReadOnlyList<ShachainEntry> CommittedShachain { get; private set; } = [];
    public int Saves { get; private set; }

    public void Seed(ChannelCommitments commitments) => Committed = commitments;

    public void Commit()
    {
        if (_staged is not null)
            Committed = _staged;
        if (_stagedShachain is not null)
            CommittedShachain = _stagedShachain;
        _staged = null;
        _stagedShachain = null;
        Pruned.AddRange(_stagedPrunes);
        _stagedPrunes.Clear();
        Saves++;
    }

    public Task InitializeAsync(ChannelCommitments snapshot, ChannelStateExtras? extras = null)
    {
        _staged = snapshot;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(ChannelCommitments next, ChannelTransition transition, ChannelStateExtras? extras = null)
    {
        _staged = next;
        if (extras?.RemoteShachain is { } shachain)
            _stagedShachain = shachain;
        return Task.CompletedTask;
    }

    public Task<PersistedChannelState?> LoadAsync(ChannelId channelId, CommitmentParams @params) =>
        throw new NotSupportedException();

    public Task SetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc, Secret sharedSecret)
    {
        _onionSecrets[htlc] = sharedSecret;
        return Task.CompletedTask;
    }

    public Task<Secret?> GetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc) =>
        Task.FromResult(_onionSecrets.TryGetValue(htlc, out var secret) ? secret : (Secret?)null);

    public Task PruneSettledHtlcsAsync(ChannelId channelId, IEnumerable<HtlcKey> htlcs)
    {
        _stagedPrunes.AddRange(htlcs);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShachainEntry>> GetByChannelIdAsync(ChannelId channelId) =>
        Task.FromResult(CommittedShachain);

    public Task SaveAsync(ChannelId channelId, IReadOnlyList<ShachainEntry> entries)
    {
        _stagedShachain = entries;
        return Task.CompletedTask;
    }
}

/// <summary>A minimal <see cref="IChannelMemoryRepository"/> for one node (real channels only).</summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryChannelRepository : IChannelMemoryRepository
{
    private readonly Dictionary<ChannelId, ChannelModel> _channels = [];

    public event EventHandler<ChannelUpgradedEventArgs>? OnChannelUpgraded;
    public event EventHandler<ChannelUpdatedEventArgs>? OnChannelUpdated;

    public bool TryGetChannel(ChannelId channelId, [MaybeNullWhen(false)] out ChannelModel channel) =>
        _channels.TryGetValue(channelId, out channel);

    public List<ChannelModel> FindChannels(Func<ChannelModel, bool> predicate) =>
        _channels.Values.Where(predicate).ToList();

    public bool TryGetChannelState(ChannelId channelId, out ChannelState channelState)
    {
        channelState = _channels.TryGetValue(channelId, out var channel) ? channel.State : ChannelState.None;
        return channel is not null;
    }

    public void AddChannel(ChannelModel channel) => _channels[channel.ChannelId] = channel;

    public void UpdateChannel(ChannelModel channel)
    {
        _channels[channel.ChannelId] = channel;
        OnChannelUpdated?.Invoke(this, null!);
    }

    public bool TryRemoveChannel(ChannelId channelId) => _channels.Remove(channelId);

    public bool TryGetTemporaryChannel(CompactPubKey compactPubKey, ChannelId channelId,
                                       [MaybeNullWhen(false)] out ChannelModel channel)
    {
        channel = null;
        return false;
    }

    public bool TryGetTemporaryChannelState(CompactPubKey compactPubKey, ChannelId channelId,
                                            out ChannelState channelState)
    {
        channelState = ChannelState.None;
        return false;
    }

    public void AddTemporaryChannel(CompactPubKey compactPubKey, ChannelModel channel) =>
        throw new NotSupportedException();

    public void UpdateTemporaryChannel(CompactPubKey compactPubKey, ChannelModel channel) =>
        throw new NotSupportedException();

    public bool TryRemoveTemporaryChannel(CompactPubKey compactPubKey, ChannelId channelId) => false;

    public void UpgradeChannel(ChannelId oldChannelId, ChannelModel tempChannel)
    {
        OnChannelUpgraded?.Invoke(this, null!);
        throw new NotSupportedException();
    }
}