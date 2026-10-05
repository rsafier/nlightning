using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Taproot;

using Abcd;
using Application.Payments.Invoices;
using Bolt11.Models;
using Day0;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Domain.Serialization.Interfaces;
using Gossip;
using Infrastructure.Serialization.Messages;
using Utils;

/// <summary>
/// The regtest end-to-end proof of public simple taproot channels (taproot gossip, BOLTs PR #1059, NL-878 T7): three
/// NLightning nodes on the taproot network's bitcoind (<see cref="LndTaprootNetworkFixture"/>; its LND is not used),
/// each with <c>Features:AllowExperimentalFeatures</c>, <c>Features:OptionSimpleTaproot=Optional</c>,
/// <c>Features:OptionGossipV2=Optional</c> and <c>Gossip:AcceptPublicChannels</c>, the real chain lookups and the
/// daemon's client handlers (<c>openchannel --public --channel-type taproot</c>, <c>listgraphchannels</c>,
/// <c>listnodes</c>, <c>createinvoice</c>, <c>payinvoice</c>, <c>closechannel</c>).
/// </summary>
/// <remarks>
/// <para>(1) alice opens a public taproot channel to bob (dual-funded, bob contributes: v2 by the NL-551 rules); at
/// the announcement depth both graphs hold it as a v2 channel with both <c>channel_update_2</c>s and both
/// <c>node_announcement_2</c>s, the MuSig2 proof checks against the P2TR funding output the real
/// <c>FundingOutputLookup</c> reads from bitcoind, and no v1 <c>announcement_signatures</c> or
/// <c>channel_announcement</c> crossed the wire. (2) carol, connected to bob only, learns the channel by gossip queries
/// (her ingress checks the proof against the chain) and pays alice's hint-free invoice over carol → bob → alice.
/// (4) alice closes cooperatively (<c>option_simple_close</c>) and every graph marks the channel spent and forgets it
/// 72 blocks later. (3) bob stops between the funding depth and the announcement depth; on his restart both
/// <c>channel_reestablish</c>es carry the announcement nonces (TLV 7) and the announcement completes. The negative:
/// a node without <c>option_gossip_v2</c> neither opens nor is opened a public taproot channel.</para>
/// <para>In this suite (<c>scripts/run-cluster.sh --suite taproot</c>, the <c>Docker.Taproot</c> namespace) because it
/// needs only a bitcoind and the taproot network costs one namespace; our nodes never connect to its LND, so its
/// proofs are unaffected.</para>
/// </remarks>
[Collection(LndTaprootRegtestCollection.Name)]
public sealed class TaprootPublicChannelFlowTests : IAsyncLifetime
{
    private const long OpenerFundingSat = 600_000;
    private const long AccepterContributionSat = 300_000;
    private const long CarolChannelSat = 400_000;
    private const long PaymentSat = 25_000;
    private const int AnnouncementDepth = 6;
    private const ulong SpliceInSat = 200_000;
    private const ulong SpliceOutSat = 100_000;
    private const ushort ChannelReestablishType = 136;
    private const ushort AnnouncementSignaturesType = 259;
    private const ushort AnnouncementSignatures2Type = 260;
    private const ushort ChannelAnnouncementType = 256;

    private static readonly TimeSpan s_graphTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_blockEvery = TimeSpan.FromSeconds(15);

