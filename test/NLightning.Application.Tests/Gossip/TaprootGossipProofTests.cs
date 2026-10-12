using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Splicing;
using Application.Gossip.Announcements.Interfaces;
using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Sync;
using Application.Payments.Invoices;
using Application.Payments.Routing;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Enums;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Graph;
using Infrastructure.Bitcoin.Gossip;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Sync;

/// <summary>
/// The T7 goal proof (taproot gossip, BOLTs PR #1059, NL-878), in process: Alice and Bob, two NLightning nodes with
/// real signers and SQLite stores, run the <c>channel_announcement_2</c> MuSig2 session of their public simple taproot
/// channel through their channel managers (announcement nonces in <c>channel_ready</c>, <c>announcement_signatures_2</c>
/// both ways) and publish it; Bob's graph holds the channel with both <c>channel_update_2</c>s; Carol, with only a
/// private channel to Bob, learns the channel from Bob by the gossip queries (her sync manager against Bob's, her real
/// ingress validating the MuSig2 proof against the P2TR funding output) and pays Alice's invoice, which carries no
/// route hint, over Carol → Bob → Alice.
/// </summary>
public class TaprootGossipProofTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private const long SpliceInSatoshis = 100_000;
    private const uint SpliceHeight = ThreeNodeHarness.BlockHeight + 10;
    private const uint SpliceDepth3 = SpliceHeight + 2;
    private const uint SpliceDepth6 = SpliceHeight + 5;

    [Fact]
    public async Task Given_APublicTaprootChannel_When_AnnouncedAndSynced_Then_AThirdNodePaysOverItWithoutHints()
    {
        // Arrange, Act 1-2 and their asserts: announced by Alice and Bob, learned by Carol from Bob
        var ct = TestContext.Current.CancellationToken;
        await using var proof = await AnnounceAndSyncAsync(splicing: false, ct);
        var harness = proof.Harness;

        // Act 3: Carol pays Alice's invoice, which names no route hint
        var (invoice, result) = await CarolPaysAliceAsync(harness, "taproot gossip", ct);

        // Assert 3: paid over Carol → Bob → Alice, the last hop on the announced taproot channel
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        await AssertSettledAsync(harness, invoice);
        Assert.Contains(harness.Alice.Received, m => m is UpdateAddHtlcMessage add
                                                  && add.Payload.ChannelId == ThreeNodeHarness.AliceBobChannelId);
    }

    /// <summary>
    /// NL-1131 on top of the T7 goal proof: the announced taproot channel is spliced (Alice splices in), the splice
    /// locks on both sides, and at its 6th confirmation Alice and Bob re-announce the channel under the splice
    /// (<c>announcement_signatures_2</c> for the splice's txid and short channel id, a new <c>channel_announcement_2</c>
    /// and both <c>channel_update_2</c>s). Carol's graph marks the old short channel id spent at the splice's block,
    /// learns the new one from Bob by gossip queries (her ingress checking the MuSig2 proof against the splice's P2TR
    /// output, the rotated funding keys), and pays a hint-less invoice of Alice over the new short channel id; an onion
    /// that still names the old one is forwarded by Bob through the retired short channel id map.
    /// </summary>
    [Fact]
    public async Task Given_AnAnnouncedTaprootChannel_When_SplicedAndReannounced_Then_AThirdNodePaysOverTheNewScid()
    {
        // Arrange: announced and learned by Carol (as the goal proof); Alice holds a wallet output to splice in
        var ct = TestContext.Current.CancellationToken;
        await using var proof = await AnnounceAndSyncAsync(splicing: true, ct);
        var harness = proof.Harness;
        var aliceChannel = harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId);
        var oldFunding = aliceChannel.FundingOutput!;
        var (oldTxId, oldIndex) = (oldFunding.TransactionId!.Value, oldFunding.Index!.Value);
        harness.Alice.Splicing!.Fund(SpliceInSatoshis + 200_000);

        // Act 1: Alice splices in (quiescence, interactive-tx, splice commitment_signed, tx_signatures)
        var splice = harness.Alice.Services.GetRequiredService<SpliceService>()
                            .StartAsync(new SpliceRequest(ThreeNodeHarness.AliceBobChannelId, SpliceInSatoshis,
                                                          SpliceFeeratePerKw), ct);
        await harness.PumpUntilAsync(splice);
        var spliced = await splice;
        Assert.True(spliced.State == SpliceNegotiationState.Signed, $"{spliced.State}: {spliced.FailureReason}");
        var spliceTxId = spliced.SpliceTxId!.Value;

        // Act 2: the splice confirms at SpliceHeight (index 1) and reaches the channel's depth at both ends, which
        // exchange splice_locked (with their announcement nonces for the splice) and lock it
        harness.SetTip(SpliceDepth3);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            await node.Services.GetRequiredService<SpliceService>()
                      .OnSpliceDepthReachedAsync(ThreeNodeHarness.AliceBobChannelId, spliceTxId, SpliceHeight, 1, ct);
            await harness.PumpUntilAsync(Task.CompletedTask);
        }

        // Assert 2: locked both ways on the splice, its short channel id taken, the old one retired (still resolving)
        var spliceScid = new ShortChannelId(SpliceHeight, 1, aliceChannel.FundingOutput!.Index!.Value);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var channel = node.Channel(ThreeNodeHarness.AliceBobChannelId);
            Assert.Equal(spliceTxId, channel.FundingOutput!.TransactionId);
            Assert.Equal(spliceScid, channel.ShortChannelId);
            Assert.True(node.Services.GetRequiredService<IRetiredScidMap>()
                            .TryResolve(ThreeNodeHarness.AliceBobScid, out _));
        }

        var spliceLocked = harness.Sent.Select(s => s.Message).OfType<SpliceLockedMessage>().ToList();
        Assert.Equal(2, spliceLocked.Count);
        Assert.All(spliceLocked, m => Assert.NotNull(m.AnnouncementNodeNonceTlv));
        Assert.Equal(0, CountSignatures2(harness, spliceScid));

        // Act 3: the splice's 6th confirmation; the re-announcement runs through the channel managers
        harness.SetTip(SpliceDepth6);
        proof.BobGraph.TipHeight = SpliceDepth6;
        var announcements = harness.Alice.Services.GetRequiredService<IChannelAnnouncement2Service>();
        harness.Alice.ChannelManager.Publish(harness.Bob.NodeId, announcements.Advance(aliceChannel,
                                                                                       harness.Bob.NodeId));
        await harness.PumpAsync();
        await proof.BobGraph.Ingress.WhenOwnGossipAppliedAsync(ct);

        // Assert 3: one announcement_signatures_2 each way for the splice; Alice's new announcement is valid against the
        // splice's P2TR output (both rotated funding keys)
        Assert.Equal(2, CountSignatures2(harness, spliceScid));
        Assert.All(harness.Sent.Select(s => s.Message).OfType<AnnouncementSignatures2Message>()
                          .Where(m => m.Payload.ShortChannelId == spliceScid),
                   m => Assert.Equal(spliceTxId, m.Payload.FundingTxId));
        var (reannounced, capacity) = proof.AliceSink.ChannelAnnouncements2[^1];
        Assert.Equal(spliceScid, reannounced.ShortChannelId);
        Assert.Equal(spliceTxId, reannounced.FundingTxId);
        var spliceScript = FundingScriptOf(harness, aliceChannel);
        Assert.NotEqual(proof.FundingScript, spliceScript);
        Assert.Equal(GossipV2ProofResult.Valid,
                     harness.Alice.Services.GetRequiredService<IGossipV2SignatureVerifier>()
                            .CheckChannelProof(reannounced, spliceScript));
        Assert.Equal((long)ThreeNodeHarness.FundingSatoshis + SpliceInSatoshis, capacity.Satoshi);

        // Bob's graph holds the new channel with both channel_update_2s (his own, Alice's as relayed to him)
        await FeedAliceUpdatesAsync(proof, spliceScid, ct);
        AssertV2ChannelWithBothPolicies(proof.BobGraph, spliceScid);

        // Act 4: Carol's chain sees the splice spend the old funding output (her pruner marks the old short channel id
        // spent), then she re-syncs from Bob by queries at the splice's 6th block
        proof.CarolGraph.TipHeight = SpliceDepth6;
        var carolPruner = new GraphPruner(proof.CarolGraph.Store, new Mock<IBlockchainMonitor>().Object,
                                          proof.CarolGraph.FundingLookup.Object,
                                          Microsoft.Extensions.Options.Options.Create(proof.CarolGraph.Options),
                                          Microsoft.Extensions.Options.Options.Create(new NodeOptions
                                          {
                                              BitcoinNetwork = BitcoinNetwork.Regtest
                                          }), NullLogger<GraphPruner>.Instance);
        Assert.Equal(1, carolPruner.ApplyBlock(SpliceHeight, [(oldTxId, oldIndex)]));
        proof.CarolGraph.OutputFound(spliceScript, capacity.Satoshi, txId: spliceTxId);
        await SyncAsync(proof.CarolGraph, proof.BobGraph, ct, SpliceDepth6);

        // Assert 4: the old channel is spent at the splice's block, the new one verified with both policies
        Assert.True(proof.CarolGraph.Store.TryGetChannel(ThreeNodeHarness.AliceBobScid, out var old));
        Assert.Equal(SpliceHeight, old!.SpentAtHeight);
        var atCarol = AssertV2ChannelWithBothPolicies(proof.CarolGraph, spliceScid);
        Assert.Equal(GraphChannelVerification.Verified, atCarol.Verification);
        Assert.Equal((ulong)capacity.Satoshi, atCarol.CapacitySat);

        // Act 5: Carol pays a fresh hint-less invoice of Alice
        var (invoice, result) = await CarolPaysAliceAsync(harness, "after the splice", ct);

        // Assert 5: paid over Carol → Bob → Alice, Bob's hop to Alice named by the splice's short channel id
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal([ThreeNodeHarness.BobCarolScid, spliceScid],
                     result.Payment.Route.Select(h => h.ShortChannelId).ToArray());
        await AssertSettledAsync(harness, invoice);
        Assert.Contains(harness.Alice.Received, m => m is UpdateAddHtlcMessage add
                                                  && add.Payload.PaymentHash.Span.SequenceEqual((byte[])invoice.PaymentHash)
                                                  && add.Payload.ChannelId == ThreeNodeHarness.AliceBobChannelId);

        // Act 6: within the 72 blocks, an onion that still names the old short channel id for Bob's hop
        var legacy = await harness.Alice.Invoices.CreateInvoiceAsync(LightningMoney.Satoshis(5_000),
                                                                     "the old scid", null, ct);
        var amount = legacy.Amount!;
        var finalCltv = SpliceDepth6 + legacy.MinFinalCltvExpiry + 12u;
        var route = new PaymentRoute(
            [
                new RouteHop(harness.Bob.NodeId, amount, finalCltv, ThreeNodeHarness.AliceBobScid),
                new RouteHop(harness.Alice.NodeId, amount, finalCltv, null)
            ], amount + ThreeNodeHarness.ForwardingFeeOf(ThreeNodeHarness.BobRouting, amount),
            finalCltv + ThreeNodeHarness.BobRouting.CltvExpiryDelta, legacy.PaymentHash, legacy.PaymentSecret);
        var onion = await harness.Carol.Services.GetRequiredService<PaymentOnionFactory>().CreateAsync(route);
        await harness.Carol.Operations.OfferHtlcAsync(ThreeNodeHarness.BobCarolChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                      null, HtlcOrigin.Local(route.PaymentHash), ct);
        await harness.PumpAsync();

        // Assert 6: Bob resolved the retired short channel id to the spliced channel and Alice settled the invoice
        await AssertSettledAsync(harness, legacy);
        Assert.Contains(harness.Alice.Received, m => m is UpdateAddHtlcMessage add
                                                  && add.Payload.PaymentHash.Span.SequenceEqual((byte[])legacy.PaymentHash)
                                                  && add.Payload.ChannelId == ThreeNodeHarness.AliceBobChannelId);
    }

    private const uint SpliceFeeratePerKw = 1_000;

    /// <summary>
    /// The T7 goal proof's first half: Alice and Bob announce their public taproot channel through their channel
    /// managers (Alice's proof checked against the P2TR funding output), Bob's graph gets both
    /// <c>channel_update_2</c>s, and Carol learns the channel from Bob by gossip queries.
    /// </summary>
    private static async Task<TaprootGossipProof> AnnounceAndSyncAsync(bool splicing, CancellationToken ct)
    {
        // Arrange: Bob's graph is his own-gossip sink; Alice's is a recorder (her gossip reaches Bob as relayed)
        var aliceSink = new Announcements.RecordingOwnGossipSink();
        GraphTestKit? bobGraph = null;
        var carolGraph = new GraphTestKit(gossipV2: true, tipHeight: ThreeNodeHarness.BlockHeight);
        var harness = await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
                node.Options.Features = Features();

            bobGraph = new GraphTestKit(gossipV2: true, tipHeight: ThreeNodeHarness.BlockHeight,
                                        ourNodeId: h.Bob.NodeId);
            h.Alice.ConfigureServices = services =>
            {
                AddTaprootGossip(services);
                services.AddSingleton<IOwnGossipSink>(aliceSink);
                services.Configure<InvoiceOptions>(o => o.RouteHints = InvoiceRouteHintMode.Never);
            };
            h.Bob.ConfigureServices = services =>
            {
                AddTaprootGossip(services);
                services.AddSingleton<IOwnGossipSink>(bobGraph.Ingress);
            };
            h.Carol.ConfigureServices = services =>
            {
                services.AddPaymentSendServices();
                services.AddSingleton<IGraphStore>(carolGraph.Store);
            };
        }, simpleTaproot: true, announceAliceBob: true, splicing: splicing);
        var proof = new TaprootGossipProof(harness, aliceSink, bobGraph!, carolGraph);
        try
        {
            harness.NegotiatedFeatures = Features();

            // Act 1: the MuSig2 session (Alice's nonces go out at the depth; the rest follows through the managers)
            var announcements = harness.Alice.Services.GetRequiredService<IChannelAnnouncement2Service>();
            var aliceChannel = harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId);
            harness.Alice.ChannelManager.Publish(harness.Bob.NodeId, announcements.Advance(aliceChannel,
                                                                                           harness.Bob.NodeId));
            await harness.PumpAsync();
            await proof.BobGraph.Ingress.WhenOwnGossipAppliedAsync(ct);

            // Assert 1: both announced the channel, Alice with a valid proof against the P2TR funding output
            Assert.Single(harness.Bob.Received, m => m is AnnouncementSignatures2Message);
            Assert.Single(harness.Alice.Received, m => m is AnnouncementSignatures2Message);
            var (announcement, _) = Assert.Single(aliceSink.ChannelAnnouncements2);
            proof.FundingScript = FundingScriptOf(harness, aliceChannel);
            Assert.Equal(GossipV2ProofResult.Valid,
                         harness.Alice.Services.GetRequiredService<IGossipV2SignatureVerifier>()
                                .CheckChannelProof(announcement, proof.FundingScript));

            // Alice's channel_update_2 and node_announcement_2 reach Bob's graph (what his relay feed would hand him)
            await FeedAliceUpdatesAsync(proof, ThreeNodeHarness.AliceBobScid, ct);
            AssertV2ChannelWithBothPolicies(proof.BobGraph, ThreeNodeHarness.AliceBobScid);

            // Act 2: Carol syncs from Bob by queries (her manager against his), her ingress checking every message
            carolGraph.OutputFound(proof.FundingScript, (long)ThreeNodeHarness.FundingSatoshis,
                                   txId: aliceChannel.FundingOutput!.TransactionId);
            await SyncAsync(carolGraph, proof.BobGraph, ct, ThreeNodeHarness.BlockHeight);

            // Assert 2: Carol's graph holds the v2 channel with both directions' policies
            var atCarol = AssertV2ChannelWithBothPolicies(carolGraph, ThreeNodeHarness.AliceBobScid);
            Assert.Equal(GraphChannelVerification.Verified, atCarol.Verification);
            return proof;
        }
        catch
        {
            await proof.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The nodes' features and the ones every link negotiated: taproot channels and gossip (experimental), with the
    /// defaults (<c>option_simple_close</c>, <c>option_quiesce</c>, <c>option_splice</c> Optional) spelled out.
    /// </summary>
    private static FeatureOptions Features() => new()
    {
        AllowExperimentalFeatures = true,
        OptionSimpleTaproot = FeatureSupport.Optional,
        OptionSimpleClose = FeatureSupport.Optional,
        OptionGossipV2 = FeatureSupport.Optional,
        OptionQuiesce = FeatureSupport.Optional,
        OptionSplice = FeatureSupport.Optional
    };

    /// <summary>Alice's <c>channel_update_2</c>s for <paramref name="shortChannelId"/> into Bob's graph.</summary>
    private static async Task FeedAliceUpdatesAsync(TaprootGossipProof proof, ShortChannelId shortChannelId,
                                                    CancellationToken ct)
    {
        var alicePeer = new FakeGossipPeer(0x0A, gossipV2: true);
        foreach (var update in proof.AliceSink.ChannelUpdates2.Where(u => u.ShortChannelId == shortChannelId))
            Assert.Equal(GossipIngressOutcome.Accepted,
                         (await proof.BobGraph.Ingress.ProcessAsync(alicePeer, new ChannelUpdate2Message(update), 0,
                                                                    ct)).Outcome);
    }

    private static GraphChannel AssertV2ChannelWithBothPolicies(GraphTestKit graph, ShortChannelId shortChannelId)
    {
        Assert.True(graph.Store.TryGetChannel(shortChannelId, out var channel));
        Assert.True(channel!.Versions.HasFlag(GraphGossipVersions.V2));
        Assert.NotNull(channel.GetRoutingPolicy(0));
        Assert.NotNull(channel.GetRoutingPolicy(1));
        return channel;
    }

    private static byte[] FundingScriptOf(ThreeNodeHarness harness, ChannelModel channel) =>
        harness.Alice.Services.GetRequiredService<IMusig2Service>()
               .AggregateTaprootKeyPath(channel.LocalFundingPubKey, channel.RemoteFundingPubKey!.Value)
               .GetTaprootScriptPubKey();

    private static int CountSignatures2(ThreeNodeHarness harness, ShortChannelId shortChannelId) =>
        harness.Sent.Count(s => s.Message is AnnouncementSignatures2Message m
                             && m.Payload.ShortChannelId == shortChannelId);

    /// <summary>Carol pays a fresh 10,000 sat invoice of Alice, which names no route hint.</summary>
    private static async Task<(InvoiceModel Invoice, PayInvoiceResult Result)> CarolPaysAliceAsync(
        ThreeNodeHarness harness, string description, CancellationToken ct)
    {
        var invoice = await harness.Alice.Invoices.CreateInvoiceAsync(LightningMoney.Satoshis(10_000), description,
                                                                      null, ct);
        Assert.Empty(Bolt11.Models.Invoice.Decode(invoice.Bolt11!, BitcoinNetwork.Regtest).RouteHints);
        var payment = harness.Carol.Services.GetRequiredService<IPaymentService>()
                             .PayInvoiceAsync(invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = s_timeout }, ct);
        while (!payment.IsCompleted)
            await harness.PumpAsync();
        return (invoice, await payment);
    }

    private static async Task AssertSettledAsync(ThreeNodeHarness harness, InvoiceModel invoice)
    {
        var stored = await harness.Alice.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(
                                                         invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    /// <summary>The nodes, Alice's gossip recorder and Bob's and Carol's graphs of one proof run.</summary>
    private sealed class TaprootGossipProof(ThreeNodeHarness harness, Announcements.RecordingOwnGossipSink aliceSink,
                                            GraphTestKit bobGraph, GraphTestKit carolGraph) : IAsyncDisposable
    {
        public ThreeNodeHarness Harness { get; } = harness;
        public Announcements.RecordingOwnGossipSink AliceSink { get; } = aliceSink;
        public GraphTestKit BobGraph { get; } = bobGraph;
        public GraphTestKit CarolGraph { get; } = carolGraph;

        /// <summary>The P2TR script of the channel's original funding output.</summary>
        public byte[] FundingScript { get; set; } = [];

        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }

    /// <summary>
    /// Carol's <see cref="GossipSyncManager"/> against Bob's: what Carol sends goes to Bob's manager (his responder),
    /// what Bob sends back goes to Carol's ingress (gossip) or manager (query replies), until nothing moves.
    /// </summary>
    private static async Task SyncAsync(GraphTestKit carol, GraphTestKit bob, CancellationToken ct,
                                        uint tipHeight)
    {
        var carolManager = CreateManager(carol, tipHeight);
        var bobManager = CreateManager(bob, tipHeight);
        var bobAsSeenByCarol = new FakeGossipPeer(0x0B, gossipQueriesEx: true, gossipV2: true);
        var carolAsSeenByBob = new FakeGossipPeer(0x0C, gossipQueriesEx: true, gossipV2: true);
        bobManager.OnPeerInitialized(carolAsSeenByBob);
        carolManager.OnPeerInitialized(bobAsSeenByCarol);

        int carolSent = 0, bobSent = 0;
        for (var idle = 0; idle < 20;)
        {
            var moved = false;
            foreach (var message in bobAsSeenByCarol.Sent.Skip(carolSent).ToList())
            {
                carolSent++;
                moved = true;
                bobManager.HandleMessage(carolAsSeenByBob, message);
            }

            foreach (var message in carolAsSeenByBob.Sent.Skip(bobSent).ToList())
            {
                bobSent++;
                moved = true;
                if (message is ChannelAnnouncement2Message or ChannelUpdate2Message or NodeAnnouncement2Message
                                                          or ChannelAnnouncementMessage or ChannelUpdateMessage
                                                          or NodeAnnouncementMessage)
                    await carol.Ingress.ProcessAsync(bobAsSeenByCarol, message, 0, ct);
                else
                    carolManager.HandleMessage(bobAsSeenByCarol, message);
            }

            if (moved)
                idle = 0;
            else
            {
                idle++;
                await Task.Delay(25, ct);
            }
        }

        Assert.True(carolManager.HasCompletedInitialSync);
        Assert.Empty(bobAsSeenByCarol.Warnings);
    }

    private static GossipSyncManager CreateManager(GraphTestKit kit, uint tipHeight) =>
        new(kit.Store, Microsoft.Extensions.Options.Options.Create(new GossipSyncOptions
        {
            MinQueryInterval = TimeSpan.Zero
        }),
            Microsoft.Extensions.Options.Options.Create(new NodeOptions
            {
                BitcoinNetwork = BitcoinNetwork.Regtest,
                Features = new FeatureOptions
                {
                    AllowExperimentalFeatures = true,
                    OptionGossipV2 = FeatureSupport.Optional
                }
            }), NullLogger<GossipSyncManager>.Instance, ingress: kit.Ingress,
            getTipHeight: () => tipHeight);

    private static void AddTaprootGossip(IServiceCollection services)
    {
        services.TryAddSingleton<IGossipV2SignatureVerifier, GossipV2SignatureVerifier>();
        services.AddScoped<IChannelMessageHandler<ChannelReadyMessage>, ChannelReadyMessageHandler>();
        services.AddScoped<IChannelMessageHandler<AnnouncementSignatures2Message>,
            AnnouncementSignatures2MessageHandler>();
    }
}