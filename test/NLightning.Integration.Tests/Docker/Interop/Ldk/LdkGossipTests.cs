using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Abcd;
using Application.Payments.Invoices;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using Gossip;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Public channels and BOLT 7 gossip against ldk-server (NL-556; the fixture gives LDK an alias and an announced
/// address, so LDK Node may announce channels): (a) we open a public channel to LDK: both ends exchange
/// <c>announcement_signatures</c> at 6 confirmations, LDK's network graph has the channel with our policy and our
/// <c>node_announcement</c>, our graph has LDK's policy and node, our invoice carries no route hint and LDK pays it over
/// its graph; (b) LDK opens a public channel to us (<c>open-channel --announce-channel</c>): LDK offers a
/// <c>max_htlc_value_in_flight_msat</c> of 25 % of the channel on announced channels (rust-lightning 0.3's
/// <c>announced_channel_max_inbound_htlc_value_in_flight_percentage</c>; 100 % on unannounced ones), which our v1 accepter refused
/// below 64 % before NL-552 and accepts now; the channel is announced on both ends and carries payments both ways.
/// </summary>
/// <remarks>
/// Each test builds its own node (gossip on, public channels accepted, its own alias, color and routing policy) and
/// channel. Run with <c>scripts/run-interop.sh ldk Release -class
/// NLightning.Integration.Tests.Docker.Interop.Ldk.LdkGossipTests</c>.
/// </remarks>
[Collection(LdkInteropCollection.Name)]
[Trait("Category", LdkInteropCollection.Category)]
public sealed class LdkGossipTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 16 * 60 * 1_000;
    private const int MaxAnnounceBlocks = 20;

    /// <summary>What our nodes announce in their <c>channel_update</c>s (not our defaults).</summary>
    private static readonly (uint FeeBaseMsat, uint FeePpm, ushort CltvExpiryDelta) s_policy = (1_111, 222, 44);

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    /// <summary>Our scid query to LDK times out after 2 min, then LDK streams its graph to our filter (NL-722).</summary>
    private static readonly TimeSpan s_nodeAnnouncementTimeout = TimeSpan.FromMinutes(4);

    private readonly LdkFixture _fixture;
    private readonly List<LdkChannelSession> _sessions = [];

    public LdkGossipTests(LdkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var session in _sessions)
            {
                Console.WriteLine($"[ldk] channel at failure: {await session.DescribeAsync(CancellationToken.None)}");
                foreach (var line in session.Node.NodeLog.TakeLast(200))
                    Console.WriteLine(line);
            }

            await DockerDiagnostics.DumpContainerLogsAsync([LdkFixture.LdkContainerName], 400);
        }

        foreach (var session in _sessions)
            await session.DisposeAsync();
    }

    /// <summary>
    /// (a) Our public channel to LDK (1M sat, 400k pushed) is announced on both ends; LDK pays our hint-free invoice
    /// and we pay LDK's.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPublicChannelToLdk_When_Announced_Then_BothGraphsHaveItAndPaymentsNeedNoHints()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        const string alias = "nltg-ldk-pub-a";
        var session = await OwnAsync(LdkChannelSession.BuildOurFundedAsync(
                                         _fixture, "nltg-ldk-pub-a", s_capacity, LightningMoney.Satoshis(400_000),
                                         ct, n => ConfigureGossipNode(n, alias), isPublic: true));
        GossipTestNodes.VerifyBoundGossipOptions(session.Node.Services, alias);

        // Assert
        Assert.True(AnchorsHarness.GetModel(session.Node, session.ChannelId).AnnounceChannel);
        Assert.True((await session.GetLdkChannelAsync(ct))["is_announced"]?.GetValue<bool>(),
                    "LDK does not list the channel as announced");
        var scid = await WaitAnnouncedAsync(session, alias, ct);
        await AssertLdkPaysOurHintFreeInvoiceAsync(session, scid, LightningMoney.Satoshis(21_000), ct);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(13_000), ct);
    }

    /// <summary>
    /// (b) LDK opens a 1M sat public channel to us: we accept LDK's 25 % in-flight limit (NL-552), the channel is
    /// announced on both ends, and payments flow both ways (LDK pays our hint-free invoice).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdkOpensAPublicChannelToUs_When_Announced_Then_WeAcceptItsInFlightLimitAndPaymentsFlow()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        const string alias = "nltg-ldk-pub-b";
        var session = await OwnAsync(LdkChannelSession.BuildLdkFundedAsync(
                                         _fixture, "nltg-ldk-pub-b", s_capacity, ct,
                                         configureNode: n => ConfigureGossipNode(n, alias), announce: true));
        GossipTestNodes.VerifyBoundGossipOptions(session.Node.Services, alias);

        // Assert: LDK's in-flight limit on an announced channel, below the 64 % our accepter required before NL-552
        var model = AnchorsHarness.GetModel(session.Node, session.ChannelId);
        Assert.True(model.AnnounceChannel);
        var inFlight = model.ChannelParams.Remote.MaxHtlcValueInFlight;
        Console.WriteLine($"[ldk] LDK's max_htlc_value_in_flight_msat {inFlight.MilliSatoshi} on a "
                        + $"{s_capacity.Satoshi} sat public channel; to_self_delay "
                        + $"{model.ChannelParams.Remote.ToSelfDelay}");
        Assert.Equal(s_capacity.MilliSatoshi * 25 / 100, inFlight.MilliSatoshi);

        var scid = await WaitAnnouncedAsync(session, alias, ct);
        await AssertLdkPaysOurHintFreeInvoiceAsync(session, scid, LightningMoney.Satoshis(30_000), ct);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(10_000), ct);
    }

    private static void ConfigureGossipNode(NLightningTestNode node, string alias)
    {
        node.ExtraConfiguration["Gossip:Enabled"] = "true";
        node.ExtraConfiguration["Gossip:AcceptPublicChannels"] = "true";
        node.ExtraConfiguration["Node:Alias"] = alias;
        node.ExtraConfiguration["Node:Color"] = GossipTestNodes.Color;
        // Invoices leave hints out 5 s after the announced channel is in our graph with both policies
        node.ExtraConfiguration[$"{InvoiceOptions.SectionName}:{nameof(InvoiceOptions.PublicChannelGracePeriod)}"] =
            "00:00:05";
        GossipTestNodes.SetRoutingPolicy(node, s_policy.FeeBaseMsat, s_policy.FeePpm, s_policy.CltvExpiryDelta);
    }

    /// <summary>
    /// Mines until LDK's graph has the channel with both directions and our <c>node_announcement</c>, and our graph has
    /// the channel with both policies and LDK's <c>node_announcement</c>; checks what each side announced.
    /// </summary>
    /// <returns>The channel's short channel id.</returns>
    private async Task<ulong> WaitAnnouncedAsync(LdkChannelSession session, string alias, CancellationToken ct)
    {
        var ours = await session.GetOurChannelAsync(ct);
        Assert.NotNull(ours.ShortChannelId);
        var scid = ours.ShortChannelId.Value.ToUInt64();
        var status = string.Empty;
        for (var i = 0; ; i++)
        {
            var ldkChannel = await _fixture.Ldk.GraphGetChannelAsync(scid, ct);
            var ldkNode = await _fixture.Ldk.GraphGetNodeAsync(session.Node.NodeIdHex, ct);
            var ourChannel = await GossipGraphProbe.TryGetOurGraphChannelAsync(session.Node, scid);
            status = $"LDK channel {ldkChannel?.ToJsonString() ?? "none"}; LDK node {ldkNode?.ToJsonString() ?? "none"}; "
                   + $"our channel {(ourChannel is null ? "none" : $"policies {ourChannel.Policy1 is not null}/{ourChannel.Policy2 is not null}")}; "
                   + $"our LDK node {(await GossipGraphProbe.TryGetOurGraphNodeAsync(session.Node, session.LdkPubKey))?.AliasText ?? "none"}";
            if (ldkChannel?["one_to_two"] is JsonObject && ldkChannel["two_to_one"] is JsonObject
             && ldkNode?["announcement_info"] is JsonObject
             && ourChannel is { Policy1: not null, Policy2: not null })
                break;

            if (i >= MaxAnnounceBlocks)
                throw new TimeoutException($"The public channel {scid} was not announced on both ends: {status}");

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        Console.WriteLine($"[ldk] announced {scid}: {status}");

        // LDK has our channel_update and node_announcement as we sent them
        var channel = (await _fixture.Ldk.GraphGetChannelAsync(scid, ct))!;
        var weAreOne = string.Equals(channel["node_one"]!.GetValue<string>(), session.Node.NodeIdHex,
                                     StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_fixture.LdkNodeId, channel[weAreOne ? "node_two" : "node_one"]!.GetValue<string>(),
                     ignoreCase: true);
        Assert.Equal((long)s_capacity.Satoshi, channel["capacity_sats"]!.GetValue<long>());
        var ourUpdate = channel[weAreOne ? "one_to_two" : "two_to_one"]!;
        Assert.Equal(s_policy.CltvExpiryDelta, ourUpdate["cltv_expiry_delta"]!.GetValue<uint>());
        Assert.Equal(s_policy.FeeBaseMsat, ourUpdate["fees"]!["base_msat"]!.GetValue<uint>());
        Assert.Equal(s_policy.FeePpm, ourUpdate["fees"]!["proportional_millionths"]!.GetValue<uint>());
        Assert.True(ourUpdate["enabled"]!.GetValue<bool>());
        var announcement = (await _fixture.Ldk.GraphGetNodeAsync(session.Node.NodeIdHex, ct))!["announcement_info"]!;
        Assert.Equal(alias, announcement["alias"]!.GetValue<string>());
        Assert.Equal(GossipTestNodes.Color.TrimStart('#'), announcement["rgb"]!.GetValue<string>(), ignoreCase: true);

        await AssertOurGraphHasLdksNodeAsync(session, ct);
        return scid;
    }

    /// <summary>
    /// Our graph has LDK's <c>node_announcement</c> whenever LDK serves it. LDK Node broadcasts its
    /// <c>node_announcement</c> once an announced channel is <c>channel_ready</c> (before the
    /// <c>announcement_signatures</c> exchange at 6 confirmations) and then at most once an hour
    /// (<c>NODE_ANN_BCAST_INTERVAL</c>); a node_announcement that comes before any channel_announcement of its node is
    /// ignored by BOLT 7 receivers (B7: SHOULD ignore), ours, LND's and LDK's own graph included. So when LDK's graph
    /// has its own announcement we must get it, from the broadcast or from LDK's full sync: LDK never answers
    /// <c>query_short_channel_ids</c>, and our sync then sends the backlog <c>gossip_timestamp_filter</c>, to which
    /// LDK streams its whole graph (NL-722); when its graph has none, LDK has nothing to serve until its next
    /// broadcast, which is logged.
    /// </summary>
    private async Task AssertOurGraphHasLdksNodeAsync(LdkChannelSession session, CancellationToken ct)
    {
        var ldkSelf = await _fixture.Ldk.GraphGetNodeAsync(_fixture.LdkNodeId, ct);
        var info = await _fixture.Ldk.GetNodeInfoAsync(ct);
        Console.WriteLine($"[ldk] LDK's own node in its graph: {ldkSelf?.ToJsonString() ?? "none"}; last "
                        + $"node_announcement broadcast {info["latest_node_announcement_broadcast_timestamp"]}");
        if (await GossipGraphProbe.TryGetOurGraphNodeAsync(session.Node, session.LdkPubKey) is { } direct)
        {
            Console.WriteLine($"[ldk] our graph had LDK's node_announcement from its broadcast: {direct.AliasText}");
            Assert.Equal(LdkFixture.LdkAlias, direct.AliasText);
            return;
        }

        if (ldkSelf?["announcement_info"] is not JsonObject)
        {
            Console.WriteLine("[ldk] LDK serves no node_announcement of its own (its broadcast came before the "
                            + "channel was announced); nothing to query until its next hourly broadcast");
            return;
        }

        // LDK ignores query_short_channel_ids (rust-lightning 0.3: "Not implemented"), so our range sync with it
        // times out after Gossip:SyncReplyTimeout (2 min) and asks for its gossip with the backlog
        // gossip_timestamp_filter instead (NL-722), to which LDK streams its whole graph
        var ldkNode = await Poll.ForAsync(() => GossipGraphProbe.TryGetOurGraphNodeAsync(session.Node,
                                                                                       session.LdkPubKey),
                                          s_nodeAnnouncementTimeout, "our graph has LDK's node_announcement", ct,
                                          TimeSpan.FromSeconds(2));
        Console.WriteLine($"[ldk] our graph has LDK's node_announcement from its full sync: {ldkNode.AliasText}; "
                        + $"fallback filter lines {session.Node.CountLogLines("(NL-722)")}");
        Assert.Equal(LdkFixture.LdkAlias, ldkNode.AliasText);
    }

    /// <summary>
    /// Our invoice for <paramref name="amount"/> once it carries no route hint (LDK's <c>decode-invoice</c>), paid by
    /// LDK over its graph; our invoice settled and our channel balance grown by the amount.
    /// </summary>
    private async Task AssertLdkPaysOurHintFreeInvoiceAsync(LdkChannelSession session, ulong scid,
                                                            LightningMoney amount, CancellationToken ct)
    {
        var invoice = await Poll.ForAsync(async () =>
        {
            var created = await session.Node.CreateInvoiceAsync(amount, $"ldk pays public {Guid.NewGuid():N}", ct);
            var decoded = await _fixture.Ldk.RunAsync("decode-invoice", ct, created.Bolt11!);
            var hints = decoded["route_hints"]?.AsArray().Count ?? 0;
            Console.WriteLine($"[ldk] our invoice has {hints} route hint(s)");
            return hints == 0 ? created : null;
        }, s_timeout, $"our invoice without route hints once {scid} is announced", ct, TimeSpan.FromSeconds(3));
        var before = await session.GetOurChannelAsync(ct);

        var payment = await session.Ldk.PayAsync(invoice.Bolt11!, 60, ct);

        Console.WriteLine($"[ldk] LDK paid our hint-free invoice: {payment.ToJsonString()}");
        var details = payment["payment"] ?? payment;
        Assert.Equal("SUCCEEDED", LdkClient.StatusOf(details), StringComparer.OrdinalIgnoreCase);
        var preimage = Convert.FromHexString(LdkChannelSession.FindString(details, "preimage")!);
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));
        var settled = await Poll.ForAsync(async () => await session.Node.GetInvoiceAsync(invoice.PaymentHash, ct) is
        { Status: InvoiceStatus.Settled } i
                                                          ? i
                                                          : null,
                                          s_timeout, "our invoice settled", ct);
        Assert.Equal(amount, settled.AmountReceived);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    private async Task<LdkChannelSession> OwnAsync(Task<LdkChannelSession> build)
    {
        var session = await build;
        _sessions.Add(session);
        return session;
    }
}