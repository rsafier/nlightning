using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Channels.Handlers;
using Application.Gossip.Announcements;
using Application.Gossip.Relay.Interfaces;
using Application.Protocol.Factories;
using Channels.Harness;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// The two ends of one public channel (BOLT 7 plan G1), each with a <b>real</b> <c>LocalLightningSigner</c> (node key
/// and channel keys from a seed), the real gossip signature verifier, the production
/// <see cref="ChannelAnnouncementService"/> and <see cref="AnnouncementSignaturesMessageHandler"/>, a mocked chain tip
/// and a recording persistence and gossip sink.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class AnnouncementTestPair : IDisposable
{
    public const uint FundingHeight = 700;
    public const ushort FundingOutputIndex = 1;

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x5C, 32).ToArray());
    public static readonly ShortChannelId ShortChannelId = new(FundingHeight, 7, FundingOutputIndex);
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);

    public AnnouncementTestNode Alice { get; }
    public AnnouncementTestNode Bob { get; }

    /// <param name="announce">Both channel models carry <c>announce_channel</c>.</param>
    /// <param name="tipDepth">The funding transaction's confirmations at both nodes' chain tip.</param>
    public AnnouncementTestPair(bool announce = true, uint tipDepth = GossipOptions.MinimumAnnouncementDepth,
                                GossipOptions? gossipOptions = null)
    {
        Alice = new AnnouncementTestNode("Alice", 0xA1, gossipOptions);
        Bob = new AnnouncementTestNode("Bob", 0xB2, gossipOptions);

        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x42, 32).ToArray());
        Alice.Open(CreateChannel(Alice, Bob, true, fundingTxId, announce));
        Bob.Open(CreateChannel(Bob, Alice, false, fundingTxId, announce));
        SetTipDepth(tipDepth);
    }

    /// <summary>Moves both nodes' chain tip so the funding transaction has <paramref name="depth"/> confirmations.</summary>
    public void SetTipDepth(uint depth)
    {
        Alice.Tip = FundingHeight + depth - 1;
        Bob.Tip = FundingHeight + depth - 1;
    }

    public void Dispose()
    {
        Alice.Dispose();
        Bob.Dispose();
    }

    private static ChannelModel CreateChannel(AnnouncementTestNode self, AnnouncementTestNode peer, bool isInitiator,
                                              TxId fundingTxId, bool announce)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, Capacity, 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No)
        {
            AnnounceChannel = announce
        };
        var fundingOutput = new FundingOutputInfo(Capacity, self.Basepoints.FundingPubKey,
                                                  peer.Basepoints.FundingPubKey, fundingTxId, FundingOutputIndex);
        var localKeySet = new ChannelKeySetModel(self.KeyIndex, self.Basepoints.FundingPubKey,
                                                 self.Basepoints.RevocationBasepoint, self.Basepoints.PaymentBasepoint,
                                                 self.Basepoints.DelayedPaymentBasepoint,
                                                 self.Basepoints.HtlcBasepoint, self.Point(0));
        var remoteKeySet = new ChannelKeySetModel(0, peer.Basepoints.FundingPubKey, peer.Basepoints.RevocationBasepoint,
                                                  peer.Basepoints.PaymentBasepoint,
                                                  peer.Basepoints.DelayedPaymentBasepoint,
                                                  peer.Basepoints.HtlcBasepoint, peer.Point(0));
        var obscuring = isInitiator
                            ? new CommitmentNumber(self.Basepoints.PaymentBasepoint, peer.Basepoints.PaymentBasepoint,
                                                   new Sha256())
                            : new CommitmentNumber(peer.Basepoints.PaymentBasepoint, self.Basepoints.PaymentBasepoint,
                                                   new Sha256());
        var localBalance = isInitiator ? Capacity : LightningMoney.Zero;
        return new ChannelModel(channelParams, ChannelId, obscuring, fundingOutput, isInitiator, null, null,
                                localBalance, localKeySet, 0, 0, Capacity - localBalance, remoteKeySet, 0,
                                peer.NodeId, 0, ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = ShortChannelId
        };
    }
}

