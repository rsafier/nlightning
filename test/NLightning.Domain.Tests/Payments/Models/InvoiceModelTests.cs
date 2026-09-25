namespace NLightning.Domain.Tests.Payments.Models;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;

public class InvoiceModelTests
{
    private static readonly DateTimeOffset s_createdAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static InvoiceModel CreateInvoice(LightningMoney? amount = null, uint expirySeconds = 3_600) =>
        new(new Hash(Filled(1)), new Secret(Filled(2)), new Secret(Filled(3)), amount, "coffee", "lnbcrt1test",
            s_createdAt, expirySeconds, 40);

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 32).ToArray();

    [Fact]
    public void Given_NewInvoice_When_Created_Then_OpenAndExpiresAfterExpiry()
    {
        // Act
        var invoice = CreateInvoice(LightningMoney.MilliSatoshis(50_000_123UL));

        // Assert
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(s_createdAt.AddHours(1), invoice.ExpiresAt);
        Assert.Null(invoice.AmountReceived);
        Assert.Null(invoice.SettledAt);
    }

    [Fact]
    public void Given_OpenInvoice_When_IsExpiredChecked_Then_TrueFromExpiresAt()
    {
        // Arrange
        var invoice = CreateInvoice();

        // Assert
        Assert.False(invoice.IsExpired(s_createdAt.AddSeconds(3_599)));
        Assert.True(invoice.IsExpired(s_createdAt.AddSeconds(3_600)));
    }

    [Fact]
    public void Given_OpenInvoice_When_AcceptedThenSettled_Then_SettledWithAmountAndTime()
    {
        // Arrange
        var invoice = CreateInvoice();
        var settledAt = s_createdAt.AddMinutes(5);

        // Act
        invoice.Accept(LightningMoney.MilliSatoshis(1_000UL));
        invoice.Settle(settledAt);

        // Assert
        Assert.Equal(InvoiceStatus.Settled, invoice.Status);
        Assert.Equal(1_000UL, invoice.AmountReceived!.MilliSatoshi);
        Assert.Equal(settledAt, invoice.SettledAt);
        Assert.False(invoice.IsExpired(s_createdAt.AddDays(1)));
    }

    [Fact]
    public void Given_OpenInvoice_When_SettledWithoutAccept_Then_Throws()
    {
        // Arrange
        var invoice = CreateInvoice();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => invoice.Settle(s_createdAt));
    }

    [Fact]
    public void Given_AcceptedInvoice_When_Canceled_Then_Throws()
    {
        // Arrange
        var invoice = CreateInvoice();
        invoice.Accept(LightningMoney.MilliSatoshis(1_000UL));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(invoice.Cancel);
    }

    [Fact]
    public void Given_CanceledInvoice_When_Accepted_Then_Throws()
    {
        // Arrange
        var invoice = CreateInvoice();
        invoice.Cancel();

        // Act & Assert
        Assert.Equal(InvoiceStatus.Canceled, invoice.Status);
        Assert.Throws<InvalidOperationException>(() => invoice.Accept(LightningMoney.MilliSatoshis(1UL)));
    }

    [Fact]
    public void Given_ZeroAmount_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateInvoice(LightningMoney.Zero));
    }

    [Fact]
    public void Given_ZeroExpiry_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateInvoice(expirySeconds: 0));
    }

    [Fact]
    public void Given_SettledStatusWithoutAmount_When_Restored_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new InvoiceModel(new Hash(Filled(1)), new Secret(Filled(2)),
                                                                new Secret(Filled(3)), null, null, "lnbcrt1test",
                                                                s_createdAt, 3_600, 40, InvoiceStatus.Settled,
                                                                null, s_createdAt));
    }
}