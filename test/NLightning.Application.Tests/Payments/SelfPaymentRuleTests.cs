using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments;

using Application.Payments;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Domain.Payments.Models;

/// <summary>
/// NL-670: both halves of a rebalance flag <c>selfPayment</c> by one rule, "our node is the payee", never by the
/// payment hash alone (a peer that learned an invoice's preimage can reuse its hash).
/// </summary>
public class SelfPaymentRuleTests
{
    private static readonly CompactPubKey s_us = new TestNodeKeyManager(0x0b).NodeId;
    private static readonly CompactPubKey s_mallory = new TestNodeKeyManager(0x0c).NodeId;
    private static readonly Secret s_preimage = new(RandomNumberGenerator.GetBytes(32));
    private static readonly Hash s_hash = new(SHA256.HashData((byte[])s_preimage));
    private static readonly LightningMoney s_amount = LightningMoney.Satoshis(1_000);

    [Fact]
    public void Given_OurPaymentOfOurOwnBolt11Invoice_When_Checked_Then_ItIsASelfPayment()
    {
        // Arrange
        var payment = Payment(s_us, PaymentStatus.Succeeded);

        // Act
        var paying = SelfPaymentRule.IsSelfPayment(payment, OurBolt11Invoice(), s_us);
        var receiving = SelfPaymentRule.IsSettledBySelfPayment(OurBolt11Invoice(), payment, s_us);

        // Assert
        Assert.True(paying);
        Assert.True(receiving);
    }

    [Fact]
    public void Given_AnInFlightRebalance_When_OurInvoiceSettles_Then_ItIsASelfPayment()
    {
        // Arrange: the incoming side settles before the payment row is marked Succeeded
        var payment = Payment(s_us, PaymentStatus.InFlight);

        // Act
        var receiving = SelfPaymentRule.IsSettledBySelfPayment(OurBolt11Invoice(), payment, s_us);

        // Assert
        Assert.True(receiving);
    }

    [Fact]
    public void Given_MalloryReusesTheHashOfOurInvoice_When_WePayHerInvoice_Then_ItIsNoSelfPayment()
    {
        // Arrange: case A of the review, Mallory learned the preimage of our invoice and billed us on the same hash
        var payment = Payment(s_mallory, PaymentStatus.Succeeded);

        // Act
        var paying = SelfPaymentRule.IsSelfPayment(payment, OurBolt11Invoice(), s_us);

        // Assert
        Assert.False(paying);
    }

    [Fact]
    public void Given_AKeysendOnTheHashOfAPaymentWeMade_When_ItSettles_Then_ItIsNoSelfPayment()
    {
        // Arrange: case B of the review, we paid Mallory's invoice; she keysends us with its preimage
        var payment = Payment(s_mallory, PaymentStatus.Succeeded);
        var keysend = new InvoiceModel(s_hash, s_preimage, new Secret(new byte[32]), null, null, null,
                                       DateTimeOffset.UtcNow, 86_400, 18, keysend: KeysendDetails.Empty);

        // Act
        var receiving = SelfPaymentRule.IsSettledBySelfPayment(keysend, payment, s_us);
        var evenIfPayeeWereUs = SelfPaymentRule.IsSettledBySelfPayment(keysend, Payment(s_us, PaymentStatus.Succeeded),
                                                                       s_us);

        // Assert: a keysend record is never an invoice of ours
        Assert.False(receiving);
        Assert.False(evenIfPayeeWereUs);
    }

    [Fact]
    public void Given_ABolt12InvoiceOrAKeysendPayment_When_Checked_Then_ItIsNoSelfPayment()
    {
        // Arrange
        var bolt12 = new InvoiceModel(s_hash, s_preimage, new Secret(new byte[32]), s_amount, null, null,
                                      DateTimeOffset.UtcNow, 7_200, 40,
                                      bolt12: new Bolt12InvoiceDetails(new Hash(new byte[32]), new byte[] { 0x01 },
                                                                       s_mallory));
        var keysendPayment = PaymentModel.Restore(s_hash, null, s_us, s_amount, LightningMoney.Zero,
                                                  DateTimeOffset.UtcNow, PaymentStatus.Succeeded, null, null,
                                                  s_preimage, null, null, null, DateTimeOffset.UtcNow,
                                                  keysend: KeysendDetails.Empty);

        // Act / Assert
        Assert.False(SelfPaymentRule.IsSelfPayment(Payment(s_us, PaymentStatus.Succeeded), bolt12, s_us));
        Assert.False(SelfPaymentRule.IsSelfPayment(keysendPayment, OurBolt11Invoice(), s_us));
    }

    [Fact]
    public void Given_UnknownPiecesOrAFailedPayment_When_Checked_Then_ItIsNoSelfPayment()
    {
        // Arrange
        var payment = Payment(s_us, PaymentStatus.Succeeded);
        var otherHash = PaymentModel.Restore(new Hash(new byte[32]), "lnbcrt1other", s_us, s_amount,
                                             LightningMoney.Zero, DateTimeOffset.UtcNow, PaymentStatus.Succeeded,
                                             null, null, s_preimage, null, null, null, DateTimeOffset.UtcNow);

        // Act / Assert
        Assert.False(SelfPaymentRule.IsSelfPayment(null, OurBolt11Invoice(), s_us));
        Assert.False(SelfPaymentRule.IsSelfPayment(payment, null, s_us));
        Assert.False(SelfPaymentRule.IsSelfPayment(payment, OurBolt11Invoice(), null));
        Assert.False(SelfPaymentRule.IsSelfPayment(otherHash, OurBolt11Invoice(), s_us));
        Assert.False(SelfPaymentRule.IsSettledBySelfPayment(OurBolt11Invoice(), Payment(s_us, PaymentStatus.Failed),
                                                            s_us));
        Assert.False(SelfPaymentRule.IsSettledBySelfPayment(OurBolt11Invoice(), null, s_us));
    }

    private static InvoiceModel OurBolt11Invoice() =>
        new(s_hash, s_preimage, new Secret(new byte[32]), s_amount, "ours", "lnbcrt1ours", DateTimeOffset.UtcNow,
            3_600, 18);

    private static PaymentModel Payment(CompactPubKey payee, PaymentStatus status) =>
        PaymentModel.Restore(s_hash, "lnbcrt1invoice", payee, s_amount, LightningMoney.Zero, DateTimeOffset.UtcNow,
                             status, null, null, status == PaymentStatus.Succeeded ? s_preimage : (Secret?)null,
                             null, null, null, status == PaymentStatus.InFlight ? null : DateTimeOffset.UtcNow);
}