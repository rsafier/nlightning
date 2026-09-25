using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker;

using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Protocol.Messages;
using Fixtures;
using Infrastructure.Transport.Interfaces;
using TestCollections;
using Utils;

/// <summary>
/// BOLT2 plan Proof N7 against LND: (a) we restart, (b) LND restarts, (c) we crash right after persisting a
/// commitment_signed and before LND's revoke_and_ack arrives. Each time channel_reestablish brings the channel back
/// Active on both sides without a force close, and an HTLC from LND is then (or still) locked in and failed back with
/// both commitment numbers consistent (2/2).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class ReestablishFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_activeTimeout = TimeSpan.FromSeconds(90);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private const long AmountMsat = 10_000_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public ReestablishFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "reestablish");
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Proof N7 (a): an idle restart of our node (same database and key).</summary>
    [Fact]
    public async Task Given_OurNodeRestarts_When_Reconnected_Then_ReestablishedAndHtlcsFlowAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenChannelAndWaitUntilActiveAsync(alice, ct);

        // Act
        await Node.StopAsync();
        await Node.StartAsync(ct);
        await WaitUntilReestablishedAsync(alice, channel, ct);

        // Assert - still Active on both sides with no force close, and an HTLC goes through the whole dance
        Assert.True(Node.CountLogLines("reestablished with peer") >= 1);
        await AssertNoForceCloseAsync(alice, channel, ct);
        await PayAndExpectFailBackAsync(alice, lndChannel, ct);
        await AssertSettledAtTwoTwoAsync(alice, channel, ct);
    }

    /// <summary>Proof N7 (b): LND restarts (the alice container) and we reconnect with backoff.</summary>
    /// <remarks>
    /// Explicit: restarting the shared alice can change its container address (another test's container may take the
    /// released IP), which breaks every later test of the collection that talks to alice. Run it on its own:
    /// <c>dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~Docker.ReestablishFlowTests" --
    /// xUnit.Explicit=on</c>.
    /// </remarks>
    [Fact(Explicit = true)]
    public async Task Given_LndRestarts_When_Reconnected_Then_ReestablishedAndHtlcsFlowAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (channel, _) = await OpenChannelAndWaitUntilActiveAsync(GetAlice(), ct);

        // Act - a plain container restart (same container, network and data; LNUnit's RestartByAlias can leave the
        // shared alice unreachable for the tests that follow when it runs into its timeouts)
        using (var docker = new DockerClientConfiguration().CreateClient())
            await docker.Containers.RestartContainerAsync("alice", new ContainerRestartParameters
            {
                WaitBeforeKillSeconds = 1
            }, ct);
        var alice = await WaitUntilLndSyncedAsync(ct);
        await WaitUntilReestablishedAsync(alice, channel, ct);

        // Assert
        await AssertNoForceCloseAsync(alice, channel, ct);
        var lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        await PayAndExpectFailBackAsync(alice, lndChannel, ct);
        await AssertSettledAtTwoTwoAsync(alice, channel, ct);
    }

    /// <summary>
    /// Proof N7 (c): LND sends an HTLC; we persist our commitment_signed and the connection dies right then (before
    /// LND's revoke_and_ack can arrive), and the node restarts. After channel_reestablish either we retransmit the
    /// stored commitment_signed or LND retransmits its revoke_and_ack, and the dance completes.
    /// </summary>
    [Fact]
    public async Task Given_CrashAfterOurCommitmentSignedIsPersisted_When_Restarted_Then_TheDanceCompletes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenChannelAndWaitUntilActiveAsync(alice, ct);
        var tcpService = Assert.IsType<CrashableTcpService>(Node.Services.GetRequiredService<ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the save; cut every connection before anything else
            if (args.ResponseMessage is not CommitmentSignedMessage || crashed.Task.IsCompleted)
                return;

            tcpService.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };

        // Act - LND's payment stays in flight across our crash and restart
        var payment = SendToUsAsync(alice, lndChannel, ct);
        await crashed.Task.WaitAsync(s_activeTimeout, ct);
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [reestablish] crashed after persisting commitment_signed");
        await Node.StopAsync();
        await Node.StartAsync(ct);
        await WaitUntilReestablishedAsync(alice, channel, ct);
        var attempt = await payment.WaitAsync(s_activeTimeout, ct);

        // Assert
        AssertFailedBackByUs(attempt);
        await AssertSettledAtTwoTwoAsync(alice, channel, ct);
        await AssertNoForceCloseAsync(alice, channel, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private string OurNodeIdHex => Node.NodeIdHex;

    private LNDNodeConnection GetAlice()
    {
        var alice = _fixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == "alice");
        Assert.NotNull(alice);
        return alice;
    }

    private async Task<(OpenChannelClientSubscriptionResponse Channel, Channel LndChannel)>
        OpenChannelAndWaitUntilActiveAsync(LNDNodeConnection alice, CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var aliceAddress = await Node.ConnectToAsync(alice, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({ChannelPoint(channel)})");

        var deadline = DateTime.UtcNow + s_activeTimeout;
        while (true)
        {
            var lndChannel = await GetLndChannelAsync(alice, channel, ct);
            var ours = await GetOurChannelAsync(channel.ChannelId, ct);
            if (lndChannel is { Active: true } && ours.State == ChannelState.Open)
                return (channel, lndChannel);

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Channel not active in time: LND active={lndChannel?.Active}, ours={ours.State}");

            await Node.MineBlocksAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// A bounded wait for the restarted alice to answer and be synced to the chain (LNUnit's own readiness wait can
    /// block without a deadline).
    /// </summary>
    private async Task<LNDNodeConnection> WaitUntilLndSyncedAsync(CancellationToken ct)
    {
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var alice = GetAlice();
                var info = await alice.LightningClient.GetInfoAsync(new GetInfoRequest(),
                                                                    deadline: DateTime.UtcNow.AddSeconds(5),
                                                                    cancellationToken: ct);
                return info.SyncedToChain ? alice : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }, s_activeTimeout, "alice did not come back synced after its restart", ct);
    }

    /// <summary>
    /// Both sides use the channel again: we processed LND's channel_reestablish on the current connection and LND
    /// lists the channel Active.
    /// </summary>
    private async Task WaitUntilReestablishedAsync(LNDNodeConnection alice, OpenChannelClientSubscriptionResponse channel,
                                                   CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            if (!Node.IsConnectedTo(alice.LocalNodePubKeyBytes)
             || !Node.Services.GetRequiredService<IReestablishTracker>().IsReestablished(channel.ChannelId))
                return false;

            var lndChannel = await GetLndChannelAsync(alice, channel, ct);
            return lndChannel is { Active: true };
        }, s_activeTimeout, "the channel was not reestablished and Active on both sides", ct);

        var ours = await GetOurChannelAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelState.Open, ours.State);
        Assert.True(ours.IsPeerConnected);
    }

    private async Task PayAndExpectFailBackAsync(LNDNodeConnection alice, Channel lndChannel, CancellationToken ct)
    {
        var attempt = await SendToUsAsync(alice, lndChannel, ct);
        AssertFailedBackByUs(attempt);
    }

    /// <summary>LND pays us over its channel with a random hash (as N6-T5): we can only fail it back.</summary>
    private async Task<HTLCAttempt> SendToUsAsync(LNDNodeConnection alice, Channel lndChannel, CancellationToken ct)
    {
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var route = await alice.RouterClient.BuildRouteAsync(new BuildRouteRequest
        {
            AmtMsat = AmountMsat,
            FinalCltvDelta = 40,
            OutgoingChanId = lndChannel.ChanId,
            HopPubkeys = { ByteString.CopyFrom(Node.NodeId) }
        }, cancellationToken: ct);

        return await alice.RouterClient.SendToRouteV2Async(new Routerrpc.SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Route = route.Route
        }, cancellationToken: ct);
    }

    private static void AssertFailedBackByUs(HTLCAttempt attempt)
    {
        Console.WriteLine($"SendToRouteV2: {attempt.Status}, {attempt.Failure?.Code}, index {attempt.Failure?.FailureSourceIndex}");
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.NotNull(attempt.Failure);
        Assert.Equal(Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails, attempt.Failure.Code);
        Assert.Equal(1u, attempt.Failure.FailureSourceIndex);
    }

    /// <summary>
    /// After one HTLC added and failed back on a fresh channel, both commitment numbers are 2 on our side, nothing is
    /// pending anywhere, and the balances are the opening ones.
    /// </summary>
    private async Task AssertSettledAtTwoTwoAsync(LNDNodeConnection alice, OpenChannelClientSubscriptionResponse channel,
                                                  CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await GetOurChannelAsync(channel.ChannelId, ct);
            return ours is { LocalCommitmentNumber: 2, RemoteCommitmentNumber: 2 };
        }, s_activeTimeout, "our commitment numbers did not reach 2/2", ct);

        var oursAfter = await GetOurChannelAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelState.Open, oursAfter.State);
        Assert.Equal(0, oursAfter.OfferedHtlcCount + oursAfter.ReceivedHtlcCount);
        Assert.Equal(s_push.MilliSatoshi, oursAfter.RemoteBalance.MilliSatoshi);
        Assert.False(oursAfter.DataLossDetected);

        var lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        Assert.True(lndChannel.Active, "LND no longer lists the channel as active");
        Assert.Empty(lndChannel.PendingHtlcs);
        Assert.Equal(s_push.Satoshi, lndChannel.LocalBalance);
    }

    private async Task AssertNoForceCloseAsync(LNDNodeConnection alice, OpenChannelClientSubscriptionResponse channel,
                                               CancellationToken ct)
    {
        var pending = await alice.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                      cancellationToken: ct);
        var channelPoint = ChannelPoint(channel);
        Assert.DoesNotContain(pending.PendingForceClosingChannels,
                              c => c.Channel.ChannelPoint.Equals(channelPoint, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pending.WaitingCloseChannels,
                              c => c.Channel.ChannelPoint.Equals(channelPoint, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, Node.CountLogLines("Failing channel"));
    }

    private async Task<Channel?> GetLndChannelAsync(LNDNodeConnection alice,
                                                    OpenChannelClientSubscriptionResponse channel,
                                                    CancellationToken ct)
    {
        var channels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: ct);
        var channelPoint = ChannelPoint(channel);
        return channels.Channels.FirstOrDefault(
            c => c.RemotePubkey.Equals(OurNodeIdHex, StringComparison.OrdinalIgnoreCase)
              && c.ChannelPoint.Equals(channelPoint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ChannelInfoClientResponse> GetOurChannelAsync(ChannelId channelId, CancellationToken ct)
    {
        var channels = await Node.ListChannelsAsync(ct);
        return Assert.Single(channels.Channels, c => c.ChannelId == channelId);
    }

    /// <summary>LND's <c>txid:index</c>: the display-order txid (our stored txid reversed).</summary>
    private static string ChannelPoint(OpenChannelClientSubscriptionResponse channel)
    {
        Assert.NotNull(channel.TxId);
        var displayOrder = ((byte[])channel.TxId.Value).Reverse().ToArray();
        return $"{Convert.ToHexString(displayOrder).ToLowerInvariant()}:{channel.Index}";
    }
}