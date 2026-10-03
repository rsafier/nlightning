namespace NLightning.Daemon.Tests.Handlers;

using Application.Payments.Events;
using Daemon.Handlers;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;

public class WaitInvoiceClientHandlerTests
{
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Hash s_otherHash = new(Enumerable.Repeat((byte)0x12, 32).ToArray());

    private readonly PaymentEventHub _hub = new();
    private readonly Mock<IInvoiceService> _invoiceServiceMock = new();

    [Fact]
    public async Task Given_ASettledInvoice_When_Waiting_Then_ItAnswersAtOnce()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.GetInvoiceAsync(s_paymentHash, It.IsAny<CancellationToken>()))
                           .ReturnsAsync(Invoice(InvoiceStatus.Settled));
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new WaitInvoiceClientRequest(s_paymentHash, 5),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.False(response.TimedOut);
        Assert.Equal(InvoiceStatus.Settled, response.Invoice.Status);
        Assert.Equal(0, _hub.SubscriberCount);
    }

    [Fact]
    public async Task Given_AnOpenInvoice_When_ItIsSettled_Then_TheEventEndsTheWait()
    {
        // Arrange: open on the first read, settled once its event was published
        var settled = false;
        _invoiceServiceMock.Setup(x => x.GetInvoiceAsync(s_paymentHash, It.IsAny<CancellationToken>()))
                           .ReturnsAsync(() => Invoice(settled ? InvoiceStatus.Settled : InvoiceStatus.Open));
        var handler = CreateHandler();

        // Act
        var wait = handler.HandleAsync(new WaitInvoiceClientRequest(s_paymentHash, 30),
                                       TestContext.Current.CancellationToken);
        await WaitForSubscriberAsync();
        _hub.Publish(new InvoiceSettledEvent(s_otherHash, LightningMoney.MilliSatoshis(1), DateTimeOffset.UtcNow));
        settled = true;
        _hub.Publish(new InvoiceSettledEvent(s_paymentHash, LightningMoney.MilliSatoshis(5_000),
                                             DateTimeOffset.UtcNow));
        var response = await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert: answered well before the 5 s recheck, and the subscription is gone
        Assert.False(response.TimedOut);
        Assert.Equal(InvoiceStatus.Settled, response.Invoice.Status);
        Assert.Equal(0, _hub.SubscriberCount);
    }

    [Fact]
    public async Task Given_AnInvoiceThatStaysOpen_When_TheTimeoutEnds_Then_ItAnswersTimedOut()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.GetInvoiceAsync(s_paymentHash, It.IsAny<CancellationToken>()))
                           .ReturnsAsync(() => Invoice(InvoiceStatus.Open));
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new WaitInvoiceClientRequest(s_paymentHash, 1),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.TimedOut);
        Assert.Equal(InvoiceStatus.Open, response.Invoice.Status);
        Assert.Equal(0, _hub.SubscriberCount);
    }

    [Fact]
    public async Task Given_NoEventSource_When_TheInvoiceIsCanceled_Then_TheRecheckFindsIt()
    {
        // Arrange: no event bus; the periodic read sees the cancel
        var reads = 0;
        _invoiceServiceMock.Setup(x => x.GetInvoiceAsync(s_paymentHash, It.IsAny<CancellationToken>()))
                           .ReturnsAsync(() => Invoice(++reads > 1 ? InvoiceStatus.Canceled : InvoiceStatus.Open));
        var handler = new WaitInvoiceClientHandler(_invoiceServiceMock.Object, null, TimeProvider.System);

        // Act
        var response = await handler.HandleAsync(new WaitInvoiceClientRequest(s_paymentHash, 30),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.False(response.TimedOut);
        Assert.Equal(InvoiceStatus.Canceled, response.Invoice.Status);
    }

    [Fact]
    public async Task Given_AnUnknownHash_When_Waiting_Then_InvalidOperation()
    {
        // Arrange
        var handler = CreateHandler();

        // Act
        var error = await Assert.ThrowsAsync<ClientException>(
                        () => handler.HandleAsync(new WaitInvoiceClientRequest(s_paymentHash),
                                                  TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, error.ErrorCode);
        Assert.Equal(0, _hub.SubscriberCount);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(WaitInvoiceClientHandler.MaxTimeoutSeconds + 1)]
    public async Task Given_ATimeoutOutOfRange_When_Waiting_Then_InvalidOperationBeforeAnyRead(uint timeoutSeconds)
    {
        // Arrange
        var handler = CreateHandler();

        // Act
        var error = await Assert.ThrowsAsync<ClientException>(
                        () => handler.HandleAsync(new WaitInvoiceClientRequest(s_paymentHash, timeoutSeconds),
                                                  TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, error.ErrorCode);
        _invoiceServiceMock.VerifyNoOtherCalls();
    }

    private WaitInvoiceClientHandler CreateHandler() =>
        new(_invoiceServiceMock.Object, _hub, TimeProvider.System);

    private async Task WaitForSubscriberAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_hub.SubscriberCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, _hub.SubscriberCount);
    }

    private static InvoiceModel Invoice(InvoiceStatus status) =>
        new(s_paymentHash, new Secret(Enumerable.Repeat((byte)0xcd, 32).ToArray()),
            new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), LightningMoney.MilliSatoshis(5_000), "coffee",
            "lnbcrt1test", DateTimeOffset.UtcNow, 600, 40, status,
            status == InvoiceStatus.Settled ? LightningMoney.MilliSatoshis(5_000) : null,
            status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null);
}