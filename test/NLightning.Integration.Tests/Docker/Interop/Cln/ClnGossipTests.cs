using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Fixtures;
using Gossip;
using Utils;

/// <summary>
/// BOLT 7 interop with Core Lightning (<see cref="ClnFixture.ClnTag"/>): plan Proof G3 (d) and the CLN goal proof
/// (e). Public channels both ways: N1 funds a public channel to CLN, CLN funds a public channel to N2 (two of our
/// nodes). CLN's <c>listchannels</c> shows both with our policies and <c>listnodes</c> our aliases; our graph stores
/// CLN's <c>channel_announcement</c>, <c>channel_update</c>s and <c>node_announcement</c>; CLN queries our SCIDs
/// (<c>dev-query-scids</c>) and we answer with <c>full_information = 1</c>; and N1 pays N2's hint-free invoice through
/// CLN at exactly CLN's announced fee, and CLN pays N2's hint-free invoice.
/// </summary>
/// <remarks>
/// <para>The topology is built once per fixture (<see cref="ClnGossipTopology"/>, cached with its failure like
/// <see cref="ClnChannelSession"/>); each test asserts only on it.</para>
/// <para>Needs gossip wave G-C (lane C1 query answers and relay of others' gossip, lane C2 graph paths in payments);
/// written against the plan in parallel with them.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnGossipTests : IAsyncLifetime
{
    private static readonly TimeSpan s_gossipTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromMinutes(1);

    private readonly ClnFixture _fixture;
    private ClnGossipTopology? _topology;

    public ClnGossipTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private ClnClient Cln => _fixture.Cln;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Console.WriteLine($"CLN version: {(await Cln.GetInfoAsync(ct))["version"]}");
        _topology = await ClnGossipTopology.GetAsync(_fixture, ct);
        await _topology.WaitUsableAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        var unusual = await Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 20, "unusual");
        if (!string.IsNullOrWhiteSpace(unusual))
            Console.WriteLine($"CLN UNUSUAL/BROKEN lines:\n{unusual}");
    }

    /// <summary>Proof G3 (d), first half: CLN accepted our announcements.</summary>
    [Fact]
    public async Task Given_PublicChannelsBothWays_When_Announced_Then_ClnListsOurPoliciesAndAliases()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var topology = _topology!;

        foreach (var (node, scid, policy, alias) in topology.OurEnds)
        {
            // Act
            var ours = await Poll.ForAsync(async () => (await ListClnChannelAsync(scid, ct))
                                              .FirstOrDefault(c => c?["source"]?.GetValue<string>() == node.NodeIdHex),
                                           s_gossipTimeout, $"CLN has {node.Name}'s policy on {ClnScid(scid)}", ct,
                                           GossipGraphProbe.PollInterval);
            var announced = await Poll.ForAsync(async () =>
            {
                var nodes = (await Cln.CallAsync("listnodes", ct, ("id", node.NodeIdHex)))["nodes"]!.AsArray();
                return nodes.FirstOrDefault(n => n?["alias"] is not null);
            }, s_gossipTimeout, $"CLN has {node.Name}'s node_announcement", ct, GossipGraphProbe.PollInterval);

            // Assert
            Console.WriteLine($"CLN listchannels {ClnScid(scid)} from {node.Name}: {ours!.ToJsonString()}");
            Console.WriteLine($"CLN listnodes {node.Name}: {announced!.ToJsonString()}");
            Assert.True(ours["public"]!.GetValue<bool>());
            Assert.True(ours["active"]!.GetValue<bool>());
            Assert.Equal(policy.FeeBaseMsat, ours["base_fee_millisatoshi"]!.GetValue<uint>());
            Assert.Equal(policy.FeePpm, ours["fee_per_millionth"]!.GetValue<uint>());
            Assert.Equal(policy.CltvExpiryDelta, ours["delay"]!.GetValue<uint>());
            Assert.Equal(alias, announced["alias"]!.GetValue<string>());
            Assert.Equal(GossipTestNodes.Color.TrimStart('#'), announced["color"]!.GetValue<string>(),
                         StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Proof G3 (d), second half: we stored CLN's announcements.</summary>
    [Fact]
    public async Task Given_PublicChannelsBothWays_When_Announced_Then_OurGraphHasClnsChannelsPoliciesAndNode()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var topology = _topology!;
        var n1 = topology.N1;

        // Act: N1's graph has both channels (its own and CLN-N2, relayed by CLN) with both policies, and CLN's node
        foreach (var scid in new[] { topology.N1Scid, topology.N2Scid })
            await Poll.UntilAsync(async () => await GossipGraphProbe.TryGetOurGraphChannelAsync(n1, scid) is
            { Policy1: not null, Policy2: not null },
                                  s_gossipTimeout, $"N1's graph has {ClnScid(scid)} with both policies", ct,
                                  GossipGraphProbe.PollInterval);
        var clnNode = await Poll.ForAsync(() => GossipGraphProbe.TryGetOurGraphNodeAsync(n1, topology.ClnId),
                                          s_gossipTimeout, "N1's graph has CLN's node_announcement", ct,
                                          GossipGraphProbe.PollInterval);

        // Assert: CLN's policy on CLN-N2 in our graph equals what CLN lists
        Assert.Equal("nltg-cln", clnNode.AliasText);
        var stored = (await GossipGraphProbe.TryGetOurGraphChannelAsync(n1, topology.N2Scid))!;
        var clnPolicy = await GetClnPolicyAsync(topology.N2Scid, ct);
        var ours = stored.GetPolicy(stored.GetDirectionFrom(topology.ClnId))!;
        Console.WriteLine($"CLN's policy on {ClnScid(topology.N2Scid)}: CLN {clnPolicy.ToJsonString()}, our graph "
                        + $"base {ours.FeeBaseMsat}, ppm {ours.FeeProportionalMillionths}, delta {ours.CltvExpiryDelta}");
        Assert.Equal(clnPolicy["base_fee_millisatoshi"]!.GetValue<uint>(), ours.FeeBaseMsat);
        Assert.Equal(clnPolicy["fee_per_millionth"]!.GetValue<uint>(), ours.FeeProportionalMillionths);
        Assert.Equal(clnPolicy["delay"]!.GetValue<uint>(), ours.CltvExpiryDelta);
        Assert.Equal((ulong?)ClnGossipTopology.Capacity.Satoshi, stored.CapacitySat);
    }

    /// <summary>Proof G3 (d), queries: CLN queries us and gets <c>full_information</c>.</summary>
    [Fact]
    public async Task Given_AnnouncedChannels_When_ClnQueriesOurScids_Then_WeAnswerWithFullInformation()
    {
        // Arrange: N1's graph has both channels
        var ct = TestContext.Current.CancellationToken;
        var topology = _topology!;
        foreach (var scid in new[] { topology.N1Scid, topology.N2Scid })
            await Poll.UntilAsync(async () => await GossipGraphProbe.TryGetOurGraphChannelAsync(topology.N1, scid) is
            { Policy1: not null, Policy2: not null },
                                  s_gossipTimeout, $"N1's graph has {ClnScid(scid)}", ct,
                                  GossipGraphProbe.PollInterval);
        var scids = new JsonArray(ClnScid(topology.N1Scid), ClnScid(topology.N2Scid));

        // Act: a developer command (the fixture runs CLN with --developer); retried while our initial sync with CLN
        // has not completed (full_information is 0 until then, G3-T1)
        var result = await Poll.ForAsync(async () =>
        {
            var reply = await Cln.CallAsync("dev-query-scids", ct, ("id", topology.N1.NodeIdHex), ("scids", scids));
            Console.WriteLine($"CLN dev-query-scids: {reply.ToJsonString()}");
            return reply["complete"]?.GetValue<bool>() == true ? reply : null;
        }, s_gossipTimeout, "our reply_short_channel_ids_end with full_information = 1", ct, TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(result["complete"]!.GetValue<bool>());
        Assert.True(topology.N1.IsConnectedTo(topology.ClnId));
    }

    /// <summary>Goal proof (e): we pay over public channels through CLN, without hints.</summary>
    [Fact]
    public async Task Given_OurNodesPublicThroughCln_When_N1PaysN2sInvoiceWithoutHints_Then_ClnChargesItsAnnouncedFee()
    {
        // Arrange: N1 knows CLN-N2 with CLN's policy; N2's invoice carries no route hint
        var ct = TestContext.Current.CancellationToken;
        var topology = _topology!;
        var (n1, n2) = (topology.N1, topology.N2);
        await Poll.UntilAsync(async () => await GossipGraphProbe.TryGetOurGraphChannelAsync(n1, topology.N2Scid) is
        { Policy1: not null, Policy2: not null },
                              s_gossipTimeout, "N1's graph has CLN-N2 with both policies", ct,
                              GossipGraphProbe.PollInterval);
        var amountMsat = PublicTopology.UniqueAmountMsat(20_000_000);
        var invoice = await n2.CreateInvoiceAsync(LightningMoney.MilliSatoshis(amountMsat), "goal (e) N1 -> N2", ct);
        await AssertNoRoutesAsync(invoice.Bolt11, ct);
        var clnPolicy = await GetClnPolicyAsync(topology.N2Scid, ct);
        var expectedFee = LndRoutingProbe.FeeFor(clnPolicy["base_fee_millisatoshi"]!.GetValue<ulong>(),
                                                 clnPolicy["fee_per_millionth"]!.GetValue<ulong>(), amountMsat);
        var n1Before = await PublicTopology.WaitSettledAsync(n1, topology.N1ChannelId, ct);
        var n2Before = await PublicTopology.WaitSettledAsync(n2, topology.N2ChannelId, ct);

        // Act
        var payment = await PublicTopology.PayInOnePartAsync(n1, invoice.Bolt11, ct);

        // Assert: paid through CLN at exactly its announced fee
        Console.WriteLine($"N1's payment: {payment.Status}, fee {payment.Fee.MilliSatoshi} (CLN's policy gives "
                        + $"{expectedFee}): {payment.FailureCode} at {payment.FailureSourceIndex} {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(expectedFee, payment.Fee.MilliSatoshi);
        var forward = (await Cln.CallAsync("listforwards", ct, ("status", "settled")))["forwards"]!.AsArray()
                                                                                               .LastOrDefault(f =>
                                                                                                    f?["out_msat"]
                                                                                                      ?.GetValue<ulong>()
                                                                                                 == amountMsat);
        Assert.NotNull(forward);
        Console.WriteLine($"CLN forward: {forward.ToJsonString()}");
        Assert.Equal(expectedFee, forward["fee_msat"]!.GetValue<ulong>());

        // Assert: N2 received exactly the amount, N1 paid amount + fee
        var received = await Poll.ForAsync(async () =>
        {
            var current = await n2.GetInvoiceAsync(invoice.PaymentHash, ct);
            return current?.Status == InvoiceStatus.Settled ? current : null;
        }, s_settleTimeout, "N2's invoice settled", ct);
        Assert.Equal(amountMsat, received.AmountReceived?.MilliSatoshi);
        var n1After = await PublicTopology.WaitSettledAsync(n1, topology.N1ChannelId, ct);
        var n2After = await PublicTopology.WaitSettledAsync(n2, topology.N2ChannelId, ct);
        Assert.Equal(n1Before.LocalBalance.MilliSatoshi - amountMsat - expectedFee, n1After.LocalBalance.MilliSatoshi);
        Assert.Equal(n2Before.LocalBalance.MilliSatoshi + amountMsat, n2After.LocalBalance.MilliSatoshi);
    }

    /// <summary>Goal proof (e): CLN pays our hint-free invoice over our public channel.</summary>
    [Fact]
    public async Task Given_PublicChannelFromCln_When_ClnPaysOurInvoiceWithoutHints_Then_WeReceiveTheExactAmount()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var topology = _topology!;
        var n2 = topology.N2;
        var amountMsat = PublicTopology.UniqueAmountMsat(15_000_000);
        var invoice = await n2.CreateInvoiceAsync(LightningMoney.MilliSatoshis(amountMsat), "goal (e) CLN -> N2", ct);
        await AssertNoRoutesAsync(invoice.Bolt11, ct);
        var before = await PublicTopology.WaitSettledAsync(n2, topology.N2ChannelId, ct);

        // Act
        JsonNode result;
        try
        {
            result = await Cln.CallAsync("xpay", ct, ("invstring", invoice.Bolt11), ("retry_for", 30));
        }
        catch (ClnRpcException e)
        {
            Assert.Fail($"CLN could not pay N2's invoice: {e.Message}\nCLN log:\n"
                      + await Cln.GetLogLinesAsync(n2.NodeIdHex[..16], ct));
            throw;
        }

        // Assert
        Console.WriteLine($"CLN xpay: {result.ToJsonString()}");
        var received = await Poll.ForAsync(async () =>
        {
            var current = await n2.GetInvoiceAsync(invoice.PaymentHash, ct);
            return current?.Status == InvoiceStatus.Settled ? current : null;
        }, s_settleTimeout, "N2's invoice settled", ct);
        Assert.Equal(amountMsat, received.AmountReceived?.MilliSatoshi);
        var after = await PublicTopology.WaitSettledAsync(n2, topology.N2ChannelId, ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + amountMsat, after.LocalBalance.MilliSatoshi);
    }

    /// <summary>
    /// CLN's <c>decode</c> of <paramref name="bolt11"/> has no <c>routes</c> (BOLT 11 <c>r</c>).
    /// </summary>
    /// <remarks>
    /// TODO(G-C integrator): our InvoiceService hints every Open channel with the peer's update, public ones too
    /// (NL-245); this fails until it skips announced channels.
    /// </remarks>
    private async Task AssertNoRoutesAsync(string bolt11, CancellationToken ct)
    {
        var decoded = await Cln.CallAsync("decode", ct, ("string", bolt11));
        Console.WriteLine($"CLN decode: routes {decoded["routes"]?.ToJsonString() ?? "none"}");
        Assert.True(decoded["routes"] is null || decoded["routes"]!.AsArray().Count == 0,
                    "the invoice carries route hints");
    }

    private async Task<JsonArray> ListClnChannelAsync(ulong scid, CancellationToken ct) =>
        (await Cln.CallAsync("listchannels", ct, ("short_channel_id", ClnScid(scid))))["channels"]!.AsArray();

    /// <summary>
    /// CLN's own policy on <paramref name="scid"/> (the <c>listchannels</c> entry whose source is CLN).
    /// </summary>
    private async Task<JsonNode> GetClnPolicyAsync(ulong scid, CancellationToken ct) =>
        await Poll.ForAsync(async () => (await ListClnChannelAsync(scid, ct))
                               .FirstOrDefault(c => c?["source"]?.GetValue<string>() == _fixture.ClnNodeId),
                            s_gossipTimeout, $"CLN lists its own policy on {ClnScid(scid)}", ct,
                            GossipGraphProbe.PollInterval) ?? throw new InvalidOperationException();

    /// <summary>
    /// CLN's <c>BLOCKxTXxOUT</c> form of a short channel id.
    /// </summary>
    internal static string ClnScid(ulong scid) => new ShortChannelId(scid).ToString();
}

/// <summary>
/// The public topology of <see cref="ClnGossipTests"/>, built once per <see cref="ClnFixture"/>: N1 funds a public
/// channel to CLN, CLN funds a public channel to N2; both announced on both ends.
/// </summary>
public sealed class ClnGossipTopology : IAsyncDisposable
{
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);

    /// <summary>What N1 and N2 announce in their <c>channel_update</c>s (distinct, and not our defaults).</summary>
    public static readonly (uint FeeBaseMsat, uint FeePpm, ushort CltvExpiryDelta) N1Policy = (1_111, 222, 44);

    public static readonly (uint FeeBaseMsat, uint FeePpm, ushort CltvExpiryDelta) N2Policy = (2_222, 333, 48);

    private const string CacheKey = "cln-gossip-topology";
    private const string N1Alias = "nltg-cln-g1";
    private const string N2Alias = "nltg-cln-g2";
    private static readonly TimeSpan s_buildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_announceTimeout = TimeSpan.FromMinutes(4);

    private readonly ClnFixture _fixture;

    private ClnGossipTopology(ClnFixture fixture, NLightningTestNode n1, NLightningTestNode n2)
    {
        _fixture = fixture;
        N1 = n1;
        N2 = n2;
    }

    public NLightningTestNode N1 { get; }
    public NLightningTestNode N2 { get; }
    public ChannelId N1ChannelId { get; private set; }
    public ChannelId N2ChannelId { get; private set; }
    public ulong N1Scid { get; private set; }
    public ulong N2Scid { get; private set; }
    public byte[] ClnId => Convert.FromHexString(_fixture.ClnNodeId);

    /// <summary>
    /// Our end of each channel with the policy and alias it announces.
    /// </summary>
    public IEnumerable<(NLightningTestNode Node, ulong Scid, (uint FeeBaseMsat, uint FeePpm, ushort CltvExpiryDelta)
        Policy, string Alias)> OurEnds =>
        [(N1, N1Scid, N1Policy, N1Alias), (N2, N2Scid, N2Policy, N2Alias)];

    public static Task<ClnGossipTopology> GetAsync(ClnFixture fixture, CancellationToken cancellationToken) =>
        ClnChannelSession.GetOrBuildDetachedAsync(factory => fixture.GetOrCreateAsync(CacheKey, factory),
                                                  ct => BuildAsync(fixture, ct), s_buildTimeout,
                                                  "CLN public gossip topology", cancellationToken);

    /// <summary>
    /// Starts a node a failed test left stopped and waits until both channels are usable with no HTLC in flight.
    /// </summary>
    public async Task WaitUsableAsync(CancellationToken cancellationToken)
    {
        foreach (var node in new[] { N1, N2 })
            if (!node.IsRunning)
                await node.StartAsync(cancellationToken);

        await PublicTopology.WaitSettledAsync(N1, N1ChannelId, cancellationToken);
        await PublicTopology.WaitSettledAsync(N2, N2ChannelId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await N1.DisposeAsync();
        await N2.DisposeAsync();
    }

    private static async Task<ClnGossipTopology> BuildAsync(ClnFixture fixture, CancellationToken ct)
    {
        var n1 = await GossipTestNodes.StartGossipNodeAsync(
                     fixture.Bitcoin, "cln-gossip-n1", N1Alias, ct,
                     n => GossipTestNodes.SetRoutingPolicy(n, N1Policy.FeeBaseMsat, N1Policy.FeePpm,
                                                           N1Policy.CltvExpiryDelta));
        NLightningTestNode? n2 = null;
        try
        {
            n2 = await GossipTestNodes.StartGossipNodeAsync(
                     fixture.Bitcoin, "cln-gossip-n2", N2Alias, ct,
                     n => GossipTestNodes.SetRoutingPolicy(n, N2Policy.FeeBaseMsat, N2Policy.FeePpm,
                                                           N2Policy.CltvExpiryDelta));
            var topology = new ClnGossipTopology(fixture, n1, n2);
            await topology.OpenAsync(ct);
            return topology;
        }
        catch
        {
            await n1.DisposeAsync();
            if (n2 is not null)
                await n2.DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        var cln = _fixture.Cln;

        // N1 -> CLN, public, at our default feerate (inside CLN's enforced range, NL-288)
        await N1.FundWalletAsync(LightningMoney.Satoshis(Capacity.Satoshi * 5 / 2), AddressType.P2Wpkh, ct);
        await _fixture.WaitAllAtTipAsync([N1, N2], ct);
        await ConnectAsync(N1, ct);
        var n1Channel = await N1.OpenChannelAsync(
                            GossipTestNodes.MarkPublic(new OpenChannelClientRequest(_fixture.ClnAddress, Capacity)), ct);
        N1ChannelId = n1Channel.ChannelId;
        Console.WriteLine($"[cln-gossip] N1 opened public {N1ChannelId} ({n1Channel.ChannelPoint()}) to CLN");

        // CLN -> N2, public (CLN picks its own feerate as opener; we accept from 253 sat/kw, NL-289)
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(Capacity.Satoshi * 2), [N1, N2], ct);
        await ConnectAsync(N2, ct);
        var funded = await cln.CallAsync("fundchannel", ct, ("id", N2.NodeIdHex), ("amount", Capacity.Satoshi),
                                         ("announce", true));
        N2ChannelId = Convert.FromHexString(funded["channel_id"]!.GetValue<string>());
        Console.WriteLine($"[cln-gossip] CLN opened public {N2ChannelId} to N2: {funded.ToJsonString()}");

        // Confirm both, then mine until each is announced on both ends (6 confirmations) and CLN lists both
        // directions of both channels
        var deadline = DateTime.UtcNow + s_announceTimeout;
        while (true)
        {
            var n1Channel2 = await TryGetUsableScidAsync(N1, N1ChannelId, ct);
            var n2Channel = await TryGetUsableScidAsync(N2, N2ChannelId, ct);
            if (n1Channel2 is { } s1 && n2Channel is { } s2)
            {
                N1Scid = s1;
                N2Scid = s2;
                if (await ClnListsBothDirectionsAsync(s1, ct) && await ClnListsBothDirectionsAsync(s2, ct))
                    break;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"The CLN gossip topology was not announced in time: N1 {n1Channel2?.ToString() ?? "-"}, "
                  + $"N2 {n2Channel?.ToString() ?? "-"}");

            await _fixture.MineAndWaitAsync(1, [N1, N2], ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        Console.WriteLine($"[cln-gossip] announced: N1 {new ShortChannelId(N1Scid)}, N2 {new ShortChannelId(N2Scid)}");
    }

    private async Task ConnectAsync(NLightningTestNode node, CancellationToken ct)
    {
        await node.PeerManager.ConnectToPeerAsync(new Domain.Node.ValueObjects.PeerAddressInfo(_fixture.ClnAddress))
                  .WaitAsync(ct);
        await Poll.UntilAsync(async () => node.IsConnectedTo(ClnId)
                                       && await _fixture.Cln.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), $"{node.Name} and CLN connected", ct);
        // Ask CLN for its whole graph, as the LND proofs do (harmless once our own sync, G3-T2, runs)
        var peer = node.PeerManager.GetPeer(ClnId);
        if (peer is not null && peer.TryGetPeerService(out var peerService))
            await peerService.SendGossipMessageAsync(
                new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 0,
                                                                                  uint.MaxValue)));
    }

    private static async Task<ulong?> TryGetUsableScidAsync(NLightningTestNode node, ChannelId channelId,
                                                            CancellationToken ct)
    {
        var channel = await node.GetChannelAsync(channelId, ct);
        Console.WriteLine($"[{node.Name}] {channel.Describe()}");
        return channel.IsUsable() && channel.ShortChannelId is { } scid ? scid.ToUInt64() : null;
    }

    private async Task<bool> ClnListsBothDirectionsAsync(ulong scid, CancellationToken ct)
    {
        var channels = (await _fixture.Cln.CallAsync("listchannels", ct,
                                                     ("short_channel_id", ClnGossipTests.ClnScid(scid))))["channels"]!
           .AsArray();
        Console.WriteLine($"[cln-gossip] CLN listchannels {ClnGossipTests.ClnScid(scid)}: {channels.Count} directions");
        return channels.Count == 2;
    }
}