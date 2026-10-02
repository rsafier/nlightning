using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Gossip.Interfaces;
using Application.Offers.Receive;
using Application.Payments.Invoices;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Payments;

/// <summary>
/// NL-452: the blinded payment paths of our BOLT 12 invoices are introduced by an announced channel when one can
/// carry the payment (any payer reaches it through the graph), with a private channel only as the fallback so a
/// private-only node stays payable by its own peers.
/// </summary>
public sealed class BlindedPaymentPathFactoryTests : IDisposable
{
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private readonly TestNodeKeyManager _us = new(0x07);
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IChannelUpdateService> _updates = new();
    private readonly Mock<IBlockchainMonitor> _chain = new();
    private readonly List<ChannelModel> _open = [];
    private readonly ServiceProvider _provider;

    public BlindedPaymentPathFactoryTests()
    {
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => _open.Where(predicate).ToList());
        _chain.SetupGet(c => c.LastProcessedBlockHeight).Returns(800);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(_us);
        services.AddBitcoinInfrastructure();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Given_APublicAndAPrivateChannel_When_CreatingPaths_Then_ThePublicPeerIntroducesThem()
    {
        // Arrange: the private channel's peer even has more to spend; only the public one may introduce us
        var publicPeer = new TestNodeKeyManager(0x0c).NodeId;
        var privatePeer = new TestNodeKeyManager(0x0e).NodeId;
        var announced = AddChannel(publicPeer, 1, new ShortChannelId(401, 2, 1), 600_000, announced: true);
        var hidden = AddChannel(privatePeer, 2, new ShortChannelId(402, 2, 1), 900_000);
        SetPeerUpdate(announced);
        SetPeerUpdate(hidden);

        // Act
        var paths = await CreateFactory().CreateAsync(s_preimage, LightningMoney.Satoshis(50_000), 7_200,
                                                      TestContext.Current.CancellationToken);

        // Assert: a third-party payer routes to the announced introduction; the private channel stays hidden
        var path = Assert.Single(paths);
        Assert.Equal(publicPeer, path.Path.FirstNodeId);
    }

    [Fact]
    public async Task Given_OnlyPrivateChannels_When_CreatingPaths_Then_APrivatePeerStillIntroducesUs()
    {
        // Arrange
        var privatePeer = new TestNodeKeyManager(0x0e).NodeId;
        var hidden = AddChannel(privatePeer, 2, new ShortChannelId(402, 2, 1), 600_000);
        SetPeerUpdate(hidden);

        // Act
        var paths = await CreateFactory().CreateAsync(s_preimage, LightningMoney.Satoshis(50_000), 7_200,
                                                      TestContext.Current.CancellationToken);

        // Assert: the fallback keeps today's behavior, so a private-only node works with its own peers
        var path = Assert.Single(paths);
        Assert.Equal(privatePeer, path.Path.FirstNodeId);
    }

    [Fact]
    public async Task Given_APublicChannelTooSmallForTheAmount_When_CreatingPaths_Then_ThePrivateOneIsUsed()
    {
        // Arrange: the public channel's peer holds only 30,000 sat (20,000 of them reserve)
        var publicPeer = new TestNodeKeyManager(0x0c).NodeId;
        var privatePeer = new TestNodeKeyManager(0x0e).NodeId;
        var announced = AddChannel(publicPeer, 1, new ShortChannelId(401, 2, 1), 30_000, announced: true);
        var hidden = AddChannel(privatePeer, 2, new ShortChannelId(402, 2, 1), 600_000);
        SetPeerUpdate(announced);
        SetPeerUpdate(hidden);

        // Act
        var paths = await CreateFactory().CreateAsync(s_preimage, LightningMoney.Satoshis(50_000), 7_200,
                                                      TestContext.Current.CancellationToken);

        // Assert: no public channel can carry the payment, so the private one introduces it
        var path = Assert.Single(paths);
        Assert.Equal(privatePeer, path.Path.FirstNodeId);
    }

