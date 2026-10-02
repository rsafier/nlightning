using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Channels.RoutingPolicies;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Gossip;
using TestCollections;
using Utils;

/// <summary>
/// Wave sp1 lane SP1-G proof against LND 0.21: <c>setchannelpolicy</c> changes one channel's routing policy at
/// runtime. A new signed <c>channel_update</c> goes out at once (LND <c>GetChanInfo</c> shows the new fee, rate,
/// CLTV delta and HTLC range without a reconnection or restart), our forwarding enforces the channel's values (an
/// HTLC above its <c>htlc_maximum_msat</c> is refused with <c>temporary_channel_failure</c> before anything is offered
/// downstream; a payment at the new fee and delta goes through and pays exactly the new fee), a reset brings
/// <c>Node:Routing</c> back, and the policy survives a restart.
/// </summary>
/// <remarks>
/// Topology: LND alice → us → LND david, both channels funded by us, private (alice gets a push so she can pay).
/// david's invoices carry a route hint through our channel to him with the policy we set, as
/// <c>ChannelSafetyFlowTests</c> does. The policy commands go through the daemon's client handlers; the node registers
/// them itself (<c>AddChannelPolicyIpcServices</c>, idempotent) until <c>AddNltgNodeServices</c> does.
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class ChannelPolicyFlowTests : IAsyncLifetime
{
    private const uint NodeFeeBaseMsat = 1_000;
    private const uint NodeFeePpm = 100;
    private const ushort NodeCltvExpiryDelta = 40;

    private const uint ChannelFeeBaseMsat = 2_500;
    private const uint ChannelFeePpm = 1_000;
    private const ushort ChannelCltvExpiryDelta = 60;
    private const ulong ChannelHtlcMinimumMsat = 2_000;
    private const ulong ChannelHtlcMaximumMsat = 20_000_000;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_policyTimeout = TimeSpan.FromSeconds(30);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public ChannelPolicyFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "policy", configureNodeOptions: o =>
        {
            o.Routing.FeeBaseMsat = NodeFeeBaseMsat;
            o.Routing.FeeProportionalMillionths = NodeFeePpm;
            o.Routing.CltvExpiryDelta = NodeCltvExpiryDelta;
        });
        _node.ConfigureServices = services => services.AddChannelPolicyIpcServices();
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_AForwardingChannel_When_ItsPolicyIsSet_Then_LndSeesItAtOnceAndForwardingEnforcesIt()
    {
        // Arrange: alice -> us -> david
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        var upstream = await OpenUsableChannelAsync(node, alice, LightningMoney.Satoshis(500_000), ct);
        var downstream = await OpenUsableChannelAsync(node, david, null, ct);
        var upstreamLnd = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
        Assert.NotNull(upstreamLnd);
        var downstreamScid = (await node.GetChannelAsync(downstream.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(downstreamScid);
        var chanId = downstreamScid.Value.ToUInt64();

        // david has our open-time update with Node:Routing
        var before = await Poll.ForAsync(async () => await GetOurPolicyAsync(node, david, chanId, ct) is
        { FeeBaseMsat: NodeFeeBaseMsat } policy
                                                         ? policy
                                                         : null,
                                         s_policyTimeout, "david has our open-time policy", ct);
        var sentBefore = node.CountLogLines($"Sending channel_update for channel {downstream.ChannelId}");

        // Act: setchannelpolicy on the downstream channel, by its short channel id
        var set = await SetAsync(node, new SetChannelPolicyClientRequest(new ChannelReference(downstreamScid.Value))
        {
            FeeBaseMsat = ChannelFeeBaseMsat,
            FeeProportionalMillionths = ChannelFeePpm,
            CltvExpiryDelta = ChannelCltvExpiryDelta,
            HtlcMinimumMsat = ChannelHtlcMinimumMsat,
            HtlcMaximumMsat = ChannelHtlcMaximumMsat
        }, ct);

        // Assert: the policy in force
        Assert.Equal(downstream.ChannelId, set.Policy.ChannelId);
        Assert.Equal(ChannelFeeBaseMsat, set.Policy.FeeBaseMsat);
        Assert.Equal(ChannelFeePpm, set.Policy.FeeProportionalMillionths);
        Assert.Equal(ChannelCltvExpiryDelta, set.Policy.CltvExpiryDelta);
        Assert.Equal(ChannelHtlcMaximumMsat, set.Policy.HtlcMaximumMsat);
        Assert.True(set.Policy.HtlcMinimumMsat >= ChannelHtlcMinimumMsat);

        // Assert: a new signed channel_update went out at once, without a reconnection, and david applied it
        Assert.True(node.CountLogLines($"Sending channel_update for channel {downstream.ChannelId}") > sentBefore);
        var after = await Poll.ForAsync(async () => await GetOurPolicyAsync(node, david, chanId, ct) is
        { FeeBaseMsat: ChannelFeeBaseMsat } policy
                                                        ? policy
                                                        : null,
                                        s_policyTimeout, "david has our new policy", ct);
        Console.WriteLine($"david's view of our direction: base {after.FeeBaseMsat}, rate {after.FeeRateMilliMsat}, "
                        + $"delta {after.TimeLockDelta}, htlc {after.MinHtlc}-{after.MaxHtlcMsat}, "
                        + $"last update {before.LastUpdate} -> {after.LastUpdate}");
        Assert.True(after.LastUpdate > before.LastUpdate);
        Assert.Equal(ChannelFeePpm, (uint)after.FeeRateMilliMsat);
        Assert.Equal(ChannelCltvExpiryDelta, (ushort)after.TimeLockDelta);
        Assert.Equal((long)set.Policy.HtlcMinimumMsat, after.MinHtlc);
        Assert.Equal(ChannelHtlcMaximumMsat, after.MaxHtlcMsat);
        Assert.True(node.IsConnectedTo(new Domain.Crypto.ValueObjects.CompactPubKey(david.LocalNodePubKeyBytes)));

        // listchannels shows the channel's policy, and only on that channel
        var listed = (await node.ListChannelsAsync(ct)).Channels;
        var listedDownstream = Assert.Single(listed, c => c.ChannelId == downstream.ChannelId);
        Assert.Equal(ChannelFeeBaseMsat, listedDownstream.FeeBaseMsat);
        Assert.Equal(ChannelHtlcMaximumMsat, listedDownstream.HtlcMaximumMsat);
        Assert.True(listedDownstream.HasPolicyOverride);
        var listedUpstream = Assert.Single(listed, c => c.ChannelId == upstream.ChannelId);
        Assert.Equal(NodeFeeBaseMsat, listedUpstream.FeeBaseMsat);
        Assert.False(listedUpstream.HasPolicyOverride);

        // Act: alice pays david 30,000 sat through us, above the channel's htlc_maximum_msat (20,000 sat)
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(node.NodeIdHex, chanId, ChannelFeeBaseMsat,
                                                                   ChannelFeePpm, ChannelCltvExpiryDelta));
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var tooLarge = await LndTestHelpers.AddInvoiceAsync(david, 30_000_000, [hint], ct, "sp1-g above max");
        var refused = await LndTestHelpers.SendPaymentV2Async(
                          alice, LndTestHelpers.PinnedPayment(tooLarge.PaymentRequest, [upstreamLnd.ChanId]), ct);

        // Assert: we refused it (index 1: our node) with temporary_channel_failure and offered nothing to david
        Console.WriteLine($"Above max: {refused.Status} ({refused.FailureReason}), attempts: "
                        + string.Join(", ", refused.Htlcs.Select(h => $"{h.Failure?.Code}@{h.Failure?.FailureSourceIndex}")));
        Assert.Equal(Payment.Types.PaymentStatus.Failed, refused.Status);
        Assert.Contains(refused.Htlcs, h => h.Failure is
        {
            Code: Failure.Types.FailureCode.TemporaryChannelFailure,
            FailureSourceIndex: 1
        });
        Assert.Equal(Invoice.Types.InvoiceState.Open,
                     (await LndTestHelpers.LookupInvoiceAsync(david, tooLarge.RHash.ToByteArray(), ct)).State);
        Assert.Equal(0, HtlcCount(node, downstream.ChannelId));

        // Act: 10,000 sat at the channel's fee and delta
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var withinMax = await LndTestHelpers.AddInvoiceAsync(david, 10_000_000, [hint], ct, "sp1-g within max");
        var paid = await LndTestHelpers.SendPaymentV2Async(
                       alice, LndTestHelpers.PinnedPayment(withinMax.PaymentRequest, [upstreamLnd.ChanId]), ct);

        // Assert: settled, and alice paid exactly the channel's fee (BOLT 7: 2,500 + 10,000,000 * 1,000 / 10^6)
        Console.WriteLine($"Within max: {paid.Status} ({paid.FailureReason}), fee {paid.FeeMsat} msat");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, paid.Status);
        Assert.Equal(ChannelFeeBaseMsat + 10_000_000L * ChannelFeePpm / 1_000_000, paid.FeeMsat);

        // Act: reset
        var reset = await SetAsync(node, new SetChannelPolicyClientRequest(new ChannelReference(downstream.ChannelId))
        {
            Reset = true
        }, ct);

        // Assert: Node:Routing again, announced at once
        Assert.True(reset.WasReset);
        Assert.Null(reset.Policy.Override);
        Assert.Equal(NodeFeeBaseMsat, reset.Policy.FeeBaseMsat);
        var afterReset = await Poll.ForAsync(async () => await GetOurPolicyAsync(node, david, chanId, ct) is
        { FeeBaseMsat: NodeFeeBaseMsat } policy
                                                             ? policy
                                                             : null,
                                             s_policyTimeout, "david has Node:Routing again", ct);
        Assert.True(afterReset.LastUpdate > after.LastUpdate);
        Assert.Equal(NodeFeePpm, (uint)afterReset.FeeRateMilliMsat);
        Assert.Equal(NodeCltvExpiryDelta, (ushort)afterReset.TimeLockDelta);
        Assert.True(afterReset.MaxHtlcMsat > ChannelHtlcMaximumMsat);
    }

    [Fact]
    public async Task Given_ASetPolicy_When_TheNodeRestarts_Then_ItStillAppliesAndIsAnnounced()
    {
        // Arrange: the store must be persistent (lane SP1-C's ChannelPolicies table)
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var store = node.Services.GetRequiredService<ChannelPolicyStore>();
        await store.LoadAsync(ct);
        Assert.SkipUnless(store.IsPersistent,
                          "This build's unit of work has no ChannelPolicies table (lane SP1-C): overrides are kept in "
                        + "memory only");

        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        var channel = await OpenUsableChannelAsync(node, david, null, ct);
        var scid = (await node.GetChannelAsync(channel.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(scid);
        await SetAsync(node, new SetChannelPolicyClientRequest(new ChannelReference(channel.ChannelId))
        {
            FeeProportionalMillionths = 777,
            HtlcMaximumMsat = 50_000_000
        }, ct);

        // Act
        await node.StopAsync();
        await node.StartAsync(ct);

        // Assert: getchannelpolicy after the restart, and the update david gets on the new connection
        var read = await HandleAsync<GetChannelPolicyClientRequest, ChannelPolicyClientResponse>(
                       node, new GetChannelPolicyClientRequest(new ChannelReference(scid.Value)), ct);
        Assert.Equal(777u, read.Policy.FeeProportionalMillionths);
        Assert.Equal(50_000_000ul, read.Policy.HtlcMaximumMsat);
        Assert.True(read.Policy.IsFeeProportionalMillionthsOverridden);
        var policy = await Poll.ForAsync(async () => await GetOurPolicyAsync(node, david, scid.Value.ToUInt64(), ct) is
        { FeeRateMilliMsat: 777 } p
                                                         ? p
                                                         : null,
                                         s_timeout, "david has the policy after our restart", ct);
        Assert.Equal(50_000_000ul, policy.MaxHtlcMsat);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Task<ChannelPolicyClientResponse> SetAsync(NLightningTestNode node,
                                                              SetChannelPolicyClientRequest request,
                                                              CancellationToken ct) =>
        HandleAsync<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>(node, request, ct);

    /// <summary>The daemon's client handler, as <c>nltg setchannelpolicy/getchannelpolicy</c> reaches it.</summary>
    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>()
                          .HandleAsync(request, ct);
    }

    /// <summary>Our direction of the channel in <paramref name="lnd"/>'s graph, or null while it has none.</summary>
    private static async Task<RoutingPolicy?> GetOurPolicyAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                                               ulong chanId, CancellationToken ct)
    {
        try
        {
            var info = await lnd.LightningClient.GetChanInfoAsync(new ChanInfoRequest { ChanId = chanId },
                                                                  cancellationToken: ct);
            return info.Node1Pub.Equals(node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                       ? info.Node1Policy
                       : info.Node2Policy;
        }
        catch (Grpc.Core.RpcException)
        {
            // LND does not have the edge yet
            return null;
        }
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(NLightningTestNode node,
        LNDNodeConnection peer, LightningMoney? push, CancellationToken ct)
    {
        var peerAddress = await node.ConnectToAsync(peer, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                               LightningMoney.Satoshis(1_000_000))
        {
            PushAmount = push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [node], ct);
        return channel;
    }

    private static int HtlcCount(NLightningTestNode node, ChannelId channelId) =>
        node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel)
            ? channel.Commitments?.Htlcs.Count ?? 0
            : 0;
}

/// <summary>
/// Wave sp1 lane SP1-G proof against LND 0.21 on an announced channel: a <c>setchannelpolicy</c> change of a public
/// channel reaches the network, not only the channel peer. bob, who has no channel with us, sees our new policy in his
/// graph (<c>GetChanInfo</c>) after our own-gossip relay flush (<c>Gossip:OwnGossipFlushInterval</c>, 60 s) and
/// alice's relay, without a reconnection or restart.
/// </summary>
/// <remarks>Public channels change the LND nodes' graph for good, so this runs in the gossip collection
/// (<c>scripts/run-gossip.sh 1 Release -class NLightning.Integration.Tests.Docker.ChannelPolicyPublicFlowTests</c>).
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class ChannelPolicyPublicFlowTests
{
    private const uint ChannelFeeBaseMsat = 3_100;
    private const uint ChannelFeePpm = 1_200;
    private const ushort ChannelCltvExpiryDelta = 66;
    private const ulong ChannelHtlcMaximumMsat = 30_000_000;

    private static readonly TimeSpan s_networkTimeout = TimeSpan.FromMinutes(3);

    private readonly LightningRegtestNetworkFixture _fixture;

    public ChannelPolicyPublicFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    [Fact]
    public async Task Given_AnAnnouncedChannel_When_ItsPolicyIsSet_Then_ANonPeerLndSeesTheNewPolicy()
    {
        // Arrange: a public channel to alice, announced to alice and bob
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var bob = _fixture.GetLndNode("bob");
        await using var node = await GossipTestNodes.StartGossipNodeAsync(
                                   _fixture, "policy-public", "nltg-sp1g", ct,
                                   n => n.ConfigureServices = services => services.AddChannelPolicyIpcServices());
        var channel = await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, null, [alice, bob], ct,
                                                                         syncGraph: false);
        var before = await GetOurPolicyAsync(node, bob, channel.ShortChannelId, ct);
        Assert.NotNull(before);
        Assert.NotEqual(ChannelFeeBaseMsat, (uint)before.FeeBaseMsat);

        // Act
        using (var scope = node.Services.CreateScope())
        {
            await scope.ServiceProvider
                       .GetRequiredService<IClientCommandHandler<SetChannelPolicyClientRequest,
                            ChannelPolicyClientResponse>>()
                       .HandleAsync(new SetChannelPolicyClientRequest(new ChannelReference(channel.ChannelId))
                       {
                           FeeBaseMsat = ChannelFeeBaseMsat,
                           FeeProportionalMillionths = ChannelFeePpm,
                           CltvExpiryDelta = ChannelCltvExpiryDelta,
                           HtlcMaximumMsat = ChannelHtlcMaximumMsat
                       }, ct);
        }

        // Assert: bob (no channel with us) has the new policy, through our relay and alice's
        var after = await Poll.ForAsync(async () => await GetOurPolicyAsync(node, bob, channel.ShortChannelId, ct) is
        { FeeBaseMsat: ChannelFeeBaseMsat } policy
                                                        ? policy
                                                        : null,
                                        s_networkTimeout, "bob's graph has our new policy", ct,
                                        GossipGraphProbe.PollInterval);
        Console.WriteLine($"bob's view of our direction: base {after.FeeBaseMsat}, rate {after.FeeRateMilliMsat}, "
                        + $"delta {after.TimeLockDelta}, max {after.MaxHtlcMsat}, "
                        + $"last update {before.LastUpdate} -> {after.LastUpdate}");
        Assert.True(after.LastUpdate > before.LastUpdate);
        Assert.Equal(ChannelFeePpm, (uint)after.FeeRateMilliMsat);
        Assert.Equal(ChannelCltvExpiryDelta, (ushort)after.TimeLockDelta);
        Assert.Equal(ChannelHtlcMaximumMsat, after.MaxHtlcMsat);
        Assert.False(after.Disabled);
    }

    /// <summary>Our direction of the channel in <paramref name="lnd"/>'s graph, or null while it has none.</summary>
    private static async Task<RoutingPolicy?> GetOurPolicyAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                                               ulong chanId, CancellationToken ct)
    {
        var edge = await GossipGraphProbe.TryGetChanInfoAsync(lnd, chanId, ct);
        if (edge is null)
            return null;

        return edge.Node1Pub.Equals(node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                   ? edge.Node1Policy
                   : edge.Node2Policy;
    }
}