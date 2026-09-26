using System.Globalization;
using System.Net;
using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;
using NBitcoin;
using ServiceStack;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 7 plan Proof G2 (validation and graph store) against LND 0.20: (a) connected to alice, our graph gets the
/// fixture's LND-LND channels with both policies and the alice, bob and carol node announcements; (b) they are still
/// there after a restart before any connection; (c) a public channel closed cooperatively is marked spent one block
/// after the close, still stored (and spent) 70 blocks after the spend, and removed 72 blocks after it; (d) with
/// <c>Gossip:StaleAfter</c> at 2 minutes, carol's channels leave our routes once her updates are that old.
/// </summary>
/// <remarks>
/// <para>(c) closes a channel the test opens itself (david-carol, public), not the fixture's alice-carol the plan
/// names: the other proofs of this collection relay through the fixture channels, and test order is not fixed.</para>
/// <para>(c)'s "excluded from getroute" is not asserted: the (c) node has no channel, so it has no route to anything.
/// (d) (stale after <c>Gossip:StaleAfter</c>) keeps everyone but carol fresh instead of stopping carol, a shared LND
/// node (see the test).</para>
/// <para>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture).</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class GraphStoreFlowTests
{
    /// <summary>BOLT 7: a spent channel is forgotten 72 blocks after the spend.</summary>
    private const int SpentForgetDepth = 72;

    private static readonly TimeSpan s_syncTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    /// <summary>(d): <c>Gossip:StaleAfter</c> of the node, the plan's 2 minutes.</summary>
    private static readonly TimeSpan s_staleAfter = TimeSpan.FromMinutes(2);

    /// <summary>(d): how often alice and bob re-announce their policies (under <see cref="s_staleAfter"/>).</summary>
    private static readonly TimeSpan s_keepFreshInterval = TimeSpan.FromSeconds(40);

    /// <summary>(d): LND stamps updates in whole seconds; our clock and LND's are the same host's.</summary>
    private static readonly TimeSpan s_clockTolerance = TimeSpan.FromSeconds(5);

    /// <summary>How long (c) watches the spent channel stay stored one block short of the forget delay.</summary>
    private static readonly TimeSpan s_boundaryHold = TimeSpan.FromSeconds(5);

    private readonly LightningRegtestNetworkFixture _fixture;

    public GraphStoreFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    /// <summary>Proof G2 (a).</summary>
    [Fact]
    public async Task Given_ConnectedToAlice_When_SheSendsHerGraph_Then_OurGraphHasTheLndChannelsAndNodes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = GossipLndNodes();
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(lndNodes[0], ct)}");
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g2a", "nltg-g2a", ct);
        var scids = await GetFixtureScidsAsync(lndNodes, ct);

        // Act
        await SyncFromAliceAsync(node, ct);

        // Assert
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_syncTimeout,
                              "our graph has the LND channels with both policies and the LND nodes", ct,
                              GossipGraphProbe.PollInterval);
    }

    /// <summary>Proof G2 (b).</summary>
    [Fact]
    public async Task Given_SyncedGraph_When_RestartedWithoutConnecting_Then_TheSameChannelsAndNodesArePresent()
    {
        // Arrange: (a)
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = GossipLndNodes();
        var alice = lndNodes[0];
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g2b", "nltg-g2b", ct);
        var scids = await GetFixtureScidsAsync(lndNodes, ct);
        await SyncFromAliceAsync(node, ct);
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_syncTimeout,
                              "our graph has the LND channels with both policies and the LND nodes", ct,
                              GossipGraphProbe.PollInterval);

        // Act: restart and read the graph before PeerManager starts (it reconnects to every stored peer, alice too)
        bool? connectedAtRead = null;
        bool? graphAtRead = null;
        node.BeforePeersStart = async n =>
        {
            connectedAtRead = n.IsConnectedTo(alice.LocalNodePubKeyBytes);
            graphAtRead = await GossipGraphProbe.OurGraphHasAsync(n, scids, lndNodes);
        };
        await node.StopAsync();
        try
        {
            await node.StartAsync(ct);
        }
        finally
        {
            node.BeforePeersStart = null;
        }

        // Assert: read with no connection through listgraphchannels/listnodes (IPC 18/17), which read the in-memory
        // GraphStore: this is its reload from the database
        Assert.False(connectedAtRead);
        Assert.True(graphAtRead);
    }

    /// <summary>Proof G2 (c).</summary>
    [Fact]
    public async Task Given_PublicLndChannelInOurGraph_When_ClosedCooperatively_Then_SpentAfterOneBlockAndGoneAfter72()
    {
        // Arrange: our node follows alice's graph; david opens a public channel to carol
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        var carol = _fixture.GetLndNode("carol");
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g2c", "nltg-g2c", ct);
        await SyncFromAliceAsync(node, ct);
        var (scid, channelPoint) = await OpenPublicLndChannelAsync(david, carol, node, ct);
        await GossipGraphProbe.MineUntilAsync(
            async () => await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid) is { Policy1: not null, Policy2: not null },
            () => ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, [node], ct), TimeSpan.FromSeconds(20), 6,
            s_syncTimeout, "our graph has david-carol with both policies", ct);
        var stored = await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid);
        Assert.Null(stored!.SpentAtHeight);

        // Act 1: david closes cooperatively; the close confirms in one block
        await CloseCooperativelyAsync(david, channelPoint, ct);
        var spendHeight = await ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, [node], ct);

        // Assert 1: marked spent at the close's height (still stored)
        // (not asserted through getroute: this node has no channel, so it has no route at all; see the remarks)
        await Poll.UntilAsync(async () =>
                              {
                                  var channel = await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid);
                                  Console.WriteLine(
                                      $"{new ShortChannelId(scid)} spent at {channel?.SpentAtHeight?.ToString() ?? "-"}");
                                  return channel?.SpentAtHeight == spendHeight;
                              }, s_timeout, $"david-carol marked spent at {spendHeight}", ct,
                              GossipGraphProbe.PollInterval);

        // Act 2: up to spend + 70 (71 confirmations of the spend), one block short of either reading of BOLT 7's
        // delay ("72 block confirmations" is reached at spend + 71, "a 72-block delay" at spend + 72)
        await ChainSync.MineAndWaitAsync(_fixture, SpentForgetDepth - 2, _fixture.LndNodes, [node], ct);

        // Assert 2: still stored and still marked spent at the close's height (BOLT 7 forbids forgetting a spent
        // channel before the delay, so a splice's new channel_announcement can propagate); checked right after the
        // mining and again after a few seconds (a removal is final), so a pruner that runs behind the block is caught
        await AssertStillSpentAsync(node, scid, spendHeight, $"spend + {SpentForgetDepth - 2}");
        await Task.Delay(s_boundaryHold, ct);
        await AssertStillSpentAsync(node, scid, spendHeight, $"spend + {SpentForgetDepth - 2}, after the hold");

        // Act 3: the two blocks to spend + 72, past either reading of the delay
        await ChainSync.MineAndWaitAsync(_fixture, 2, _fixture.LndNodes, [node], ct);

        // Assert 3: gone
        await Poll.UntilAsync(async () => await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid) is null,
                              s_timeout, $"david-carol removed {SpentForgetDepth} blocks after its spend", ct,
                              GossipGraphProbe.PollInterval);
    }

    /// <summary>Proof G2 (d), NL-356.</summary>
    /// <remarks>
    /// The plan stops carol so her updates stop. carol is a shared LND node (restarting one moves its address,
    /// NL-262), so instead every other policy is kept fresh: alice and bob re-announce theirs every
    /// <see cref="s_keepFreshInterval"/> while carol stays silent after one refresh at the start. With
    /// <c>Gossip:StaleAfter</c> at <see cref="s_staleAfter"/>, carol's channels (alice-carol, bob-carol: the older
    /// direction decides, B7-PR-02) leave our routes once that long has passed, while alice-bob stays routable. Seen
    /// through <c>getroute</c> (IPC 19): our node has a public channel to alice, so a route to carol exists before and
    /// none after. The real two-week value is covered by the mocked-clock unit tests (G2-T5, pathfinder).
    /// Needs lane C2: <c>getroute</c> (<see cref="GetRouteProbe"/>) and its <c>GraphPathSource</c>, which passes
    /// <c>GossipGraphOptions.StaleAfter</c> (<c>Gossip:StaleAfter</c>) to the pathfinder.
    /// </remarks>
    [Fact]
    public async Task Given_StaleAfterTwoMinutes_When_CarolStopsUpdating_Then_HerChannelsLeaveOurRoutes()
    {
        // Arrange: our node (stale after 2 min) with a public channel to alice; the graph is read only after the
        // refresh below (older updates are ignored on arrival with this StaleAfter)
        var ct = TestContext.Current.CancellationToken;
        var (alice, bob, carol) = (_fixture.GetLndNode("alice"), _fixture.GetLndNode("bob"),
                                   _fixture.GetLndNode("carol"));
        await using var node = await GossipTestNodes.StartGossipNodeAsync(
                                   _fixture, "gossip-g2d", "nltg-g2d", ct,
                                   n => n.ExtraConfiguration["Gossip:StaleAfter"] =
                                            s_staleAfter.ToString("c", CultureInfo.InvariantCulture));
        var channels = await PublicTopology.GetFixtureChannelsAsync(_fixture, ct);
        var ours = await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, null, [alice], ct,
                                                                      syncGraph: false);
        var aliceBob = channels.Where(c => IsBetween(c, alice, bob)).Select(c => c.ShortChannelId).ToList();
        var aliceCarol = PublicTopology.FixtureChannelBetween(channels, alice, carol);
        var bobCarol = PublicTopology.FixtureChannelBetween(channels, bob, carol);
        var aliceKeeps = aliceBob.Append(aliceCarol).Append(ours.ShortChannelId).ToList();
        var bobKeeps = aliceBob.Append(bobCarol).ToList();
        var carolId = new CompactPubKey(carol.LocalNodePubKeyBytes);
        var bobId = new CompactPubKey(bob.LocalNodePubKeyBytes);
        const ulong amountMsat = 10_000_000;

        var changes = new LndPolicyChanges();
        try
        {
            await changes.RefreshAsync(carol, [aliceCarol, bobCarol], 1, ct);
            var carolSilentSince = DateTimeOffset.UtcNow;
            await changes.RefreshAsync(alice, aliceKeeps, 1, ct);
            await changes.RefreshAsync(bob, bobKeeps, 1, ct);
            await changes.WaitSeenByAsync(alice, ct);
            await GossipTestNodes.SendFullTimestampFilterAsync(node, alice.LocalNodePubKeyBytes, ct);
            var before = await GetRouteProbe.WaitForRouteAsync(node, carolId, amountMsat, r => r.Found,
                                                               s_staleAfter, "a route to carol while fresh", ct);
            Assert.True(DateTimeOffset.UtcNow - carolSilentSince < s_staleAfter,
                        "the route to carol was only checked after carol's updates were already stale");
            Assert.Contains(before.ShortChannelIds, s => s == aliceCarol || s == bobCarol);

            // Act: alice and bob stay fresh, carol silent
            using var keepFresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var keeper = KeepFreshAsync(changes, [(alice, aliceKeeps), (bob, bobKeeps)], keepFresh.Token);
            RouteView after;
            try
            {
                after = await GetRouteProbe.WaitForRouteAsync(node, carolId, amountMsat, r => !r.Found,
                                                              s_staleAfter + s_timeout,
                                                              "no route to carol once her updates are stale", ct);
            }
            finally
            {
                await keepFresh.CancelAsync();
                await keeper;
            }

            // Assert: excluded only once stale, and alice-bob still routable
            var silentFor = DateTimeOffset.UtcNow - carolSilentSince;
            Console.WriteLine($"No route to carol after {silentFor} without her updates: {after}");
            Assert.True(silentFor >= s_staleAfter - s_clockTolerance,
                        $"carol's channels were excluded after {silentFor}, before {s_staleAfter}");
            var toBob = await GetRouteProbe.GetRouteAsync(node, bobId, amountMsat, ct);
            Assert.True(toBob.Found, $"no route to bob over the fresh alice-bob channels: {toBob}");
            Assert.DoesNotContain(toBob.ShortChannelIds, s => s == aliceCarol || s == bobCarol);
        }
        finally
        {
            await changes.RestoreAsync();
        }
    }

    /// <summary>
    /// Re-announces each node's policies every <see cref="s_keepFreshInterval"/> until cancelled.
    /// </summary>
    private static async Task KeepFreshAsync(LndPolicyChanges changes,
                                             IReadOnlyList<(LNDNodeConnection Lnd, List<ulong> Scids)> keepers,
                                             CancellationToken cancellationToken)
    {
        var round = 1;
        try
        {
            while (true)
            {
                await Task.Delay(s_keepFreshInterval, cancellationToken);
                round++;
                foreach (var (lnd, scids) in keepers)
                    await changes.RefreshAsync(lnd, scids, round, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // stopped by the test
        }
    }

    private static bool IsBetween((ulong ShortChannelId, string Local, string Remote, bool Private) channel,
                                  LNDNodeConnection a, LNDNodeConnection b)
    {
        var ids = new[] { a.LocalNodePubKey.ToLowerInvariant(), b.LocalNodePubKey.ToLowerInvariant() };
        return ids.Contains(channel.Local) && ids.Contains(channel.Remote);
    }

    private static async Task AssertStillSpentAsync(NLightningTestNode node, ulong scid, uint spendHeight, string when)
    {
        var stored = await GossipGraphProbe.TryGetOurGraphChannelAsync(node, scid);
        Console.WriteLine($"At {when}: {new ShortChannelId(scid)} "
                        + (stored is null ? "removed" : $"stored, spent at {stored.SpentAtHeight}"));
        Assert.NotNull(stored);
        Assert.Equal(spendHeight, stored.SpentAtHeight);
    }

    private IReadOnlyList<LNDNodeConnection> GossipLndNodes() =>
        [_fixture.GetLndNode("alice"), _fixture.GetLndNode("bob"), _fixture.GetLndNode("carol")];

    /// <summary>
    /// The fixture's LND-LND channels, once LND lists them as announced with both policies (6 confirmations).
    /// </summary>
    private async Task<IReadOnlyList<ulong>> GetFixtureScidsAsync(IReadOnlyList<LNDNodeConnection> lndNodes,
                                                                  CancellationToken ct)
    {
        var channels = await GossipGraphProbe.GetFixtureChannelsAsync(lndNodes, ct);
        Assert.NotEmpty(channels);
        Assert.All(channels, c => Assert.False(c.Private, $"{c.ShortChannelId} is private"));
        var alice = lndNodes[0];
        await GossipGraphProbe.MineUntilAsync(async () =>
        {
            foreach (var (scid, _, _, _) in channels)
                if (await GossipGraphProbe.TryGetChanInfoAsync(alice, scid, ct) is not
                    { Node1Policy: not null, Node2Policy: not null })
                    return false;

            return true;
        }, () => ChainSync.MineAndWaitAsync(_fixture, 1, lndNodes, [], ct), TimeSpan.FromSeconds(20), 6,
                                              s_timeout, "alice has every fixture channel with both policies", ct);
        return channels.Select(c => c.ShortChannelId).ToList();
    }

    /// <summary>
    /// Connects to alice and asks for her whole graph.
    /// </summary>
    private async Task SyncFromAliceAsync(NLightningTestNode node, CancellationToken ct)
    {
        var alice = _fixture.GetLndNode("alice");
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);
        await node.ConnectToAsync(alice, ct);
        await GossipTestNodes.SendFullTimestampFilterAsync(node, alice.LocalNodePubKeyBytes, ct);
    }

    /// <summary>
    /// <paramref name="funder"/> opens a public 1M sat channel to <paramref name="peer"/> (funding its wallet first if
    /// needed) and mines 6 blocks; returns the short channel id and LND's channel point.
    /// </summary>
    private async Task<(ulong ShortChannelId, ChannelPoint ChannelPoint)> OpenPublicLndChannelAsync(
        LNDNodeConnection funder, LNDNodeConnection peer, NLightningTestNode node, CancellationToken ct)
    {
        await EnsureLndFundsAsync(funder, node, ct);
        var peerHost = (await Dns.GetHostAddressesAsync(peer.Host.SplitOnFirst("//")[1].SplitOnFirst(":")[0], ct))
           .First();
        if (!await LndTestHelpers.IsConnectedToAsync(funder, peer.LocalNodePubKey, ct))
            await funder.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
            {
                Addr = new LightningAddress { Pubkey = peer.LocalNodePubKey, Host = $"{peerHost}:9735" }
            }, cancellationToken: ct);

        var point = await funder.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom(peer.LocalNodePubKeyBytes),
            LocalFundingAmount = 1_000_000,
            Private = false
        }, cancellationToken: ct);
        var channelPoint =
            $"{Convert.ToHexStringLower(point.FundingTxidBytes.ToByteArray().Reverse().ToArray())}:{point.OutputIndex}";
        await ChainSync.MineAndWaitAsync(_fixture, 6, _fixture.LndNodes, [node], ct);
        var channel = await Poll.ForAsync(() => LndTestHelpers.GetChannelByPointAsync(funder, channelPoint, ct),
                                          s_timeout, $"{funder.LocalAlias} lists {channelPoint}", ct,
                                          GossipGraphProbe.PollInterval);
        Console.WriteLine($"{funder.LocalAlias} opened public {channelPoint} to {peer.LocalAlias}: "
                        + $"{new ShortChannelId(channel.ChanId)} ({channel.ChanId})");
        return (channel.ChanId, point);
    }

    /// <summary>
    /// Sends 0.1 BTC to <paramref name="lnd"/>'s wallet when it holds less than 0.05 BTC (david starts without
    /// channels; other suites may have spent his coins).
    /// </summary>
    private async Task EnsureLndFundsAsync(LNDNodeConnection lnd, NLightningTestNode node, CancellationToken ct)
    {
        var balance = await lnd.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(), cancellationToken: ct);
        Console.WriteLine($"{lnd.LocalAlias} confirmed wallet balance {balance.ConfirmedBalance} sat");
        if (balance.ConfirmedBalance >= 5_000_000)
            return;

        var address = await lnd.LightningClient.NewAddressAsync(
                          new NewAddressRequest { Type = AddressType.WitnessPubkeyHash }, cancellationToken: ct);
        await _fixture.Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Address, NBitcoin.Network.RegTest),
                                                  Money.Coins(0.1m), cancellationToken: ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, [node], ct);
    }

    private static async Task CloseCooperativelyAsync(LNDNodeConnection lnd, ChannelPoint point, CancellationToken ct)
    {
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest { ChannelPoint = point },
                                                               cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);
        Console.WriteLine($"{lnd.LocalAlias} close pending: "
                        + Convert.ToHexStringLower(pending.Txid.ToByteArray().Reverse().ToArray()));
    }
}