    private readonly LndTaprootNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public TaprootPublicChannelFlowTests(LndTaprootNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
            foreach (var node in _nodes)
            {
                Console.WriteLine($"===== {node.Name}: last log lines =====");
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            }

        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    [Fact]
    public async Task Given_APublicTaprootChannel_When_AnnouncedSyncedPaidOverAndClosed_Then_EveryGraphFollowsItOverGossipV2()
    {
        // Arrange: alice and bob record their gossip (v1 and v2) and announcement_signatures(_2); bob contributes
        var ct = TestContext.Current.CancellationToken;
        var aliceTraffic = new GossipTrafficRecorder();
        var bobTraffic = new GossipTrafficRecorder();
        var alice = await StartNodeAsync("tpr-alice", "nltg-tpr-alice", true, ct, traffic: aliceTraffic);
        var bob = await StartNodeAsync("tpr-bob", "nltg-tpr-bob", true, ct, AccepterContributionSat, bobTraffic);
        var carol = await StartNodeAsync("tpr-carol", "nltg-tpr-carol", true, ct);
        await alice.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await bob.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await carol.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(alice, bob, ct);

        // Act 1: openchannel <bob> 600000 --public --channel-type taproot, mined to the announcement depth
        var channelId = await OpenPublicTaprootChannelAsync(alice, bob, ct);
        var (openAlice, _) = await Day0Harness.MineUntilUsableAsync(_fixture, [], alice, bob, channelId, ct);
        var scid = openAlice.ShortChannelId!.Value;
        await MineUntilAnnouncedAsync([alice, bob], scid, [alice, bob], ct);

        // Assert 1: a dual-funded public taproot channel
        foreach (var node in new[] { alice, bob })
        {
            var channel = Channel(node, channelId);
            Assert.True(channel.ChannelParams.OptionSimpleTaproot);
            Assert.True(channel.ChannelParams.AnnounceChannel);
            Assert.Equal(ChannelVersion.V2, channel.Version);
            Assert.Equal(CommitmentFormat.SimpleTaproot, (await node.GetChannelAsync(channelId, ct)).ChannelType);
        }

        Assert.Equal(OpenerFundingSat + AccepterContributionSat, openAlice.Capacity.Satoshi);

        // Assert 1: both graphs hold the channel as v2 only, both channel_update_2s, both node_announcement_2s
        foreach (var node in new[] { alice, bob })
        {
            var stored = await AssertV2ChannelAsync(node, scid, [alice, bob]);
            Assert.Equal(GraphChannelVerification.Own, stored.Verification);
            Assert.Equal((ulong)(OpenerFundingSat + AccepterContributionSat), stored.CapacitySat);

            // The MuSig2 proof against the P2TR funding output bitcoind holds, read by the node's real lookup
            var fundingScript = await AssertP2TrFundingOutputAsync(node, scid, openAlice.FundingTxId!.Value, ct);
            var announcement = ChannelAnnouncement2Payload.Parse(stored.RawAnnouncement2.Span);
            Assert.Equal(scid, announcement.ShortChannelId);
            Assert.Equal(openAlice.FundingTxId!.Value, announcement.FundingTxId);
            Assert.Equal(GossipV2ProofResult.Valid,
                         node.Services.GetRequiredService<IGossipV2SignatureVerifier>()
                             .CheckChannelProof(announcement, fundingScript));
        }

        // Assert 1: the MuSig2 session ran over announcement_signatures_2; nothing of BOLT 7 v1 for this channel
        foreach (var (node, traffic) in new[] { (alice, aliceTraffic), (bob, bobTraffic) })
        {
            Console.WriteLine($"[{node.Name}] gossip traffic: {traffic.Describe()}");
            Assert.True(traffic.CountSent(AnnouncementSignatures2Type) >= 1);
            Assert.True(traffic.CountReceived(AnnouncementSignatures2Type) >= 1);
            Assert.Equal(0, traffic.CountSent(AnnouncementSignaturesType));
            Assert.Equal(0, traffic.CountReceived(AnnouncementSignaturesType));
            Assert.Equal(0, traffic.CountSent(ChannelAnnouncementType));
            Assert.Equal(0, traffic.CountReceived(ChannelAnnouncementType));
        }

        // Act 2: carol, bob's peer only, syncs by gossip queries; then she opens a private channel to bob to pay
        await carol.ConnectToAsync(bob, ct);
        var atCarol = await Poll.ForAsync(async () =>
        {
            var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(carol, scid.ToUInt64());
            return channel is { HasV2: true, Policy1V2: not null, Policy2V2: not null } ? channel : null;
        }, s_graphTimeout, "carol's graph has the taproot channel with both channel_update_2s", ct,
                                         GossipGraphProbe.PollInterval);
        var carolChannel = await carol.OpenChannelAsync(new OpenChannelClientRequest(bob.Address,
                                                            LightningMoney.Satoshis(CarolChannelSat)), ct);
        await Day0Harness.MineUntilUsableAsync(_fixture, [], carol, bob, carolChannel.ChannelId, ct);
        await Day0Harness.WaitSettledAsync(alice, channelId, ct);
        var invoice = await CreateHintFreeInvoiceAsync(alice, ct);

        // Act 2: carol pays alice's invoice from her graph
        var payment = await carol.PayInvoiceAsync(invoice.Bolt11!, ct);

        // Assert 2: carol's ingress verified the v2 announcement against the chain
        Assert.Equal(GraphChannelVerification.Verified, atCarol.Verification);
        Assert.False(atCarol.HasV1);
        Assert.Equal(scid, ChannelAnnouncement2Payload.Parse(atCarol.RawAnnouncement2.Span).ShortChannelId);

        // Assert 2: paid over carol -> bob -> alice, bob forwarding over the taproot channel at his announced fee
        Console.WriteLine($"carol's payment: {payment.Status}, fee {payment.Fee.MilliSatoshi} msat, "
                        + $"{payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(async () => (await alice.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status
                                        == InvoiceStatus.Settled, Day0Harness.StepTimeout,
                              "alice's invoice settled", ct);
        var forward = await Poll.ForAsync(async () =>
        {
            var current = await bob.GetForwardAsync(invoice.PaymentHash, ct);
            return current?.Status == ForwardCircuitStatus.Fulfilled ? current : null;
        }, Day0Harness.StepTimeout, "bob's forward fulfilled", ct);
        Assert.Equal(carolChannel.ChannelId, forward.IncomingChannelId);
        Assert.Equal(scid, forward.OutgoingShortChannelId);
        Assert.Equal(PaymentSat * 1_000, (long)forward.OutgoingAmount.MilliSatoshi);
        Assert.Equal(forward.Fee, payment.Fee);
        Assert.Equal(GraphPolicyFee(atCarol, bob, PaymentSat * 1_000), forward.Fee.MilliSatoshi);

        // Act 4: alice closes cooperatively; mined until the funding output is spent
        await Day0Harness.WaitSettledAsync(alice, channelId, ct);
        var closed = await Day0Harness.HandleAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                         alice, new CloseChannelClientRequest(channelId) { WaitSeconds = 120 }, ct);
        Console.WriteLine($"closechannel: {closed.State}, closing tx {Day0Harness.Display(closed.ClosingTxId)}");
        Assert.Equal(ChannelState.Closing, closed.State);
        var spentAt = await MineUntilFundingSpentAsync(openAlice, [alice, bob, carol], ct);

        // Assert 4: every graph marks the channel spent at the closing block ...
        foreach (var node in new[] { alice, bob, carol })
        {
            var spent = await Poll.ForAsync(async () =>
            {
                var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid.ToUInt64());
                return channel?.SpentAtHeight is not null ? channel : null;
            }, s_graphTimeout, $"{node.Name}'s graph marks the channel spent", ct, GossipGraphProbe.PollInterval);
            Assert.Equal(spentAt, spent.SpentAtHeight);
        }

