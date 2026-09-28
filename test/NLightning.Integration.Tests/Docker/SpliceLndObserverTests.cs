using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Day0;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Gossip;
using Utils;

/// <summary>
/// NL-496 (splicing plan Proof SP2 (c) against LND): LND 0.20, which does not splice itself, learns a public channel
/// that two NLightning nodes spliced. A and B share a public v1 channel (A funds it); A also has a public channel to
/// LND alice, and B is alice's peer without a channel. A splices in; after the lock both ends announce the channel again
/// under the splice's short channel id at 6 confirmations. Alice (a direct peer of both) and bob (who hears it only
/// from alice) list the new edge on the splice's outpoint with the new capacity and both policies, alice routes a
/// payment to B over the new short channel id, and both forget the old one within BOLT 7's 72-block delay.
/// </summary>
/// <remarks>
/// Written by wave spr lane SPR-E for the integrator. Public channels change the LND nodes' graph for good, so it runs
/// in the gossip collection, in its own process:
/// <c>scripts/run-gossip.sh 1 Release -class NLightning.Integration.Tests.Docker.SpliceLndObserverTests</c>. Both
/// nodes run the day-0 feature set (<see cref="Day0Harness.EnableDay0Features"/>) and flush their own gossip every
/// 5 s. Unlike the day-0 script the channel is a v1 (not dual-funded) open, so what LND sees depends on the splice
/// alone.
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public sealed class SpliceLndObserverTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 30 * 60 * 1_000;

    private const long ChannelCapacitySat = 600_000;
    private const long ChannelPushSat = 100_000;
    private const long AliceChannelPushSat = 400_000;
    private const ulong SpliceInSat = 250_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public SpliceLndObserverTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
            {
                Console.WriteLine($"===== {node.Name}: last log lines =====");
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            }

            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "bob"]);
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ASplicedPublicChannel_When_Locked_Then_LndLearnsTheNewScidRoutesOverItAndForgetsTheOld()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var bob = _fixture.GetLndNode("bob");
        IReadOnlyList<LNDNodeConnection> observers = [alice];

        // Arrange: A (public channel to alice, who gets a push so she can pay B through A) and B, both splice-capable
        var a = await StartNodeAsync("splice-lnd-a", "nltg-splice-lnd-a", ct);
        var b = await StartNodeAsync("splice-lnd-b", "nltg-splice-lnd-b", ct);
        var aliceChannel = await PublicTopology.OpenPublicChannelToAliceAsync(
                               _fixture, a, LightningMoney.Satoshis(AliceChannelPushSat), observers, ct,
                               syncGraph: false);
        Console.WriteLine($"[splice-lnd] A's channel to alice: {new ShortChannelId(aliceChannel.ShortChannelId)}");
        await a.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(a, b, ct);
        await b.ConnectToAsync(alice, ct);

        // A public v1 channel A -> B, announced at 6 confirmations
        var opened = await a.OpenChannelAsync(
                         GossipTestNodes.MarkPublic(new OpenChannelClientRequest(
                                                        b.Address, LightningMoney.Satoshis(ChannelCapacitySat))
                         {
                             PushAmount = LightningMoney.Satoshis(ChannelPushSat)
                         }), ct);
        var channelId = opened.ChannelId;
        var (openA, openB) = await Day0Harness.MineUntilUsableAsync(_fixture, observers, a, b, channelId, ct);
        var scidOpen = openA.ShortChannelId!.Value.ToUInt64();
        Assert.Equal(scidOpen, openB.ShortChannelId!.Value.ToUInt64());
        var openEdge = await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidOpen, a, b, ct);
        Assert.Equal(ChannelCapacitySat, openEdge.Capacity);
        Assert.Equal(ChannelPoint(openA), openEdge.ChanPoint);

        // Act: A splices in; the splice locks on both ends
        var before = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        var splice = await Day0Harness.SpliceInAsync(a, channelId, SpliceInSat, ct);
        var spliceTxId = Day0Harness.AssertSigned(splice);
        var (lockedA, lockedB) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, observers, a, b, channelId,
                                                                              spliceTxId, ct);
        Assert.NotNull(lockedA.ShortChannelId);
        var scidSplice = lockedA.ShortChannelId.Value.ToUInt64();
        Assert.Equal(scidSplice, lockedB.ShortChannelId!.Value.ToUInt64());
        Assert.NotEqual(scidOpen, scidSplice);
        Assert.Equal(before.Capacity.Satoshi + (long)SpliceInSat, lockedA.Capacity.Satoshi);
        Assert.Equal(spliceTxId, Day0Harness.ToUint256(lockedA.FundingTxId!.Value));

        // Assert (1): alice, a direct peer of both ends, has the new edge on the splice's outpoint and capacity
        var edge = await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidSplice, a, b, ct);
        Console.WriteLine($"[splice-lnd] alice's edge {new ShortChannelId(scidSplice)}: {edge.ChanPoint}, capacity "
                        + $"{edge.Capacity}");
        Assert.Equal(ChannelPoint(lockedA), edge.ChanPoint);
        Assert.Equal(lockedA.Capacity.Satoshi, edge.Capacity);

        // (2) bob hears it only through alice's relay
        var bobEdge = await Day0Harness.WaitLndHasChannelAsync(_fixture, bob, scidSplice, a, b, ct);
        Assert.Equal(ChannelPoint(lockedA), bobEdge.ChanPoint);

        // (3) alice routes to B over the new short channel id
        var payment = await Day0Harness.LndPaysAsync(alice, b, 20_000, "splice-lnd alice->a->b", ct);
        var route = payment.Htlcs.Single(h => h.Status == Lnrpc.HTLCAttempt.Types.HTLCStatus.Succeeded).Route;
        Assert.Equal(b.NodeIdHex, route.Hops[^1].PubKey, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(scidSplice, route.Hops[^1].ChanId);

        // (4) both forget the old short channel id within BOLT 7's delay after the splice spent its funding output
        var aliceDelay = await Day0Harness.WaitLndForgotChannelAsync(_fixture, alice, scidOpen, a, b, ct);
        var bobDelay = await Day0Harness.WaitLndForgotChannelAsync(_fixture, bob, scidOpen, a, b, ct);
        Console.WriteLine($"[splice-lnd] alice forgot {new ShortChannelId(scidOpen)} after {aliceDelay} more "
                        + $"block(s), bob after {bobDelay} more");
        Assert.NotNull(await GossipGraphProbe.TryGetChanInfoAsync(alice, scidSplice, ct));

        // And the channel still carries payments both ways between the NLightning nodes
        await Day0Harness.PayAsync(a, b, 10_000, "splice-lnd a->b", ct);
        await Day0Harness.PayAsync(b, a, 5_000, "splice-lnd b->a", ct);
    }

    /// <summary>LND's <c>txid:index</c> of the channel's current funding.</summary>
    private static string ChannelPoint(ChannelInfoClientResponse channel)
    {
        Assert.NotNull(channel.FundingTxId);
        Assert.NotNull(channel.FundingOutputIndex);
        return $"{Day0Harness.ToUint256(channel.FundingTxId.Value)}:{channel.FundingOutputIndex}";
    }

    private async Task<NLightningTestNode> StartNodeAsync(string name, string alias, CancellationToken ct)
    {
        var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, name, alias, ct, n =>
        {
            Day0Harness.EnableDay0Features(n);
            n.ExtraConfiguration["Gossip:OwnGossipFlushInterval"] = "00:00:05";
        });
        _nodes.Add(node);
        return node;
    }
}