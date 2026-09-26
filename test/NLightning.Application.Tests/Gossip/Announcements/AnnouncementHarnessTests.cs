using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Gossip;
using Application.Gossip.Announcements;
using Application.Gossip.Announcements.Interfaces;
using Application.Gossip.Relay.Interfaces;
using Channels.Harness;
using Domain.Bitcoin.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// BOLT 7 plan G1-T4 over <see cref="TwoNodeHarness"/>: two nodes with real <c>LocalLightningSigner</c>s, the
/// production channel managers, <see cref="ChannelAnnouncementService"/>, <see cref="AnnouncementSignaturesMessageHandler"/>,
/// channel update and node announcement services, announcing their public channel driven by blocks and reconnections.
/// </summary>
public class AnnouncementHarnessTests
{
    private const uint Depth5 = TwoNodeHarness.FundingHeight + 4;
    private const uint Depth6 = TwoNodeHarness.FundingHeight + 5;

    [Fact]
    public async Task Given_APublicChannel_When_BothReachSixConfirmations_Then_BothAssembleTheSameAnnouncement()
    {
        // Arrange
        using var harness = CreateHarness();
        var verifier = harness.Alice.Services.GetRequiredService<IGossipSignatureVerifier>();

        // Act 1: five confirmations at both ends
        await harness.Alice.RaiseBlockAsync(Depth5);
        await harness.Bob.RaiseBlockAsync(Depth5);
        await harness.PumpAsync();

        // Assert 1: BOLT 7, nothing before the funding transaction is 6 deep
        Assert.Equal(0, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Equal(0, CountAnnouncementSignatures(harness.Bob.Received));

        // Act 2: Alice's sixth block, then Bob's
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements);
        Assert.Empty(Sink(harness.Bob).ChannelAnnouncements);
        await harness.Bob.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();

        // Assert 2: one announcement_signatures each way (no ping-pong), the same 256 bytes at both ends, 4 valid sigs
        Assert.Equal(1, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Equal(1, CountAnnouncementSignatures(harness.Bob.Received));
        var (atAlice, capacity) = Assert.Single(Sink(harness.Alice).ChannelAnnouncements);
        var (atBob, _) = Assert.Single(Sink(harness.Bob).ChannelAnnouncements);
        Assert.Equal(atAlice.GetBytes(), atBob.GetBytes());
        Assert.Equal(TwoNodeHarness.ShortChannelId, atAlice.ShortChannelId);
        Assert.Equal((long)TwoNodeHarness.FundingSatoshis, capacity.Satoshi);
        Assert.True(verifier.VerifyAll(ChannelAnnouncementBuilder.GetAllSignatureChecks(atAlice)));
        Assert.Equal(ChainConstants.Regtest, atAlice.ChainHash);
    }

    [Fact]
    public async Task Given_OurHalfLostWithTheLink_When_Reconnected_Then_BothRetransmitAndAssemble()
    {
        // Arrange: both at 6 confirmations; only Alice saw the block, and her announcement_signatures is lost
        using var harness = CreateHarness();
        harness.Bob.SetTip(Depth6);
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.DisconnectAsync();
        Assert.Equal(1, CountAnnouncementSignatures(harness.Alice.Lost));

        // Act: BOLT 7, on reconnection each side that has not received the other's sends its own
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Equal(1, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Equal(1, CountAnnouncementSignatures(harness.Bob.Received));
        var (atAlice, _) = Assert.Single(Sink(harness.Alice).ChannelAnnouncements);
        var (atBob, _) = Assert.Single(Sink(harness.Bob).ChannelAnnouncements);
        Assert.Equal(atAlice.GetBytes(), atBob.GetBytes());

        // Act 2: both halves exchanged: a later reconnection sends nothing again
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 2
        Assert.Equal(1, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Equal(1, CountAnnouncementSignatures(harness.Bob.Received));
    }

    [Fact]
    public async Task Given_ANewConnectionNotReestablishedYet_When_ABlockArrives_Then_TheReestablishGoesFirst()
    {
        // Arrange: the link is back (messages get through) but no channel_reestablish was exchanged yet
        using var harness = CreateHarness();
        harness.Bob.SetTip(Depth6);
        await harness.DisconnectAsync();
        harness.Alice.PeerAlive = true;
        harness.Bob.PeerAlive = true;

        // Act: Alice's sixth block arrives first
        await harness.Alice.RaiseBlockAsync(Depth6);
        var queuedBeforeReestablish = !harness.Alice.OutboxIsEmpty;
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: BOLT 2, channel_reestablish is the first message for the channel; ours followed it once
        Assert.False(queuedBeforeReestablish);
        Assert.IsType<ChannelReestablishMessage>(harness.Bob.Received[0]);
        Assert.Equal(1, CountAnnouncementSignatures(harness.Bob.Received));
        Assert.Equal(1, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Single(Sink(harness.Alice).ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_FiveConfirmations_When_BlocksAndAReconnection_Then_NothingIsSent()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.SetTip(Depth5);
        harness.Bob.SetTip(Depth5);

        // Act
        await harness.Alice.RaiseBlockAsync(Depth5);
        await harness.Bob.RaiseBlockAsync(Depth5);
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Equal(0, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Equal(0, CountAnnouncementSignatures(harness.Bob.Received));
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements);
        Assert.Empty(Relay(harness.Alice).Queued);
    }

    [Fact]
    public async Task Given_AShutdown_When_SixConfirmations_Then_NothingIsSent()
    {
        // Arrange (BOLT 7: MUST NOT send announcement_signatures once a shutdown was sent)
        using var harness = CreateHarness();
        var script = new BitcoinScript(Enumerable.Repeat((byte)0x51, 22).ToArray());
        harness.Alice.Channel.SetLocalShutdownScript(script);
        harness.Bob.Channel.SetRemoteShutdownScript(script);

        // Act
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.Bob.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(0, CountAnnouncementSignatures(harness.Alice.Received));
        Assert.Equal(0, CountAnnouncementSignatures(harness.Bob.Received));
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements);
        Assert.Empty(Sink(harness.Bob).ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_TheAnnouncementAssembled_When_HandedOn_Then_PublicUpdateAndNodeAnnouncementFollow()
    {
        // Arrange
        using var harness = CreateHarness();
        var verifier = harness.Alice.Services.GetRequiredService<IGossipSignatureVerifier>();

        // Act
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.Bob.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();
        await NodeAnnouncements(harness.Alice).LastRequest;

        // Assert: our public channel_update (dont_forward clear, real scid, our node's signature)
        var update = Assert.Single(Sink(harness.Alice).ChannelUpdates);
        Assert.False(update.DontForward);
        Assert.False(update.IsDisabled);
        Assert.Equal(TwoNodeHarness.ShortChannelId, update.ShortChannelId);
        Assert.True(verifier.Verify(update.GetSignatureHash(), update.Signature, harness.Alice.NodeId));

        // node_announcement: alias, color, signature
        var node = Assert.Single(Sink(harness.Alice).NodeAnnouncements);
        Assert.Equal(harness.Alice.NodeId, node.NodeId);
        Assert.Equal("Alice", node.GetAliasText());
        Assert.Equal(Convert.FromHexString("3399ff"), node.RgbColor.ToArray());
        Assert.True(verifier.Verify(node.GetSignatureHash(), node.Signature, harness.Alice.NodeId));

        // The relay got the three in the order 256, 258, 257
        var queued = Relay(harness.Alice).Queued;
        Assert.Collection(queued, p => Assert.IsType<ChannelAnnouncementPayload>(p),
                          p => Assert.IsType<ChannelUpdatePayload>(p),
                          p => Assert.IsType<NodeAnnouncementPayload>(p));
    }

    [Fact]
    public async Task Given_BothNodesRestartBetweenTheHalves_When_BobReachesTheDepth_Then_TheSavedHalvesComplete()
    {
        // Arrange (NL-355): Alice is 6 deep and sends her half, Bob (5 deep) stores it and cannot answer yet
        using var harness = CreateHarness();
        harness.Bob.SetTip(Depth5);
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();
        var aliceHalf = harness.Bob.Channel.RemoteAnnouncementSignatures;
        var aliceSentAt = harness.Alice.Channel.LocalAnnouncementSignaturesSentAt;
        Assert.NotNull(aliceHalf);
        Assert.NotNull(aliceSentAt);
        Assert.Equal(1, CountAnnouncementSignatures(harness.Bob.Received));

        // Act 1: both processes restart from what they saved
        var alice = await harness.RestartNodeAsync(harness.Alice);
        var bob = await harness.RestartNodeAsync(harness.Bob);

        // Assert 1: the fresh channel models carry the saved announcement state
        Assert.Equal(aliceHalf, bob.Channel.RemoteAnnouncementSignatures);
        Assert.Null(bob.Channel.LocalAnnouncementSignaturesSentAt);
        Assert.Equal(aliceSentAt, alice.Channel.LocalAnnouncementSignaturesSentAt);
        Assert.Null(alice.Channel.RemoteAnnouncementSignatures);

        // Act 2: reconnection (Alice lacks Bob's half, so she sends hers again), then Bob's sixth block
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        Assert.Empty(Sink(bob).ChannelAnnouncements);
        await bob.RaiseBlockAsync(Depth6);
        await harness.PumpAsync();

        // Assert 2: Bob's half answers with the stored one of Alice's, and both assemble the same announcement
        Assert.Equal(2, CountAnnouncementSignatures(bob.Received));
        Assert.Equal(1, CountAnnouncementSignatures(alice.Received));
        var (atAlice, _) = Assert.Single(Sink(alice).ChannelAnnouncements);
        var (atBob, _) = Assert.Single(Sink(bob).ChannelAnnouncements);
        Assert.Equal(atAlice.GetBytes(), atBob.GetBytes());
        Assert.Equal(TwoNodeHarness.ShortChannelId, atBob.ShortChannelId);
        Assert.Equal(harness.Bob.Channel.LocalAnnouncementSignaturesSentAt,
                     harness.Bob.Store.CommittedAnnouncement?.LocalSentAt);
    }

    private static TwoNodeHarness CreateHarness() =>
        new(announceChannel: true, configureServices: (node, services) =>
        {
            services.AddSingleton(Options.Create(new NodeOptions
            {
                EnableHtlcs = true,
                BitcoinNetwork = BitcoinNetwork.Regtest,
                Alias = node.Name
            }));
            services.AddGossipServices();
            services.AddSingleton<IOwnGossipSink>(new RecordingOwnGossipSink());
            services.AddSingleton<IGossipRelayScheduler>(new RecordingRelayScheduler());
            services
               .AddScoped<IChannelMessageHandler<AnnouncementSignaturesMessage>, AnnouncementSignaturesMessageHandler>();
        });

    private static RecordingOwnGossipSink Sink(HarnessNode node) =>
        (RecordingOwnGossipSink)node.Services.GetRequiredService<IOwnGossipSink>();

    private static RecordingRelayScheduler Relay(HarnessNode node) =>
        (RecordingRelayScheduler)node.Services.GetRequiredService<IGossipRelayScheduler>();

    private static NodeAnnouncementService NodeAnnouncements(HarnessNode node) =>
        (NodeAnnouncementService)node.Services.GetRequiredService<INodeAnnouncementService>();

    private static int CountAnnouncementSignatures(IEnumerable<IChannelMessage> messages) =>
        messages.Count(m => m is AnnouncementSignaturesMessage);
}