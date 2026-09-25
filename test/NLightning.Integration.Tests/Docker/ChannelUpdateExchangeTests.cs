using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker;

using Application.Gossip.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// ABCD W1-E proof: the direct <c>channel_update</c> exchange with LND on a private channel we opened. LND must show
/// our routing policy for our side of the graph edge (<c>GetChanInfo</c>), and we must accept and keep LND's own
/// update (its node signature checked).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class ChannelUpdateExchangeTests : IAsyncLifetime
{
    private const uint FeeBaseMsat = 1_234;
    private const uint FeePpm = 567;
    private const ushort CltvExpiryDelta = 44;

    private const string SendingChannelUpdateLog = "Sending channel_update for channel";

    private static readonly TimeSpan s_activeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_policyTimeout = TimeSpan.FromSeconds(30);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public ChannelUpdateExchangeTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "bob", configureNodeOptions: options =>
        {
            options.Routing.FeeBaseMsat = FeeBaseMsat;
            options.Routing.FeeProportionalMillionths = FeePpm;
            options.Routing.CltvExpiryDelta = CltvExpiryDelta;
        });
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_ChannelWeOpenedToLnd_When_Open_Then_LndHasOurPolicyAndWeHaveLnds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");

        // Act
        var (channel, lndChannel) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        var ourChannel = Assert.Single((await node.ListChannelsAsync(ct)).Channels,
                                       c => c.ChannelId == channel.ChannelId);
        Console.WriteLine($"Channel {channel.ChannelId}: LND chan_id {lndChannel.ChanId}");

        // Assert: LND's graph edge carries our policy for our direction
        var ourPolicy = await Poll.ForAsync<RoutingPolicy>(async () =>
        {
            var info = await alice.LightningClient.GetChanInfoAsync(new ChanInfoRequest { ChanId = lndChannel.ChanId },
                                                                    cancellationToken: ct);
            var policy = info.Node1Pub.Equals(node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                             ? info.Node1Policy
                             : info.Node2Policy;
            return policy is { FeeBaseMsat: > 0 } ? policy : null;
        }, s_policyTimeout, "alice has our channel policy", ct);

        Assert.Equal(FeeBaseMsat, (uint)ourPolicy.FeeBaseMsat);
        Assert.Equal(FeePpm, (uint)ourPolicy.FeeRateMilliMsat);
        Assert.Equal(CltvExpiryDelta, (ushort)ourPolicy.TimeLockDelta);
        Assert.False(ourPolicy.Disabled);
        Assert.True(ourPolicy.MaxHtlcMsat > 0 && ourPolicy.MaxHtlcMsat <= (ulong)lndChannel.Capacity * 1_000);

        // Not asserted: `addinvoice --private` still gives no hint through us. LND only hints through a public node
        // (invoices.chanCanBeHopHint checks IsPublicNode), and we send no node_announcement (NL-099). The ABCD tests
        // pass explicit route hints instead (roadmap decision 1).

        // Assert: we checked and kept LND's own update for its direction
        var channelUpdateService = node.Services.GetRequiredService<IChannelUpdateService>();
        var aliceUpdate = await Poll.ForAsync(
            () => channelUpdateService.TryGetRemoteChannelUpdate(channel.ChannelId, out var update) ? update : null,
            s_policyTimeout, "we have alice's channel_update", ct);
        var aliceInfo = await alice.LightningClient.GetChanInfoAsync(new ChanInfoRequest { ChanId = lndChannel.ChanId },
                                                                     cancellationToken: ct);
        var alicePolicy = aliceInfo.Node1Pub.Equals(node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                              ? aliceInfo.Node2Policy
                              : aliceInfo.Node1Policy;
        Assert.Equal(ourChannel.ShortChannelId ?? new ShortChannelId(lndChannel.ChanId), aliceUpdate.ShortChannelId);
        Assert.Equal((uint)alicePolicy.FeeBaseMsat, aliceUpdate.FeeBaseMsat);
        Assert.Equal((uint)alicePolicy.FeeRateMilliMsat, aliceUpdate.FeeProportionalMillionths);
        Assert.Equal((ushort)alicePolicy.TimeLockDelta, aliceUpdate.CltvExpiryDelta);
        AssertNoIgnoredUpdate(node);

        // Act: reconnect. The new connection must carry a fresh update (newer timestamp) of the same policy
        var sentBefore = node.CountLogLines(SendingChannelUpdateLog);
        await ReconnectAsync(node, alice, ct);

        // Assert
        var refreshed = await Poll.ForAsync<RoutingPolicy>(async () =>
        {
            var policy = await GetOurPolicyAsync(node, alice, lndChannel.ChanId, ct);
            return policy is not null && policy.LastUpdate > ourPolicy.LastUpdate ? policy : null;
        }, s_policyTimeout, "alice has our channel_update sent after the reconnect", ct);
        Assert.True(node.CountLogLines(SendingChannelUpdateLog) > sentBefore);
        Assert.Equal(FeeBaseMsat, (uint)refreshed.FeeBaseMsat);
        Assert.Equal(FeePpm, (uint)refreshed.FeeRateMilliMsat);
        Assert.Equal(CltvExpiryDelta, (ushort)refreshed.TimeLockDelta);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);

        if (_node is not null)
            await _node.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private static async Task<RoutingPolicy?> GetOurPolicyAsync(NLightningTestNode node, LNDNodeConnection alice,
                                                               ulong chanId, CancellationToken ct)
    {
        var info = await alice.LightningClient.GetChanInfoAsync(new ChanInfoRequest { ChanId = chanId },
                                                                cancellationToken: ct);
        return info.Node1Pub.Equals(node.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                   ? info.Node1Policy
                   : info.Node2Policy;
    }

    /// <summary>
    /// Drops the connection to alice and connects again. Alice may reconnect first (we are a channel peer); either
    /// way a new connection is installed.
    /// </summary>
    private static async Task ReconnectAsync(NLightningTestNode node, LNDNodeConnection alice, CancellationToken ct)
    {
        CompactPubKey aliceId = alice.LocalNodePubKeyBytes;
        node.PeerManager.DisconnectPeer(aliceId);
        await Poll.UntilAsync(() => Task.FromResult(!node.IsConnectedTo(aliceId)), s_policyTimeout,
                              "alice disconnected", ct);

        try
        {
            await node.ConnectToAsync(alice, ct);
        }
        catch (InvalidOperationException)
        {
            // Alice's own reconnect won
        }

        await Poll.UntilAsync(() => Task.FromResult(node.IsConnectedTo(aliceId)), s_policyTimeout,
                              "alice connected again", ct);
    }

    private static void AssertNoIgnoredUpdate(NLightningTestNode node)
    {
        Assert.Equal(0, node.CountLogLines("Ignoring channel_update"));
    }

    private static async Task<(OpenChannelClientSubscriptionResponse Channel, Channel LndChannel)>
        OpenChannelAndWaitUntilActiveAsync(NLightningTestNode node, LNDNodeConnection alice, CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);

        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress,
                                                                               LightningMoney.Satoshis(1_000_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        var channelPoint = ChannelPoint(channel);

        var deadline = DateTime.UtcNow + s_activeTimeout;
        while (true)
        {
            var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
            var ours = Assert.Single((await node.ListChannelsAsync(ct)).Channels,
                                     c => c.ChannelId == channel.ChannelId);
            if (lndChannel is { Active: true } && ours.State == ChannelState.Open)
                return (channel, lndChannel);

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Channel not active in time: LND active={lndChannel?.Active}, ours={ours.State}");

            // LND may want more confirmations than we do
            await node.MineBlocksAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// LND's <c>txid:index</c>: the txid in display order, which is our stored (internal order) txid reversed.
    /// </summary>
    private static string ChannelPoint(OpenChannelClientSubscriptionResponse channel)
    {
        Assert.NotNull(channel.TxId);
        var displayOrder = ((byte[])channel.TxId.Value).Reverse().ToArray();
        return $"{Convert.ToHexString(displayOrder).ToLowerInvariant()}:{channel.Index}";
    }
}