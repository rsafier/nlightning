using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Gossip.Interfaces;
using Application.Payments.Invoices;
using Bolt11.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// NL-245: our invoices carry a route hint per private channel, with the peer's own policy from its
/// <c>channel_update</c>.
/// </summary>
public class InvoiceRouteHintTests : IDisposable
{
    private readonly TestNodeKeyManager _us = new(0x0d);
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IChannelUpdateService> _updates = new();
    private readonly List<ChannelModel> _open = [];
    private readonly ServiceProvider _provider;

    public InvoiceRouteHintTests()
    {
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => _open.Where(predicate).ToList());

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

    private InvoiceService CreateService() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _us,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            NullLogger<InvoiceService>.Instance, _channels.Object, _updates.Object);

    private ChannelModel AddChannel(CompactPubKey peer, byte tag, ShortChannelId shortChannelId, ulong remoteSat,
                                    FeatureSupport scidAlias = FeatureSupport.No)
    {
        var key = new CompactPubKey(new NBitcoin.Key().PubKey.ToBytes());
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(2_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false, scidAlias);
        var keySet = new ChannelKeySetModel(tag, key, key, key, key, key, key);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null, null,
                                       true, null, null, LightningMoney.Satoshis(2_000_000 - remoteSat), keySet, 0, 0,
                                       LightningMoney.Satoshis(remoteSat), keySet, 0, peer, 0, ChannelState.Open,
                                       ChannelVersion.V1)
        {
            ShortChannelId = shortChannelId
        };
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