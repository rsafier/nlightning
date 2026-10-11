using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Splicing;
using Application.Gossip;
using Application.Gossip.Announcements;
using Application.Gossip.Relay.Interfaces;
using Channels.Harness;
using Channels.Splicing;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Splicing plan SP2-B (Proof of SP-G-01, D12, NL-478) over <see cref="SpliceHarness"/> on the real engine, the real
/// <c>LocalLightningSigner</c>s (rotated funding keys) and the production announcement services: a public channel is
/// announced, spliced, locked (<c>splice_locked</c> both ways) and announced again at the splice's 6th confirmation
/// with the splice's short channel id and the current funding keys, while its old short channel id keeps resolving.
/// </summary>
public class SpliceAnnouncementHarnessTests
{
    private const long SpliceInSatoshis = 100_000;
    private const uint SpliceHeight = TwoNodeHarness.BlockHeight + 3;

    [Fact]
    public async Task Given_AnAnnouncedChannel_When_SplicedAndSixDeep_Then_ReannouncedWithTheNewScidAndFundingKeys()
    {
        // Arrange: the channel is public and announced on its original funding
        using var harness = CreateHarness();
        await RaiseBlockAsync(harness, TwoNodeHarness.BlockHeight);
        var (original, _) = Assert.Single(Sink(harness.Alice).ChannelAnnouncements);
        Assert.Equal(TwoNodeHarness.ShortChannelId, original.ShortChannelId);
        Assert.Single(Sink(harness.Bob).ChannelAnnouncements);

        // Act 1: Alice splices in; the splice confirms and reaches its depth on both sides
        harness.Alice.Fund(SpliceInSatoshis + 200_000);
        var result = await harness.SpliceAsync(harness.Alice, SpliceInSatoshis);
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var spliceTxId = result.SpliceTxId!.Value;
        await harness.ConfirmAsync(spliceTxId, SpliceHeight, harness.Alice, harness.Bob);

        // Assert 1: locked with the splice's short channel id; the old one resolves for 72 blocks (D12); the halves of
        // the replaced funding are forgotten and nothing is announced before the splice is 6 deep (SP-G-01)
        var spliceScid = new ShortChannelId(SpliceHeight, 1, harness.Alice.Node.Channel.FundingOutput!.Index!.Value);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var channel = node.Node.Channel;
            Assert.Equal(spliceTxId, channel.FundingOutput!.TransactionId);
            Assert.Equal(spliceScid, channel.ShortChannelId);
            Assert.Null(channel.RemoteAnnouncementSignatures);
            Assert.Null(channel.LocalAnnouncementSignaturesSentAt);
            var retired = node.Node.Services.GetRequiredService<IRetiredScidMap>();
            Assert.True(retired.TryResolve(TwoNodeHarness.ShortChannelId, out var channelId));
            Assert.Equal(TwoNodeHarness.ChannelId, channelId);
            var entry = Assert.Single(retired.GetByChannel(TwoNodeHarness.ChannelId));
            Assert.Equal(SpliceHeight + 72, entry.ExpiresAtHeight);
            Assert.Single(Sink(node).ChannelAnnouncements);

            // After a restart the map is empty until the host's LoadAsync: the rows the real lock saved rebuild the
            // same entry (D12 across restarts)
            using var reloaded = await LoadFromSavedRowsAsync(node, SpliceHeight);
            Assert.Equal(entry, Assert.Single(reloaded.GetByChannel(TwoNodeHarness.ChannelId)));
            Assert.True(reloaded.TryResolve(TwoNodeHarness.ShortChannelId, out _));
        }

        var signaturesBefore = CountAnnouncementSignatures(harness);

        // Act 2: five confirmations of the splice at both ends
        await RaiseBlockAsync(harness, SpliceHeight + 4);

        // Assert 2: not yet
        Assert.Equal(signaturesBefore, CountAnnouncementSignatures(harness));

        // Act 3: the sixth
        await RaiseBlockAsync(harness, SpliceHeight + 5);

        // Assert 3: one announcement_signatures each way for the splice's short channel id, and the same new
        // channel_announcement at both ends: the splice's scid, the current (rotated) funding keys, the new capacity,
        // four valid signatures (NL-478)
        var sent = harness.Transcript.Select(t => t.Message).OfType<AnnouncementSignaturesMessage>()
                          .Where(m => m.Payload.ShortChannelId == spliceScid).ToList();
        Assert.Equal(2, sent.Count);
        var (atAlice, capacity) = Sink(harness.Alice).ChannelAnnouncements[^1];
        var (atBob, _) = Sink(harness.Bob).ChannelAnnouncements[^1];
        Assert.Equal(atAlice.GetBytes(), atBob.GetBytes());
        Assert.Equal(spliceScid, atAlice.ShortChannelId);
        Assert.Equal((long)(TwoNodeHarness.FundingSatoshis + SpliceInSatoshis), capacity.Satoshi);

