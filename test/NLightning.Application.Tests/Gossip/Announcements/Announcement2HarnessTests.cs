using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Gossip;
using Application.Gossip.Announcements;
using Application.Gossip.Relay.Interfaces;
using Channels.Harness;
using Domain.Crypto.Interfaces;
using Domain.Enums;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Gossip;

/// <summary>
/// Taproot gossip (BOLTs PR #1059, NL-878 T7) over <see cref="TwoNodeHarness"/>: two nodes with real
/// <c>LocalLightningSigner</c>s and a public simple taproot channel run the <c>channel_announcement_2</c> MuSig2
/// session (announcement nonces in a re-sent <c>channel_ready</c> at the depth, or in <c>channel_reestablish</c> after a
/// reconnection or a restart), exchange <c>announcement_signatures_2</c> and publish the same valid announcement, then
/// their <c>channel_update_2</c> and <c>node_announcement_2</c>.
/// </summary>
public class Announcement2HarnessTests
{
    private const uint Depth5 = TwoNodeHarness.FundingHeight + 4;
    private const uint Depth6 = TwoNodeHarness.FundingHeight + 5;

    [Fact]
    public async Task Given_APublicTaprootChannel_When_BothReachTheDepth_Then_BothPublishTheSameValidAnnouncement2()
    {
        // Arrange
        using var harness = CreateHarness();

        // Act 1: five confirmations: nothing yet
        await harness.Alice.RaiseBlockAsync(Depth5);
        await harness.Bob.RaiseBlockAsync(Depth5);
        await harness.PumpAsync();
        Assert.Equal(0, Count<AnnouncementSignatures2Message>(harness.Alice.Received));
        Assert.DoesNotContain(harness.Bob.Received, IsNonceChannelReady);

        // Act 2: Alice's sixth block first (her nonces go out, Bob keeps them), then Bob's
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();
        Assert.Single(harness.Bob.Received, IsNonceChannelReady);
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements2);
        await harness.Bob.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();