        // ... and forgets it 72 blocks later (BOLT 7)
        await ChainSync.MineAndWaitAsync(_fixture, Day0Harness.SpentChannelPruneDelayBlocks, [], [alice, bob, carol],
                                         ct, TimeSpan.FromMinutes(3));
        foreach (var node in new[] { alice, bob, carol })
            await Poll.UntilAsync(async () => await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid.ToUInt64())
                                              is null, s_graphTimeout, $"{node.Name}'s graph forgot the channel", ct,
                                  GossipGraphProbe.PollInterval);
    }

    [Fact]
    public async Task Given_ThePeerStopsBeforeTheAnnouncementDepth_When_ItRestartsAfterIt_Then_ReestablishCarriesTheNoncesAndTheAnnouncementCompletes()
    {
        // Arrange: bob records the channel_reestablishes besides his gossip
        var ct = TestContext.Current.CancellationToken;
        var bobTraffic = new GossipTrafficRecorder(ChannelReestablishType);
        var alice = await StartNodeAsync("tpr-restart-alice", "nltg-tpr-ra", true, ct);
        var bob = await StartNodeAsync("tpr-restart-bob", "nltg-tpr-rb", true, ct, AccepterContributionSat,
                                       bobTraffic);
        await alice.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await bob.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(alice, bob, ct);
        var channelId = await OpenPublicTaprootChannelAsync(alice, bob, ct);
        var (openAlice, _) = await Day0Harness.MineUntilUsableAsync(_fixture, [], alice, bob, channelId, ct);
        var scid = openAlice.ShortChannelId!.Value;
        var depth = await FundingDepthAsync(scid, ct);
        Console.WriteLine($"Usable at depth {depth}; the announcement waits for depth {AnnouncementDepth}");
        Assert.True(depth < AnnouncementDepth, $"the channel became usable at depth {depth}");
        Assert.Equal(0, bobTraffic.CountSent(AnnouncementSignatures2Type));

        // Act: bob stops, the funding reaches the announcement depth without him, bob starts again
        await bob.StopAsync();
        await ChainSync.MineAndWaitAsync(_fixture, AnnouncementDepth - depth + 1, [], [alice], ct);
        Assert.Null(await GossipGraphProbe.TryGetOurGraphChannelAsync(alice, scid.ToUInt64()));
        var beforeRestart = bobTraffic.Traffic.Count;
        await bob.StartAsync(ct);
        await Day0Harness.EnsureConnectedAsync(alice, bob, ct);
        await MineUntilAnnouncedAsync([alice, bob], scid, [alice, bob], ct, maxBlocks: 1);

        // Assert: both reestablishes of the new connection carried the announcement nonces (TLV 7)
        var afterRestart = bobTraffic.Traffic.Skip(beforeRestart).ToList();
        var serializer = new MessageSerializer(NullLogger<MessageSerializer>.Instance,
                                               bob.Services.GetRequiredService<IMessageTypeSerializerFactory>());
        foreach (var outbound in new[] { true, false })
        {
            var reestablishes = new List<ChannelReestablishMessage>();
            foreach (var recorded in afterRestart.Where(t => t.Type == ChannelReestablishType
                                                          && t.Outbound == outbound))
            {
                using var stream = new MemoryStream(recorded.Wire, false);
                reestablishes.Add(Assert.IsType<ChannelReestablishMessage>(
                                      await serializer.DeserializeMessageAsync(stream)));
            }

            Assert.Contains(reestablishes, r => r.Payload.ChannelId == channelId && r.AnnouncementNoncesTlv is not null);
        }

        // Assert: the MuSig2 session ran on the new connection, and the channel is announced v2 on both
        Assert.Contains(afterRestart, t => t is { Type: AnnouncementSignatures2Type, Outbound: true });
        Assert.Contains(afterRestart, t => t is { Type: AnnouncementSignatures2Type, Outbound: false });
        foreach (var node in new[] { alice, bob })
            await AssertV2ChannelAsync(node, scid, [alice, bob]);
    }

    [Fact]
    public async Task Given_APublicTaprootChannel_When_SplicedInAndOut_Then_EveryGraphFollowsTheNewScidAndTheOldOneStillForwards()
    {
        // Arrange: alice and bob announce their public taproot channel; carol, bob's peer only, learns it and opens a
        // private channel to bob
        var ct = TestContext.Current.CancellationToken;
        var alice = await StartNodeAsync("tps-alice", "nltg-tps-alice", true, ct);
        var bob = await StartNodeAsync("tps-bob", "nltg-tps-bob", true, ct, AccepterContributionSat);
        var carol = await StartNodeAsync("tps-carol", "nltg-tps-carol", true, ct);
        await alice.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await bob.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await carol.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(alice, bob, ct);
        var channelId = await OpenPublicTaprootChannelAsync(alice, bob, ct);
        var (openAlice, _) = await Day0Harness.MineUntilUsableAsync(_fixture, [], alice, bob, channelId, ct);
        var openScid = openAlice.ShortChannelId!.Value;
        await MineUntilAnnouncedAsync([alice, bob], openScid, [alice, bob], ct);
        await carol.ConnectToAsync(bob, ct);
        var carolChannel = await carol.OpenChannelAsync(new OpenChannelClientRequest(bob.Address,
                                                            LightningMoney.Satoshis(CarolChannelSat)), ct);
        await Day0Harness.MineUntilUsableAsync(_fixture, [], carol, bob, carolChannel.ChannelId, ct);
        await MineUntilAnnouncedAsync([carol], openScid, [alice, bob], ct);

        // Act 1: alice splices in (NL-1131: refused before), mined until locked both ways
        await Day0Harness.WaitSettledAsync(alice, channelId, ct);
        var spliceIn = await Day0Harness.SpliceInAsync(alice, channelId, SpliceInSat, ct);
        var spliceInTxId = Day0Harness.AssertSigned(spliceIn);
        await Day0Harness.WaitInMempoolAsync(_fixture, spliceInTxId, ct);
        var (lockedIn, _) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, [], alice, bob, channelId,
                                                                        spliceInTxId, ct);
        var spliceInScid = lockedIn.ShortChannelId!.Value;
        Assert.NotEqual(openScid, spliceInScid);
        Assert.Contains(lockedIn.RetiredShortChannelIds, r => r.ShortChannelId == openScid);

        // Act 2: inside the 72-block window, carol pays alice over the OLD scid (alice's invoice re-signed with a
        // route hint naming it; carol's graph has no usable route to alice before the splice is announced)
        var (oldScidPayment, oldScidHash) = await PayOverHintAsync(carol, alice, bob, openScid, ct);

        // Assert 2: bob forwarded it through the retired map onto the channel
        Assert.Equal(PaymentStatus.Succeeded, oldScidPayment.Status);
        var retiredForward = await WaitForwardFulfilledAsync(bob, oldScidHash, ct);
        Assert.Equal(channelId, retiredForward.OutgoingChannelId);

        // Act 3: mined until every graph holds the splice's scid as a v2 channel
        await MineUntilAnnouncedAsync([alice, bob, carol], spliceInScid, [alice, bob], ct);

        // Assert 3: the new channel_announcement_2 checks against the splice's P2TR output, the old scid is spent
        await AssertSpliceAnnouncedAsync([alice, bob, carol], [alice, bob], spliceInScid, lockedIn, openScid, ct);

        // Act 4: bob splices out to a bitcoind address, mined until locked and announced again
        await Day0Harness.WaitSettledAsync(bob, channelId, ct);
        var address = await _fixture.Bitcoin.GetNewAddressAsync(ct);
        var spliceOut = await Day0Harness.SpliceOutAsync(bob, channelId, SpliceOutSat, address.ToString(), ct);
        var spliceOutTxId = Day0Harness.AssertSigned(spliceOut);
        await Day0Harness.WaitInMempoolAsync(_fixture, spliceOutTxId, ct);
        var (lockedOut, _) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, [], alice, bob, channelId,
                                                                         spliceOutTxId, ct);
        var spliceOutScid = lockedOut.ShortChannelId!.Value;
        Assert.NotEqual(spliceInScid, spliceOutScid);
        Assert.InRange(lockedIn.Capacity.Satoshi - (long)SpliceOutSat - lockedOut.Capacity.Satoshi, 0, 20_000);
        await MineUntilAnnouncedAsync([alice, bob, carol], spliceOutScid, [alice, bob], ct);
        await AssertSpliceAnnouncedAsync([alice, bob, carol], [alice, bob], spliceOutScid, lockedOut, spliceInScid,
                                         ct);

        // Act 5: carol pays alice's hint-free invoice from her graph
        await Day0Harness.WaitSettledAsync(alice, channelId, ct);
        var invoice = await CreateHintFreeInvoiceAsync(alice, ct);
        var payment = await carol.PayInvoiceAsync(invoice.Bolt11!, ct);

        // Assert 5: over carol -> bob -> alice by the latest scid
        Console.WriteLine($"carol's payment: {payment.Status}, fee {payment.Fee.MilliSatoshi} msat, "
                        + $"{payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var forward = await WaitForwardFulfilledAsync(bob, invoice.PaymentHash, ct);
        Assert.Equal(spliceOutScid, forward.OutgoingShortChannelId);
        Assert.Equal(carolChannel.ChannelId, forward.IncomingChannelId);
    }

    [Fact]
    public async Task Given_APeerWithoutGossipV2_When_APublicTaprootChannelIsAsked_Then_EitherSideRefusesItWithTheReason()
    {
        // Arrange: alice with taproot gossip, dave with simple taproot channels but without option_gossip_v2
        var ct = TestContext.Current.CancellationToken;
        var alice = await StartNodeAsync("tpr-v2-alice", "nltg-tpr-v2", true, ct);
        var dave = await StartNodeAsync("tpr-v1-dave", "nltg-tpr-v1", false, ct);
        await alice.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await dave.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await alice.ConnectToAsync(dave, ct);

        // Act: openchannel --public --channel-type taproot both ways
        var toDave = await Assert.ThrowsAsync<ClientException>(
                         () => Day0Harness.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                             alice, PublicTaprootRequest(dave), ct));
        var toAlice = await Assert.ThrowsAsync<ClientException>(
                          () => Day0Harness.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                              dave, PublicTaprootRequest(alice), ct));

        // Assert: refused with the reason, and nothing was opened
        Console.WriteLine($"alice -> dave: {toDave.Message}");
        Console.WriteLine($"dave -> alice: {toAlice.Message}");
        Assert.Contains("option_gossip_v2", toDave.Message, StringComparison.Ordinal);
        Assert.Contains("peer did not negotiate", toDave.Message, StringComparison.Ordinal);
        Assert.Contains("Features:OptionGossipV2=Optional", toAlice.Message, StringComparison.Ordinal);
        Assert.Empty((await alice.ListChannelsAsync(ct)).Channels);
        Assert.Empty((await dave.ListChannelsAsync(ct)).Channels);
        Assert.True(alice.IsConnectedTo(dave.NodeId));
    }

    /// <summary>
    /// A started node with simple taproot channels, taproot gossip when <paramref name="gossipV2"/>, public channels
    /// accepted, its own gossip flushed every 5 s, a 5 s invoice route-hint grace period, and
    /// <paramref name="acceptContributionSat"/> contributed to a peer's dual-funded open.
    /// </summary>
    private async Task<NLightningTestNode> StartNodeAsync(string name, string alias, bool gossipV2,
                                                          CancellationToken ct, long acceptContributionSat = 0,
                                                          GossipTrafficRecorder? traffic = null)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, configureNodeOptions: o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.OptionSimpleTaproot = FeatureSupport.Optional;
            o.Features.OptionGossipV2 = gossipV2 ? FeatureSupport.Optional : FeatureSupport.No;
        });
        await GossipTestNodes.StartGossipNodeAsync(node, alias, ct, n =>
        {
            n.ExtraConfiguration["Node:DualFund:AcceptContributionSat"] =
                acceptContributionSat.ToString(CultureInfo.InvariantCulture);
            n.ExtraConfiguration["Gossip:OwnGossipFlushInterval"] = "00:00:05";
            n.ExtraConfiguration[$"{InvoiceOptions.SectionName}:{nameof(InvoiceOptions.PublicChannelGracePeriod)}"] =
                "00:00:05";
            if (traffic is not null)
                n.ConfigureServices = traffic.Install;
        });
        _nodes.Add(node);

        var features = node.Services.GetRequiredService<IOptions<NodeOptions>>().Value.Features;
        Assert.True(features.IsSimpleTaprootAdvertised, $"{name} does not advertise option_simple_taproot");
        Assert.Equal(gossipV2, features.IsGossipV2Advertised);
        return node;
    }

    private static OpenChannelClientRequest PublicTaprootRequest(NLightningTestNode peer) =>
        new(peer.Address, LightningMoney.Satoshis(OpenerFundingSat)) { IsPublic = true, IsSimpleTaproot = true };

    /// <summary>
    /// <c>openchannel &lt;peer&gt; 600000 --public --channel-type taproot</c> as the operator types it (no
    /// <c>--v1</c>, no push: a dual-funded open by the NL-551 rules); returns once the funding is published.
    /// </summary>
    private async Task<ChannelId> OpenPublicTaprootChannelAsync(NLightningTestNode opener, NLightningTestNode peer,
                                                                CancellationToken ct)
    {
        var opened = await Day0Harness.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         opener, PublicTaprootRequest(peer), ct);
        Console.WriteLine($"[{opener.Name}] public taproot channel {opened.ChannelId} to {peer.Name}, funding "
                        + Day0Harness.Display(opened.FundingTxId));
        Assert.NotNull(opened.FundingTxId);
        await Day0Harness.WaitInMempoolAsync(_fixture, Day0Harness.ToUint256(opened.FundingTxId.Value), ct);
        return opened.ChannelId;
    }

    /// <summary>
    /// Mines one block every <see cref="s_blockEvery"/> (up to <paramref name="maxBlocks"/>) until every graph of
    /// <paramref name="observers"/> holds <paramref name="scid"/> as a v2 channel with both <c>channel_update_2</c>s
    /// and a <c>node_announcement_2</c> of every node of <paramref name="ends"/>.
    /// </summary>
    private async Task MineUntilAnnouncedAsync(IReadOnlyList<NLightningTestNode> observers, ShortChannelId scid,
                                               IReadOnlyList<NLightningTestNode> ends, CancellationToken ct,
                                               int maxBlocks = AnnouncementDepth)
    {
        await GossipGraphProbe.MineUntilAsync(async () =>
        {
            foreach (var observer in observers)
                if (!await HasV2ChannelAsync(observer, scid, ends))
                    return false;

            return true;
        }, () => ChainSync.MineAndWaitAsync(_fixture, 1, [], observers, ct), s_blockEvery, maxBlocks, s_graphTimeout,
                                              $"{string.Join(", ", observers.Select(o => o.Name))} hold {scid} as a "
                                            + "v2 channel with both updates and both node announcements", ct);
    }

    private static async Task<bool> HasV2ChannelAsync(NLightningTestNode observer, ShortChannelId scid,
                                                      IReadOnlyList<NLightningTestNode> ends)
    {
        var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(observer, scid.ToUInt64());
        Console.WriteLine($"[{observer.Name}] graph {scid}: "
                        + (channel is null
                               ? "absent"
                               : $"versions {channel.Versions}, v2 policies {channel.Policy1V2 is not null}/"
                               + $"{channel.Policy2V2 is not null}, verification {channel.Verification}"));
        if (channel is not { HasV2: true, Policy1V2: not null, Policy2V2: not null })
            return false;

        foreach (var end in ends)
            if (await GossipGraphProbe.TryGetOurGraphNodeAsync(observer, end.NodeId) is not { HasV2: true })
                return false;

        return true;
    }

    /// <summary>
    /// <paramref name="observer"/>'s graph channel <paramref name="scid"/>: announced with <c>channel_announcement_2</c>
    /// only (no BOLT 7 announcement or policy), both <c>channel_update_2</c>s, and a <c>node_announcement_2</c> of each
    /// end.
    /// </summary>
    private static async Task<GraphChannel> AssertV2ChannelAsync(NLightningTestNode observer, ShortChannelId scid,
                                                                 IReadOnlyList<NLightningTestNode> ends)
    {
        var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(observer, scid.ToUInt64());
        Assert.NotNull(channel);
        Assert.Equal(GraphGossipVersions.V2, channel.Versions);
        Assert.True(channel.RawAnnouncement.IsEmpty);
        Assert.False(channel.RawAnnouncement2.IsEmpty);
        Assert.Null(channel.Policy1);
        Assert.Null(channel.Policy2);
        Assert.NotNull(channel.Policy1V2);
        Assert.NotNull(channel.Policy2V2);
        Assert.Null(channel.SpentAtHeight);
        foreach (var end in ends)
        {
            Assert.Contains(end.NodeId, new[] { channel.NodeId1, channel.NodeId2 });
            var node = await GossipGraphProbe.TryGetOurGraphNodeAsync(observer, end.NodeId);
            Assert.NotNull(node);
            Assert.True(node.HasV2, $"{observer.Name} has no node_announcement_2 of {end.Name}");
            Assert.False(node.RawAnnouncement2.IsEmpty);
        }

        return channel;
    }

    /// <summary>
    /// The funding output <paramref name="scid"/> names, as <paramref name="node"/>'s <c>FundingOutputLookup</c> reads
    /// it from bitcoind: found, the channel's funding transaction, a P2TR script (<c>OP_1 &lt;32 bytes&gt;</c>).
    /// </summary>
    private static async Task<byte[]> AssertP2TrFundingOutputAsync(NLightningTestNode node, ShortChannelId scid,
                                                                   Domain.Bitcoin.ValueObjects.TxId fundingTxId,
                                                                   CancellationToken ct)
    {
        var lookup = await node.Services.GetRequiredService<IFundingOutputLookup>().LookupAsync(scid, ct);
        Assert.Equal(FundingOutputStatus.Found, lookup.Status);
        Assert.Equal(fundingTxId, lookup.TransactionId);
        Assert.True(lookup.Confirmations >= AnnouncementDepth);
        var script = Assert.IsType<byte[]>(lookup.ScriptPubKey);
        Assert.Equal(34, script.Length);
        Assert.Equal(0x51, script[0]);
        Assert.Equal(0x20, script[1]);
        return script;
    }

    /// <summary>
    /// <c>createinvoice</c> on <paramref name="payee"/> until the invoice carries no route hint (its announced channel
    /// can receive and has been in its graph for the grace period).
    /// </summary>
    private static Task<InvoiceInfoClientResponse> CreateHintFreeInvoiceAsync(NLightningTestNode payee,
                                                                             CancellationToken ct) =>
        Poll.ForAsync(async () =>
        {
            var created = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), "taproot gossip e2e", ct);
            var hints = Invoice.Decode(created.Bolt11!, BitcoinNetwork.Regtest).RouteHints.Count;
            Console.WriteLine($"[{payee.Name}] invoice {created.Bolt11}: {hints} route hints");
            return hints == 0 ? created : null;
        }, s_graphTimeout, $"{payee.Name}'s invoice without route hints", ct, TimeSpan.FromSeconds(2));

    /// <summary>
    /// The fee <paramref name="forwarder"/>'s <c>channel_update_2</c> on <paramref name="channel"/> charges for
    /// forwarding <paramref name="amountMsat"/>.
    /// </summary>
    private static ulong GraphPolicyFee(GraphChannel channel, NLightningTestNode forwarder, long amountMsat)
    {
        var policy = channel.GetPolicy(channel.GetDirectionFrom(forwarder.NodeId), 2);
        Assert.NotNull(policy);
        return policy.FeeBaseMsat + (ulong)amountMsat * policy.FeeProportionalMillionths / 1_000_000;
    }

    /// <summary>
    /// Every graph of <paramref name="observers"/> holds <paramref name="scid"/> as a v2 channel of the splice
    /// <paramref name="locked"/> runs on (capacity, the MuSig2 proof against the splice's P2TR output from bitcoind) and
    /// marks <paramref name="previous"/> spent at the splice's block (BOLT 7: forgotten 72 blocks later).
    /// </summary>
    private static async Task AssertSpliceAnnouncedAsync(IReadOnlyList<NLightningTestNode> observers,
                                                         IReadOnlyList<NLightningTestNode> ends, ShortChannelId scid,
                                                         ChannelInfoClientResponse locked, ShortChannelId previous,
                                                         CancellationToken ct)
    {
        foreach (var node in observers)
        {
            var stored = await AssertV2ChannelAsync(node, scid, ends);
            Assert.Equal((ulong)locked.Capacity.Satoshi, stored.CapacitySat);
            var fundingScript = await AssertP2TrFundingOutputAsync(node, scid, locked.FundingTxId!.Value, ct);
            var announcement = ChannelAnnouncement2Payload.Parse(stored.RawAnnouncement2.Span);
            Assert.Equal(locked.FundingTxId!.Value, announcement.FundingTxId);
            Assert.Equal(GossipV2ProofResult.Valid,
                         node.Services.GetRequiredService<IGossipV2SignatureVerifier>()
                             .CheckChannelProof(announcement, fundingScript));
            var spent = await Poll.ForAsync(async () =>
            {
                var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(node, previous.ToUInt64());
                return channel?.SpentAtHeight is not null ? channel : null;
            }, s_graphTimeout, $"{node.Name}'s graph marks {previous} spent", ct, GossipGraphProbe.PollInterval);
            Assert.Equal(scid.BlockHeight, spent.SpentAtHeight);
            Console.WriteLine($"[{node.Name}] {scid}: v2, {stored.Verification}; {previous} spent at "
                            + spent.SpentAtHeight);
        }
    }

    /// <summary>
    /// <paramref name="payer"/> pays a fresh invoice of <paramref name="payee"/> re-signed with one route hint, from
    /// <paramref name="forwarder"/> over <paramref name="scid"/> at the forwarder's policy on the channel.
    /// </summary>
    private static async Task<(PaymentInfoClientResponse Payment, Domain.Crypto.ValueObjects.Hash PaymentHash)>
        PayOverHintAsync(NLightningTestNode payer, NLightningTestNode payee, NLightningTestNode forwarder,
                         ShortChannelId scid, CancellationToken ct)
    {
        var created = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), "retired scid", ct);
        var invoice = Invoice.Decode(created.Bolt11!, BitcoinNetwork.Regtest);
        var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(payee, scid.ToUInt64());
        var policy = channel?.GetPolicy(channel.GetDirectionFrom(forwarder.NodeId), 2);
        invoice.RoutingInfos =
        [
            new Domain.Models.RoutingInfo(forwarder.NodeId, scid, (uint)(policy?.FeeBaseMsat ?? 1_000),
                                          policy?.FeeProportionalMillionths ?? 1_000,
                                          policy?.CltvExpiryDelta ?? 144)
        ];
        var nodeKey = payee.SecureKeyManager.GetNodeKeyPair();
        using var key = new Key(nodeKey.PrivKey.Value.ToArray());
        var bolt11 = invoice.Encode(key);
        Console.WriteLine($"[{payee.Name}] invoice re-signed with a hint over {scid}: {bolt11}");
        var payment = await payer.PayInvoiceAsync(bolt11, ct);
        Console.WriteLine($"[{payer.Name}] payment over {scid}: {payment.Status}, {payment.FailureReason}");
        return (payment, created.PaymentHash);
    }

    private static Task<ForwardInfoClientResponse> WaitForwardFulfilledAsync(
        NLightningTestNode forwarder, Domain.Crypto.ValueObjects.Hash paymentHash, CancellationToken ct) =>
        Poll.ForAsync(async () =>
        {
            var current = await forwarder.GetForwardAsync(paymentHash, ct);
            return current?.Status == ForwardCircuitStatus.Fulfilled ? current : null;
        }, Day0Harness.StepTimeout, $"{forwarder.Name}'s forward fulfilled", ct);

    /// <summary>The confirmations of the funding transaction <paramref name="scid"/> names.</summary>
    private async Task<int> FundingDepthAsync(ShortChannelId scid, CancellationToken ct) =>
        (int)(await _fixture.Bitcoin.GetBlockCountAsync(ct) - scid.BlockHeight + 1);

    /// <summary>
    /// Mines one block at a time until bitcoind no longer lists the funding output as unspent; returns the height of
    /// the block that spent it.
    /// </summary>
    private async Task<uint> MineUntilFundingSpentAsync(ChannelInfoClientResponse channel,
                                                        IReadOnlyList<NLightningTestNode> nodes, CancellationToken ct)
    {
        var fundingTxId = Day0Harness.ToUint256(channel.FundingTxId!.Value);
        var index = (int)channel.FundingOutputIndex!.Value;
        // The closing transactions are published after closechannel returns: wait for one in the mempool
        await Poll.UntilAsync(async () =>
        {
            foreach (var txId in await _fixture.Bitcoin.GetRawMempoolAsync(ct))
                if (await SpendsAsync(txId, fundingTxId, index, ct))
                    return true;

            return false;
        }, Day0Harness.StepTimeout, "a closing transaction in the mempool", ct, TimeSpan.FromMilliseconds(500));
        for (var block = 1; block <= Day0Harness.MaxBlocks; block++)
        {
            var tip = await ChainSync.MineAndWaitAsync(_fixture, 1, [], nodes, ct);
            if (await _fixture.Bitcoin.GetTxOutAsync(fundingTxId, index, includeMempool: false) is null)
            {
                Console.WriteLine($"The funding output {fundingTxId}:{index} was spent in block {tip}");
                return tip;
            }
        }

        throw new TimeoutException($"The funding output {fundingTxId}:{index} is unspent after "
                                 + $"{Day0Harness.MaxBlocks} blocks");
    }

    private async Task<bool> SpendsAsync(uint256 txId, uint256 fundingTxId, int index, CancellationToken ct)
    {
        var tx = await _fixture.Bitcoin.GetRawTransactionAsync(txId, true, ct);
        return tx.Inputs.Any(i => i.PrevOut.Hash == fundingTxId && i.PrevOut.N == index);
    }

    private static ChannelModel Channel(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{node.Name} has no channel {channelId}");
}