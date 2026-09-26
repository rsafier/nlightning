using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Channels.Interfaces;
using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Interfaces;
using Application.Payments.Invoices;
using Bolt11.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// NL-245: our invoices carry a route hint per private channel, with the peer's own policy from its
/// <c>channel_update</c>; since BOLT 7 G4 none once an announced channel can receive the payment
/// (<see cref="InvoiceRouteHintMode"/>).
/// </summary>
public class InvoiceRouteHintTests : IDisposable
{
    private readonly TestNodeKeyManager _us = new(0x0d);
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IChannelUpdateService> _updates = new();
    private readonly Mock<IPeerLivenessProbe> _links = new();
    private readonly Mock<IGraphStore> _graph = new();
    private readonly Dictionary<ShortChannelId, (GraphChannel Channel, DateTimeOffset ReceivedAt)> _graphChannels = [];
    private readonly List<ChannelModel> _open = [];
    private readonly ServiceProvider _provider;

    public InvoiceRouteHintTests()
    {
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => _open.Where(predicate).ToList());
        _links.Setup(l => l.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                         It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
        GraphChannel? found = null;
        _graph.Setup(g => g.TryGetChannel(It.IsAny<ShortChannelId>(), out found))
              .Returns((ShortChannelId scid, out GraphChannel? channel) =>
               {
                   var known = _graphChannels.TryGetValue(scid, out var entry);
                   channel = known ? entry.Channel : null;
                   return known;
               });
        var at = DateTimeOffset.MinValue;
        _graph.Setup(g => g.TryGetChannelReceivedAt(It.IsAny<ShortChannelId>(), out at))
              .Returns((ShortChannelId scid, out DateTimeOffset receivedAt) =>
               {
                   var known = _graphChannels.TryGetValue(scid, out var entry);
                   receivedAt = known ? entry.ReceivedAt : default;
                   return known;
               });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddScoped<IInvoiceDbRepository>(_ => new InMemoryInvoiceDbRepository());
        services.AddScoped(_ => unitOfWork.Object);
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Given_OpenChannelWithPeerUpdate_When_CreatingAnInvoice_Then_ItHintsThroughThePeerWithItsPolicy()
    {
        // Arrange
        var peer = new TestNodeKeyManager(0x0c).NodeId;
        var channel = AddChannel(peer, 1, new ShortChannelId(401, 2, 1), remoteSat: 600_000);
        SetPeerUpdate(channel, feeBase: 2_000, feePpm: 500, cltvDelta: 40);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(LightningMoney.MilliSatoshis(50_000_123), "hint",
                                                               null, TestContext.Current.CancellationToken);

        // Assert: the hint decodes with the peer's policy, not ours (our routing options are the defaults)
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(peer, hint.CompactPubKey);
        Assert.Equal(new ShortChannelId(401, 2, 1), hint.ShortChannelId);
        Assert.Equal((2_000u, 500u, (ushort)40),
                     (hint.FeeBaseMsat, hint.FeeProportionalMillionths, hint.CltvExpiryDelta));
    }

    [Fact]
    public async Task Given_NoPeerUpdateOrADisabledOne_When_CreatingAnInvoice_Then_NoHint()
    {
        // Arrange
        AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1), remoteSat: 600_000);
        var disabled = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                  remoteSat: 600_000);
        SetPeerUpdate(disabled, 1_000, 1, 40, ChannelUpdatePayload.ChannelFlagDisable);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(null, "none", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints);
    }

    [Fact]
    public async Task Given_PeerCannotSendTheAmount_When_CreatingAnInvoice_Then_ThatChannelIsSkipped()
    {
        // Arrange: the first peer holds only 10,000 sat on its side
        var poor = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                              remoteSat: 10_000);
        var rich = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                              remoteSat: 600_000);
        SetPeerUpdate(poor, 1_000, 1, 40);
        SetPeerUpdate(rich, 1_000, 1, 40);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "amount", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(rich.RemoteNodeId, hint.CompactPubKey);
    }

    [Fact]
    public async Task Given_FourChannels_When_CreatingAnInvoice_Then_ThreeHintsLargestPeerBalanceFirst()
    {
        // Arrange
        for (byte i = 1; i <= 4; i++)
        {
            var channel = AddChannel(new TestNodeKeyManager((byte)(0x20 + i)).NodeId, i,
                                     new ShortChannelId(400 + (uint)i, 1, 0), remoteSat: 100_000UL * i);
            SetPeerUpdate(channel, 1_000, 1, 40);
        }

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(null, "many", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        var hints = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints;
        Assert.Equal(InvoiceService.MaxRouteHints, hints.Count);
        Assert.Equal([new ShortChannelId(404, 1, 0), new ShortChannelId(403, 1, 0), new ShortChannelId(402, 1, 0)],
                     hints.Select(h => Assert.Single(h).ShortChannelId));
    }

    [Fact]
    public async Task Given_ScidAliasChannel_When_CreatingAnInvoice_Then_TheHintUsesThePeersAlias()
    {
        // Arrange
        var alias = new ShortChannelId(16_000_000, 7, 7);
        var channel = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                 remoteSat: 600_000, FeatureSupport.Optional);
        channel.RemoteAlias = alias;
        SetPeerUpdate(channel, 1_000, 1, 40);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(null, "alias", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(alias, hint.ShortChannelId);
    }

    [Fact]
    public async Task Given_NoChannelServices_When_CreatingAnInvoice_Then_NoHint()
    {
        // Arrange
        var service = new InvoiceService(_provider.GetRequiredService<IServiceScopeFactory>(), _us,
                                         Microsoft.Extensions.Options.Options.Create(
                                             new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                         NullLogger<InvoiceService>.Instance);

        // Act
        var invoice = await service.CreateInvoiceAsync(null, "plain", null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints);
    }

    [Theory]
    [InlineData(true, 580_000_000UL)]
    [InlineData(false, 577_760_000UL)]
    public void Given_PeerBalance_When_ComputingWhatThePeerCanSend_Then_ReserveAndItsCommitFeeAreSubtracted(
        bool weFunded, ulong expectedMsat)
    {
        // Arrange: 600,000 sat on the peer's side, 20,000 sat reserve, 2,500 sat/kw; when the peer funded the channel
        // it also pays the commitment fee with one more HTLC: 2,500 * (724 + 172) / 1,000 = 2,240 sat
        var channel = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                 remoteSat: 600_000, weFunded: weFunded);

        // Act
        var spendable = InvoiceService.GetPeerSpendable(channel);

        // Assert
        Assert.Equal(expectedMsat, spendable);
    }

    [Fact]
    public async Task Given_AmountAboveThePeersSpendableButBelowItsBalance_When_CreatingAnInvoice_Then_NoHint()
    {
        // Arrange: the peer holds 60,000 sat but must keep a 20,000 sat reserve
        var channel = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                 remoteSat: 60_000);
        SetPeerUpdate(channel, 1_000, 1, 40);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "reserve", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints);
    }

    [Fact]
    public async Task Given_PeerLinkDown_When_CreatingAnInvoice_Then_ThatChannelIsSkipped()
    {
        // Arrange
        var away = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                              remoteSat: 900_000);
        var connected = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                   remoteSat: 600_000);
        SetPeerUpdate(away, 1_000, 1, 40);
        SetPeerUpdate(connected, 1_000, 1, 40);
        _links.Setup(l => l.IsAliveAsync(away.ChannelId, away.RemoteNodeId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(null, "links", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(connected.RemoteNodeId, hint.CompactPubKey);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannelThePeerCanPayUsOver_When_CreatingAnInvoice_Then_NoHints()
    {
        // Arrange: a public channel with inbound, and a private one that would get a hint
        var announced = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                   remoteSat: 600_000, announced: true);
        var hidden = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                remoteSat: 600_000);
        SetPeerUpdate(announced, 1_000, 1, 40);
        SetPeerUpdate(hidden, 1_000, 1, 40);
        PutInGraph(announced, TimeSpan.FromHours(1));

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "public", null,
                                                               TestContext.Current.CancellationToken);

        // Assert: payers find us through the graph; the private channel stays unrevealed
        Assert.Empty(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannelAndHintsForced_When_CreatingAnInvoice_Then_Hints()
    {
        // Arrange
        var announced = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                   remoteSat: 600_000, announced: true);
        SetPeerUpdate(announced, 1_000, 1, 40);

        // Act
        var invoice = await CreateService(InvoiceRouteHintMode.Always)
                         .CreateInvoiceAsync(null, "forced", null, TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(announced.RemoteNodeId, hint.CompactPubKey);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannelWithoutEnoughInbound_When_CreatingAnInvoice_Then_ThePrivateHintStays()
    {
        // Arrange: the public channel's peer holds only 30,000 sat (20,000 of them reserve)
        var announced = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                   remoteSat: 30_000, announced: true);
        var hidden = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                remoteSat: 600_000);
        SetPeerUpdate(announced, 1_000, 1, 40);
        SetPeerUpdate(hidden, 1_000, 1, 40);
        PutInGraph(announced, TimeSpan.FromHours(1));

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "inbound", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(hidden.RemoteNodeId, hint.CompactPubKey);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannelWhoseLinkIsDown_When_CreatingAnInvoice_Then_HintsStay()
    {
        // Arrange
        var announced = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                   remoteSat: 600_000, announced: true);
        var hidden = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                remoteSat: 600_000);
        SetPeerUpdate(announced, 1_000, 1, 40);
        SetPeerUpdate(hidden, 1_000, 1, 40);
        _links.Setup(l => l.IsAliveAsync(announced.ChannelId, announced.RemoteNodeId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        PutInGraph(announced, TimeSpan.FromHours(1));

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(null, "down", null,
                                                               TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(hidden.RemoteNodeId, hint.CompactPubKey);
    }

    [Fact]
    public async Task Given_HintsTurnedOff_When_CreatingAnInvoiceOnAPrivateOnlyNode_Then_NoHints()
    {
        // Arrange
        var hidden = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                remoteSat: 600_000);
        SetPeerUpdate(hidden, 1_000, 1, 40);

        // Act
        var invoice = await CreateService(InvoiceRouteHintMode.Never)
                         .CreateInvoiceAsync(null, "never", null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints);
    }

    [Theory]
    [InlineData(false, true, 60)] // announced, but not in our graph yet
    [InlineData(true, true, 5)] // in our graph for 5 minutes only (grace 10)
    [InlineData(true, false, 60)] // in our graph without the peer's policy
    public async Task Given_AnAnnouncedChannelPayersMayNotSeeYet_When_CreatingAnInvoice_Then_ThePrivateHintStays(
        bool inGraph, bool bothPolicies, int minutesInGraph)
    {
        // Arrange
        var announced = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                   remoteSat: 600_000, announced: true);
        var hidden = AddChannel(new TestNodeKeyManager(0x0e).NodeId, 2, new ShortChannelId(402, 2, 1),
                                remoteSat: 600_000);
        SetPeerUpdate(announced, 1_000, 1, 40);
        SetPeerUpdate(hidden, 1_000, 1, 40);
        if (inGraph)
            PutInGraph(announced, TimeSpan.FromMinutes(minutesInGraph), bothPolicies);

        // Act
        var invoice = await CreateService().CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "early", null,
                                                               TestContext.Current.CancellationToken);

        // Assert: payers that route from their graph could not reach us without the hints
        var hints = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints;
        Assert.Contains(hints, h => h.Any(e => e.CompactPubKey == hidden.RemoteNodeId));
    }

    [Fact]
    public async Task Given_AnAnnouncedChannelWithoutAGraphStore_When_CreatingAnInvoice_Then_HintsStay()
    {
        // Arrange
        var announced = AddChannel(new TestNodeKeyManager(0x0c).NodeId, 1, new ShortChannelId(401, 2, 1),
                                   remoteSat: 600_000, announced: true);
        SetPeerUpdate(announced, 1_000, 1, 40);
        PutInGraph(announced, TimeSpan.FromHours(1));

        // Act
        var invoice = await CreateService(withGraph: false)
                         .CreateInvoiceAsync(null, "no graph", null, TestContext.Current.CancellationToken);

        // Assert
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(announced.RemoteNodeId, hint.CompactPubKey);
    }

    private void PutInGraph(ChannelModel channel, TimeSpan inGraphFor, bool bothPolicies = true)
    {
        var us = _us.NodeId;
        var peer = channel.RemoteNodeId;
        var (node1, node2) = GraphChannel.CompareNodeIds(us, peer) < 0 ? (us, peer) : (peer, us);
        var graphChannel = new GraphChannel(channel.ShortChannelId, node1, node2, node1, node2, 2_000_000);
        var peerDirection = node1 == peer ? (byte)0 : (byte)1;
        graphChannel = graphChannel.WithPolicy(Policy((byte)(1 - peerDirection)));
        if (bothPolicies)
            graphChannel = graphChannel.WithPolicy(Policy(peerDirection));
        _graphChannels[channel.ShortChannelId] = (graphChannel, DateTimeOffset.UtcNow - inGraphFor);

        static GraphPolicy Policy(byte direction) =>
            new(1_700_000_000, ChannelUpdatePayload.MessageFlagMustBeOne, direction, 40, 1, 1_000_000_000, 1_000, 1);
    }

    private InvoiceService CreateService(InvoiceRouteHintMode mode = InvoiceRouteHintMode.Auto, bool withGraph = true) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _us,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            NullLogger<InvoiceService>.Instance, _channels.Object, _updates.Object, _links.Object,
            Microsoft.Extensions.Options.Options.Create(new InvoiceOptions { RouteHints = mode }),
            withGraph ? _graph.Object : null);

    private ChannelModel AddChannel(CompactPubKey peer, byte tag, ShortChannelId shortChannelId, ulong remoteSat,
                                    FeatureSupport scidAlias = FeatureSupport.No, bool weFunded = true,
                                    bool announced = false)
    {
        var key = new CompactPubKey(new NBitcoin.Key().PubKey.ToBytes());
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(2_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false, scidAlias)
        {
            AnnounceChannel = announced
        };
        var keySet = new ChannelKeySetModel(tag, key, key, key, key, key, key);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null, null,
                                       weFunded, null, null, LightningMoney.Satoshis(2_000_000 - remoteSat), keySet,
                                       0, 0,
                                       LightningMoney.Satoshis(remoteSat), keySet, 0, peer, 0, ChannelState.Open,
                                       ChannelVersion.V1)
        {
            ShortChannelId = shortChannelId
        };
        if (announced)
        {
            // Both halves of announcement_signatures exchanged (ChannelAnnouncementService.IsAnnounced)
            channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(new byte[64], new byte[64]));
            channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UnixEpoch);
        }

        _open.Add(channel);
        return channel;
    }

    private void SetPeerUpdate(ChannelModel channel, uint feeBase, uint feePpm, ushort cltvDelta,
                               byte channelFlags = 0)
    {
        ChannelUpdatePayload? update = new(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest,
                                           channel.ShortChannelId, 1, ChannelUpdatePayload.MessageFlagMustBeOne,
                                           channelFlags, cltvDelta, 1_000, feeBase, feePpm, 2_000_000_000);
        _updates.Setup(u => u.TryGetRemoteChannelUpdate(channel.ChannelId, out update)).Returns(true);
    }
}