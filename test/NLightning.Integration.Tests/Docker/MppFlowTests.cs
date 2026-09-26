using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// ABCD W6-B Docker proof of multi-part receiving (BOLT 4 <c>basic_mpp</c>): LND alice has two channels to us (we fund
/// both and push part of each to alice), and pays one invoice of ours that neither channel can carry alone with
/// <c>max_parts</c> 4 over both. We hold the first part until the parts reach <c>total_msat</c>, then fulfill all of
/// them; LND reports the payment succeeded over both channels and our invoice is settled for the full amount.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class MppFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    /// <summary>More than alice can send over either channel (300,000 sat minus our reserve), less than both.</summary>
    private static readonly LightningMoney s_amount = LightningMoney.Satoshis(450_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public MppFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "mpp");
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_TwoChannelsFromLnd_When_LndPaysOurInvoiceWithMaxParts_Then_PartsSplitAcrossBothAndSettled()
    {
        // Arrange: two channels with alice, each with 300,000 sat on alice's side
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var first = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var second = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var firstLnd = await LndTestHelpers.GetChannelByPointAsync(alice, first.ChannelPoint(), ct);
        var secondLnd = await LndTestHelpers.GetChannelByPointAsync(alice, second.ChannelPoint(), ct);
        Assert.NotNull(firstLnd);
        Assert.NotNull(secondLnd);
        Assert.True(firstLnd.LocalBalance < (long)s_amount.Satoshi && secondLnd.LocalBalance < (long)s_amount.Satoshi,
                    "each channel alone must be too small for the payment");
        var before = await SnapshotAsync([first.ChannelId, second.ChannelId], ct);

        var invoice = await Node.CreateInvoiceAsync(s_amount, "w6b mpp", ct);
        var decoded = await alice.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = invoice.Bolt11 },
                                                                    cancellationToken: ct);
        Assert.Contains(decoded.Features, f => f.Key == 17 && f.Value.IsKnown); // basic_mpp, optional
        Assert.DoesNotContain(decoded.Features, f => f.Key == 16);

        // Act: LND splits over both channels (max_parts 4)
        var payment = await PayUntilDoneAsync(alice, invoice.Bolt11, [firstLnd.ChanId, secondLnd.ChanId], ct);

        // Assert: LND succeeded with at least two parts, over both channels
        Console.WriteLine($"LND's payment: {payment.Status} {payment.FailureReason}, fee {payment.FeeMsat} msat, "
                        + $"{payment.Htlcs.Count} attempt(s)");
        foreach (var attempt in payment.Htlcs)
            Console.WriteLine($"  attempt {attempt.AttemptId}: {attempt.Status} {attempt.Route.TotalAmtMsat} msat over "
                            + $"{attempt.Route.Hops[0].ChanId} {attempt.Failure?.Code}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal((long)s_amount.MilliSatoshi, payment.ValueMsat);
        Assert.Equal(0, payment.FeeMsat);
        var succeeded = payment.Htlcs.Where(h => h.Status == HTLCAttempt.Types.HTLCStatus.Succeeded).ToList();
        Assert.True(succeeded.Count >= 2, $"{succeeded.Count} part(s) succeeded");
        Assert.Equal((long)s_amount.MilliSatoshi, succeeded.Sum(h => h.Route.TotalAmtMsat));
        Assert.Equal(new[] { firstLnd.ChanId, secondLnd.ChanId }.Order(),
                     succeeded.Select(h => h.Route.Hops[0].ChanId).Distinct().Order());
        Assert.Equal(Convert.ToHexString((byte[])invoice.PaymentHash), payment.PaymentHash, ignoreCase: true);

        // Our invoice is settled for the whole amount, and the balances moved by it across both channels
        var ours = await Poll.ForAsync(async () =>
        {
            var stored = await Node.GetInvoiceAsync(invoice.PaymentHash, ct);
            return stored?.Status == InvoiceStatus.Settled ? stored : null;
        }, s_timeout, "our invoice settled", ct);
        Assert.Equal(s_amount, ours.AmountReceived);
        await Poll.UntilAsync(async () =>
        {
            foreach (var id in new[] { first.ChannelId, second.ChannelId })
            {
                var channel = await Node.GetChannelAsync(id, ct);
                if (channel.OfferedHtlcCount + channel.ReceivedHtlcCount != 0)
                    return false;
            }

            return true;
        }, s_timeout, "no HTLC pending on our channels", ct);
        var after = await SnapshotAsync([first.ChannelId, second.ChannelId], ct);
        var deltas = after.Zip(before, (a, b) => (long)a - (long)b).ToList();
        Console.WriteLine($"Our local balance deltas: {string.Join(", ", deltas)} msat");
        Assert.All(deltas, d => Assert.True(d > 0, "each channel carried a part"));
        Assert.Equal((long)s_amount.MilliSatoshi, deltas.Sum());
        foreach (var id in new[] { first.ChannelId, second.ChannelId })
            Assert.True((await Node.GetChannelAsync(id, ct)).IsUsable());
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LNDNodeConnection peer,
        string peerAddress, CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [Node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [Node], ct);
        return channel;
    }

    private async Task<List<ulong>> SnapshotAsync(IEnumerable<ChannelId> channelIds, CancellationToken ct)
    {
        var balances = new List<ulong>();
        foreach (var id in channelIds)
            balances.Add((await Node.GetChannelAsync(id, ct)).LocalBalance.MilliSatoshi);
        return balances;
    }

    /// <summary>
    /// LND pays <paramref name="bolt11"/> with up to 4 parts over <paramref name="chanIds"/>. A payment that LND fails
    /// for want of a route or balance (its router adds a fresh private channel's edge a moment after the channel turns
    /// active, NL-319) is started again; the final update of any other outcome is returned.
    /// </summary>
    private static async Task<Payment> PayUntilDoneAsync(LNDNodeConnection lnd, string bolt11, ulong[] chanIds,
                                                         CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_timeout);
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            var request = LndTestHelpers.PinnedPayment(bolt11, chanIds, timeoutSeconds: 90);
            request.MaxParts = 4;
            var payment = await LndTestHelpers.SendPaymentV2Async(lnd, request, ct, TimeSpan.FromMinutes(2));
            if (payment.Status == Payment.Types.PaymentStatus.Succeeded
             || payment.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                              or PaymentFailureReason.FailureReasonNoRoute)
             || payment.Htlcs.Any(h => h.Status == HTLCAttempt.Types.HTLCStatus.Succeeded))
                return payment;

            Console.WriteLine($"{lnd.LocalAlias}'s payment failed with {payment.FailureReason} after "
                            + $"{payment.Htlcs.Count} attempt(s); retrying");
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }
}