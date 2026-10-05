using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Gossip.Announcements.Interfaces;
using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Sync;
using Application.Payments.Invoices;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Crypto.Interfaces;
using Domain.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Graph;
using Infrastructure.Bitcoin.Gossip;
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

    [Fact]
    public async Task Given_APublicTaprootChannel_When_AnnouncedAndSynced_Then_AThirdNodePaysOverItWithoutHints()
    {
        // Arrange: Bob's graph is his own-gossip sink; Alice's is a recorder (her gossip reaches Bob as relayed)
        var ct = TestContext.Current.CancellationToken;
        var aliceSink = new Announcements.RecordingOwnGossipSink();
        GraphTestKit? bobGraph = null;
        var carolGraph = new GraphTestKit(gossipV2: true, tipHeight: ThreeNodeHarness.BlockHeight);
        await using var harness = await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
                node.Options.Features = new FeatureOptions
                {
                    AllowExperimentalFeatures = true,
                    OptionSimpleTaproot = FeatureSupport.Optional,
                    OptionGossipV2 = FeatureSupport.Optional
                };

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
        }, simpleTaproot: true, announceAliceBob: true);

        harness.NegotiatedFeatures = new FeatureOptions
        {
            AllowExperimentalFeatures = true,
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionSimpleClose = FeatureSupport.Optional,
            OptionGossipV2 = FeatureSupport.Optional
        };

        // Act 1: the MuSig2 session (Alice's nonces go out at the depth; the rest follows through the managers)
        var announcements = harness.Alice.Services.GetRequiredService<IChannelAnnouncement2Service>();
        var aliceChannel = harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId);
        harness.Alice.ChannelManager.Publish(harness.Bob.NodeId, announcements.Advance(aliceChannel,
                                                                                       harness.Bob.NodeId));
        await harness.PumpAsync();
        await bobGraph!.Ingress.WhenOwnGossipAppliedAsync(ct);

        // Assert 1: both announced the channel, Alice with a valid proof against the P2TR funding output
        Assert.Single(harness.Bob.Received, m => m is AnnouncementSignatures2Message);
        Assert.Single(harness.Alice.Received, m => m is AnnouncementSignatures2Message);
        var (announcement, _) = Assert.Single(aliceSink.ChannelAnnouncements2);
        var musig2 = harness.Alice.Services.GetRequiredService<IMusig2Service>();
        var fundingScript = musig2.AggregateTaprootKeyPath(aliceChannel.LocalFundingPubKey,
                                                           aliceChannel.RemoteFundingPubKey!.Value)
                                  .GetTaprootScriptPubKey();
        Assert.Equal(Domain.Gossip.Enums.GossipV2ProofResult.Valid,
                     harness.Alice.Services.GetRequiredService<IGossipV2SignatureVerifier>()
                            .CheckChannelProof(announcement, fundingScript));

        // Alice's channel_update_2 and node_announcement_2 reach Bob's graph (what his relay feed would hand him)
        var alicePeer = new FakeGossipPeer(0x0A, gossipV2: true);
        foreach (var update in aliceSink.ChannelUpdates2)
            Assert.Equal(GossipIngressOutcome.Accepted,
                         (await bobGraph.Ingress.ProcessAsync(alicePeer, new ChannelUpdate2Message(update), 0, ct))
                        .Outcome);

        Assert.True(bobGraph.Store.TryGetChannel(ThreeNodeHarness.AliceBobScid, out var atBob));
        Assert.True(atBob!.Versions.HasFlag(GraphGossipVersions.V2));
        Assert.NotNull(atBob.GetRoutingPolicy(0));
        Assert.NotNull(atBob.GetRoutingPolicy(1));

        // Act 2: Carol syncs from Bob by queries (her manager against his), her ingress checking every message
        carolGraph.OutputFound(fundingScript, (long)ThreeNodeHarness.FundingSatoshis,
                               txId: aliceChannel.FundingOutput!.TransactionId);
        await SyncAsync(carolGraph, bobGraph, ct);

        // Assert 2: Carol's graph holds the v2 channel with both directions' policies
        Assert.True(carolGraph.Store.TryGetChannel(ThreeNodeHarness.AliceBobScid, out var atCarol));
        Assert.True(atCarol!.Versions.HasFlag(GraphGossipVersions.V2));
        Assert.Equal(GraphChannelVerification.Verified, atCarol.Verification);
        Assert.NotNull(atCarol.GetRoutingPolicy(0));
        Assert.NotNull(atCarol.GetRoutingPolicy(1));

        // Act 3: Carol pays Alice's invoice, which names no route hint
        var invoice = await harness.Alice.Invoices.CreateInvoiceAsync(LightningMoney.Satoshis(10_000), "taproot gossip",
                                                                      null, ct);
        Assert.Empty(Bolt11.Models.Invoice.Decode(invoice.Bolt11!, BitcoinNetwork.Regtest).RouteHints);
        var payment = harness.Carol.Services.GetRequiredService<IPaymentService>()
                             .PayInvoiceAsync(invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = s_timeout }, ct);
        while (!payment.IsCompleted)
            await harness.PumpAsync();
        var result = await payment;

        // Assert 3: paid over Carol → Bob → Alice, the last hop on the announced taproot channel
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        var stored = await harness.Alice.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(
                                                         invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        Assert.Contains(harness.Alice.Received, m => m is UpdateAddHtlcMessage add
                                                  && add.Payload.ChannelId == ThreeNodeHarness.AliceBobChannelId);
    }

    /// <summary>
    /// Carol's <see cref="GossipSyncManager"/> against Bob's: what Carol sends goes to Bob's manager (his responder),
    /// what Bob sends back goes to Carol's ingress (gossip) or manager (query replies), until nothing moves.
    /// </summary>
    private static async Task SyncAsync(GraphTestKit carol, GraphTestKit bob, CancellationToken ct)
    {
        var carolManager = CreateManager(carol);
        var bobManager = CreateManager(bob);
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

    private static GossipSyncManager CreateManager(GraphTestKit kit) =>
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
            getTipHeight: () => ThreeNodeHarness.BlockHeight);

    private static void AddTaprootGossip(IServiceCollection services)
    {
        services.TryAddSingleton<IGossipV2SignatureVerifier, GossipV2SignatureVerifier>();
        services.AddScoped<IChannelMessageHandler<ChannelReadyMessage>, ChannelReadyMessageHandler>();
        services.AddScoped<IChannelMessageHandler<AnnouncementSignatures2Message>,
            AnnouncementSignatures2MessageHandler>();
    }
}