using System.Collections.Concurrent;
using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker;

using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Protocol.Messages;
using Fixtures;
using Mock;
using TestCollections;
using Utils;

/// <summary>
/// BOLT2 plan proofs for N0 and N1 (a channel we open to LND stays usable while idle) and N6 (an HTLC from LND is
/// locked in and failed back with an error onion LND can read).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class NormalOperationFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_activeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_idleTime = TimeSpan.FromSeconds(30);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly NLightningTestNode _node;
    private readonly ConcurrentBag<ChannelId> _sentChannelReady = [];

    public NormalOperationFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));

        var port = PortPoolUtil.GetAvailablePortAsync().GetAwaiter().GetResult();
        Assert.True(port > 0);
        _node = new NLightningTestNode(fixture, $"nlightning_normal_op_{Guid.NewGuid()}.db",
                                       new FakeSecureKeyManager(), port);
    }

    public async ValueTask InitializeAsync()
    {
        await _node.StartAsync(TestContext.Current.CancellationToken);

        // Every channel message we send goes through this event (replies included, N0-T3)
        _node.Services.GetRequiredService<IChannelManager>().OnResponseMessageReady += RecordChannelReady;
    }

    [Fact]
    public async Task Given_NewChannel_When_Idle_Then_PeerStaysConnected()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();

        // Act
        var (channel, lndChannel) =
            await OpenChannelAndWaitUntilActiveAsync(alice, LightningMoney.Satoshis(1_000_000), null, ct);
        await Task.Delay(s_idleTime, ct);

        // Assert
        lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        Assert.True(lndChannel.Active, "LND no longer lists the channel as active after 30 s idle");

        var alicePeers = await alice.LightningClient.ListPeersAsync(new ListPeersRequest(), cancellationToken: ct);
        Assert.Contains(alicePeers.Peers, p => p.PubKey.Equals(OurNodeIdHex, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(_node.PeerManager.GetPeer(alice.LocalNodePubKeyBytes));

        var ours = await GetOurChannelAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelState.Open, ours.State);
        Assert.True(ours.IsPeerConnected);
        Assert.Single(_sentChannelReady, id => id == channel.ChannelId);
    }

    [Fact]
    public async Task Given_ChannelWithPush_When_Idle_Then_LndActiveAndBalancesAgree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var capacity = LightningMoney.Satoshis(1_000_000);
        var push = LightningMoney.Satoshis(300_000);

        // Act
        var (channel, lndChannel) = await OpenChannelAndWaitUntilActiveAsync(alice, capacity, push, ct);
        await Task.Delay(s_idleTime, ct);

        // Assert
        lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        Assert.True(lndChannel.Active, "LND no longer lists the channel as active after 30 s idle");
        Assert.False(lndChannel.Initiator);
        Assert.Equal(capacity.Satoshi, lndChannel.Capacity);

        var ours = await GetOurChannelAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelState.Open, ours.State);
        Assert.Equal(capacity, ours.Capacity);

        // LND is the non-funder: its balance is exactly the push; ours is the rest, from which LND's view of our
        // side also deducts the commitment fee we pay as the funder (no anchors, plan D5)
        Assert.Equal(push.Satoshi, lndChannel.LocalBalance);
        Assert.Equal(push.MilliSatoshi, ours.RemoteBalance.MilliSatoshi);
        Assert.Equal((capacity - push).MilliSatoshi, ours.LocalBalance.MilliSatoshi);
        Assert.Equal(ours.LocalBalance.Satoshi, lndChannel.RemoteBalance + lndChannel.CommitFee);
        Assert.Single(_sentChannelReady, id => id == channel.ChannelId);
    }

    /// <summary>
    /// BOLT2 plan N6-T5: LND sends an HTLC to us over a channel we opened (<c>SendToRouteV2</c>, random payment
    /// hash); we lock it in, peel the onion, see we are the final hop without an invoice and fail it back with an
    /// encrypted <c>incorrect_or_unknown_payment_details</c>. LND decodes our failure, and the channel stays active
    /// with both commitment numbers at 2 (one commitment for the add, one for the removal, each way).
    /// </summary>
    [Fact]
    public async Task Given_LndPaysUs_When_LockedIn_Then_FailedBackAndChannelActive()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenChannelAndWaitUntilActiveAsync(
                                        alice, LightningMoney.Satoshis(1_000_000), LightningMoney.Satoshis(300_000),
                                        ct);
        const long amountMsat = 10_000_000;
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var route = await alice.RouterClient.BuildRouteAsync(new BuildRouteRequest
        {
            AmtMsat = amountMsat,
            FinalCltvDelta = 40,
            OutgoingChanId = lndChannel.ChanId,
            HopPubkeys = { ByteString.CopyFrom(_node.NodeId) }
        }, cancellationToken: ct);

        // Act
        var attempt = await alice.RouterClient.SendToRouteV2Async(new Routerrpc.SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Route = route.Route
        }, cancellationToken: ct);

        // Assert - LND read our error onion: the failure comes from us (index 1, the final hop)
        Console.WriteLine($"SendToRouteV2: {attempt.Status}, {attempt.Failure?.Code}, index {attempt.Failure?.FailureSourceIndex}, height {attempt.Failure?.Height}");
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.NotNull(attempt.Failure);
        Assert.Equal(Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails, attempt.Failure.Code);
        Assert.Equal(1u, attempt.Failure.FailureSourceIndex);
        Assert.True(attempt.Failure.Height > 0, "our failure carried no block height");

        // The channel is still usable on both sides, nothing is pending, and both commitments moved twice
        await Poll.UntilAsync(async () =>
        {
            var ours = await GetOurChannelAsync(channel.ChannelId, ct);
            return ours is { LocalCommitmentNumber: 2, RemoteCommitmentNumber: 2 };
        }, s_activeTimeout, "our commitment numbers did not reach 2/2", ct);

        lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        Assert.True(lndChannel.Active, "LND no longer lists the channel as active after the failed HTLC");
        Assert.Empty(lndChannel.PendingHtlcs);
        Assert.Equal(300_000L, lndChannel.LocalBalance);

        var oursAfter = await GetOurChannelAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelState.Open, oursAfter.State);
        Assert.True(oursAfter.IsPeerConnected);
        Assert.Equal(0, oursAfter.OfferedHtlcCount + oursAfter.ReceivedHtlcCount);
        Assert.Equal(LightningMoney.Satoshis(300_000).MilliSatoshi, oursAfter.RemoteBalance.MilliSatoshi);
        Assert.True(await LndTestHelpers.IsConnectedToAsync(alice, OurNodeIdHex, ct));
        await Poll.StaysTrueAsync(() => _node.IsConnectedTo(alice.LocalNodePubKeyBytes), TimeSpan.FromSeconds(5),
                                  "LND disconnected after the failed HTLC", ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _node.Services.GetRequiredService<IChannelManager>().OnResponseMessageReady -= RecordChannelReady;
        }
        catch (InvalidOperationException)
        {
            // The node never started
        }

        await _node.DisposeAsync();
        _node.DeleteFiles();
        PortPoolUtil.ReleasePort(_node.Port);
        GC.SuppressFinalize(this);
    }

    private string OurNodeIdHex => Convert.ToHexString(_node.SecureKeyManager.GetNodePubKey());

    private LNDNodeConnection GetAlice()
    {
        var alice = _fixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == "alice");
        Assert.NotNull(alice);
        return alice;
    }

    private void RecordChannelReady(object? _, ChannelResponseMessageEventArgs args)
    {
        if (args.ResponseMessage is ChannelReadyMessage channelReady)
            _sentChannelReady.Add(channelReady.Payload.ChannelId);
    }

    private async Task<(OpenChannelClientSubscriptionResponse Channel, Channel LndChannel)>
        OpenChannelAndWaitUntilActiveAsync(LNDNodeConnection alice, LightningMoney capacity, LightningMoney? push,
                                           CancellationToken ct)
    {
        await _node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await _node.ConnectToAsync(alice, ct);

        var channel = await _node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress, capacity)
        {
            PushAmount = push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({ChannelPoint(channel)}), state {channel.ChannelState}");

        // Wait until both sides consider the channel usable
        var deadline = DateTime.UtcNow + s_activeTimeout;
        while (true)
        {
            var lndChannel = await GetLndChannelAsync(alice, channel, ct);
            var ours = await GetOurChannelAsync(channel.ChannelId, ct);
            if (lndChannel is { Active: true } && ours.State == ChannelState.Open)
                return (channel, lndChannel);

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Channel not active in time: LND active={lndChannel?.Active}, ours={ours.State}");

            // LND may want more confirmations than we do
            await _node.MineBlocksAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<Channel?> GetLndChannelAsync(LNDNodeConnection alice,
                                                    OpenChannelClientSubscriptionResponse channel,
                                                    CancellationToken ct)
    {
        var channels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: ct);
        var ours = channels.Channels
                           .Where(c => c.RemotePubkey.Equals(OurNodeIdHex, StringComparison.OrdinalIgnoreCase))
                           .ToList();
        var channelPoint = ChannelPoint(channel);
        return ours.FirstOrDefault(c => c.ChannelPoint.Equals(channelPoint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ChannelInfoClientResponse> GetOurChannelAsync(ChannelId channelId, CancellationToken ct)
    {
        var channels = await _node.ListChannelsAsync(ct);
        return Assert.Single(channels.Channels, c => c.ChannelId == channelId);
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