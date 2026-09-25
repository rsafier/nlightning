using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Fixtures;
using Mock;
using TestCollections;
using Utils;

/// <summary>
/// BOLT2 plan proofs for N0 and N1 (a channel we open to LND stays usable while idle), N6 (an HTLC from LND is
/// locked in and failed back with an error onion LND can read) and N8 (payments over a direct channel both ways,
/// concurrent, trimmed, and across a restart, through the daemon's <c>createinvoice</c>/<c>payinvoice</c> handlers).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class NormalOperationFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_activeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_idleTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Our <c>max_accepted_htlcs</c> in these tests. The node default (<c>Node:MaxAcceptedHtlcs</c> = 5) would make LND
    /// fail the N8 concurrent payments beyond the fifth locally; LND itself accepts 483.
    /// </summary>
    private const ushort ConcurrentHtlcsAccepted = 30;

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
                                       new FakeSecureKeyManager(), port,
                                       options => options.MaxAcceptedHtlcs = ConcurrentHtlcsAccepted);
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

    /// <summary>
    /// BOLT2 plan Proof N8: LND pays an invoice we created (50,000 sat). LND gets our preimage, our invoice is
    /// settled, both sides agree on the balances to the msat and nothing stays pending.
    /// </summary>
    [Fact]
    public async Task Given_LndPaysOurInvoice_Then_Settled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenUsableChannelAsync(alice, LightningMoney.Satoshis(1_000_000),
                                                                 LightningMoney.Satoshis(300_000), ct);

        // Act + Assert
        await AssertLndPaysOurInvoiceAsync(alice, channel, lndChannel, LightningMoney.Satoshis(50_000), ct);
    }

    /// <summary>
    /// BOLT2 plan Proof N8 (fee and dust rounding, plan risk 13): a 5,000 sat HTLC at 10,000 sat/kw is below the trim
    /// threshold of both commitments (dust limit plus the HTLC-success/timeout fee, about 7,000 sat; checked from LND's
    /// channel before paying, so the test fails if that stops holding), so it has no
    /// output on either side, and it must still settle with the same balances on both sides.
    /// </summary>
    [Fact]
    public async Task Given_TrimmedHtlc_When_LndPaysOurInvoice_Then_Settled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenUsableChannelAsync(alice, LightningMoney.Satoshis(1_000_000),
                                                                 LightningMoney.Satoshis(300_000), ct);
        var amount = LightningMoney.Satoshis(5_000);
        AssertTrimmedOnBothCommitments(lndChannel, amount);

        // Act + Assert
        await AssertLndPaysOurInvoiceAsync(alice, channel, lndChannel, amount, ct);
    }

    /// <summary>
    /// BOLT2 plan Proof N8: we pay an LND invoice (20,000 sat) over our direct channel through <c>payinvoice</c>; the
    /// preimage we get back is LND's and LND's invoice is settled.
    /// </summary>
    [Fact]
    public async Task Given_LndInvoice_When_WePay_Then_PreimageReturned()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenUsableChannelAsync(alice, LightningMoney.Satoshis(1_000_000), null, ct);
        var amount = LightningMoney.Satoshis(20_000);
        var before = await GetOurChannelAsync(channel.ChannelId, ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(alice, (long)amount.MilliSatoshi, [], ct, "n8 we pay lnd");

        // Act
        var payment = await _node.PayInvoiceAsync(invoice.PaymentRequest, ct);

        // Assert
        Console.WriteLine($"Our payment: {payment.Status}, failure {payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var lndInvoice = await LndTestHelpers.WaitForInvoiceStateAsync(alice, invoice.RHash.ToByteArray(),
                                                                       Invoice.Types.InvoiceState.Settled,
                                                                       s_activeTimeout, ct);
        Assert.NotNull(payment.Preimage);
        Assert.Equal(lndInvoice.RPreimage.ToByteArray(), (byte[])payment.Preimage.Value);
        Assert.Equal((long)amount.MilliSatoshi, lndInvoice.AmtPaidMsat);
        Assert.Equal(amount, payment.Amount);
        Assert.True(payment.Fee.IsZero, $"a direct payment paid a {payment.Fee.MilliSatoshi} msat fee");

        await AssertBalancesMovedAsync(alice, channel, before, -(long)amount.MilliSatoshi, ct);
    }

    /// <summary>
    /// BOLT2 plan Proof N8: 10 payments each way at the same time over one channel, so updates and
    /// <c>commitment_signed</c> cross on the wire. Every payment succeeds and the balances net out exactly.
    /// </summary>
    [Fact]
    public async Task Given_ConcurrentPaymentsBothWays_Then_AllSucceed()
    {
        // Arrange
        const int paymentsEachWay = 10;
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, lndChannel) = await OpenUsableChannelAsync(alice, LightningMoney.Satoshis(1_000_000),
                                                                 LightningMoney.Satoshis(500_000), ct);
        var inboundAmount = LightningMoney.Satoshis(15_000);
        var outboundAmount = LightningMoney.Satoshis(12_000);
        var before = await GetOurChannelAsync(channel.ChannelId, ct);

        var ourInvoices = new List<InvoiceInfoClientResponse>();
        var lndInvoices = new List<AddInvoiceResponse>();
        for (var i = 0; i < paymentsEachWay; i++)
        {
            ourInvoices.Add(await _node.CreateInvoiceAsync(inboundAmount, $"n8 concurrent in {i}", ct));
            lndInvoices.Add(await LndTestHelpers.AddInvoiceAsync(alice, (long)outboundAmount.MilliSatoshi, [], ct,
                                                                 $"n8 concurrent out {i}"));
        }

        await LndTestHelpers.ResetMissionControlAsync(alice, ct);

        // Act
        var inbound = ourInvoices.Select(invoice => LndTestHelpers.SendPaymentV2Async(
                                             alice, LndTestHelpers.PinnedPayment(invoice.Bolt11, [lndChannel.ChanId]),
                                             ct))
                                 .ToList();
        var outbound = lndInvoices.Select(invoice => _node.PayInvoiceAsync(invoice.PaymentRequest, ct,
                                                                           timeoutSeconds: 120))
                                  .ToList();
        await Task.WhenAll(inbound.Cast<Task>().Concat(outbound));

        // Assert
        foreach (var payment in inbound.Select(t => t.Result))
        {
            Assert.True(payment.Status == Payment.Types.PaymentStatus.Succeeded,
                        $"LND's payment {payment.PaymentHash}: {payment.Status} ({payment.FailureReason})");
        }

        foreach (var payment in outbound.Select(t => t.Result))
        {
            Assert.True(payment.Status == PaymentStatus.Succeeded,
                        $"Our payment {payment.PaymentHash}: {payment.Status} ({payment.FailureCode}: "
                      + $"{payment.FailureReason})");
        }

        foreach (var invoice in ourInvoices)
            await WaitForOurInvoiceSettledAsync(invoice.PaymentHash, inboundAmount, ct);

        foreach (var invoice in lndInvoices)
        {
            await LndTestHelpers.WaitForInvoiceStateAsync(alice, invoice.RHash.ToByteArray(),
                                                          Invoice.Types.InvoiceState.Settled, s_activeTimeout, ct);
        }

        var netMsat = paymentsEachWay * ((long)inboundAmount.MilliSatoshi - (long)outboundAmount.MilliSatoshi);
        await AssertBalancesMovedAsync(alice, channel, before, netMsat, ct);
    }

    /// <summary>
    /// BOLT2 plan Proof N8: we pay an LND hold invoice, restart while LND holds the HTLC, and LND settles once we are
    /// back. The fulfill reaches us after the reestablish and our payment succeeds with LND's preimage.
    /// </summary>
    [Fact]
    public async Task Given_InFlightRestart_Then_FulfillArrivesAfterReestablish()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetAlice();
        var (channel, _) = await OpenUsableChannelAsync(alice, LightningMoney.Satoshis(1_000_000), null, ct);
        var amount = LightningMoney.Satoshis(25_000);
        var before = await GetOurChannelAsync(channel.ChannelId, ct);
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(alice, paymentHash, (long)amount.MilliSatoshi, [],
                                                                   ct, "n8 in-flight restart");
        var settled = false;
        try
        {
            var inFlight = await _node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
            Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
            await LndTestHelpers.WaitForInvoiceStateAsync(alice, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          s_activeTimeout, ct);

            // Act: restart with our HTLC held by LND, then LND settles
            _node.ChannelManager.OnResponseMessageReady -= RecordChannelReady;
            await _node.StopAsync();
            await _node.StartAsync(ct);
            _node.ChannelManager.OnResponseMessageReady += RecordChannelReady;
            await WaitUntilUsableAsync(alice, channel, ct);
            await LndTestHelpers.SettleInvoiceAsync(alice, preimage, ct);
            settled = true;

            // Assert
            var payment = await Poll.ForAsync(async () =>
            {
                var ours = await _node.GetPaymentAsync(paymentHash, ct);
                return ours is { Status: PaymentStatus.Succeeded or PaymentStatus.Failed } ? ours : null;
            }, s_activeTimeout, "our payment completed after the restart", ct);
            Assert.Equal(PaymentStatus.Succeeded, payment.Status);
            Assert.NotNull(payment.Preimage);
            Assert.Equal(preimage, (byte[])payment.Preimage.Value);

            await AssertBalancesMovedAsync(alice, channel, before, -(long)amount.MilliSatoshi, ct);
        }
        finally
        {
            if (!settled)
                await CancelHoldInvoiceQuietlyAsync(alice, paymentHash);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);

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

    /// <summary>
    /// Asserts that an HTLC of <paramref name="amount"/> offered by LND is trimmed on both commitments (BOLT 3 "Trimmed
    /// Outputs", non-anchor channel types): below LND's dust limit plus the HTLC-timeout fee (weight 663) on LND's
    /// commitment, where it is offered, and below our dust limit plus the HTLC-success fee (weight 703) on ours, where
    /// it is received. Fails loudly when the channel type, a dust limit or the feerate no longer makes it trimmed.
    /// </summary>
    private static void AssertTrimmedOnBothCommitments(Channel lndChannel, LightningMoney amount)
    {
        const ulong htlcTimeoutWeight = 663;
        const ulong htlcSuccessWeight = 703;

        Assert.True(lndChannel.CommitmentType is CommitmentType.Legacy or CommitmentType.StaticRemoteKey,
                    $"the trim thresholds below assume a non-anchor channel, LND reports {lndChannel.CommitmentType}");
        var feePerKw = (ulong)lndChannel.FeePerKw;
        var lndThresholdSat = lndChannel.LocalConstraints.DustLimitSat + htlcTimeoutWeight * feePerKw / 1_000;
        var ourThresholdSat = lndChannel.RemoteConstraints.DustLimitSat + htlcSuccessWeight * feePerKw / 1_000;
        Console.WriteLine($"Trim thresholds at {feePerKw} sat/kw: LND commitment {lndThresholdSat} sat (dust "
                        + $"{lndChannel.LocalConstraints.DustLimitSat}), ours {ourThresholdSat} sat (dust "
                        + $"{lndChannel.RemoteConstraints.DustLimitSat})");
        Assert.True((ulong)amount.Satoshi < lndThresholdSat,
                    $"{amount.Satoshi} sat is not trimmed on LND's commitment (threshold {lndThresholdSat} sat)");
        Assert.True((ulong)amount.Satoshi < ourThresholdSat,
                    $"{amount.Satoshi} sat is not trimmed on our commitment (threshold {ourThresholdSat} sat)");
    }

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

    /// <summary>
    /// Opens a channel to alice and waits until it carries HTLCs on both sides and both are at the chain tip (no block
    /// is mined after this, so no HTLC ever sees one).
    /// </summary>
    private async Task<(OpenChannelClientSubscriptionResponse Channel, Channel LndChannel)>
        OpenUsableChannelAsync(LNDNodeConnection alice, LightningMoney capacity, LightningMoney? push,
                               CancellationToken ct)
    {
        var (channel, _) = await OpenChannelAndWaitUntilActiveAsync(alice, capacity, push, ct);
        await WaitUntilUsableAsync(alice, channel, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], [_node], ct);

        var lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        return (channel, lndChannel);
    }

    /// <summary>
    /// Waits until our end is <c>Open</c>, connected and reestablished and LND lists the channel active.
    /// </summary>
    private async Task WaitUntilUsableAsync(LNDNodeConnection alice, OpenChannelClientSubscriptionResponse channel,
                                            CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await GetOurChannelAsync(channel.ChannelId, ct);
            var lndChannel = await GetLndChannelAsync(alice, channel, ct);
            return ours.IsUsable() && lndChannel is { Active: true };
        }, s_activeTimeout, $"channel {channel.ChannelId} usable on both sides", ct);
    }

    /// <summary>
    /// LND pays an invoice we create for <paramref name="amount"/> over <paramref name="lndChannel"/>, and every side
    /// agrees on the outcome.
    /// </summary>
    private async Task AssertLndPaysOurInvoiceAsync(LNDNodeConnection alice,
                                                    OpenChannelClientSubscriptionResponse channel, Channel lndChannel,
                                                    LightningMoney amount, CancellationToken ct)
    {
        var before = await GetOurChannelAsync(channel.ChannelId, ct);
        var invoice = await _node.CreateInvoiceAsync(amount, $"n8 lnd pays us {amount.Satoshi} sat", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);

        // Act
        var payment = await LndTestHelpers.SendPaymentV2Async(
                          alice, LndTestHelpers.PinnedPayment(invoice.Bolt11, [lndChannel.ChanId]), ct);

        // Assert: LND has our preimage and paid no fee over its own channel
        Console.WriteLine($"LND's payment: {payment.Status}, fee {payment.FeeMsat} msat, reason {payment.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(0, payment.FeeMsat);
        Assert.Equal((long)amount.MilliSatoshi, payment.ValueMsat);
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(payment.PaymentPreimage)));

        await WaitForOurInvoiceSettledAsync(invoice.PaymentHash, amount, ct);
        await AssertBalancesMovedAsync(alice, channel, before, (long)amount.MilliSatoshi, ct);
    }

    /// <summary>
    /// Waits until nothing is pending on either side, then checks our balance moved by exactly
    /// <paramref name="localDeltaMsat"/>, both sides agree on both balances, and the channel is still usable.
    /// </summary>
    private async Task AssertBalancesMovedAsync(LNDNodeConnection alice,
                                                OpenChannelClientSubscriptionResponse channel,
                                                ChannelInfoClientResponse before, long localDeltaMsat,
                                                CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await GetOurChannelAsync(channel.ChannelId, ct);
            var lnd = await GetLndChannelAsync(alice, channel, ct);
            return ours.OfferedHtlcCount + ours.ReceivedHtlcCount == 0 && lnd is { PendingHtlcs.Count: 0 };
        }, s_activeTimeout, "no HTLC pending on either side", ct);

        var after = await GetOurChannelAsync(channel.ChannelId, ct);
        var lndChannel = await GetLndChannelAsync(alice, channel, ct);
        Assert.NotNull(lndChannel);
        Console.WriteLine($"Ours: local {before.LocalBalance.MilliSatoshi} -> {after.LocalBalance.MilliSatoshi} msat, "
                        + $"commitments {after.LocalCommitmentNumber}/{after.RemoteCommitmentNumber}; LND: local "
                        + $"{lndChannel.LocalBalance} remote {lndChannel.RemoteBalance} commit fee {lndChannel.CommitFee}");

        // Ours, to the msat
        Assert.Equal(localDeltaMsat, (long)after.LocalBalance.MilliSatoshi - (long)before.LocalBalance.MilliSatoshi);
        Assert.Equal(-localDeltaMsat,
                     (long)after.RemoteBalance.MilliSatoshi - (long)before.RemoteBalance.MilliSatoshi);

        // LND agrees: it is the non-funder, so its to_local is its whole balance; ours also pays the commitment fee
        Assert.Equal(after.RemoteBalance.Satoshi, lndChannel.LocalBalance);
        Assert.Equal(after.LocalBalance.Satoshi, lndChannel.RemoteBalance + lndChannel.CommitFee);

        Assert.True(lndChannel.Active, "LND no longer lists the channel as active");
        Assert.True(after.IsUsable(), $"our channel is no longer usable: {after.Describe()}");
        Assert.True(await LndTestHelpers.IsConnectedToAsync(alice, OurNodeIdHex, ct), "LND disconnected");
    }

    private async Task WaitForOurInvoiceSettledAsync(Hash paymentHash, LightningMoney amount, CancellationToken ct)
    {
        var settled = await Poll.ForAsync(async () =>
        {
            var invoice = await _node.GetInvoiceAsync(paymentHash, ct);
            return invoice is { Status: InvoiceStatus.Settled } ? invoice : null;
        }, s_activeTimeout, $"our invoice {paymentHash} settled", ct);
        Assert.Equal(amount, settled.AmountReceived);
    }

    /// <summary>
    /// Fails back a hold invoice a failed run left accepted, so the channel does not keep the HTLC.
    /// </summary>
    private static async Task CancelHoldInvoiceQuietlyAsync(LNDNodeConnection alice, byte[] paymentHash)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LndTestHelpers.CancelInvoiceAsync(alice, paymentHash, timeoutCts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not cancel the hold invoice: {e.Message}");
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