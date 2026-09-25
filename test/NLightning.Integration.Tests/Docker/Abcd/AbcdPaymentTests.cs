using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// ABCD roadmap §3 happy path and variant (a): Alice pays David through Bob and Carol with an invoice whose route
/// hints carry our policies, so LND does the pathfinding and the fee maths, and our nodes forward, enforce their
/// policies, peel and wrap error onions.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AbcdPaymentTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    : AbcdTestBase(fixture, output)
{
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_HintedInvoiceAtDavid_When_AlicePays_Then_SucceedsWithBolt7FeesAndExactBalances()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        const long amountMsat = 50_000_123;
        var fees = AbcdPathFees.ToDavid(amountMsat);
        var before = await Network.SnapshotAsync(ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(Network.David, amountMsat, [Network.HintThroughBobAndCarol()],
                                                           ct, "abcd happy path");

        // Act
        var payment = await Network.PayFromAliceAsync(invoice.PaymentRequest, ct);

        // Assert
        await AssertPaidThroughBobAndCarolAsync(payment, invoice.RHash.ToByteArray(), fees, before, ct);
    }

    /// <summary>
    /// Variant (a): David canceled the invoice, so he fails the HTLC with <c>incorrect_or_unknown_payment_details</c>.
    /// Alice decodes it as coming from index 3 (David), which only works if Carol and Bob each wrapped the error
    /// onion with their shared secret.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_CanceledInvoiceAtDavid_When_AlicePays_Then_FailureDecodedFromDavidAndNothingMoves()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        const long amountMsat = 20_000_457;
        var before = await Network.SnapshotAsync(ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(Network.David, amountMsat, [Network.HintThroughBobAndCarol()],
                                                           ct, "abcd canceled");
        await LndTestHelpers.CancelInvoiceAsync(Network.David, invoice.RHash.ToByteArray(), ct);

        // Act
        var payment = await Network.PayFromAliceAsync(invoice.PaymentRequest, ct);

        // Assert
        Console.WriteLine($"Payment {payment.PaymentHash}: {payment.Status}, reason {payment.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Failed, payment.Status);
        Assert.Equal(PaymentFailureReason.FailureReasonIncorrectPaymentDetails, payment.FailureReason);

        var attempt = Assert.Single(payment.Htlcs);
        Console.WriteLine($"Attempt: {attempt.Status}, {attempt.Failure?.Code}, index {attempt.Failure?.FailureSourceIndex}");
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.NotNull(attempt.Failure);
        Assert.Equal(Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails, attempt.Failure.Code);
        Assert.Equal(3u, attempt.Failure.FailureSourceIndex);
        Assert.Equal(3, attempt.Route.Hops.Count);

        await AssertBalancesUnchangedAsync(before, ct);
    }
}