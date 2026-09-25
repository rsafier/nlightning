namespace NLightning.Domain.Tests.Payments.Models;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;

public class PaymentModelTests
{
    private static readonly DateTimeOffset s_createdAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly CompactPubKey s_payee = new([0x02, .. Enumerable.Repeat((byte)7, 32)]);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)9, 32).ToArray());

    private static PaymentModel CreatePayment() =>
        new(new Hash(Enumerable.Repeat((byte)1, 32).ToArray()), "lnbcrt1test", s_payee,
            LightningMoney.MilliSatoshis(50_000_123UL), LightningMoney.MilliSatoshis(27_000UL), s_createdAt);

    [Fact]
    public void Given_NewPayment_When_Created_Then_InFlightWithTotal()
    {
        // Act
        var payment = CreatePayment();

        // Assert
        Assert.Equal(PaymentStatus.InFlight, payment.Status);
        Assert.Equal(50_027_123UL, payment.TotalAmount.MilliSatoshi);
        Assert.Null(payment.OutgoingHtlcId);
    }

    [Fact]
    public void Given_InFlightPayment_When_HtlcAddedTwice_Then_SecondThrows()
    {
        // Arrange
        var payment = CreatePayment();
        payment.AddOutgoingHtlc(s_channelId, 0);

        // Act & Assert
        Assert.Equal(s_channelId, payment.OutgoingChannelId);
        Assert.Equal(0UL, payment.OutgoingHtlcId);
        Assert.Throws<InvalidOperationException>(() => payment.AddOutgoingHtlc(s_channelId, 1));
    }

    [Fact]
    public void Given_InFlightPayment_When_Succeeded_Then_PreimageStoredAndFinal()
    {
        // Arrange
        var payment = CreatePayment();
        var preimage = new Secret(Enumerable.Repeat((byte)5, 32).ToArray());

        // Act
        payment.Succeed(preimage, s_createdAt.AddSeconds(3));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(preimage, payment.Preimage);
        Assert.Throws<InvalidOperationException>(() => payment.Fail(null, null, "late", s_createdAt));
    }

    [Fact]
    public void Given_InFlightPayment_When_Failed_Then_FailureStoredAndFinal()
    {
        // Arrange
        var payment = CreatePayment();

        // Act
        payment.Fail(FailureCode.IncorrectOrUnknownPaymentDetails, 3, null, s_createdAt.AddSeconds(3));

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(3, payment.FailureSourceIndex);
        Assert.Throws<InvalidOperationException>(() => payment.Succeed(new Secret(new byte[32]), s_createdAt));
    }

    [Fact]
    public void Given_ZeroAmount_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaymentModel(new Hash(new byte[32]), null, s_payee,
                                                                          LightningMoney.Zero, LightningMoney.Zero,
                                                                          s_createdAt));
    }

    [Fact]
    public void Given_StoredSucceededPayment_When_Restored_Then_AllFieldsKept()
    {
        // Arrange
        var preimage = new Secret(Enumerable.Repeat((byte)5, 32).ToArray());
        var completedAt = s_createdAt.AddSeconds(2);

        // Act
        var payment = PaymentModel.Restore(new Hash(new byte[32]), null, s_payee, LightningMoney.MilliSatoshis(1UL),
                                           LightningMoney.Zero, s_createdAt, PaymentStatus.Succeeded, s_channelId, 4,
                                           preimage, null, null, null, completedAt);

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(4UL, payment.OutgoingHtlcId);
        Assert.Equal(preimage, payment.Preimage);
        Assert.Equal(completedAt, payment.CompletedAt);
    }

    [Fact]
    public void Given_SucceededWithoutPreimage_When_Restored_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => PaymentModel.Restore(new Hash(new byte[32]), null, s_payee,
                                                                    LightningMoney.MilliSatoshis(1UL),
                                                                    LightningMoney.Zero, s_createdAt,
                                                                    PaymentStatus.Succeeded, null, null, null, null,
                                                                    null, null, s_createdAt));
    }

    [Fact]
    public void Given_HtlcIdWithoutChannel_When_Restored_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => PaymentModel.Restore(new Hash(new byte[32]), null, s_payee,
                                                                    LightningMoney.MilliSatoshis(1UL),
                                                                    LightningMoney.Zero, s_createdAt,
                                                                    PaymentStatus.InFlight, null, 1, null, null,
                                                                    null, null, null));
    }

    [Fact]
    public void Given_RouteEndingAtThePayee_When_Created_Then_RouteAndSharedSecretsAreKeptInOrder()
    {
        // Arrange
        var hop = new CompactPubKey([0x03, .. Enumerable.Repeat((byte)8, 32)]);
        PaymentHop[] route =
        [
            new(hop, new ShortChannelId(1, 2, 3), LightningMoney.MilliSatoshis(50_027_123UL), 900,
                new Secret(Enumerable.Repeat((byte)0xA1, 32).ToArray())),
            new(s_payee, new ShortChannelId(4, 5, 6), LightningMoney.MilliSatoshis(50_000_123UL), 860,
                new Secret(Enumerable.Repeat((byte)0xA2, 32).ToArray()))
        ];

        // Act
        var payment = new PaymentModel(new Hash(Enumerable.Repeat((byte)1, 32).ToArray()), null, s_payee,
                                       LightningMoney.MilliSatoshis(50_000_123UL),
                                       LightningMoney.MilliSatoshis(27_000UL), s_createdAt, route);

        // Assert
        Assert.Equal(route, payment.Route);
        Assert.Equal(route.Select(h => h.SharedSecret), payment.HopSharedSecrets);
    }

    [Fact]
    public void Given_RouteNotEndingAtThePayee_When_Created_Then_Throws()
    {
        // Arrange
        var other = new CompactPubKey([0x03, .. Enumerable.Repeat((byte)8, 32)]);
        PaymentHop[] route =
        [
            new(other, new ShortChannelId(1, 2, 3), LightningMoney.MilliSatoshis(1UL), 900,
                new Secret(new byte[32]))
        ];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new PaymentModel(new Hash(new byte[32]), null, s_payee,
                                                                LightningMoney.MilliSatoshis(1UL),
                                                                LightningMoney.Zero, s_createdAt, route));
    }

    [Fact]
    public void Given_NoRoute_When_Created_Then_RouteIsEmpty()
    {
        // Act
        var payment = CreatePayment();

        // Assert
        Assert.Empty(payment.Route);
        Assert.Empty(payment.HopSharedSecrets);
    }
}