        // Assert: one nonce channel_ready and one announcement_signatures_2 each way; no v1 announcement_signatures
        Assert.Single(harness.Alice.Received, IsNonceChannelReady);
        Assert.Equal(1, Count<AnnouncementSignatures2Message>(harness.Alice.Received));
        Assert.Equal(1, Count<AnnouncementSignatures2Message>(harness.Bob.Received));
        Assert.Equal(0, Count<AnnouncementSignaturesMessage>(harness.Alice.Received));
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements);

        var (atAlice, capacity) = Assert.Single(Sink(harness.Alice).ChannelAnnouncements2);
        var (atBob, _) = Assert.Single(Sink(harness.Bob).ChannelAnnouncements2);
        Assert.Equal(TwoNodeHarness.ShortChannelId, atAlice.ShortChannelId);
        Assert.Equal(atAlice.ShortChannelId, atBob.ShortChannelId);
        Assert.Equal(atAlice.GetSignedData(), atBob.GetSignedData());
        Assert.Equal((long)TwoNodeHarness.FundingSatoshis, capacity.Satoshi);
        Assert.Equal(ChainConstants.Regtest, atAlice.ChainHash);
        Assert.Equal(GossipV2ProofResult.Valid, CheckProof(harness, atAlice));
        Assert.Equal(GossipV2ProofResult.Valid, CheckProof(harness, atBob));

        // ... then each side's channel_update_2 (BIP 340, block height) and node_announcement_2
        var verifier = harness.Alice.Services.GetRequiredService<IGossipV2SignatureVerifier>();
        var update = Assert.Single(Sink(harness.Alice).ChannelUpdates2);
        Assert.Equal(TwoNodeHarness.ShortChannelId, update.ShortChannelId);
        Assert.Equal(Depth6, update.BlockHeight);
        Assert.Equal(harness.Alice.NodeId == atAlice.NodeId1 ? (byte)0 : (byte)1, update.Direction);
        Assert.True(verifier.VerifyBip340(update.GetSignatureHash(), update.Signature, harness.Alice.NodeId));
        await WaitForNodeAnnouncementAsync(harness.Alice);
        var node = Assert.Single(Sink(harness.Alice).NodeAnnouncements2.DistinctBy(n => n.BlockHeight));
        Assert.Equal(harness.Alice.NodeId, node.NodeId);
        Assert.True(verifier.VerifyBip340(node.GetSignatureHash(), node.Signature, harness.Alice.NodeId));
    }

    [Fact]
    public async Task Given_TheNoncesLostWithTheLink_When_Reconnected_Then_TheReestablishSessionAnnouncesTheChannel()
    {
        // Arrange: both at the depth; only Alice saw the block, and her nonce channel_ready is lost
        using var harness = CreateHarness();
        harness.Bob.SetTip(Depth6);
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.DisconnectAsync();
        Assert.Single(harness.Alice.Lost, IsNonceChannelReady);

        // Act: BOLTs #1059, the reestablish carries fresh nonces and retransmit bit 1 both ways
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        var reestablish = Assert.IsType<ChannelReestablishMessage>(harness.Bob.Received.First(
                                                                       m => m is ChannelReestablishMessage));
        Assert.NotNull(reestablish.AnnouncementNoncesTlv);
        Assert.Equal(MyCurrentFundingLockedFlag, reestablish.MyCurrentFundingLockedTlv!.RetransmitFlags & 0x02);
        Assert.Equal(1, Count<AnnouncementSignatures2Message>(harness.Alice.Received));
        Assert.Equal(1, Count<AnnouncementSignatures2Message>(harness.Bob.Received));
        var (atAlice, _) = Assert.Single(Sink(harness.Alice).ChannelAnnouncements2);
        Assert.Equal(GossipV2ProofResult.Valid, CheckProof(harness, atAlice));
        Assert.Single(Sink(harness.Bob).ChannelAnnouncements2);

        // Act 2: complete in both processes: a later reconnection carries nonces without bit 1 and signs nothing
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 2
        Assert.Equal(1, Count<AnnouncementSignatures2Message>(harness.Alice.Received));
        Assert.Equal(1, Count<AnnouncementSignatures2Message>(harness.Bob.Received));
        var last = harness.Bob.Received.OfType<ChannelReestablishMessage>().Last();
        Assert.Equal(0, last.MyCurrentFundingLockedTlv!.RetransmitFlags & 0x02);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannel_When_ANodeRestarts_Then_AFreshSessionAnnouncesItAgain()
    {
        // Arrange: announced
        using var harness = CreateHarness();
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.Bob.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();
        Assert.Single(Sink(harness.Alice).ChannelAnnouncements2);

        // Act: Bob restarts (nothing of the session is stored) and reconnects
        var negotiated = harness.Bob.NegotiatedFeatures;
        await harness.RestartNodeAsync(harness.Bob);
        harness.Bob.NegotiatedFeatures = negotiated;
        harness.Bob.SetTip(Depth6);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: Bob asked (bit 1), both signed a fresh session with new nonces, Bob published again
        Assert.Equal(2, Count<AnnouncementSignatures2Message>(harness.Alice.Received));
        Assert.Equal(2, Count<AnnouncementSignatures2Message>(harness.Bob.Received));
        var first = harness.Alice.Received.OfType<AnnouncementSignatures2Message>().First().Payload;
        var second = harness.Alice.Received.OfType<AnnouncementSignatures2Message>().Last().Payload;
        Assert.NotEqual(first.NodePartialSignature, second.NodePartialSignature);
        var (atBob, _) = Assert.Single(Sink(harness.Bob).ChannelAnnouncements2);
        Assert.Equal(GossipV2ProofResult.Valid, CheckProof(harness, atBob));
    }

    [Fact]
    public async Task Given_AnInvalidPartialSignature_When_Received_Then_WarningAndNothingPublished()
    {
        // Arrange: both nonce pairs exchanged (Alice's channel_ready to Bob, Bob's to Alice), no signatures delivered
        using var harness = CreateHarness();
        harness.Bob.SetTip(Depth6);
        await harness.Alice.RaiseBlockAsync(Depth6);
        harness.DeliveryBudget = harness.Delivered + 2;
        await harness.PumpAsync();
        harness.DeliveryBudget = null;

        // Act: a forged announcement_signatures_2 from Bob (zero partial signatures)
        var channel = harness.Alice.Channel;
        var forged = new AnnouncementSignatures2Message(Domain.Protocol.Payloads.AnnouncementSignatures2Payload.Create(
                                                            channel.ChannelId, channel.ShortChannelId,
                                                            new Domain.Crypto.ValueObjects.MusigPartialSignature(
                                                                new byte[32]),
                                                            new Domain.Crypto.ValueObjects.MusigPartialSignature(
                                                                new byte[32]),
                                                            channel.FundingOutput!.TransactionId!.Value));

        // Assert
        await Assert.ThrowsAsync<Domain.Exceptions.ChannelWarningException>(
            () => harness.Alice.ChannelManager.HandleChannelMessageAsync(forged, harness.Bob.NegotiatedFeatures,
                                                                         harness.Bob.NodeId));
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements2);
    }

    private const int MyCurrentFundingLockedFlag = 0x02;

    private static TwoNodeHarness CreateHarness()
    {
        var harness = new TwoNodeHarness(announceChannel: true, simpleTaproot: true,
                                         configureServices: (node, services) =>
                                         {
                                             services.AddSingleton(Options.Create(new NodeOptions
                                             {
                                                 EnableHtlcs = true,
                                                 BitcoinNetwork = BitcoinNetwork.Regtest,
                                                 Alias = node.Name,
                                                 Features = GossipV2Features()
                                             }));
                                             services.AddGossipServices();
                                             services.TryAddSingleton<IGossipV2SignatureVerifier,
                                                 GossipV2SignatureVerifier>();
                                             services.AddSingleton<IOwnGossipSink>(new RecordingOwnGossipSink());
                                             services.AddSingleton<IGossipRelayScheduler>(
                                                 new RecordingRelayScheduler());
                                             services
                                                .AddScoped<IChannelMessageHandler<AnnouncementSignaturesMessage>,
                                                     AnnouncementSignaturesMessageHandler>();
                                             services
                                                .AddScoped<IChannelMessageHandler<AnnouncementSignatures2Message>,
                                                     AnnouncementSignatures2MessageHandler>();
                                         });
        var negotiated = new FeatureOptions
        {
            AllowExperimentalFeatures = true,
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionSimpleClose = FeatureSupport.Optional,
            OptionGossipV2 = FeatureSupport.Optional
        };
        harness.Alice.NegotiatedFeatures = negotiated;
        harness.Bob.NegotiatedFeatures = negotiated;
        return harness;
    }

    private static FeatureOptions GossipV2Features() => new()
    {
        AllowExperimentalFeatures = true,
        OptionSimpleTaproot = FeatureSupport.Optional,
        OptionGossipV2 = FeatureSupport.Optional
    };

    private static GossipV2ProofResult CheckProof(TwoNodeHarness harness,
                                                  Domain.Protocol.Payloads.ChannelAnnouncement2Payload announcement)
    {
        var musig2 = harness.Alice.Services.GetRequiredService<IMusig2Service>();
        var verifier = harness.Alice.Services.GetRequiredService<IGossipV2SignatureVerifier>();
        var script = musig2.AggregateTaprootKeyPath(harness.Alice.Basepoints.FundingPubKey,
                                                    harness.Bob.Basepoints.FundingPubKey)
                           .GetTaprootScriptPubKey();
        return verifier.CheckChannelProof(announcement, script);
    }

    private static async Task WaitForNodeAnnouncementAsync(HarnessNode node)
    {
        var service = (NodeAnnouncementService)node.Services
                                                   .GetRequiredService<Application.Gossip.Announcements.Interfaces.
                                                        INodeAnnouncementService>();
        await service.LastRequest;
    }

    private static bool IsNonceChannelReady(IChannelMessage message) =>
        message is ChannelReadyMessage { AnnouncementNodeNonceTlv: not null, AnnouncementBitcoinNonceTlv: not null };

    private static int Count<T>(IEnumerable<IChannelMessage> messages) => messages.Count(m => m is T);

    private static RecordingOwnGossipSink Sink(HarnessNode node) =>
        (RecordingOwnGossipSink)node.Services.GetRequiredService<IOwnGossipSink>();
}