        var alice = harness.Alice.Node;
        var aliceIsNode1 = ChannelAnnouncementBuilder.IsNode1(alice.NodeId, harness.Bob.Node.NodeId);
        var aliceKey = aliceIsNode1 ? atAlice.BitcoinKey1 : atAlice.BitcoinKey2;
        var bobKey = aliceIsNode1 ? atAlice.BitcoinKey2 : atAlice.BitcoinKey1;
        Assert.Equal(alice.Channel.LocalFundingPubKey, aliceKey);
        Assert.Equal(harness.Bob.Node.Channel.LocalFundingPubKey, bobKey);
        Assert.NotEqual(alice.Basepoints.FundingPubKey, aliceKey);
        Assert.NotEqual(original.GetBytes(), atAlice.GetBytes());
        var verifier = alice.Services.GetRequiredService<IGossipSignatureVerifier>();
        Assert.True(verifier.VerifyAll(ChannelAnnouncementBuilder.GetAllSignatureChecks(atAlice)));
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task Given_APrivateSplicedChannel_When_SixDeep_Then_NothingIsAnnounced()
    {
        // Arrange
        using var harness = CreateHarness(announce: false);
        harness.Alice.Fund(SpliceInSatoshis + 200_000);
        var result = await harness.SpliceAsync(harness.Alice, SpliceInSatoshis);
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");

        // Act
        await harness.ConfirmAsync(result.SpliceTxId!.Value, SpliceHeight, harness.Alice, harness.Bob);
        await RaiseBlockAsync(harness, SpliceHeight + 5);

        // Assert: BOLT 7 MUST NOT send announcement_signatures without announce_channel
        Assert.Equal(0, CountAnnouncementSignatures(harness));
        Assert.Empty(Sink(harness.Alice).ChannelAnnouncements);
        Assert.Empty(harness.Failures);
    }

    private static SpliceHarness CreateHarness(bool announce = true) =>
        new(realEngine: true, announceChannel: announce, configureServices: (node, services) =>
        {
            var options = new NodeOptions
            {
                EnableHtlcs = true,
                BitcoinNetwork = BitcoinNetwork.Regtest,
                Alias = node.Name
            };
            options.Features.AllowExperimentalFeatures = true;
            options.Features.OptionQuiesce = FeatureSupport.Optional;
            options.Features.OptionSplice = FeatureSupport.Optional;
            services.AddSingleton(Options.Create(options));
            services.AddGossipServices();
            services.AddSingleton<IOwnGossipSink>(new RecordingOwnGossipSink());
            services.AddSingleton<IGossipRelayScheduler>(new RecordingRelayScheduler());
            services
               .AddScoped<IChannelMessageHandler<AnnouncementSignaturesMessage>, AnnouncementSignaturesMessageHandler>();
        });

    private static async Task RaiseBlockAsync(SpliceHarness harness, uint height)
    {
        await harness.Alice.Node.RaiseBlockAsync(height);
        await harness.Bob.Node.RaiseBlockAsync(height);
        await harness.PumpAsync();
    }

    /// <summary>A new (restarted) map loaded from the node's saved <c>ChannelFundings</c> rows, in creation order as
    /// the database's <c>Sequence</c> keeps them.</summary>
    private static async Task<RetiredScidMap> LoadFromSavedRowsAsync(SpliceNode node, uint height)
    {
        var channel = node.Node.Channel;
        var rows = node.FundingRows.Committed.Values
                       .OrderBy(f => f.Kind == ChannelFundingKind.Initial ? 0 : 1)
                       .ThenBy(f => f.ConfirmedHeight ?? uint.MaxValue)
                       .ToList();
        var channelDb = new Mock<IChannelDbRepository>();
        channelDb.Setup(r => r.GetByIdAsync(channel.ChannelId)).ReturnsAsync(channel);
        var fundingDb = new Mock<IChannelFundingDbRepository>();
        fundingDb.Setup(r => r.GetChannelIdsWithRetiredFundingsAsync()).ReturnsAsync([channel.ChannelId]);
        fundingDb.Setup(r => r.GetByChannelIdAsync(channel.ChannelId)).ReturnsAsync(rows);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundingDb.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var map = new RetiredScidMap(services.BuildServiceProvider(), NullLogger<RetiredScidMap>.Instance);
        await map.LoadAsync(height, TestContext.Current.CancellationToken);
        return map;
    }

    private static RecordingOwnGossipSink Sink(SpliceNode node) =>
        (RecordingOwnGossipSink)node.Node.Services.GetRequiredService<IOwnGossipSink>();

    private static int CountAnnouncementSignatures(SpliceHarness harness) =>
        harness.Transcript.Count(t => t.Message is AnnouncementSignaturesMessage);
}