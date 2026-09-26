using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin.Secp256k1;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Gossip;

/// <summary>A clock that stays where it is set.</summary>
[ExcludeFromCodeCoverage]
internal sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A secp256k1 key for signing test gossip.</summary>
[ExcludeFromCodeCoverage]
internal sealed class TestGossipKey
{
    private readonly ECPrivKey _key;

    public TestGossipKey(byte seed)
    {
        var bytes = new byte[32];
        bytes[31] = seed;
        bytes[0] = 0x11;
        _key = Context.Instance.CreateECPrivKey(bytes);
        var pubKey = new byte[33];
        _key.CreatePubKey().WriteToSpan(true, pubKey, out _);
        PubKey = new CompactPubKey(pubKey);
    }

    public CompactPubKey PubKey { get; }

    public CompactSignature Sign(Hash hash)
    {
        if (!_key.TrySignECDSA((byte[])hash, out var signature) || signature is null)
            throw new InvalidOperationException("Signing failed");

        var compact = new byte[64];
        signature.WriteCompactToSpan(compact);
        return new CompactSignature(compact);
    }
}

/// <summary>
/// Builds a graph store over <see cref="InMemoryGraphDbRepository"/> and an ingress with the real
/// <see cref="GossipSignatureVerifier"/> and a mocked funding output lookup, and signs test gossip.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class GraphTestKit
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    public GraphTestKit(InMemoryGraphDbRepository? repository = null, DateTimeOffset? now = null,
                        Action<GossipGraphOptions>? configure = null,
                        IChannelMemoryRepository? channelMemoryRepository = null)
    {
        Repository = repository ?? new InMemoryGraphDbRepository();
        Clock = new SettableTimeProvider(now ?? DefaultNow);
        Options = new GossipGraphOptions { Workers = 1 };
        configure?.Invoke(Options);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.GraphDbRepository).Returns(Repository);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            Repository.Commit();
            return Task.CompletedTask;
        });
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();

        Store = new GraphStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<GraphStore>.Instance,
                               Clock);
        FundingLookup = new Mock<IFundingOutputLookup>();
        var nodeOptions = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve("regtest") };
        Ingress = new GossipIngress(Store, new GossipSignatureVerifier(), FundingLookup.Object,
                                    Microsoft.Extensions.Options.Options.Create(Options),
                                    Microsoft.Extensions.Options.Options.Create(nodeOptions),
                                    NullLogger<GossipIngress>.Instance, Clock, channelMemoryRepository);
    }

    public InMemoryGraphDbRepository Repository { get; }
    public SettableTimeProvider Clock { get; }
    public GossipGraphOptions Options { get; }
    public GraphStore Store { get; }
    public Mock<IFundingOutputLookup> FundingLookup { get; }
    public GossipIngress Ingress { get; }

    /// <summary>Every funding output is found, unspent, <paramref name="confirmations"/> deep, with this amount.</summary>
    public void FundingFound(long amountSat = 1_000_000, uint confirmations = 6)
    {
        FundingLookup.Setup(l => l.VerifyAsync(It.IsAny<ShortChannelId>(), It.IsAny<CompactPubKey>(),
                                               It.IsAny<CompactPubKey>(), It.IsAny<LightningMoney?>(),
                                               It.IsAny<CancellationToken>()))
                     .ReturnsAsync((ShortChannelId scid, CompactPubKey _, CompactPubKey _, LightningMoney? _,
                                    CancellationToken _) =>
                                       FundingOutputLookupResult.WithOutput(
                                           FundingOutputStatus.Found, TxIdFor(scid),
                                           LightningMoney.Satoshis(amountSat), [0x00, 0x20], confirmations));
    }

    public void FundingFails(FundingOutputStatus status) =>
        FundingLookup.Setup(l => l.VerifyAsync(It.IsAny<ShortChannelId>(), It.IsAny<CompactPubKey>(),
                                               It.IsAny<CompactPubKey>(), It.IsAny<LightningMoney?>(),
                                               It.IsAny<CancellationToken>()))
                     .ReturnsAsync(FundingOutputLookupResult.Failed(status));

    public static TxId TxIdFor(ShortChannelId shortChannelId)
    {
        var bytes = new byte[32];
        ((byte[])shortChannelId).CopyTo(bytes, 0);
        bytes[31] = 0xAA;
        return new TxId(bytes);
    }

    public static Mock<IPeerService> CreatePeer(byte seed = 0x77)
    {
        var peer = new Mock<IPeerService>();
        peer.SetupGet(p => p.PeerPubKey).Returns(new TestGossipKey(seed).PubKey);
        peer.Setup(p => p.SendWarningAsync(It.IsAny<Domain.Exceptions.WarningException>()))
            .Returns(Task.CompletedTask);
        return peer;
    }

    /// <summary>A signed announcement between two test nodes (ordered by node id), with their funding keys.</summary>
    public static ChannelAnnouncementMessage SignedChannelAnnouncement(ShortChannelId shortChannelId,
                                                                       TestGossipKey nodeA, TestGossipKey nodeB,
                                                                       TestGossipKey bitcoinA,
                                                                       TestGossipKey bitcoinB,
                                                                       ChainHash? chainHash = null)
    {
        var (node1, node2, bitcoin1, bitcoin2) =
            ((byte[])nodeA.PubKey).AsSpan().SequenceCompareTo((byte[])nodeB.PubKey) < 0
                ? (nodeA, nodeB, bitcoinA, bitcoinB)
                : (nodeB, nodeA, bitcoinB, bitcoinA);
        var unsigned = new ChannelAnnouncementPayload(ChannelAnnouncementPayload.EmptySignature,
                                                      ChannelAnnouncementPayload.EmptySignature,
                                                      ChannelAnnouncementPayload.EmptySignature,
                                                      ChannelAnnouncementPayload.EmptySignature,
                                                      ReadOnlyMemory<byte>.Empty,
                                                      chainHash ?? ChainConstants.Regtest, shortChannelId,
                                                      node1.PubKey, node2.PubKey, bitcoin1.PubKey, bitcoin2.PubKey);
        var hash = unsigned.GetSignatureHash();
        return new ChannelAnnouncementMessage(unsigned.WithSignatures(node1.Sign(hash), node2.Sign(hash),
                                                                      bitcoin1.Sign(hash), bitcoin2.Sign(hash)));
    }

    /// <summary>A signed update from <paramref name="origin"/> for the given direction.</summary>
    public static ChannelUpdateMessage SignedChannelUpdate(ShortChannelId shortChannelId, TestGossipKey origin,
                                                           byte direction, uint timestamp, uint feeBaseMsat = 1_000,
                                                           byte messageFlags = ChannelUpdatePayload.MessageFlagMustBeOne,
                                                           bool disabled = false)
    {
        var channelFlags = (byte)(direction | (disabled ? ChannelUpdatePayload.ChannelFlagDisable : 0));
        var unsigned = new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest,
                                                shortChannelId, timestamp, messageFlags, channelFlags, 40, 1_000,
                                                feeBaseMsat, 100, 500_000_000);
        return new ChannelUpdateMessage(unsigned.WithSignature(origin.Sign(unsigned.GetSignatureHash())));
    }

    /// <summary>A signed node announcement with one IPv4 address.</summary>
    public static NodeAnnouncementMessage SignedNodeAnnouncement(TestGossipKey node, uint timestamp,
                                                                 string alias = "test", byte[]? addresses = null)
    {
        addresses ??= [1, 127, 0, 0, 1, 0x26, 0x07];
        var unsigned = new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty,
                                                   timestamp, node.PubKey, new byte[] { 1, 2, 3 },
                                                   NodeAnnouncementPayload.EncodeAlias(alias), addresses);
        return new NodeAnnouncementMessage(unsigned.WithSignature(node.Sign(unsigned.GetSignatureHash())));
    }

    /// <summary>
    /// Asserts two graphs hold the same channels, policies and nodes, raw signed bytes included (the read-model
    /// records compare their byte fields by reference, so they are compared here by content).
    /// </summary>
    public static void AssertSameGraph(Domain.Gossip.Graph.IGraphView expected, Domain.Gossip.Graph.IGraphView actual)
    {
        Assert.Equal(expected.ChannelCount, actual.ChannelCount);
        foreach (var channel in expected.Channels)
        {
            Assert.True(actual.TryGetChannel(channel.ShortChannelId, out var other), $"{channel.ShortChannelId}");
            Assert.Equal(channel with { Policy1 = null, Policy2 = null },
                         other with { Policy1 = null, Policy2 = null });
            Assert.Equal(channel.RawAnnouncement.ToArray(), other.RawAnnouncement.ToArray());
            AssertSamePolicy(channel.Policy1, other.Policy1);
            AssertSamePolicy(channel.Policy2, other.Policy2);
        }

        Assert.Equal(expected.Nodes.Count(), actual.Nodes.Count());
        foreach (var node in expected.Nodes)
        {
            Assert.True(actual.TryGetNode(node.NodeId, out var other), $"{node.NodeId}");
            Assert.Equal(node, other);
            Assert.Equal(node.RawAnnouncement.ToArray(), other.RawAnnouncement.ToArray());
        }
    }

    private static void AssertSamePolicy(Domain.Gossip.Graph.GraphPolicy? expected,
                                         Domain.Gossip.Graph.GraphPolicy? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected with { RawUpdate = default, ExtraData = default },
                     actual with { RawUpdate = default, ExtraData = default });
        Assert.Equal(expected.RawUpdate.ToArray(), actual.RawUpdate.ToArray());
        Assert.Equal(expected.ExtraData.ToArray(), actual.ExtraData.ToArray());
    }

    /// <summary>The direction whose origin is <paramref name="node"/> in a channel with <paramref name="other"/>.</summary>
    public static byte DirectionOf(TestGossipKey node, TestGossipKey other) =>
        ((byte[])node.PubKey).AsSpan().SequenceCompareTo((byte[])other.PubKey) < 0 ? (byte)0 : (byte)1;
}