using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Day0;

using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Gossip;
using Utils;

/// <summary>
/// NL-880 and NL-881 on the cluster: the 2026-10-03 Mutinynet liquidity-ads test opened a dual-funded channel
/// between two NLightning nodes, spliced it from 140k to 210k, and the accepter still could send at most
/// 48,000,000 msat (80 % of the opener's own 60k share, announced in <c>open_channel2</c> and fixed for the channel's
/// lifetime by BOLT 2). Here A opens 100,000 sat dual-funded to B (B contributes nothing), splices in 200,000 sat,
/// and each side pays the other more than the 80,000 sat (80 % of the opening capacity) the old limits allowed: A
/// pays B 150,000 sat (bound by B's <c>accept_channel2</c> limit), then B pays A 120,000 sat (bound by A's
/// <c>open_channel2</c> limit). Both nodes negotiate <c>option_splice</c> (on by default), so both announce no cap.
/// </summary>
/// <remarks>
/// In the gossip collection's process (the fixture's bitcoind, no LND node): <c>scripts/run-cluster.sh -n 1 --suite
/// day0 --class NLightning.Integration.Tests.Docker.Day0.Day0SpliceInFlightTests</c>.
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public sealed class Day0SpliceInFlightTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 20 * 60 * 1_000;
    private const long OpenSat = 100_000;
    private const ulong SpliceInSat = 200_000;
    private const long OldLimitSat = OpenSat * 80 / 100;
    private const long AToBSat = 150_000;
    private const long BToASat = 120_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public Day0SpliceInFlightTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
            {
                Console.WriteLine($"===== {node.Name}: last log lines =====");
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            }
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ADualFundedChannelGrownByASplice_When_EachSidePaysMoreThanTheOpeningLimit_Then_ThePaymentsSucceed()
    {
        // Arrange: a dual-funded 100,000 sat channel A -> B, B contributing nothing
        var ct = TestContext.Current.CancellationToken;
        var a = await StartNodeAsync("inflight-a", ct);
        var b = await StartNodeAsync("inflight-b", ct);
        await a.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        // B accepts an anchors channel only with its on-chain reserve (NL-379)
        await b.FundWalletAsync(LightningMoney.Satoshis(200_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(a, b, ct);
        var opened = await Day0Harness.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         a, new OpenChannelClientRequest(b.Address, LightningMoney.Satoshis(OpenSat))
                         {
                             IsDualFunded = true
                         }, ct);
        var channelId = opened.ChannelId;
        var (openA, _) = await Day0Harness.MineUntilUsableAsync(_fixture, [], a, b, channelId, ct);
        Assert.Equal(OpenSat, openA.Capacity.Satoshi);
        Assert.Equal(ChannelVersion.V2, Channel(a, channelId).Version);
        // option_splice was negotiated, so neither side capped the other's HTLCs in flight (NL-880); each stores the
        // value the other announced
        foreach (var (node, other) in new[] { (a, b), (b, a) })
        {
            var parameters = Channel(node, channelId).ChannelParams;
            Console.WriteLine($"[inflight] {node.Name}: ours {parameters.Local.MaxHtlcValueInFlight.MilliSatoshi} msat, "
                            + $"the peer's {parameters.Remote.MaxHtlcValueInFlight.MilliSatoshi} msat");
            Assert.Equal(ulong.MaxValue, parameters.Local.MaxHtlcValueInFlight.MilliSatoshi);
            Assert.Equal(Channel(other, channelId).ChannelParams.Local.MaxHtlcValueInFlight,
                         parameters.Remote.MaxHtlcValueInFlight);
        }

        // ...grown to 300,000 sat by A's splice-in
        var spliceIn = await Day0Harness.SpliceInAsync(a, channelId, SpliceInSat, ct);
        var spliceTxId = Day0Harness.AssertSigned(spliceIn);
        var (lockedA, lockedB) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, [], a, b, channelId,
                                                                              spliceTxId, ct);
        Assert.Equal(OpenSat + (long)SpliceInSat, lockedA.Capacity.Satoshi);
        Assert.Equal(lockedA.Capacity, lockedB.Capacity);
        // Our channel_update follows the capacity (BOLT 7: at most the capacity and the peer's in-flight limit)
        Assert.Equal(lockedA.Capacity.MilliSatoshi, lockedA.HtlcMaximumMsat);
        Assert.Equal(lockedB.Capacity.MilliSatoshi, lockedB.HtlcMaximumMsat);

        // Act / Assert: A pays B above the old 80,000 sat limit (B's accept_channel2), then B pays A above it (A's
        // open_channel2; the Mutinynet failure: "can send at most 48000000 msat")
        Assert.True(AToBSat > OldLimitSat && BToASat > OldLimitSat);
        await Day0Harness.PayAsync(a, b, AToBSat, "inflight a->b above the opening limit", ct);
        // B can spend what it received once the fulfill is irrevocably committed on both ends
        await Day0Harness.WaitSettledAsync(a, channelId, ct);
        await Day0Harness.WaitSettledAsync(b, channelId, ct);
        await Day0Harness.PayAsync(b, a, BToASat, "inflight b->a above the opening limit", ct);
        var settled = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        Assert.Equal(lockedA.LocalBalance.MilliSatoshi - (ulong)(AToBSat - BToASat) * 1_000,
                     settled.LocalBalance.MilliSatoshi);
    }

    private async Task<NLightningTestNode> StartNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name);
        _nodes.Add(node);
        Day0Harness.EnableDay0Features(node);
        node.ExtraConfiguration["Node:DualFund:AcceptContributionSat"] = "0";
        await node.StartAsync(ct);
        return node;
    }

    private static ChannelModel Channel(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{node.Name} has no channel {channelId}");
}