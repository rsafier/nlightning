using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Gossip;
using Domain.Enums;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Gossip.Announcements;
using Harness;

/// <summary>
/// Splicing plan SP2-A-T2 on <see cref="TwoNodeHarness"/> without a splice: <c>my_current_funding_locked</c> is sent
/// only with <c>option_splice</c> (SP-RE-02), names the funding once <c>channel_ready</c> went out, and its bit 0
/// follows the <c>announcement_signatures</c> we hold of the peer; a peer's bit 0 gets ours back when we are ready
/// (SP-RE-04), once per connection.
/// </summary>
public class ReestablishFundingLockedTests
{
    private const uint Depth6 = TwoNodeHarness.FundingHeight + 5;

    [Fact]
    public async Task Given_NoOptionSplice_When_Reconnected_Then_NoFundingTlvIsSent()
    {
        // Arrange
        // option_splice is on by default since D13: pin it off (the handler reads the negotiated features, the
        // reestablish we send reads our own options in a harness without a peer manager)
        using var harness = new TwoNodeHarness(configureServices: (_, services) => services.AddSingleton(
                                                   Options.Create(new NodeOptions
                                                   {
                                                       EnableHtlcs = true,
                                                       Features = { OptionSplice = FeatureSupport.No }
                                                   })));
        foreach (var node in new[] { harness.Alice, harness.Bob })
            node.NegotiatedFeatures = new FeatureOptions { OptionSplice = FeatureSupport.No };

        // Act
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var reestablish = Assert.IsType<ChannelReestablishMessage>(node.Received[0]);
            Assert.Null(reestablish.NextFundingTlv);
            Assert.Null(reestablish.MyCurrentFundingLockedTlv);
        }
    }

    [Fact]
    public async Task Given_OptionSpliceOnAPrivateChannel_When_Reconnected_Then_FundingLockedNamesTheFundingWithoutBit0()
    {
        // Arrange
        using var harness = CreateHarness(announceChannel: false);
        var fundingTxId = harness.Alice.Channel.FundingOutput!.TransactionId!.Value;

        // Act
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var locked = Assert.IsType<ChannelReestablishMessage>(node.Received[0]).MyCurrentFundingLockedTlv;
            Assert.NotNull(locked);
            Assert.Equal(fundingTxId, locked.FundingTxId);
            Assert.Equal(0, locked.RetransmitFlags);
        }

        // channel_ready is still retransmitted: the funding locked names no splice (SP-RE-05)
        Assert.Equal(harness.Alice.State.LocalCommit.Number == 0,
                     harness.Bob.Received.Any(m => m is ChannelReadyMessage));
    }

    [Fact]
    public async Task Given_APublicChannelWithoutThePeersHalf_When_Reconnected_Then_Bit0AsksAndEachSideRetransmitsOnce()
    {
        // Arrange: at 6 confirmations Alice's announcement_signatures is lost with the link, Bob never sent his
        using var harness = CreateHarness(announceChannel: true);
        harness.Bob.SetTip(Depth6);
        await harness.Alice.RaiseBlockAsync(Depth6);
        await harness.DisconnectAsync();
        Assert.Contains(harness.Alice.Lost, m => m is AnnouncementSignaturesMessage);

        // Act
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: both ask (SP-RE-02 bit 0), both answer once (SP-RE-04), both assemble the announcement
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var locked = Assert.IsType<ChannelReestablishMessage>(node.Received[0]).MyCurrentFundingLockedTlv;
            Assert.Equal(1, locked!.RetransmitFlags);
            Assert.Single(node.Received, m => m is AnnouncementSignaturesMessage);
            Assert.Single(Sink(node).ChannelAnnouncements);
        }

        // Act 2: both halves are in; a new connection asks for nothing and sends nothing again
        var aliceMark = harness.Alice.Received.Count;
        var bobMark = harness.Bob.Received.Count;
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 2
        foreach (var (node, mark) in new[] { (harness.Alice, aliceMark), (harness.Bob, bobMark) })
        {
            var received = node.Received.Skip(mark).ToList();
            var locked = Assert.IsType<ChannelReestablishMessage>(received[0]).MyCurrentFundingLockedTlv;
            Assert.Equal(0, locked!.RetransmitFlags);
            Assert.DoesNotContain(received, m => m is AnnouncementSignaturesMessage);
        }
    }

    private static FeatureOptions SpliceFeatures() => new()
    {
        AllowExperimentalFeatures = true,
        OptionQuiesce = FeatureSupport.Optional,
        OptionSplice = FeatureSupport.Optional
    };

    private static TwoNodeHarness CreateHarness(bool announceChannel)
    {
        var harness = new TwoNodeHarness(announceChannel: announceChannel, configureServices: (node, services) =>
        {
            var options = new NodeOptions
            {
                EnableHtlcs = true,
                BitcoinNetwork = BitcoinNetwork.Regtest,
                Alias = node.Name,
                Features = SpliceFeatures()
            };
            services.AddSingleton(Options.Create(options));
            if (!announceChannel)
                return;

            services.AddGossipServices();
            services.AddSingleton<IOwnGossipSink>(new RecordingOwnGossipSink());
            services.AddSingleton<Application.Gossip.Relay.Interfaces.IGossipRelayScheduler>(
                new RecordingRelayScheduler());
            services
               .AddScoped<IChannelMessageHandler<AnnouncementSignaturesMessage>, AnnouncementSignaturesMessageHandler>();
        });
        harness.Alice.NegotiatedFeatures = SpliceFeatures();
        harness.Bob.NegotiatedFeatures = SpliceFeatures();
        return harness;
    }

    private static RecordingOwnGossipSink Sink(HarnessNode node) =>
        (RecordingOwnGossipSink)node.Services.GetRequiredService<IOwnGossipSink>();
}