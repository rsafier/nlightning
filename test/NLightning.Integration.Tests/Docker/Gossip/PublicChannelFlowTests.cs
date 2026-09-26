using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 7 plan Proof G1 (public channels) against LND 0.20: (a) a public channel we open to alice is announced (both
/// policies at alice, relayed to bob, our <c>node_announcement</c> with alias and color at bob); (b) a public channel
/// alice opens to us is announced once we send our <c>announcement_signatures</c> at 6 confirmations; (c) a restart of
/// our node between confirmation 3 and 6 does not stop the announcement.
/// </summary>
/// <remarks>
/// <para>Each test uses its own node (fresh key) and its own channel, and asserts only on that channel and node.</para>
/// <para>(d) (NL-255 re-check): once we are public, david's private-channel invoice hints through us.</para>
/// <para>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture).</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class PublicChannelFlowTests
{
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_blockEvery = TimeSpan.FromSeconds(15);

    private readonly LightningRegtestNetworkFixture _fixture;

    public PublicChannelFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    /// <summary>Proof G1 (a).</summary>
    [Fact]
    public async Task Given_WeOpenPublicChannelToAlice_When_SixConfirmations_Then_AliceAndBobHaveTheChannelAndOurNode()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var bob = _fixture.GetLndNode("bob");
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(alice, ct)}");
        const string alias = "nltg-g1a";
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g1a", alias, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);

        // Act: a public channel, 6 blocks (OpenChannelAsync mines them after funding_signed)
        var opened = await node.OpenChannelAsync(GossipTestNodes.PublicChannelRequest(aliceAddress, s_capacity), ct);
        var scid = await WaitForShortChannelIdAsync(node, opened.ChannelId, ct);
        Console.WriteLine($"Public channel {opened.ChannelId} to alice: {new ShortChannelId(scid)} ({scid})");

        // Assert
        await AssertAnnouncedAsync(node, alice, bob, scid, ct);
        await AssertOurNodeAnnouncedAsync(node, bob, alias, ct);
    }

    /// <summary>Proof G1 (b).</summary>
    [Fact]
    public async Task Given_AliceOpensPublicChannelToUs_When_SixConfirmations_Then_WeSignAndBobHasTheChannel()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var bob = _fixture.GetLndNode("bob");
        const string alias = "nltg-g1b";
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g1b", alias, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);
        await node.ConnectToAsync(alice, ct);

        // Act: alice funds a public channel to us (we accept it: Gossip:AcceptPublicChannels)
        var point = await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])node.NodeId),
            LocalFundingAmount = (long)s_capacity.Satoshi,
            Private = false
        }, cancellationToken: ct);
        Console.WriteLine(
            $"alice opened a public channel to us: {Convert.ToHexStringLower(point.FundingTxidBytes.ToByteArray().Reverse().ToArray())}:{point.OutputIndex}");
        var ours = await Poll.ForAsync(async () =>
        {
            var channels = await node.ListChannelsAsync(ct);
            return channels.Channels.Count == 1 ? channels.Channels[0] : null;
        }, s_timeout, "our end of alice's public channel", ct);
        await ChainSync.MineAndWaitAsync(_fixture, 6, _fixture.LndNodes, [node], ct);
        var scid = await WaitForShortChannelIdAsync(node, ours.ChannelId, ct);
        Console.WriteLine($"alice's public channel {ours.ChannelId}: {new ShortChannelId(scid)} ({scid})");

        // Assert: bob sees the channel only if both sides signed it, so our announcement_signatures reached alice
        await AssertAnnouncedAsync(node, alice, bob, scid, ct);
        await AssertOurNodeAnnouncedAsync(node, bob, alias, ct);
    }

    /// <summary>Proof G1 (c).</summary>
    [Fact]
    public async Task Given_PublicChannelAt3Confirmations_When_WeRestart_Then_TheAnnouncementStillCompletes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var bob = _fixture.GetLndNode("bob");
        const string alias = "nltg-g1c";
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g1c", alias, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);
        var opened = await GossipTestNodes.OpenUntilFundingSignedAsync(
                         node, GossipTestNodes.PublicChannelRequest(aliceAddress, s_capacity), ct);
        await ChainSync.MineAndWaitAsync(_fixture, 3, _fixture.LndNodes, [node], ct);
        Console.WriteLine($"Public channel {opened.ChannelId} to alice at 3 confirmations: restarting");

        // Act: restart between confirmation 3 and 6, then the remaining 3 blocks
        await node.StopAsync();
        await node.StartAsync(ct);
        await Poll.UntilAsync(() => node.IsConnectedTo(alice.LocalNodePubKeyBytes), s_timeout,
                              "reconnected to alice (a peer with channels)", ct);
        await ChainSync.MineAndWaitAsync(_fixture, 3, _fixture.LndNodes, [node], ct);
        var scid = await WaitForShortChannelIdAsync(node, opened.ChannelId, ct);
        Console.WriteLine($"Public channel {opened.ChannelId}: {new ShortChannelId(scid)} ({scid})");

        // Assert
        await AssertAnnouncedAsync(node, alice, bob, scid, ct);
        await AssertOurNodeAnnouncedAsync(node, bob, alias, ct);
    }

    /// <summary>
    /// Proof G1 (d), NL-255 re-check: once our node is public (an announced channel to alice and our
    /// <c>node_announcement</c>), david's private-channel invoice (<c>addinvoice --private</c>) over a private channel
    /// we opened to him hints through us. Before G1, LND never hinted through a node it had no announcement of.
    /// </summary>
    /// <remarks>
    /// Plan: "unverified LND behaviour; record the result": the hint david chose (or none) is logged before the
    /// assertion. david learns our announcements from us directly (we relay our own gossip to every connected peer,
    /// G1-T7).
    /// </remarks>
    [Fact]
    public async Task Given_OurNodeIsPublic_When_DavidInvoicesOverOurPrivateChannel_Then_HisInvoiceHintsThroughUs()
    {
        // Arrange: a public channel to alice (announced), then a private channel we fund to david
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var bob = _fixture.GetLndNode("bob");
        var david = _fixture.GetLndNode("david");
        const string alias = "nltg-g1d";
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g1d", alias, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(3_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);
        var publicChannel = await node.OpenChannelAsync(GossipTestNodes.PublicChannelRequest(aliceAddress, s_capacity),
                                                        ct);
        var publicScid = await WaitForShortChannelIdAsync(node, publicChannel.ChannelId, ct);
        await AssertAnnouncedAsync(node, alice, bob, publicScid, ct);

        var davidAddress = await node.ConnectToAsync(david, ct);
        var privateChannel = await node.OpenChannelAsync(new OpenChannelClientRequest(davidAddress, s_capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        await WaitForShortChannelIdAsync(node, privateChannel.ChannelId, ct);
        var davidsChannel = await Poll.ForAsync(
                                () => LndTestHelpers.GetChannelByPointAsync(david, privateChannel.ChannelPoint(), ct),
                                s_timeout, "david lists our private channel", ct, GossipGraphProbe.PollInterval);
        Assert.True(davidsChannel.Private);
        await Poll.ForAsync(() => GossipGraphProbe.TryGetNodeInfoAsync(david, node.NodeIdHex, ct), s_timeout,
                            "david has our node_announcement", ct, GossipGraphProbe.PollInterval);
        await Poll.UntilAsync(async () => (await LndTestHelpers.GetChannelByPointAsync(
                                               david, privateChannel.ChannelPoint(), ct))?.Active == true,
                              s_timeout, "david's end of our private channel active", ct,
                              GossipGraphProbe.PollInterval);

        // Act
        var invoice = await LndTestHelpers.AddInvoiceAsync(david, 10_000_000, [], ct, "G1 (d)", addPrivateHints: true);
        var decoded = await david.LightningClient.DecodePayReqAsync(
                          new PayReqString { PayReq = invoice.PaymentRequest }, cancellationToken: ct);

        // Assert: a one-hop hint from our node over our private channel (LND names it by its SCID or an alias)
        var davidsIds = new HashSet<ulong>(davidsChannel.AliasScids) { davidsChannel.ChanId, davidsChannel.PeerScidAlias };
        foreach (var hint in decoded.RouteHints)
            Console.WriteLine("NL-255 result: david's hint " + string.Join(
                                  " -> ", hint.HopHints.Select(h => $"{h.NodeId[..16]}…/{h.ChanId}"
                                                                  + $" (base {h.FeeBaseMsat}, ppm "
                                                                  + $"{h.FeeProportionalMillionths}, delta "
                                                                  + $"{h.CltvExpiryDelta})")));
        Console.WriteLine($"NL-255 result: {decoded.RouteHints.Count} hints; our channel is {string.Join("/", davidsIds)}");
        Assert.Contains(decoded.RouteHints.SelectMany(h => h.HopHints),
                        h => string.Equals(h.NodeId, node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                          && davidsIds.Contains(h.ChanId));
    }

    /// <summary>
    /// Waits until the channel is usable and has its real short channel id; returns it in LND's <c>u64</c> form.
    /// </summary>
    private async Task<ulong> WaitForShortChannelIdAsync(NLightningTestNode node, ChannelId channelId,
                                                         CancellationToken ct)
    {
        ChannelInfoClientResponse? last = null;
        try
        {
            var channel = await Poll.ForAsync(async () =>
            {
                last = await node.GetChannelAsync(channelId, ct);
                return last.IsUsable() && last.ShortChannelId is not null ? last : null;
            }, s_timeout, "channel usable with a short channel id", ct, GossipGraphProbe.PollInterval);
            return channel.ShortChannelId!.Value.ToUInt64();
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"Channel: {last?.Describe()}");
            throw;
        }
    }

    /// <summary>
    /// alice has the channel with both policies (ours included, not disabled), and bob, who got it relayed by alice,
    /// lists it in <c>DescribeGraph</c>. Mines a block now and then: both ends announce at 6 confirmations.
    /// </summary>
    private async Task AssertAnnouncedAsync(NLightningTestNode node, LNDNodeConnection alice, LNDNodeConnection bob,
                                            ulong scid, CancellationToken ct)
    {
        await GossipGraphProbe.MineUntilAsync(
            async () => await GossipGraphProbe.TryGetChanInfoAsync(alice, scid, ct) is
            { Node1Policy: not null, Node2Policy: not null },
            () => ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, [node], ct), s_blockEvery, 6, s_timeout,
            "alice has our public channel with both policies", ct);

        var edge = await GossipGraphProbe.TryGetChanInfoAsync(alice, scid, ct);
        Assert.NotNull(edge);
        Assert.Contains(node.NodeIdHex, new[] { edge.Node1Pub, edge.Node2Pub }, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(alice.LocalNodePubKey, new[] { edge.Node1Pub, edge.Node2Pub }, StringComparer.OrdinalIgnoreCase);
        Assert.Equal((long)s_capacity.Satoshi, edge.Capacity);
        var ourPolicy = string.Equals(edge.Node1Pub, node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                            ? edge.Node1Policy
                            : edge.Node2Policy;
        Assert.False(ourPolicy.Disabled);

        await Poll.UntilAsync(() => GossipGraphProbe.GraphHasChannelAsync(bob, scid, ct), s_timeout,
                              "bob's graph has our public channel (relayed by alice)", ct, GossipGraphProbe.PollInterval);
    }

    /// <summary>
    /// bob has our <c>node_announcement</c> with the alias and color we configured.
    /// </summary>
    private static async Task AssertOurNodeAnnouncedAsync(NLightningTestNode node, LNDNodeConnection bob,
                                                          string alias, CancellationToken ct)
    {
        var announced = await Poll.ForAsync(() => GossipGraphProbe.TryGetNodeInfoAsync(bob, node.NodeIdHex, ct),
                                            s_timeout, "bob has our node_announcement", ct,
                                            GossipGraphProbe.PollInterval);
        Assert.Equal(alias, announced.Alias);
        Assert.Equal(GossipTestNodes.Color, announced.Color, StringComparer.OrdinalIgnoreCase);
    }
}