    [Fact]
    public async Task Given_ThePathsOfAnInvoice_When_APayerAddsLdksLargestShadowOffset_Then_EveryHopStillAcceptsIt()
    {
        // Arrange (NL-723): LDK adds a random "shadow" CLTV offset of up to 432 blocks
        // (MAX_SHADOW_CLTV_EXPIRY_DELTA_OFFSET) to a blinded path's final CLTV; our hops' max_cltv_expiry refused it
        const uint ldkMaxShadowOffset = 3 * 144;
        var peerKeys = new TestNodeKeyManager(0x0c);
        var announced = AddChannel(peerKeys.NodeId, 1, new ShortChannelId(401, 2, 1), 600_000, announced: true);
        SetPeerUpdate(announced);
        var routeBlinding = _provider.GetRequiredService<IRouteBlindingService>();
        var finalDelta = new NodeOptions().Routing.InvoiceMinFinalCltvExpiry;

        // Act
        var path = Assert.Single(await CreateFactory().CreateAsync(s_preimage, LightningMoney.Satoshis(50_000),
                                                                   7_200, TestContext.Current.CancellationToken));

        // Assert: walk the path as its hops would (the introduction node, then our own hops) and check each hop's
        // payment_constraints against the CLTV of an HTLC that carries the largest shadow offset (BOLT 4: a hop's
        // incoming cltv_expiry is the next hop's plus its delta)
        var unblinded = new List<BlindedRecipientData>();
        var pathKey = path.Path.FirstPathKey;
        for (var i = 0; i < path.Path.Hops.Count; i++)
        {
            var key = i == 0 ? peerKeys.GetNodeKeyPair().PrivKey : _us.GetNodeKeyPair().PrivKey;
            var hop = routeBlinding.Unblind(key, pathKey, path.Path.Hops[i].EncryptedRecipientData);
            unblinded.Add(hop.RecipientData);
            pathKey = hop.NextPathKey;
        }

        var cltvExpiry = 800 + finalDelta + ldkMaxShadowOffset;
        for (var i = unblinded.Count - 1; i >= 0; i--)
        {
            if (i < unblinded.Count - 1)
                cltvExpiry += unblinded[i].PaymentRelay!.CltvExpiryDelta;
            Assert.True(cltvExpiry <= unblinded[i].PaymentConstraints!.MaxCltvExpiry,
                        $"hop {i}: cltv_expiry {cltvExpiry} above max_cltv_expiry "
                      + $"{unblinded[i].PaymentConstraints!.MaxCltvExpiry}");
        }
    }

    private BlindedPaymentPathFactory CreateFactory() =>
        new(new BlindedPathBuilder(_provider.GetRequiredService<IRouteBlindingService>(), _us, _channels.Object,
                                   _updates.Object, NullLogger<BlindedPathBuilder>.Instance),
            Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            Options.Create(new OfferOptions()), _chain.Object);

    private ChannelModel AddChannel(CompactPubKey peer, byte tag, ShortChannelId shortChannelId, ulong remoteSat,
                                    bool announced = false)
    {
        var key = TestPaths.Point(tag);
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(2_000_000),
                                     144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No)
        {
            AnnounceChannel = announced
        };
        var keySet = new ChannelKeySetModel(tag, key, key, key, key, key, key);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null, null,
                                       true, null, null, LightningMoney.Satoshis(2_000_000 - remoteSat), keySet,
                                       0, 0, LightningMoney.Satoshis(remoteSat), keySet, 0, peer, 0,
                                       ChannelState.Open, ChannelVersion.V1)
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

    private void SetPeerUpdate(ChannelModel channel)
    {
        ChannelUpdatePayload? update = new(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest,
                                           channel.ShortChannelId, 1, ChannelUpdatePayload.MessageFlagMustBeOne, 0,
                                           40, 1_000, 1_000, 1, 2_000_000_000);
        _updates.Setup(u => u.TryGetRemoteChannelUpdate(channel.ChannelId, out update)).Returns(true);
    }
}