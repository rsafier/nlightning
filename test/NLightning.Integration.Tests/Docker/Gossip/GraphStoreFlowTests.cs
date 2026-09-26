using System.Net;
using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;
using NBitcoin;
using ServiceStack;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Channels.ValueObjects;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 7 plan Proof G2 (validation and graph store) against LND 0.20: (a) connected to alice, our graph gets the
/// fixture's LND-LND channels with both policies and the alice, bob and carol node announcements; (b) they are still
/// there after a restart before any connection; (c) a public channel closed cooperatively is marked spent one block
/// after the close, still stored (and spent) 70 blocks after the spend, and removed 72 blocks after it.
/// </summary>
/// <remarks>
/// <para>(c) closes a channel the test opens itself (david-carol, public), not the fixture's alice-carol the plan
/// names: the other proofs of this collection relay through the fixture channels, and test order is not fixed.</para>
/// <para>(c)'s "excluded from getroute" waits for G4 (IPC <c>getroute</c>); (d) (stale after
/// <c>Gossip:StaleAfter</c>) stops carol, a shared LND node, and stays with the integrator.</para>
/// <para>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture).</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class GraphStoreFlowTests
{
    /// <summary>BOLT 7: a spent channel is forgotten 72 blocks after the spend.</summary>
    private const int SpentForgetDepth = 72;

    private static readonly TimeSpan s_syncTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

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

        // Act: restart; the node has no channels, so it connects to nobody at startup
        await node.StopAsync();
        await node.StartAsync(ct);

        // Assert: read before any connection
        // through listgraphchannels/listnodes (IPC 18/17), which read the in-memory GraphStore: this is its reload
        Assert.False(node.IsConnectedTo(alice.LocalNodePubKeyBytes));
        Assert.True(await GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes));
        Assert.False(node.IsConnectedTo(alice.LocalNodePubKeyBytes));
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
        // TODO(G4-T4): also assert the channel is excluded from getroute
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