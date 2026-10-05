using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// NL-995 Docker proof of hold invoices: we issue an invoice for a payment hash whose preimage only the test knows,
/// LND alice pays it over one channel (we fund and push to her), and the completed HTLC set is held — LND's payment
/// sits IN_FLIGHT, nothing fulfilled — until we settle with the preimage (alice then succeeds with it) or cancel
/// (alice's payment fails with our incorrect_or_unknown_payment_details).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class HoldInvoiceFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);

    /// <summary>What we push to alice: she pays the hold from it, well under her balance minus the reserve.</summary>
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private static readonly LightningMoney s_amount = LightningMoney.Satoshis(200_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public HoldInvoiceFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "hold");
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_ALndPaymentToOurHoldInvoice_When_WeSettleWithThePreimage_Then_LndSucceedsWithIt()
    {
        // Arrange: one channel with alice, 300,000 sat on her side to pay us with
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channel = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var localBefore = (await Node.GetChannelAsync(channel.ChannelId, ct)).LocalBalance.MilliSatoshi;

        // The preimage comes from outside (a Cashu melt); the invoice only ever sees its hash
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hash = new Hash(paymentHash);
        var invoice = await Node.CreateHoldInvoiceAsync(hash, s_amount, "nl995 settle", ct);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.NotNull(invoice.Bolt11);

        // Act: alice pays and her payment locks in at ours, held without a fulfill while she waits
        var paymentTask = await PayAndHearNothingAsync(alice, invoice.Bolt11!, lndChannel.ChanId, ct);
        var held = await Poll.ForAsync(async () =>
        {
            var stored = await Node.GetInvoiceAsync(hash, ct);
            return stored?.Status == InvoiceStatus.Held ? stored : null;
        }, s_timeout, "our hold invoice held", ct);
        Assert.Equal(s_amount, held.AmountReceived);

        // Assert: LND's payment is still in flight — no preimage of ours exists to fulfill it with
        var inFlight = await GetLndPaymentAsync(alice, paymentHash, ct);
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, inFlight.Status);
        // LND reports an all-zero placeholder, not an empty string, while the payment is held in flight
        Assert.True(string.IsNullOrEmpty(inFlight.PaymentPreimage)
                 || inFlight.PaymentPreimage.Trim('0').Length == 0,
                    $"the held payment must not carry a preimage: {inFlight.PaymentPreimage}");

        // Act: the operator settles with the outside preimage
        var settled = await Node.SettleHoldInvoiceAsync(hash, new Secret(preimage), ct);
        Assert.Equal(InvoiceStatus.Settled, settled.Status);
        var payment = await paymentTask;

        // Assert: LND succeeded, carrying exactly the preimage we settled with
        Console.WriteLine($"LND's payment: {payment.Status} {payment.FailureReason}, "
                        + $"{payment.Htlcs.Count} attempt(s)");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal((long)s_amount.MilliSatoshi, payment.ValueMsat);
        Assert.Equal(Convert.ToHexString(preimage), payment.PaymentPreimage, ignoreCase: true);
        Assert.Equal(Convert.ToHexString(paymentHash), payment.PaymentHash, ignoreCase: true);

        // Our invoice is settled for the whole amount and the channel carries no HTLC anymore: the money moved
        var ours = await Node.GetInvoiceAsync(hash, ct);
        Assert.Equal(InvoiceStatus.Settled, ours!.Status);
        Assert.Equal(s_amount, ours.AmountReceived);
        await Poll.UntilAsync(async () =>
        {
            var state = await Node.GetChannelAsync(channel.ChannelId, ct);
            Console.WriteLine($"channel: {state.Describe()}");
            return state.OfferedHtlcCount + state.ReceivedHtlcCount == 0;
        }, s_timeout, "no HTLC pending on our channel", ct);
        var localAfter = (await Node.GetChannelAsync(channel.ChannelId, ct)).LocalBalance.MilliSatoshi;
        Assert.Equal(localBefore + s_amount.MilliSatoshi, localAfter);
    }

    [Fact]
    public async Task Given_ALndPaymentToOurHoldInvoice_When_WeCancel_Then_LndFailsAndNothingMoved()
    {
        // Arrange: one channel with alice and a held payment of our hold invoice
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channel = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var localBefore = (await Node.GetChannelAsync(channel.ChannelId, ct)).LocalBalance.MilliSatoshi;

        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hash = new Hash(paymentHash);
        var invoice = await Node.CreateHoldInvoiceAsync(hash, s_amount, "nl995 cancel", ct);
        var paymentTask = await PayAndHearNothingAsync(alice, invoice.Bolt11!, lndChannel.ChanId, ct);
        await Poll.ForAsync(async () =>
        {
            var stored = await Node.GetInvoiceAsync(hash, ct);
            return stored?.Status == InvoiceStatus.Held ? stored : null;
        }, s_timeout, "our hold invoice held", ct);

        // Act: the operator cancels instead of settling
        var canceled = await Node.CancelHoldInvoiceAsync(hash, ct);
        Assert.Equal(InvoiceStatus.Canceled, canceled.Status);
        var payment = await paymentTask;

        // Assert: LND's payment failed with our incorrect_or_unknown_payment_details (a canceled invoice's failure)
        Console.WriteLine($"LND's payment: {payment.Status} {payment.FailureReason}, "
                        + $"{payment.Htlcs.Count} attempt(s)");
        Assert.Equal(Payment.Types.PaymentStatus.Failed, payment.Status);
        Assert.Equal(PaymentFailureReason.FailureReasonIncorrectPaymentDetails, payment.FailureReason);
        var failed = Assert.Single(payment.Htlcs, h => h.Status == HTLCAttempt.Types.HTLCStatus.Failed);
        Assert.Equal(Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails, failed.Failure!.Code);

        // Our invoice is canceled, the preimage never revealed, and no money moved on the channel
        Assert.Equal(InvoiceStatus.Canceled, (await Node.GetInvoiceAsync(hash, ct))!.Status);
        var state = await Node.GetChannelAsync(channel.ChannelId, ct);
        Assert.Equal(0, state.OfferedHtlcCount + state.ReceivedHtlcCount);
        Assert.Equal(localBefore, state.LocalBalance.MilliSatoshi);

        // The preimage we kept back settles nothing anymore: the invoice is final
        await Assert.ThrowsAsync<Domain.Client.Exceptions.ClientException>(() =>
                                                                              Node.SettleHoldInvoiceAsync(
                                                                                  hash, new Secret(preimage), ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync(["alice"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// LND starts paying <paramref name="bolt11"/> pinned to <paramref name="chanId"/> and the call is returned while
    /// the payment is still in flight (a payment LND fails outright for want of a route or balance, NL-319, is started
    /// again); the task later completes with the payment's final update.
    /// </summary>
    private static async Task<Task<Payment>> PayAndHearNothingAsync(LndNodeConnection alice, string bolt11,
                                                                    ulong chanId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_timeout);
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var request = LndTestHelpers.PinnedPayment(bolt11, [chanId], timeoutSeconds: 110);
            var paymentTask = LndTestHelpers.SendPaymentV2Async(alice, request, ct, TimeSpan.FromMinutes(4));

            // Still no final update after the link's round trips: the HTLC is locked in at ours and held
            if (await Task.WhenAny(paymentTask, Task.Delay(TimeSpan.FromSeconds(10), deadline.Token)) != paymentTask)
                return paymentTask;

            var failed = await paymentTask;
            if (failed.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                          or PaymentFailureReason.FailureReasonNoRoute))
                throw new InvalidOperationException(
                    $"The payment towards the hold invoice ended with {failed.Status} {failed.FailureReason} before it "
                    + "could be held");

            Console.WriteLine($"{alice.LocalAlias}'s payment failed with {failed.FailureReason}; retrying");
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    /// <summary>LND's payment for <paramref name="paymentHash"/>, in flight ones included.</summary>
    private static async Task<Payment> GetLndPaymentAsync(LndNodeConnection alice, byte[] paymentHash,
                                                          CancellationToken ct)
    {
        var payments = await alice.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            IncludeIncomplete = true
        },
                                                                    cancellationToken: ct);
        return Assert.Single(payments.Payments,
                             p => p.PaymentHash.Equals(Convert.ToHexString(paymentHash),
                                                       StringComparison.OrdinalIgnoreCase));
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LndNodeConnection peer,
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
}