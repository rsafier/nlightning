namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;

public class PaymentsClientHandlerTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());

    private static readonly CompactPubKey s_payee =
        new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);

    private readonly Mock<IInvoiceService> _invoiceServiceMock = new();
    private readonly Mock<IPaymentService> _paymentServiceMock = new();
    private readonly FixedTimeProvider _timeProvider = new(s_now);

    #region CreateInvoice

    [Fact]
    public async Task Given_AmountDescriptionAndExpiry_When_CreateInvoice_Then_ServiceIsCalledAndInvoiceMapped()
    {
        // Arrange
        var invoice = CreateInvoiceModel(LightningMoney.MilliSatoshis(50_000_123), s_now.AddSeconds(-10), 600);
        _invoiceServiceMock
           .Setup(x => x.CreateInvoiceAsync(LightningMoney.MilliSatoshis(50_000_123), "coffee", 600U,
                                            It.IsAny<CancellationToken>()))
           .ReturnsAsync(invoice);
        var handler = new CreateInvoiceClientHandler(_invoiceServiceMock.Object, _timeProvider);

        // Act
        var response = await handler.HandleAsync(new CreateInvoiceClientRequest
        {
            Amount = LightningMoney.MilliSatoshis(50_000_123),
            Description = "coffee",
            ExpirySeconds = 600
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("lnbcrt1test", response.Invoice.Bolt11);
        Assert.Equal(s_paymentHash, response.Invoice.PaymentHash);
        Assert.Equal(50_000_123UL, response.Invoice.Amount!.MilliSatoshi);
        Assert.Equal("coffee", response.Invoice.Description);
        Assert.Equal(InvoiceStatus.Open, response.Invoice.Status);
        Assert.Equal(s_now.AddSeconds(590), response.Invoice.ExpiresAt);
        Assert.False(response.Invoice.IsExpired);
    }

    [Fact]
    public async Task Given_AnyAmountAndDefaultExpiry_When_CreateInvoice_Then_NullsArePassedThrough()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.CreateInvoiceAsync(null, string.Empty, null, It.IsAny<CancellationToken>()))
                           .ReturnsAsync(CreateInvoiceModel(null, s_now, 3_600));
        var handler = new CreateInvoiceClientHandler(_invoiceServiceMock.Object, _timeProvider);

        // Act
        var response = await handler.HandleAsync(new CreateInvoiceClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(response.Invoice.Amount);
        _invoiceServiceMock.Verify(x => x.CreateInvoiceAsync(null, string.Empty, null, It.IsAny<CancellationToken>()),
                                   Times.Once);
    }

    [Fact]
    public async Task Given_ZeroAmount_When_CreateInvoice_Then_InvalidOperationAndServiceNotCalled()
    {
        // Arrange
        var handler = new CreateInvoiceClientHandler(_invoiceServiceMock.Object, _timeProvider);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                      new CreateInvoiceClientRequest
                                                                      {
                                                                          Amount = LightningMoney.Zero
                                                                      }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _invoiceServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ZeroExpiry_When_CreateInvoice_Then_InvalidOperation()
    {
        // Arrange
        var handler = new CreateInvoiceClientHandler(_invoiceServiceMock.Object, _timeProvider);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                      new CreateInvoiceClientRequest
                                                                      {
                                                                          ExpirySeconds = 0
                                                                      }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
    }

    [Fact]
    public async Task Given_ServiceRejectsArguments_When_CreateInvoice_Then_InvalidOperationWithReason()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.CreateInvoiceAsync(It.IsAny<LightningMoney?>(), It.IsAny<string>(),
                                                            It.IsAny<uint?>(), It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new ArgumentException("description too long"));
        var handler = new CreateInvoiceClientHandler(_invoiceServiceMock.Object, _timeProvider);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                      new CreateInvoiceClientRequest(),
                                                                      TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("description too long", exception.Message);
    }

    #endregion

    #region PayInvoice

    [Fact]
    public async Task Given_Invoice_When_PayInvoice_Then_DefaultTimeoutIsUsedAndPaymentMapped()
    {
        // Arrange
        var payment = CreatePayment();
        payment.AddOutgoingHtlc(new byte[32], 3);
        payment.Succeed(s_preimage, s_now.AddSeconds(2));
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync("lnbcrt1pay", null, TimeSpan.FromSeconds(60),
                                                         It.IsAny<CancellationToken>()))
                           .ReturnsAsync(payment);
        var handler = new PayInvoiceClientHandler(_paymentServiceMock.Object);

        // Act
        var response = await handler.HandleAsync(new PayInvoiceClientRequest(" lnbcrt1pay\n"),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, response.Payment.Status);
        Assert.Equal(s_preimage, response.Payment.Preimage);
        Assert.Equal(3UL, response.Payment.OutgoingHtlcId);
        Assert.Equal(50_000_123UL, response.Payment.Amount.MilliSatoshi);
        Assert.Equal(3_025UL, response.Payment.Fee.MilliSatoshi);
    }

    [Fact]
    public async Task Given_AmountAndTimeout_When_PayInvoice_Then_TheyArePassedThrough()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                         It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(CreatePayment());
        var handler = new PayInvoiceClientHandler(_paymentServiceMock.Object);

        // Act
        var response = await handler.HandleAsync(new PayInvoiceClientRequest("lnbcrt1pay")
        {
            Amount = LightningMoney.MilliSatoshis(7_000),
            TimeoutSeconds = 5
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PaymentStatus.InFlight, response.Payment.Status);
        _paymentServiceMock.Verify(x => x.PayInvoiceAsync("lnbcrt1pay", LightningMoney.MilliSatoshis(7_000),
                                                          TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()),
                                   Times.Once);
    }

    [Fact]
    public async Task Given_FailedPayment_When_PayInvoice_Then_FailureIsReported()
    {
        // Arrange
        var payment = CreatePayment();
        payment.Fail(FailureCode.IncorrectOrUnknownPaymentDetails, 2, "payee refused", s_now.AddSeconds(1));
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                         It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(payment);
        var handler = new PayInvoiceClientHandler(_paymentServiceMock.Object);

        // Act
        var response = await handler.HandleAsync(new PayInvoiceClientRequest("lnbcrt1pay"),
                                                 TestContext.Current.CancellationToken);

        // Assert: a failed payment is a result, not an error
        Assert.Equal(PaymentStatus.Failed, response.Payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, response.Payment.FailureCode);
        Assert.Equal(2, response.Payment.FailureSourceIndex);
        Assert.Equal("payee refused", response.Payment.FailureReason);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("lnbcrt1pay", 0UL, null)]
    [InlineData("lnbcrt1pay", null, 0U)]
    [InlineData("lnbcrt1pay", null, 3_601U)]
    public async Task Given_InvalidRequest_When_PayInvoice_Then_InvalidOperationAndNothingSent(
        string bolt11, ulong? amountMsat, uint? timeoutSeconds)
    {
        // Arrange
        var handler = new PayInvoiceClientHandler(_paymentServiceMock.Object);
        var request = new PayInvoiceClientRequest(bolt11)
        {
            Amount = amountMsat is { } msat ? LightningMoney.MilliSatoshis(msat) : null,
            TimeoutSeconds = timeoutSeconds
        };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                      request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _paymentServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ServiceRejectsInvoice_When_PayInvoice_Then_InvalidOperationWithReason()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                         It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new ArgumentException("invoice expired"));
        var handler = new PayInvoiceClientHandler(_paymentServiceMock.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                      new PayInvoiceClientRequest("lnbcrt1pay"),
                                                                      TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("invoice expired", exception.Message);
    }

    [Fact]
    public async Task Given_PaymentAlreadyInFlight_When_PayInvoice_Then_InvalidOperationWithReason()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                         It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new InvalidOperationException("already in flight"));
        var handler = new PayInvoiceClientHandler(_paymentServiceMock.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                      new PayInvoiceClientRequest("lnbcrt1pay"),
                                                                      TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Equal("already in flight", exception.Message);
    }

    #endregion

    #region ListInvoices / ListPayments

    [Fact]
    public async Task Given_Invoices_When_ListInvoices_Then_PageIsPassedAndExpiryUsesNow()
    {
        // Arrange
        var expired = CreateInvoiceModel(null, s_now.AddHours(-2), 3_600);
        var fresh = CreateInvoiceModel(LightningMoney.MilliSatoshis(1_000), s_now, 3_600);
        _invoiceServiceMock.Setup(x => x.ListInvoicesAsync(5, 10, It.IsAny<CancellationToken>()))
                           .ReturnsAsync([fresh, expired]);
        var handler = new ListInvoicesClientHandler(_invoiceServiceMock.Object, _timeProvider);

        // Act
        var response = await handler.HandleAsync(new ListInvoicesClientRequest { Skip = 5, Take = 10 },
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(response.Invoices,
                          first => Assert.False(first.IsExpired),
                          second => Assert.True(second.IsExpired));
    }

    [Fact]
    public async Task Given_Payments_When_ListPayments_Then_PageIsPassedAndPaymentsMapped()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.ListPaymentsAsync(0, 100, It.IsAny<CancellationToken>()))
                           .ReturnsAsync([CreatePayment()]);
        var handler = new ListPaymentsClientHandler(_paymentServiceMock.Object);

        // Act
        var response = await handler.HandleAsync(new ListPaymentsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var payment = Assert.Single(response.Payments);
        Assert.Equal(s_paymentHash, payment.PaymentHash);
        Assert.Equal(s_payee, payment.PayeeNodeId);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    [InlineData(0, 1_001)]
    public async Task Given_InvalidPage_When_ListInvoicesOrPayments_Then_InvalidOperation(int skip, int take)
    {
        // Arrange
        var invoices = new ListInvoicesClientHandler(_invoiceServiceMock.Object, _timeProvider);
        var payments = new ListPaymentsClientHandler(_paymentServiceMock.Object);

        // Act
        var invoiceError = await Assert.ThrowsAsync<ClientException>(() => invoices.HandleAsync(
                                                                         new ListInvoicesClientRequest
                                                                         {
                                                                             Skip = skip,
                                                                             Take = take
                                                                         }, TestContext.Current.CancellationToken));
        var paymentError = await Assert.ThrowsAsync<ClientException>(() => payments.HandleAsync(
                                                                         new ListPaymentsClientRequest
                                                                         {
                                                                             Skip = skip,
                                                                             Take = take
                                                                         }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, invoiceError.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidOperation, paymentError.ErrorCode);
        _invoiceServiceMock.VerifyNoOtherCalls();
        _paymentServiceMock.VerifyNoOtherCalls();
    }

    #endregion

    private static InvoiceModel CreateInvoiceModel(LightningMoney? amount, DateTimeOffset createdAt,
                                                   uint expirySeconds) =>
        new(s_paymentHash, s_preimage, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), amount,
            amount is null ? null : "coffee", "lnbcrt1test", createdAt, expirySeconds, 40);

    private static PaymentModel CreatePayment() =>
        new(s_paymentHash, "lnbcrt1pay", s_payee, LightningMoney.MilliSatoshis(50_000_123),
            LightningMoney.MilliSatoshis(3_025), s_now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}