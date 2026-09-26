using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Utils;

/// <summary>
/// Our node's public channel to alice, once it is announced (see <see cref="PublicTopology"/>).
/// </summary>
/// <param name="Node">Our node.</param>
/// <param name="ChannelId">The channel.</param>
/// <param name="ShortChannelId">Its real short channel id (BOLT 7 <c>u64</c>, LND's <c>chan_id</c>).</param>
/// <param name="ChannelPoint">LND's <c>txid:index</c> of the channel.</param>
public sealed record PublicChannelToAlice(NLightningTestNode Node, ChannelId ChannelId, ulong ShortChannelId,
                                          string ChannelPoint);

/// <summary>
/// The setup the payment and graph proofs over public channels share (BOLT 7 goal proofs, G2 (d), G4): our node with
/// one public channel to alice, announced to the LND nodes that pay or route, and our graph synced with the fixture's
/// LND-LND channels (alice-bob twice, alice-carol, bob-carol).
/// </summary>
public static class PublicTopology
{
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);

    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_graphTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_blockEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// alice, bob and carol, the fixture's LND nodes with channels.
    /// </summary>
    public static IReadOnlyList<LNDNodeConnection> LndNodes(LightningRegtestNetworkFixture fixture) =>
        [fixture.GetLndNode("alice"), fixture.GetLndNode("bob"), fixture.GetLndNode("carol")];

    /// <summary>
    /// Funds <paramref name="node"/>, opens a public channel of <see cref="Capacity"/> to alice (pushing
    /// <paramref name="push"/>) and waits until it is usable, until every LND of <paramref name="observers"/> has it
    /// with both policies (mining a block now and then: both ends announce at 6 confirmations) and, unless
    /// <paramref name="syncGraph"/> is false, until our graph has the fixture's channels and nodes.
    /// </summary>
    public static async Task<PublicChannelToAlice> OpenPublicChannelToAliceAsync(
        LightningRegtestNetworkFixture fixture, NLightningTestNode node, LightningMoney? push,
        IReadOnlyList<LNDNodeConnection> observers, CancellationToken ct, bool syncGraph = true)
    {
        var alice = fixture.GetLndNode("alice");
        await node.FundWalletAsync(LightningMoney.Satoshis(Capacity.Satoshi * 2), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);

        // Our default feerate (the test node's fixed estimate), as the other Docker tests that pay over the channel
        var request = GossipTestNodes.MarkPublic(new OpenChannelClientRequest(aliceAddress, Capacity)
        {
            PushAmount = push
        });
        var opened = await node.OpenChannelAsync(request, ct);
        var scid = await WaitForShortChannelIdAsync(node, opened.ChannelId, ct);
        var channelPoint = opened.ChannelPoint();
        Console.WriteLine($"[{node.Name}] public channel {opened.ChannelId} ({channelPoint}) to alice: "
                        + $"{new ShortChannelId(scid)} ({scid}), pushed {push?.MilliSatoshi ?? 0} msat");

        foreach (var lnd in observers)
            await WaitLndHasChannelAsync(fixture, node, lnd, scid, ct);

        if (syncGraph)
            await SyncOurGraphAsync(fixture, node, ct);
        return new PublicChannelToAlice(node, opened.ChannelId, scid, channelPoint);
    }

    /// <summary>
    /// Waits until the channel is usable and has its real short channel id; returns it in LND's <c>u64</c> form.
    /// </summary>
    public static async Task<ulong> WaitForShortChannelIdAsync(NLightningTestNode node, ChannelId channelId,
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
    /// Waits until <paramref name="lnd"/>'s graph has <paramref name="scid"/> with both policies, mining a block every
    /// <see cref="s_blockEvery"/> without success (up to 6).
    /// </summary>
    public static Task WaitLndHasChannelAsync(LightningRegtestNetworkFixture fixture, NLightningTestNode node,
                                              LNDNodeConnection lnd, ulong scid, CancellationToken ct) =>
        GossipGraphProbe.MineUntilAsync(
            async () => await GossipGraphProbe.TryGetChanInfoAsync(lnd, scid, ct) is
            { Node1Policy: not null, Node2Policy: not null },
            () => ChainSync.MineAndWaitAsync(fixture, 1, fixture.LndNodes, [node], ct), s_blockEvery, 6, s_timeout,
            $"{lnd.LocalAlias} has {new ShortChannelId(scid)} with both policies", ct);

    /// <summary>
    /// The fixture's LND-LND channels, once alice lists every one with both policies.
    /// </summary>
    public static async Task<IReadOnlyList<(ulong ShortChannelId, string Local, string Remote, bool Private)>>
        GetFixtureChannelsAsync(LightningRegtestNetworkFixture fixture, CancellationToken ct)
    {
        var lndNodes = LndNodes(fixture);
        var channels = await GossipGraphProbe.GetFixtureChannelsAsync(lndNodes, ct);
        Assert.NotEmpty(channels);
        await GossipGraphProbe.MineUntilAsync(async () =>
        {
            foreach (var (scid, _, _, _) in channels)
                if (await GossipGraphProbe.TryGetChanInfoAsync(lndNodes[0], scid, ct) is not
                    { Node1Policy: not null, Node2Policy: not null })
                    return false;

            return true;
        }, () => ChainSync.MineAndWaitAsync(fixture, 1, lndNodes, [], ct), TimeSpan.FromSeconds(20), 6, s_timeout,
                                              "alice has every fixture channel with both policies", ct);
        return channels;
    }

    /// <summary>
    /// The fixture channel between <paramref name="a"/> and <paramref name="b"/> (the first one, for alice-bob).
    /// </summary>
    public static ulong FixtureChannelBetween(
        IReadOnlyList<(ulong ShortChannelId, string Local, string Remote, bool Private)> channels,
        LNDNodeConnection a, LNDNodeConnection b)
    {
        var ids = new[] { a.LocalNodePubKey.ToLowerInvariant(), b.LocalNodePubKey.ToLowerInvariant() };
        return channels.First(c => ids.Contains(c.Local) && ids.Contains(c.Remote)).ShortChannelId;
    }

    /// <summary>
    /// Asks alice for her whole graph (<see cref="GossipTestNodes.SendFullTimestampFilterAsync"/>; harmless once our
    /// own G3 sync runs) and waits until our graph has the fixture's channels with both policies and the LND nodes.
    /// </summary>
    public static async Task SyncOurGraphAsync(LightningRegtestNetworkFixture fixture, NLightningTestNode node,
                                               CancellationToken ct)
    {
        var lndNodes = LndNodes(fixture);
        var scids = (await GetFixtureChannelsAsync(fixture, ct)).Select(c => c.ShortChannelId).ToList();
        await GossipTestNodes.SendFullTimestampFilterAsync(node, lndNodes[0].LocalNodePubKeyBytes, ct);
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_graphTimeout,
                              "our graph has the LND channels with both policies and the LND nodes", ct,
                              GossipGraphProbe.PollInterval);
    }

    /// <summary>
    /// Our end of <paramref name="channelId"/> once it is usable with no HTLC in flight.
    /// </summary>
    public static Task<ChannelInfoClientResponse> WaitSettledAsync(NLightningTestNode node, ChannelId channelId,
                                                                   CancellationToken ct) =>
        Poll.ForAsync(async () =>
        {
            var channel = await node.GetChannelAsync(channelId, ct);
            return channel.IsUsable() && channel is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } ? channel : null;
        }, s_timeout, "channel usable with no HTLC in flight", ct, GossipGraphProbe.PollInterval);

    /// <summary>
    /// <c>payinvoice</c> with <c>--max-parts 1</c>, so the payment is one HTLC whose forwards can be traced by amount.
    /// </summary>
    public static async Task<PaymentInfoClientResponse> PayInOnePartAsync(NLightningTestNode node, string bolt11,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<PayInvoiceClientRequest,
                                PayInvoiceClientResponse>>();
        var response = await handler.HandleAsync(new PayInvoiceClientRequest(bolt11)
        {
            MaxParts = 1,
            TimeoutSeconds = 60
        }, ct);
        return response.Payment;
    }

    /// <summary>
    /// A payment amount in msat that no other payment of the run uses, so the LND forwards of this payment can be
    /// told apart by amount (<see cref="LndRoutingProbe.TraceForwards"/>).
    /// </summary>
    public static ulong UniqueAmountMsat(ulong baseMsat) => baseMsat + (ulong)Random.Shared.Next(1, 999_999);
}