/// <summary>One end of <see cref="AnnouncementTestPair"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class AnnouncementTestNode : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();

    public string Name { get; }
    public uint KeyIndex { get; }
    public CompactPubKey NodeId { get; }
    public ILightningSigner Signer { get; }
    public ChannelBasepoints Basepoints { get; }
    public IGossipSignatureVerifier Verifier { get; }
    public NodeOptions NodeOptions { get; } = new() { BitcoinNetwork = BitcoinNetwork.Regtest };
    public InMemoryChannelRepository Channels { get; } = new();
    public RecordingOwnGossipSink Sink { get; } = new();
    public RecordingRelayScheduler Relay { get; } = new();
    public Mock<IChannelDbRepository> ChannelDb { get; } = new();
    public Mock<IUnitOfWork> UnitOfWork { get; } = new();
    public int Saves { get; private set; }
    public ChannelAnnouncementService Service { get; }
    public AnnouncementSignaturesMessageHandler Handler { get; }
    public ChannelModel Channel => Channels.TryGetChannel(AnnouncementTestPair.ChannelId, out var c) ? c : null!;

    public uint Tip
    {
        get => _blockchainMonitor.Object.LastProcessedBlockHeight;
        set => _blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(value);
    }

    public AnnouncementTestNode(string name, byte seedTag, GossipOptions? gossipOptions)
    {
        Name = name;
        KeyIndex = seedTag;

        var nodeKey = Enumerable.Repeat(seedTag, 32).ToArray();
        using (var key = new Key(nodeKey))
            NodeId = key.PubKey.ToBytes();

        var rootKey = new ExtKey(new Key(nodeKey), new byte[32]);
        var secureKeyManager = new Mock<ISecureKeyManager>();
        secureKeyManager.Setup(x => x.GetChannelKeyAtIndex(It.IsAny<uint>()))
                        .Returns((uint index) => (ExtPrivKey)rootKey.Derive((int)index, true).ToBytes());
        secureKeyManager.Setup(x => x.GetNodeKeyPair())
                        .Returns(() => new CryptoKeyPair(new PrivKey(nodeKey.ToArray()), NodeId));
        secureKeyManager.Setup(x => x.GetNodePubKey()).Returns(NodeId);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(NodeOptions));
        services.AddSingleton(secureKeyManager.Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton<IChannelMemoryRepository>(Channels);
        services.AddBitcoinInfrastructure();
        _provider = services.BuildServiceProvider();

        Signer = _provider.GetRequiredService<ILightningSigner>();
        Verifier = _provider.GetRequiredService<IGossipSignatureVerifier>();
        Basepoints = Signer.GetChannelBasepoints(KeyIndex);

        UnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(ChannelDb.Object);
        UnitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            Saves++;
            return Task.CompletedTask;
        });

        Service = new ChannelAnnouncementService(_blockchainMonitor.Object, Verifier, Signer,
                                                 NullLogger<ChannelAnnouncementService>.Instance,
                                                 new MessageFactory(Options.Create(NodeOptions)),
                                                 new OwnGossipPublisher(Sink, Relay), Options.Create(NodeOptions),
                                                 Options.Create(gossipOptions ?? new GossipOptions()));
        Handler = new AnnouncementSignaturesMessageHandler(Service, Channels,
                                                           NullLogger<AnnouncementSignaturesMessageHandler>.Instance,
                                                           UnitOfWork.Object);
    }

    public CompactPubKey Point(ulong commitmentNumber) => Signer.GetPerCommitmentPoint(KeyIndex, commitmentNumber);

    /// <summary>Registers the channel in memory and with the (source-less) signer.</summary>
    public void Open(ChannelModel channel)
    {
        Channels.AddChannel(channel);
        Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
    }

    public void Dispose() => _provider.Dispose();
}

/// <summary>An <see cref="IGossipRelayScheduler"/> that records what it is given, in order.</summary>
[ExcludeFromCodeCoverage]
internal sealed class RecordingRelayScheduler : IGossipRelayScheduler
{
    public List<IMessagePayload> Queued { get; } = [];
    public int Flushes { get; private set; }

    public void EnqueueOwnChannelAnnouncement(ChannelAnnouncementPayload announcement) => Add(announcement);

    public void EnqueueOwnChannelUpdate(ChannelUpdatePayload update) => Add(update);

    public void EnqueueOwnNodeAnnouncement(NodeAnnouncementPayload announcement) => Add(announcement);

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Flushes++;
        return Task.CompletedTask;
    }

    private void Add(IMessagePayload payload)
    {
        lock (Queued)
            Queued.Add(payload);
    }
}

/// <summary>An <see cref="IOwnGossipSink"/> that records what it is given.</summary>
[ExcludeFromCodeCoverage]
internal sealed class RecordingOwnGossipSink : IOwnGossipSink
{
    public List<(ChannelAnnouncementPayload Announcement, LightningMoney Capacity)> ChannelAnnouncements { get; } = [];
    public List<ChannelUpdatePayload> ChannelUpdates { get; } = [];
    public List<NodeAnnouncementPayload> NodeAnnouncements { get; } = [];

    public void AddOwnChannelAnnouncement(ChannelAnnouncementPayload announcement, LightningMoney capacity)
    {
        lock (ChannelAnnouncements)
            ChannelAnnouncements.Add((announcement, capacity));
    }

    public void AddOwnChannelUpdate(ChannelUpdatePayload update)
    {
        lock (ChannelUpdates)
            ChannelUpdates.Add(update);
    }

    public void AddOwnNodeAnnouncement(NodeAnnouncementPayload announcement)
    {
        lock (NodeAnnouncements)
            NodeAnnouncements.Add(announcement);
    }
}