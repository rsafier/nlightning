namespace NLightning.Domain.Tests.Payments.Keysend;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Domain.Payments.Models;

/// <summary>
/// The keysend sides of <see cref="InvoiceModel"/>, <see cref="PaymentModel"/> and <see cref="KeysendOptions"/>
/// (lane lh1-l3).
/// </summary>
public class KeysendModelTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly CompactPubKey s_payee =
        new(Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619"));

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 32).ToArray();

    [Fact]
    public void Given_KeysendDetails_When_CreatingInvoice_Then_KindIsKeysendWithoutBolt11()
    {
        // Act
        var invoice = new InvoiceModel(new Hash(Filled(1)), new Secret(Filled(2)), new Secret(new byte[32]), null,
                                       null, null, s_now, 86_400, 18,
                                       keysend: new KeysendDetails([new CustomRecord(65537, [0x01])]));

        // Assert
        Assert.Equal(InvoiceKind.Keysend, invoice.Kind);
        Assert.Null(invoice.Bolt11);
        Assert.Equal(65537UL, Assert.Single(invoice.Keysend!.CustomRecords).Type);
    }

    [Fact]
    public void Given_KeysendAndBolt11_When_CreatingInvoice_Then_ArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new InvoiceModel(new Hash(Filled(1)), new Secret(Filled(2)),
                                                                new Secret(new byte[32]), null, null, "lnbcrt1x",
                                                                s_now, 60, 18, keysend: KeysendDetails.Empty));
    }

    [Fact]
    public void Given_KeysendAndBolt12_When_CreatingInvoice_Then_ArgumentException()
    {
        // Arrange
        var bolt12 = new Bolt12InvoiceDetails(new Hash(Filled(7)), new byte[] { 0x01 }, s_payee);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new InvoiceModel(new Hash(Filled(1)), new Secret(Filled(2)),
                                                                new Secret(new byte[32]), null, null, null, s_now, 60,
                                                                18, bolt12: bolt12, keysend: KeysendDetails.Empty));
    }

    [Fact]
    public void Given_KeysendPayment_When_Restored_Then_DetailsKept()
    {
        // Arrange
        var details = new KeysendDetails([new CustomRecord(7629169, "boost"u8)]);

        // Act
        var payment = PaymentModel.Restore(new Hash(Filled(1)), null, s_payee, LightningMoney.Satoshis(1),
                                           LightningMoney.Zero, s_now, PaymentStatus.Succeeded, null, null,
                                           new Secret(Filled(2)), null, null, null, s_now, keysend: details);

        // Assert
        Assert.Same(details, payment.Keysend);
    }

    [Fact]
    public void Given_KeysendPaymentWithBolt11_When_Created_Then_ArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new PaymentModel(new Hash(Filled(1)), "lnbcrt1x", s_payee,
                                                                LightningMoney.Satoshis(1), LightningMoney.Zero, s_now,
                                                                keysend: KeysendDetails.Empty));
    }

    [Fact]
    public void Given_DefaultKeysendOptions_When_Validated_Then_AcceptOnAndValid()
    {
        // Act
        var options = new KeysendOptions();

        // Assert
        Assert.True(options.Accept);
        Assert.Empty(options.GetValidationErrors());
        Assert.DoesNotContain(new NodeOptions().GetValidationErrors(),
                              e => e.StartsWith("Keysend", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_OutOfRangeKeysendOptions_When_Validated_Then_ErrorsForEach()
    {
        // Arrange
        var options = new KeysendOptions
        {
            MinFinalCltvExpiryDelta = 11,
            FinalCltvExpiryDelta = 17,
            RecordExpirySeconds = 0
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(3, errors.Count);
        Assert.Contains(new NodeOptions { Keysend = options }.GetValidationErrors(),
                        e => e.Contains(nameof(KeysendOptions.MinFinalCltvExpiryDelta), StringComparison.Ordinal));
    }
}