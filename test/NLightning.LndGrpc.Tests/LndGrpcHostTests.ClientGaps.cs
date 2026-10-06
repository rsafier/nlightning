using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Moq;

namespace NLightning.LndGrpc.Tests;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Events;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;

public partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_CallerKeysend_When_SendPaymentV2_Then_ItsHashRecordsAndOutcomeArePreserved()
    {
        var secret = new Secret(Enumerable.Repeat((byte)0x92, 32).ToArray());
        var hash = new Hash(SHA256.HashData((ReadOnlySpan<byte>)secret));
        using var key = new NBitcoin.Key();
        var destination = new CompactPubKey(key.PubKey.ToBytes());
        var payment = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(12_345),
                                       LightningMoney.Zero, DateTimeOffset.UtcNow);
        PaymentModel? stored = null;
        PayKeysendRequest? captured = null;
        var release = new TaskCompletionSource();
        _paymentService.Setup(p => p.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        _paymentService.Setup(p => p.PayKeysendAsync(It.IsAny<PayKeysendRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
            .Returns(async (PayKeysendRequest request, PayInvoiceOptions options, CancellationToken cancellation) =>
            {
                captured = request;
                Assert.False(cancellation.CanBeCanceled);
                stored = payment;
                await release.Task;
                payment.Succeed(secret, DateTimeOffset.UtcNow);
                return new PayInvoiceResult(payment, 1, 1);
            });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        var request = new SendPaymentRequest
        {
            Dest = ByteString.CopyFrom((byte[])destination),
            AmtMsat = 12_345,
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            TimeoutSeconds = 30
        };
        request.DestCustomRecords.Add(CustomRecordCodec.KeysendPreimageType, ByteString.CopyFrom((byte[])secret));
        request.DestCustomRecords.Add(7629169, ByteString.CopyFromUtf8("boost"));
        using var call = connection.RouterClient.SendPaymentV2(request, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, call.ResponseStream.Current.Status);
        release.SetResult();
        var succeeded = Assert.Single(await ReadAllAsync(call.ResponseStream));
        Assert.Equal(hash.ToString(), succeeded.PaymentHash);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, succeeded.Status);
        Assert.Equal(destination, captured!.Destination);
        Assert.Equal(secret, captured.Preimage);
        Assert.Equal(new CustomRecord(7629169, "boost"u8), Assert.Single(captured.CustomRecords));
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, true)]
    public async Task Given_InvalidKeysendPreimage_When_SendPaymentV2_Then_RefusedBeforeSending(int length, bool wrongHash)
    {
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        var request = new SendPaymentRequest
        {
            Dest = ByteString.CopyFrom((byte[])CreatePubKey(9)),
            Amt = 1,
            PaymentHash = ByteString.CopyFrom(new byte[32])
        };
        request.DestCustomRecords.Add(CustomRecordCodec.KeysendPreimageType,
                                     ByteString.CopyFrom(Enumerable.Repeat((byte)(wrongHash ? 1 : 0), length).ToArray()));
        using var call = connection.RouterClient.SendPaymentV2(request, cancellationToken: Bounded);
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(Bounded));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        _paymentService.Verify(p => p.PayKeysendAsync(It.IsAny<PayKeysendRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                     It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_FastPayment_When_TrackPayments_Then_StartedStateIsCapturedAndFilteringIsHonored(bool omitStarted)
    {
        var secret = new Secret(Enumerable.Repeat((byte)0x45, 32).ToArray());
        var hash = new Hash(SHA256.HashData((ReadOnlySpan<byte>)secret));
        var now = DateTimeOffset.UtcNow;
        var payment = new PaymentModel(hash, null, CreatePubKey(9), LightningMoney.Satoshis(1_000),
                                       LightningMoney.Zero, now)
        { PaymentIndex = 8 };
        payment.Succeed(secret, now);
        _paymentService.Setup(p => p.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        using var call = connection.RouterClient.TrackPayments(new TrackPaymentsRequest { NoInflightUpdates = omitStarted },
                                                               cancellationToken: Bounded);
        var next = call.ResponseStream.MoveNext(Bounded);
        while (_events.SubscriberCount == 0) await Task.Delay(10, Bounded);
        _events.Publish(new PaymentStartedEvent(hash, payment.Amount, null, 8, now));
        _events.Publish(new PaymentSucceededEvent(hash, payment.Amount, payment.Fee, secret, now));
        Assert.True(await next);
        if (!omitStarted)
        {
            Assert.Equal(Payment.Types.PaymentStatus.InFlight, call.ResponseStream.Current.Status);
            Assert.Equal(8ul, call.ResponseStream.Current.PaymentIndex);
            Assert.Empty(call.ResponseStream.Current.PaymentPreimage);
            Assert.True(await call.ResponseStream.MoveNext(Bounded));
        }
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, call.ResponseStream.Current.Status);
    }

    [Fact]
    public async Task Given_NoChannels_When_GlobalPolicyUpdated_Then_TheFutureDefaultIsStillSaved()
    {
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        var response = await connection.LightningClient.UpdateChannelPolicyAsync(new PolicyUpdateRequest
        { Global = true, BaseFeeMsat = 500, FeeRatePpm = 250, TimeLockDelta = 80 }, cancellationToken: Ct);
        Assert.Empty(response.FailedUpdates);
        _policies.Verify(p => p.SetDefaultAsync(500, 250, 80, It.IsAny<CancellationToken>()), Times.Once);
    }
}