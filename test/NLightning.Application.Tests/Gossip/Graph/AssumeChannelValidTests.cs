using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <c>Gossip:AssumeChannelValid</c> (mainnet gossip probe): channel announcements accepted on their signatures alone,
/// never looked up on chain, stored <see cref="GraphChannelVerification.Assumed"/> without capacity, and the startup
/// guard that refuses it on mainnet together with HTLCs or public channels.
/// </summary>
public class AssumeChannelValidTests
{
    private static readonly ShortChannelId s_scid = new(110, 1, 0);
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_aliceFunding = new(11);
    private static readonly TestGossipKey s_bobFunding = new(12);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Given_AssumeChannelValid_When_AValidAnnouncementArrives_Then_StoredAssumedWithoutAChainLookup()
    {
        // Arrange
        var kit = new GraphTestKit(configure: o => o.AssumeChannelValid = true);
        var message = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding);

        // Act: NL-406, the announcement enters the graph with its first update
        var result = await kit.AnnounceAsync(GraphTestKit.CreatePeer().Object, message,
                                             cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphChannelVerification.Assumed, channel.Verification);
        Assert.Null(channel.CapacitySat);
        Assert.False(channel.IsChainChecked);
        Assert.True(channel.RawAnnouncement.Span.SequenceEqual(message.Payload.GetBytes()));
        Assert.False(kit.Store.TryGetFundingTxId(s_scid, out _));
        kit.FundingLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AssumeChannelValid_When_ASignatureIsInvalid_Then_StillWarnedAndClosedWithoutALookup()
    {
        // Arrange: bob's node signature replaced by alice's
        var kit = new GraphTestKit(configure: o => o.AssumeChannelValid = true);
        var peer = GraphTestKit.CreatePeer();
        var valid = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding)
                                .Payload;
        var forged = new Domain.Protocol.Messages.ChannelAnnouncementMessage(
            valid.WithSignatures(valid.NodeSignature1, valid.NodeSignature1, valid.BitcoinSignature1,
                                 valid.BitcoinSignature2));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, forged, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
        kit.FundingLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_TheDefault_When_AValidAnnouncementArrives_Then_TheFundingOutputIsLookedUpAndGivesTheCapacity()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.FundingFound(amountSat: 2_500_000);
        var message = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding);

        // Act: NL-406, the announcement enters the graph (and is looked up) with its first update
        var result = await kit.AnnounceAsync(GraphTestKit.CreatePeer().Object, message,
                                             cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphChannelVerification.Verified, channel.Verification);
        Assert.Equal(2_500_000UL, channel.CapacitySat);
        Assert.True(channel.IsChainChecked);
        kit.FundingLookup.Verify(l => l.VerifyAsync(s_scid, It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                    It.IsAny<LightningMoney?>(), It.IsAny<CancellationToken>()),
                                 Times.Once);
    }

    [Fact]
    public async Task Given_AnAssumedChannelWithUpdates_When_Read_Then_TheCapacityEstimateIsTheLargerHtlcMaximum()
    {
        // Arrange
        var kit = new GraphTestKit(configure: o => o.AssumeChannelValid = true);
        var peer = GraphTestKit.CreatePeer().Object;
        var ct = TestContext.Current.CancellationToken;
        await kit.Ingress.ProcessAsync(peer, GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob,
                                                                                    s_aliceFunding, s_bobFunding),
                                       0, ct);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _)); // NL-406: pending until its first update

        // Act: SignedChannelUpdate sets htlc_maximum_msat to 500,000,000
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob),
                                                      s_now - 60);
        var result = await kit.Ingress.ProcessAsync(peer, update, 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Null(channel.CapacityMsat);
        Assert.Equal(500_000_000UL, channel.EstimatedCapacityMsat);
    }

    [Fact]
    public async Task Given_AnAssumedChannel_When_TheGraphIsReloaded_Then_ItStaysAssumedWithoutCapacity()
    {
        // Arrange
        var kit = new GraphTestKit(configure: o => o.AssumeChannelValid = true);
        var ct = TestContext.Current.CancellationToken;
        await kit.AnnounceAsync(GraphTestKit.CreatePeer().Object,
                                GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                       s_bobFunding), cancellationToken: ct);
        await kit.Store.FlushAsync(ct);

        // Act
        var reloaded = new GraphTestKit(kit.Repository);
        await reloaded.Store.LoadAsync(ct);

        // Assert
        Assert.True(reloaded.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphChannelVerification.Assumed, channel.Verification);
        Assert.Null(channel.CapacitySat);
    }

    [Fact]
    public async Task Given_AssumedChannelsWithoutFundingTxid_When_ThePrunerResolvesTxids_Then_NothingIsLookedUp()
    {
        // Arrange: a restart with an assumed channel (no funding txid is ever known for it)
        var kit = new GraphTestKit(configure: o => o.AssumeChannelValid = true);
        var ct = TestContext.Current.CancellationToken;
        await kit.AnnounceAsync(GraphTestKit.CreatePeer().Object,
                                GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                       s_bobFunding), cancellationToken: ct);
        Assert.Contains(s_scid, kit.Store.GetChannelsWithoutFundingTxId());
        var pruner = new GraphPruner(kit.Store, new Mock<IBlockchainMonitor>().Object, kit.FundingLookup.Object,
                                     Microsoft.Extensions.Options.Options.Create(kit.Options),
                                     Microsoft.Extensions.Options.Options.Create(
                                         new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve("regtest") }),
                                     NullLogger<GraphPruner>.Instance, kit.Clock);

        // Act
        var transient = await pruner.ResolveFundingTxIdsAsync(300, ct);
        pruner.ApplyDisconnect(100);
        await pruner.RecheckReorgedFundingAsync(301, ct);

        // Assert: the stale rule alone removes it (no lookup, no spend detection)
        Assert.Equal(0, transient);
        kit.FundingLookup.VerifyNoOtherCalls();
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Null(channel.SpentAtHeight);
    }

    [Theory]
    [InlineData("mainnet", true, true, false, 0)] // off: never an error
    [InlineData("regtest", true, true, true, 0)] // not mainnet
    [InlineData("signet", true, true, true, 0)]
    [InlineData("mainnet", false, false, true, 0)] // the probe's settings
    [InlineData("mainnet", true, false, true, 1)] // HTLCs on (the default)
    [InlineData("mainnet", false, true, true, 1)] // public channels allowed
    [InlineData("mainnet", true, true, true, 2)]
    public void Given_TheNetworkAndNodeSettings_When_AssumeChannelValidIsChecked_Then_MainnetRefusesHtlcsAndPublicChannels(
        string network, bool htlcsEnabled, bool publicChannels, bool assume, int expectedErrors)
    {
        // Arrange
        var options = new GossipGraphOptions { AssumeChannelValid = assume };

        // Act
        var errors = options.GetAssumeChannelValidErrors(BitcoinNetwork.Resolve(network), htlcsEnabled,
                                                         publicChannels);

        // Assert
        Assert.Equal(expectedErrors, errors.Count);
        Assert.All(errors, e => Assert.Contains("AssumeChannelValid", e));
    }
}