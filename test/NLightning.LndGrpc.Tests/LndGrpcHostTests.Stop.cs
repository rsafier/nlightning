using System.Security.Cryptography;
using Grpc.Core;
using Moq;

namespace NLightning.LndGrpc.Tests;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Fencing;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Google.Protobuf;
using LndGrpc.Macaroons;
using Shared.Grpc;
using Testing.Lnd.Invoicesrpc;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;
using Invoice = Testing.Lnd.Lnrpc.Invoice;
using LndChannelPoint = Testing.Lnd.Lnrpc.ChannelPoint;
using Payment = Testing.Lnd.Lnrpc.Payment;

/// <summary>
/// NL-1381: a node stop ends every open stream at once with <c>UNAVAILABLE</c> "node is shutting down" instead of
/// waiting the host's shutdown timeout, and a fenced node answers <c>FAILED_PRECONDITION</c> "node is fenced".
/// </summary>
public sealed partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_ASendPaymentV2InFlight_When_TheNodeStops_Then_TheStreamEndsUnavailableAndThePaymentGoesOn()
    {
        // Arrange: a payment that does not finish (a hold invoice the payee never settles)
        var (bolt11, hash) = CreateBolt11(0xC1, 12_000);
        var payment = new PaymentModel(hash, bolt11, CreatePubKey(9), LightningMoney.Satoshis(12_000),
                                       LightningMoney.MilliSatoshis(250), DateTimeOffset.UtcNow);
        PaymentModel? stored = null;
        var paying = new TaskCompletionSource();
        CancellationToken paymentToken = default;
        _paymentService.Setup(x => x.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        _paymentService.Setup(x => x.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                     It.IsAny<CancellationToken>()))
                       .Returns(async (string _, LightningMoney? _, PayInvoiceOptions _, CancellationToken ct) =>
                       {
                           paymentToken = ct;
                           stored = payment;
                           await paying.Task;
                           return new PayInvoiceResult(payment, 1, 1);
                       });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        using var call = connection.RouterClient.SendPaymentV2(
            new SendPaymentRequest { PaymentRequest = bolt11, TimeoutSeconds = 60 }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, call.ResponseStream.Current.Status);

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
        Assert.False(paymentToken.CanBeCanceled);
        paying.SetResult();
    }

    [Fact]
    public async Task Given_ATrackPaymentV2OfAPaymentInFlight_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange
        var (bolt11, hash) = CreateBolt11(0xC2, 5_000);
        var payment = new PaymentModel(hash, bolt11, CreatePubKey(9), LightningMoney.Satoshis(5_000),
                                       LightningMoney.MilliSatoshis(100), DateTimeOffset.UtcNow);
        _paymentService.Setup(x => x.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        using var call = connection.RouterClient.TrackPaymentV2(
            new TrackPaymentRequest { PaymentHash = ByteString.CopyFrom((byte[])hash) }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, call.ResponseStream.Current.Status);

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_ATrackPaymentsSubscriber_When_TheNodeStops_Then_TheStreamEndsUnavailableAndItsSubscriptionGoes()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        using var call = connection.RouterClient.TrackPayments(new TrackPaymentsRequest(), cancellationToken: Bounded);
        await WaitUntilAsync(() => _events.SubscriberCount == 1);

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
        Assert.Equal(0, _events.SubscriberCount);
    }

    [Fact]
    public async Task Given_AnInvoiceSubscriber_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange: the subscriber has seen add_index 1, invoice 2 is replayed
        _invoices.Add(CreateInvoice(1, DateTimeOffset.UtcNow, InvoiceStatus.Open));
        _invoices.Add(CreateInvoice(2, DateTimeOffset.UtcNow, InvoiceStatus.Open));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        using var call = connection.LightningClient.SubscribeInvoices(new InvoiceSubscription { AddIndex = 1 },
                                                                      cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_ASingleInvoiceSubscriberOfAHeldInvoice_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange: an invoice that stays open
        var invoice = CreateInvoice(2, DateTimeOffset.UtcNow, InvoiceStatus.Open);
        _invoices.Add(invoice);
        using var connection = await ConnectAsync(LndMacaroonFiles.InvoiceFileName);
        using var call = connection.InvoiceClient.SubscribeSingleInvoice(
            new SubscribeSingleInvoiceRequest { RHash = ByteString.CopyFrom((byte[])invoice.PaymentHash) },
            cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.Equal(Invoice.Types.InvoiceState.Open, call.ResponseStream.Current.State);

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_AStateSubscriber_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        using var call = connection.StateClient.SubscribeState(new SubscribeStateRequest(), cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_AChannelBackupSubscriber_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange
        _backups.Setup(x => x.CreateSnapshotAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        using var call = connection.LightningClient.SubscribeChannelBackups(new ChannelBackupSubscription(),
                                                                             cancellationToken: Bounded);
        await call.ResponseHeadersAsync;

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_AnOpenChannelStreamWaitingForTheChannel_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange: the funding is published, the channel never gets ready
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x65, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x66, 32).ToArray());
        var subscriptions = 0;
        _dispatcher.On<OpenChannelClientRequest, OpenChannelClientResponse>(_ => new OpenChannelClientResponse(channelId));
        _dispatcher.OnAsync<OpenChannelClientSubscriptionRequest, OpenChannelClientSubscriptionResponse>(
            async (_, ct) =>
            {
                // The first answer is the published funding, the next ones wait for a change that never comes
                if (Interlocked.Increment(ref subscriptions) > 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new OpenChannelClientSubscriptionResponse(channelId)
                {
                    ChannelState = ChannelState.V1FundingSigned,
                    TxId = fundingTxId,
                    Index = 0
                };
            });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        using var call = connection.LightningClient.OpenChannel(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])CreatePubKey(9)),
            LocalFundingAmount = 500_000
        }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.NotNull(call.ResponseStream.Current.ChanPending);

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_ACloseChannelStreamWaitingForTheConfirmation_When_TheNodeStops_Then_TheStreamEndsUnavailable()
    {
        // Arrange: the closing transaction is known, it never confirms
        var channel = CreateChannel(8, ChannelState.Open);
        _channels.Add(channel);
        var closingTxId = new TxId(Enumerable.Repeat((byte)0x72, 32).ToArray());
        _channelMemory.Setup(x => x.TryGetChannelState(channel.ChannelId, out It.Ref<ChannelState>.IsAny))
                      .Returns(new TryGetChannelState((ChannelId _, out ChannelState current) =>
                      {
                          current = ChannelState.Closing;
                          return true;
                      }));
        _dispatcher.On<CloseChannelClientRequest, CloseChannelClientResponse>(
            _ => new CloseChannelClientResponse(channel.ChannelId, ChannelState.Closing, closingTxId));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        using var call = connection.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new LndChannelPoint
            {
                FundingTxidBytes = ByteString.CopyFrom((byte[])channel.FundingOutput!.TransactionId!.Value),
                OutputIndex = 0
            }
        }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.NotNull(call.ResponseStream.Current.ClosePending);

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, call.ResponseStream);
    }

    [Fact]
    public async Task Given_AFencedNode_When_AUnaryCallWritesThroughTheFence_Then_ItIsFailedPreconditionNodeIsFenced()
    {
        // Arrange
        _invoiceService.Setup(x => x.GetInvoiceAsync(It.IsAny<Hash>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("save refused",
                                                                  new NodeFencedException("lease lost")));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.LightningClient.LookupInvoiceAsync(
                new PaymentHash { RHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) },
                cancellationToken: Bounded));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal(GrpcStopInterceptor.FencedDetail, error.Status.Detail);
    }

    [Fact]
    public async Task Given_AFencedNode_When_AStreamingCallHitsTheFence_Then_ItIsFailedPreconditionNodeIsFenced()
    {
        // Arrange
        _paymentService.Setup(x => x.GetPaymentAsync(It.IsAny<Hash>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new NodeFencedException());
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = connection.RouterClient.TrackPaymentV2(
                new TrackPaymentRequest { PaymentHash = ByteString.CopyFrom(new byte[32]) }, cancellationToken: Bounded);
            await call.ResponseStream.MoveNext(Bounded);
        });

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal(GrpcStopInterceptor.FencedDetail, error.Status.Detail);
    }
}