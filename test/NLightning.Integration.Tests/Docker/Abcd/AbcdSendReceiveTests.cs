using System.Security.Cryptography;
using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// ABCD roadmap §3 variant (c): our nodes as the payer and as the payee, through the daemon's client handlers
/// (<c>payinvoice</c>, <c>createinvoice</c>, <c>listinvoices</c>).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AbcdSendReceiveTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    : AbcdTestBase(fixture, output)
{
    /// <summary>
    /// c-send: Bob pays David's invoice hinted through Carol, so Bob builds the two-hop route himself (B–C, then the
    /// hint C→D) and Carol forwards it.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_InvoiceAtDavidHintedThroughCarol_When_BobPays_Then_PreimageReturnedAndCarolEarnsHerFee()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n = Network;
        const long amountMsat = 30_000_777;
        var feeCarol = AbcdPolicy.Carol.FeeFor(amountMsat);
        var before = await n.SnapshotAsync(ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(n.David, amountMsat, [n.HintThroughCarol()], ct,
                                                           "abcd bob pays david");

        // Act
        var payment = await n.Bob.PayInvoiceAsync(invoice.PaymentRequest, ct);

        // Assert
        Console.WriteLine($"Bob's payment: {payment.Status}, fee {payment.Fee.MilliSatoshi} msat, "
                        + $"failure {payment.FailureCode} at {payment.FailureSourceIndex}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var davidInvoice = await LndTestHelpers.WaitForInvoiceStateAsync(n.David, invoice.RHash.ToByteArray(),
                                                                         Invoice.Types.InvoiceState.Settled,
                                                                         AbcdNetwork.SettleTimeout, ct);
        Assert.NotNull(payment.Preimage);
        Assert.Equal(davidInvoice.RPreimage.ToByteArray(), (byte[])payment.Preimage.Value);
        Assert.Equal(amountMsat, davidInvoice.AmtPaidMsat);
        Assert.Equal((ulong)amountMsat, payment.Amount.MilliSatoshi);
        Assert.Equal((ulong)feeCarol, payment.Fee.MilliSatoshi);
        Assert.Equal(PaymentStatus.Succeeded,
                     (await n.Bob.GetPaymentAsync(invoice.RHash.ToByteArray(), ct))?.Status);

        await n.WaitNoPendingHtlcsAsync(ct);
        var after = await n.SnapshotAsync(ct);
        Console.WriteLine($"Before: {before}");
        Console.WriteLine($"After:  {after}");
        Assert.Equal(-(amountMsat + feeCarol), LocalDeltaMsat(before.BobBobCarol, after.BobBobCarol));
        Assert.Equal(amountMsat + feeCarol, LocalDeltaMsat(before.CarolBobCarol, after.CarolBobCarol));
        Assert.Equal(-amountMsat, LocalDeltaMsat(before.CarolCarolDavid, after.CarolCarolDavid));
        Assert.Equal(0, LocalDeltaMsat(before.BobAliceBob, after.BobAliceBob));
        AssertWithinOneSat(amountMsat, before.DavidCarolDavid.LocalBalance, after.DavidCarolDavid.LocalBalance,
                           "david C-D");
        Assert.Equal(before.AliceAliceBob.LocalBalance, after.AliceAliceBob.LocalBalance);
        await AssertNetworkHealthyAsync(after, ct);
    }

    /// <summary>
    /// c-receive: Alice pays Bob's own invoice over their direct channel (no routing fee).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_InvoiceFromBob_When_AlicePays_Then_BobSettlesAndReceivesTheAmount()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n = Network;
        const ulong amountMsat = 25_000_555;
        var before = await n.SnapshotAsync(ct);
        var invoice = await n.Bob.CreateInvoiceAsync(LightningMoney.MilliSatoshis(amountMsat), "abcd alice pays bob",
                                                     ct);

        // Act
        var payment = await n.PayFromAliceAsync(invoice.Bolt11, ct);

        // Assert
        Console.WriteLine($"Alice's payment: {payment.Status}, fee {payment.FeeMsat} msat, reason {payment.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(0, payment.FeeMsat);
        Assert.Single(SucceededAttempt(payment).Route.Hops);
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(payment.PaymentPreimage)));

        var settled = await Poll.ForAsync(async () =>
        {
            var ours = await n.Bob.GetInvoiceAsync(invoice.PaymentHash, ct);
            return ours is { Status: InvoiceStatus.Settled } ? ours : null;
        }, AbcdNetwork.SettleTimeout, "bob's invoice settled", ct);
        Assert.Equal(amountMsat, settled.AmountReceived?.MilliSatoshi);

        await n.WaitNoPendingHtlcsAsync(ct);
        var after = await n.SnapshotAsync(ct);
        Console.WriteLine($"Before: {before}");
        Console.WriteLine($"After:  {after}");
        Assert.Equal((long)amountMsat, LocalDeltaMsat(before.BobAliceBob, after.BobAliceBob));
        AssertWithinOneSat(-(long)amountMsat, before.AliceAliceBob.LocalBalance, after.AliceAliceBob.LocalBalance,
                           "alice A-B");
        Assert.Equal(0, LocalDeltaMsat(before.BobBobCarol, after.BobBobCarol));
        Assert.Equal(0, LocalDeltaMsat(before.CarolCarolDavid, after.CarolCarolDavid));
        await AssertNetworkHealthyAsync(after, ct);
    }
}