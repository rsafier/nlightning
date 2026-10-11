using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers.Receive;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Payments;

/// <summary>
/// Plan B3-T1: <c>createoffer</c> (BOLT 12 "Offers" writer, B12-OFR-01/02: chains off mainnet, metadata, amount only
/// with a description, issuer id = our node id, <c>offer_paths</c> introduced by a peer when we have no announced
/// channel or they are forced; stored before it is returned), <c>listoffers</c> and <c>disableoffer</c>.
/// </summary>
public sealed class OfferServiceTests : IDisposable
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly OfferTestStore _store = new();
    private readonly TestNodeKeyManager _keyManager = new(0x07);
    private readonly RecordingOnionMessageService _onionMessages = new();
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IPeerOnionMessageOutbox> _outbox = new();
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly List<ChannelModel> _open = [];
    private readonly NodeOptions _nodeOptions = new() { BitcoinNetwork = BitcoinNetwork.Regtest };
    private readonly ManualClock _clock = new(s_now);
    private readonly ServiceProvider _services;
    private readonly CompactPubKey _peer = TestPeerKey.Public;

    public OfferServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(_keyManager);
        services.AddSingleton<IOnionMessageService>(_onionMessages);
        services.AddSingleton(_outbox.Object);
        services.AddBitcoinInfrastructure();
        // After AddBitcoinInfrastructure, which registers the real signer (lane B12-B) over the node's ILightningSigner
        services.AddSingleton<IBolt12Signer>(new TestBolt12Signer(Enumerable.Repeat((byte)0x07, 32).ToArray()));
        _services = services.BuildServiceProvider();

        _peerManager.Setup(p => p.ListPeers()).Returns([new PeerModel(_peer, "127.0.0.1", 9735, "IPv4")]);
        _outbox.Setup(o => o.CanSendOnionMessage(_peer)).Returns(true);
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => _open.Where(predicate).ToList());
    }

    public void Dispose()
    {
        _services.Dispose();
        _store.Dispose();
    }

    private OfferService CreateService() =>
        new(_store.ScopeFactory, _keyManager, Options.Create(_nodeOptions), new OfferPathIds(_keyManager),
            _services.GetRequiredService<IBlindedMessagePathBuilder>(), _peerManager.Object, _channels.Object, _services,
            NullLogger<OfferService>.Instance, Options.Create(new OfferOptions()), null, _clock);

    /// <summary>
    /// An open channel with <paramref name="peer"/> (the onion-message peer): enough for
    /// <see cref="OfferService.SelectIntroductionNodes"/> and
    /// <see cref="Application.OnionMessages.OnionMessagePathFinder.HasOpenChannelWith"/>; announced makes
    /// <see cref="Application.Gossip.Announcements.ChannelAnnouncementService.IsAnnounced"/> true.
    /// </summary>
    private void AddOpenChannel(CompactPubKey peer, bool announced = false)
    {
        var key = TestPaths.Point(0x10);
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(2_000_000),
                                     144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No)
        {
            AnnounceChannel = announced
        };
        var keySet = new ChannelKeySetModel(1, key, key, key, key, key, key);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x0a, 32).ToArray()),
                                       null, null, true, null, null, LightningMoney.Satoshis(1_000_000), keySet,
                                       0, 0, LightningMoney.Satoshis(1_000_000), keySet, 0, peer, 0,
                                       ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = new ShortChannelId(800, 1, 1)
        };
        if (announced)
        {
            channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(new byte[64], new byte[64]));
            channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UnixEpoch);
        }

        _open.Add(channel);
    }

    [Fact]
    public async Task Given_APrivateNodeWithAPeer_When_CreatingAnOffer_Then_ItHasPathsTheIssuerIdAndIsStored()
    {
        // Arrange
        var service = CreateService();
        var savesBefore = _store.Saves;

        // Act
        var offer = (await service.CreateOfferAsync(
                        new CreateOfferRequest(LightningMoney.Satoshis(10_000), "nltg coffee", "nltg", 0,
                                               s_now.AddDays(1)),
                        TestContext.Current.CancellationToken)).Offer;

        // Assert
        Assert.Equal(savesBefore + 1, _store.Saves);
        Assert.Same(offer, Assert.Single(_store.Offers.Offers));
        Assert.Equal(new Hash(SHA256.HashData(offer.OfferBytes.Span)), offer.OfferId);
        Assert.StartsWith("lno1", offer.Bolt12);
        Assert.Equal(offer.OfferBytes.ToArray(), Bolt12TestStrings.Decode(offer.Bolt12, out _));
        Assert.True(offer.HasPaths);
        Assert.Equal(OfferIssuerKind.NodeId, offer.IssuerKind);
        Assert.Equal(OfferStatus.Active, offer.Status);
        Assert.Equal(Bolt12Constants.OurOfferMetadataLength, offer.Metadata.Length);

        Assert.True(Bolt12TlvStream.TryParse(offer.OfferBytes, out var stream));
        var view = new Bolt12TlvStreamView(stream!);
        Assert.All(stream!.Records, r => Assert.True(InvoiceRequestReader.IsOfferType(r.Type)));
        Assert.Equal((byte[])ChainConstants.Regtest, view.Get(Bolt12TlvTypes.OfferChains));
        Assert.Equal(offer.Metadata.ToArray(), view.Get(Bolt12TlvTypes.OfferMetadata));
        Assert.Equal(10_000_000UL, TruncatedInt.DecodeTu64(view.Get(Bolt12TlvTypes.OfferAmount)));
        Assert.Equal("nltg coffee", Encoding.UTF8.GetString(view.Get(Bolt12TlvTypes.OfferDescription)));
        Assert.Equal((ulong)s_now.AddDays(1).ToUnixTimeSeconds(),
                     TruncatedInt.DecodeTu64(view.Get(Bolt12TlvTypes.OfferAbsoluteExpiry)));
        Assert.Equal("nltg", Encoding.UTF8.GetString(view.Get(Bolt12TlvTypes.OfferIssuer)));
        Assert.Empty(view.Get(Bolt12TlvTypes.OfferQuantityMax));
        Assert.Equal((byte[])_keyManager.NodeId, view.Get(Bolt12TlvTypes.OfferIssuerId));
        Assert.False(view.Has(Bolt12TlvTypes.OfferCurrency));
        Assert.True(BlindedPathCodec.TryReadList(view.Get(Bolt12TlvTypes.OfferPaths), out var paths, out _));
        var path = Assert.Single(paths);
        Assert.Equal(_peer, path.FirstNode.NodeId);
        // The introduction peer, our hop, and our default dummy hop ending the path (NL-525)
        Assert.Equal(3, path.Hops.Count);
    }

    [Fact]
    public async Task Given_NoOnionMessagePeerWithAChannel_When_CreatingAnOffer_Then_TheOfferCarriesTheWarning()
    {
        // Arrange: the only onion-message peer has no channel with us, so it introduces the paths and we never
        // reconnect to it (NL-452)
        var service = CreateService();

        // Act
        var created = await service.CreateOfferAsync(new CreateOfferRequest(null, "fragile"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.True(created.Offer.HasPaths);
        Assert.Contains("No connected onion-message peer has an open channel with us", created.Warning);
        Assert.Contains("1 peer(s) without one", created.Warning);
        Assert.Contains("we do not reconnect to peers without channels", created.Warning);
    }

    [Fact]
    public async Task Given_AnOnionMessagePeerWithAnOpenChannel_When_CreatingAnOffer_Then_NoWarning()
    {
        // Arrange: a private channel with the peer is enough for it to introduce the paths
        AddOpenChannel(_peer);
        var service = CreateService();

        // Act
        var created = await service.CreateOfferAsync(new CreateOfferRequest(null, "fine"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.True(created.Offer.HasPaths);
        Assert.Null(created.Warning);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannel_When_CreatingAnOffer_Then_NoPathsAndNoWarning()
    {
        // Arrange: payers find us through the graph, so the offer needs no paths
        AddOpenChannel(_peer, announced: true);
        var service = CreateService();

        // Act
        var created = await service.CreateOfferAsync(new CreateOfferRequest(null, "public"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.False(created.Offer.HasPaths);
        Assert.Null(created.Warning);
    }

    [Fact]
    public async Task Given_OurOfferPath_When_OurHopIsUnblinded_Then_ItCarriesTheOffersPathId()
    {
        // Arrange
        var service = CreateService();
        var routeBlinding = _services.GetRequiredService<IRouteBlindingService>();
        var offer = (await service.CreateOfferAsync(new CreateOfferRequest(null, null),
                                                    TestContext.Current.CancellationToken)).Offer;
        Assert.True(Bolt12TlvStream.TryParse(offer.OfferBytes, out var stream));
        Assert.True(stream!.TryGetValue(Bolt12TlvTypes.OfferPaths, out var pathBytes));
        Assert.True(BlindedPathCodec.TryReadList(pathBytes.Span, out var paths, out _));
        var path = paths[0];
        // The introduction node (the peer) unblinds its hop, which names us and gives our path_key
        var pathKeyAtUs = routeBlinding.Unblind(TestPeerKey.Private, path.FirstPathKey,
                                                path.Hops[0].EncryptedRecipientData).NextPathKey;

        // Act: we unblind our real hop (it relays to our dummy) and then the dummy, which carries the offer's
        // path_id
        var ourKey = new PrivKey(Enumerable.Repeat((byte)0x07, 32).ToArray());
        var atDummy = routeBlinding.Unblind(ourKey, pathKeyAtUs, path.Hops[1].EncryptedRecipientData).NextPathKey;
        var ours = routeBlinding.Unblind(ourKey, atDummy, path.Hops[2].EncryptedRecipientData);

        // Assert
        Assert.Equal(new OfferPathIds(_keyManager).Compute(offer.Metadata.Span), ours.RecipientData.PathId!.Value
                                                                                     .ToArray());
    }

    [Fact]
    public async Task Given_NoPeerAndNoAnnouncedChannel_When_CreatingAnOffer_Then_RefusedAndNothingStored()
    {
        // Arrange
        _peerManager.Setup(p => p.ListPeers()).Returns([]);
        var service = CreateService();

        // Act
        var act = () => service.CreateOfferAsync(new CreateOfferRequest(null, "any"),
                                                 TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Empty(_store.Offers.Offers);
    }

    [Theory]
    [InlineData("zero amount")]
    [InlineData("amount without description")]
    [InlineData("expiry in the past")]
    public async Task Given_ARequestBreakingAWriterRule_When_CreatingAnOffer_Then_ArgumentException(string variant)
    {
        // Arrange
        var request = variant switch
        {
            "zero amount" => new CreateOfferRequest(LightningMoney.Zero, "x"),
            "amount without description" => new CreateOfferRequest(LightningMoney.Satoshis(1), null),
            _ => new CreateOfferRequest(null, "x", AbsoluteExpiry: s_now)
        };

        // Act
        var act = () => CreateService().CreateOfferAsync(request, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(act);
        Assert.Empty(_store.Offers.Offers);
    }

    [Theory]
    [InlineData("onion messages off")]
    [InlineData("route blinding off")]
    public async Task Given_OffersUnavailable_When_CreatingAnOffer_Then_InvalidOperation(string variant)
    {
        // Arrange
        if (variant == "onion messages off")
            _onionMessages.IsAvailable = false;
        else
            _nodeOptions.Features.OptionRouteBlinding = FeatureSupport.No;
        var service = CreateService();

        // Act
        var act = () => service.CreateOfferAsync(new CreateOfferRequest(null, "x"),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.False(service.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task Given_MainnetAndNoAmount_When_CreatingAnOffer_Then_NoChainsNoAmount()
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Mainnet;

        // Act
        var offer = (await CreateService().CreateOfferAsync(new CreateOfferRequest(null, null),
                                                            TestContext.Current.CancellationToken)).Offer;

        // Assert
        Assert.True(Bolt12TlvStream.TryParse(offer.OfferBytes, out var stream));
        var view = new Bolt12TlvStreamView(stream!);
        Assert.False(view.Has(Bolt12TlvTypes.OfferChains));
        Assert.False(view.Has(Bolt12TlvTypes.OfferAmount));
        Assert.False(view.Has(Bolt12TlvTypes.OfferDescription));
        Assert.False(view.Has(Bolt12TlvTypes.OfferQuantityMax));
        Assert.Null(offer.Amount);
    }

    [Fact]
    public async Task Given_AnActiveOffer_When_Disabled_Then_StoredDisabledAndASecondCallChangesNothing()
    {
        // Arrange
        var service = CreateService();
        var offer = (await service.CreateOfferAsync(new CreateOfferRequest(null, "x"),
                                                    TestContext.Current.CancellationToken)).Offer;
        var saves = _store.Saves;

        // Act
        var disabled = await service.DisableOfferAsync(offer.OfferId, TestContext.Current.CancellationToken);
        var again = await service.DisableOfferAsync(offer.OfferId, TestContext.Current.CancellationToken);
        var unknown = await service.DisableOfferAsync(new Hash(new byte[32]), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OfferStatus.Disabled, disabled!.Status);
        Assert.Equal(s_now, disabled.DisabledAt);
        Assert.Equal(saves + 1, _store.Saves);
        Assert.Same(disabled, again);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task Given_ActiveDisabledAndExpiredOffers_When_ListingActiveOnly_Then_OnlyTheActiveOne()
    {
        // Arrange
        var service = CreateService();
        var active = (await service.CreateOfferAsync(new CreateOfferRequest(null, "a"),
                                                     TestContext.Current.CancellationToken)).Offer;
        var disabled = (await service.CreateOfferAsync(new CreateOfferRequest(null, "b"),
                                                       TestContext.Current.CancellationToken)).Offer;
        await service.CreateOfferAsync(new CreateOfferRequest(null, "c", AbsoluteExpiry: s_now.AddMinutes(1)),
                                       TestContext.Current.CancellationToken);
        await service.DisableOfferAsync(disabled.OfferId, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(2));

        // Act
        var activeOnly = await service.ListOffersAsync(true, 0, 10, TestContext.Current.CancellationToken);
        var all = await service.ListOffersAsync(false, 0, 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(active.OfferId, Assert.Single(activeOnly).OfferId);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Given_PeersWithAndWithoutChannels_When_SelectingIntroductionNodes_Then_OnlyChannelPeersAreUsed()
    {
        // Arrange: offer_paths are fixed in the offer, and we never reconnect to a peer without a channel
        var noChannelA = TestPaths.Point(0x11);
        var channelPeerA = TestPaths.Point(0x12);
        var noChannelB = TestPaths.Point(0x13);
        var channelPeerB = TestPaths.Point(0x14);
        var channelPeerC = TestPaths.Point(0x15);
        CompactPubKey[] peers = [noChannelA, channelPeerA, noChannelB, channelPeerB, channelPeerC];
        var withChannel = new HashSet<CompactPubKey> { channelPeerA, channelPeerB, channelPeerC };

        // Act
        var selected = OfferService.SelectIntroductionNodes(peers, withChannel.Contains, 2, out var withoutChannel);

        // Assert
        Assert.Equal([channelPeerA, channelPeerB], selected);
        Assert.False(withoutChannel);
    }

    [Fact]
    public void Given_OneChannelPeer_When_SelectingIntroductionNodes_Then_NoPeerWithoutAChannelFillsTheOtherPath()
    {
        // Arrange
        var noChannel = TestPaths.Point(0x11);
        var channelPeer = TestPaths.Point(0x12);

        // Act
        var selected = OfferService.SelectIntroductionNodes([noChannel, channelPeer], p => p == channelPeer, 2,
                                                            out var withoutChannel);

        // Assert
        Assert.Equal([channelPeer], selected);
        Assert.False(withoutChannel);
    }

    [Fact]
    public void Given_NoChannelPeer_When_SelectingIntroductionNodes_Then_TheOthersAreTheFallback()
    {
        // Arrange
        CompactPubKey[] peers = [TestPaths.Point(0x11), TestPaths.Point(0x12), TestPaths.Point(0x13)];

        // Act
        var selected = OfferService.SelectIntroductionNodes(peers, _ => false, 2, out var withoutChannel);

        // Assert
        Assert.Equal(peers.Take(2), selected);
        Assert.True(withoutChannel);
    }
}

/// <summary>
/// The key of the test peer that introduces our offer paths (a real point: the path creation does ECDH with it).
/// </summary>
internal static class TestPeerKey
{
    public static readonly byte[] PrivateBytes = Enumerable.Repeat((byte)0x66, 32).ToArray();

    public static PrivKey Private => new(PrivateBytes);

    public static CompactPubKey Public => Bip340.PublicKey(PrivateBytes);
}