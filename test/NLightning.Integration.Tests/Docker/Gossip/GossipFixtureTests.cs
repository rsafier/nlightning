using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Capture;
using Fixtures;
using Utils;

/// <summary>
/// The preconditions of the BOLT 7 proofs on their own fixture, and Proof G0's Docker smoke: the LND-LND channels of
/// the fixture (alice-bob twice, alice-carol, bob-carol) are public, 6 blocks deep and in every LND's graph with both
/// policies, every LND has the others' <c>node_announcement</c>; and our node, sent alice's whole graph, parses every
/// message and stays connected.
/// </summary>
/// <remarks>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture).</remarks>
[Collection(GossipRegtestCollection.Name)]
public class GossipFixtureTests
{
    private const string DeserializeFailedLogFragment = "Failed to deserialize message";

    private static readonly TimeSpan s_graphTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_smokeDuration = TimeSpan.FromSeconds(60);

    private readonly LightningRegtestNetworkFixture _fixture;

    public GossipFixtureTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    [Fact]
    public async Task Given_GossipFixture_When_Started_Then_LndChannelsArePublicDeepAndInEveryLndGraph()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = GossipLndNodes();
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(lndNodes[0], ct)}");
        await ChainSync.WaitAllAtTipAsync(_fixture, lndNodes, [], ct);

        // Act
        var channels = await GossipGraphProbe.GetFixtureChannelsAsync(lndNodes, ct);

        // Assert: 4 public channels, alice-bob (both directions opened one), alice-carol, bob-carol
        Assert.Equal(4, channels.Count);
        Assert.All(channels, c => Assert.False(c.Private, $"{c.ShortChannelId} is private"));
        await AssertFundingDepthAsync(lndNodes, 6, ct);
        await GossipGraphProbe.MineUntilAsync(async () =>
        {
            foreach (var lnd in lndNodes)
                foreach (var (scid, _, _, _) in channels)
                    if (await GossipGraphProbe.TryGetChanInfoAsync(lnd, scid, ct) is not
                        { Node1Policy: not null, Node2Policy: not null })
                        return false;

            return true;
        }, () => ChainSync.MineAndWaitAsync(_fixture, 1, lndNodes, [], ct), TimeSpan.FromSeconds(20), 6,
                                              s_graphTimeout, "every fixture channel with both policies in every LND graph",
                                              ct);
        foreach (var lnd in lndNodes)
            foreach (var other in lndNodes.Where(o => o != lnd))
                Assert.NotNull(await Poll.ForAsync(
                                   () => GossipGraphProbe.TryGetNodeInfoAsync(lnd, other.LocalNodePubKey, ct),
                                   s_graphTimeout, $"{lnd.LocalAlias} has {other.LocalAlias}'s node_announcement", ct,
                                   GossipGraphProbe.PollInterval));
    }

    /// <summary>
    /// Proof G0 (Docker smoke): alice sends her whole graph after our <c>gossip_timestamp_filter</c>; we receive the
    /// <c>channel_announcement</c> and both <c>channel_update</c>s of every fixture channel and the
    /// <c>node_announcement</c> of alice, bob and carol (recorded on the wire by <see cref="RawGossipRecorder"/>, so a
    /// filter alice ignores fails the test), every one parses (no deserialize failure in our log) and the connection
    /// stays up for 60 s.
    /// </summary>
    [Fact]
    public async Task Given_ConnectedToAlice_When_AliceSendsHerGraph_Then_EveryMessageParsesAndTheConnectionStaysUp()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = GossipLndNodes();
        var alice = lndNodes[0];
        var channels = await GossipGraphProbe.GetFixtureChannelsAsync(lndNodes, ct);
        Assert.NotEmpty(channels);
        await GossipGraphProbe.MineUntilAsync(async () =>
        {
            foreach (var (scid, _, _, _) in channels)
                if (await GossipGraphProbe.TryGetChanInfoAsync(alice, scid, ct) is not
                    { Node1Policy: not null, Node2Policy: not null })
                    return false;

            foreach (var lnd in lndNodes.Skip(1))
                if (await GossipGraphProbe.TryGetNodeInfoAsync(alice, lnd.LocalNodePubKey, ct) is null)
                    return false;

            return true;
        }, () => ChainSync.MineAndWaitAsync(_fixture, 1, lndNodes, [], ct), TimeSpan.FromSeconds(20), 6,
                                              s_graphTimeout, "alice has the whole fixture graph", ct);
        var recorder = new RawGossipRecorder();
        await using var node = await GossipTestNodes.StartGossipNodeAsync(
                                   _fixture, "gossip-g0", "nltg-g0", ct,
                                   n => n.ConfigureServices = recorder.Install);
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);
        await node.ConnectToAsync(alice, ct);

        // Act
        await GossipTestNodes.SendFullTimestampFilterAsync(node, alice.LocalNodePubKeyBytes, ct);

        // Assert: alice's dump of the fixture's graph arrived (filtered to the fixture's own channels and nodes, since
        // alice also relays other tests' channels)
        var expectedScids = channels.Select(c => c.ShortChannelId).ToHashSet();
        var expectedNodes = lndNodes.Select(n => n.LocalNodePubKey.ToLowerInvariant()).ToHashSet();
        await Poll.UntilAsync(() =>
                              {
                                  var received = GossipWire.Summarize(recorder.Received.Select(r => r.Wire));
                                  var missing = GossipWire.Missing(received, expectedScids, expectedNodes);
                                  Console.WriteLine(missing.Count == 0
                                                        ? $"Received {received}: the fixture's graph is complete"
                                                        : $"Received {received}; missing {string.Join(", ", missing)}");
                                  return missing.Count == 0;
                              }, s_graphTimeout, "alice's dump of the fixture graph received", ct,
                              GossipGraphProbe.PollInterval);
        await Poll.StaysTrueAsync(() => node.IsConnectedTo(alice.LocalNodePubKeyBytes), s_smokeDuration,
                                  "connected to alice", ct);
        Assert.Equal(0, node.CountLogLines(DeserializeFailedLogFragment));
    }

    private IReadOnlyList<LNUnit.LND.LNDNodeConnection> GossipLndNodes() =>
        [_fixture.GetLndNode("alice"), _fixture.GetLndNode("bob"), _fixture.GetLndNode("carol")];

    /// <summary>
    /// Every funding transaction of the fixture's LND-LND channels has at least <paramref name="depth"/>
    /// confirmations (BOLT 7: announced at 6); mines the difference first.
    /// </summary>
    private async Task AssertFundingDepthAsync(IReadOnlyList<LNUnit.LND.LNDNodeConnection> lndNodes, int depth,
                                               CancellationToken ct)
    {
        var fundingTxIds = new HashSet<uint256>();
        foreach (var lnd in lndNodes)
        {
            var list = await lnd.LightningClient.ListChannelsAsync(new Lnrpc.ListChannelsRequest(),
                                                                   cancellationToken: ct);
            foreach (var channel in list.Channels)
                fundingTxIds.Add(uint256.Parse(channel.ChannelPoint.Split(':')[0]));
        }

        var shallowest = int.MaxValue;
        foreach (var txId in fundingTxIds)
        {
            var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(txId);
            Console.WriteLine($"Funding {txId}: {info.Confirmations} confirmations");
            shallowest = Math.Min(shallowest, (int)info.Confirmations);
        }

        if (shallowest < depth)
            await ChainSync.MineAndWaitAsync(_fixture, depth - shallowest, lndNodes, [], ct);
        Assert.True(fundingTxIds.Count > 0);
    }
}