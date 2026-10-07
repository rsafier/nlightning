using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Moq;

namespace NLightning.LndGrpc.Tests;

using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;

/// <summary>
/// SendToRouteV2 shards of one payment hash sent over several calls, as ln-service's multi-path pay sends them
/// (NL-1276): every call joins the payroute payment of the hash in flight and answers with its own shard.
/// </summary>
public partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_TwoShardsOfOneHashInParallel_When_SendToRouteV2_Then_EachJoinsThePaymentAndAnswersItsOwnShard()
    {
        // Arrange: shard A is held until shard B has been answered; B fails at our peer (our index 0)
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var destination = RealKey();
        var preimage = new Secret(Enumerable.Repeat((byte)0x42, 32).ToArray());
        var hash = new Hash(SHA256.HashData((ReadOnlySpan<byte>)preimage));
        var inFlight = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(20_000),
                                        LightningMoney.MilliSatoshis(1_000), DateTimeOffset.UtcNow);
        var succeeded = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(20_000),
                                         LightningMoney.MilliSatoshis(2_000), DateTimeOffset.UtcNow);
        succeeded.Succeed(preimage, DateTimeOffset.UtcNow);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = new List<PayRouteRequest>();
        var calls = 0;
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .Returns(async (PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) =>
                       {
                           lock (captured)
                               captured.Add(request);
                           if (Interlocked.Increment(ref calls) == 1)
                           {
                               await releaseA.Task;
                               return new PayRouteResult(succeeded,
                                                         [new RouteOutcome(0, PaymentPartState.Succeeded, 1, null,
                                                                           null, null)]);
                           }

                           return new PayRouteResult(inFlight,
                                                     [
                                                         new RouteOutcome(0, PaymentPartState.Failed, 2,
                                                                          FailureCode.TemporaryChannelFailure, 0,
                                                                          "our peer could not forward")
                                                     ]);
                       });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act: A goes out and waits; B is sent while A is in flight and answers with its own failure
        var shardA = connection.RouterClient.SendToRouteV2Async(Shard(hash, channel.RemoteNodeId, destination),
                                                                cancellationToken: Ct).ResponseAsync;
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        var attemptB = await connection.RouterClient.SendToRouteV2Async(
            Shard(hash, channel.RemoteNodeId, destination), cancellationToken: Ct);
        Assert.False(shardA.IsCompleted);
        releaseA.SetResult();
        var attemptA = await shardA;

        // Assert: both calls joined the hash's payroute payment as shards of one set (secret and total each)
        Assert.Equal(2, captured.Count);
        Assert.All(captured, request =>
        {
            Assert.Equal(PayRouteAttachMode.IfInFlight, request.Attach);
            Assert.True(request.IndependentShards);
            Assert.False(request.SkipTemporaryFailures);
            Assert.Equal(hash, request.PaymentHash);
            Assert.Equal(20_000ul, request.TotalAmount!.MilliSatoshi);
            Assert.Equal(Enumerable.Repeat((byte)0x66, 32).ToArray(), (byte[])request.PaymentSecret!.Value);
        });

        // Assert: B's failure is attributed at LND's index (ours + 1), A succeeded with the preimage
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attemptB.Status);
        Assert.Equal(2ul, attemptB.AttemptId);
        Assert.Equal(Failure.Types.FailureCode.TemporaryChannelFailure, attemptB.Failure.Code);
        Assert.Equal(1u, attemptB.Failure.FailureSourceIndex);
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Succeeded, attemptA.Status);
        Assert.Equal(1ul, attemptA.AttemptId);
        Assert.Equal((byte[])preimage, attemptA.Preimage.ToByteArray());
    }

    [Fact]
    public async Task Given_SkipTempErrAndShardTimes_When_SendToRouteV2_Then_TheFlagIsPassedAndTheAttemptCarriesItsOwnTimes()
    {
        // Arrange: a shard that failed while the payment stays in flight (another shard of it is held), offered and
        // resolved at its own times, later than the payment row's creation
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var destination = RealKey();
        var hash = new Hash(RandomNumberGenerator.GetBytes(32));
        var created = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var offered = created + TimeSpan.FromSeconds(20);
        var resolved = created + TimeSpan.FromSeconds(25);
        var inFlight = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(20_000),
                                        LightningMoney.MilliSatoshis(1_000), created);
        PayRouteRequest? captured = null;
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) =>
                                     captured = request)
                       .ReturnsAsync(new PayRouteResult(inFlight,
                       [
                           new RouteOutcome(0, PaymentPartState.Failed, 3, FailureCode.TemporaryChannelFailure, 0,
                                            "our peer could not forward")
                           {
                               OfferedAt = offered,
                               ResolvedAt = resolved
                           }
                       ]));
        var request = Shard(hash, channel.RemoteNodeId, destination);
        request.SkipTempErr = true;
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var attempt = await connection.RouterClient.SendToRouteV2Async(request, cancellationToken: Ct);

        // Assert: skip_temp_err reaches payroute, the attempt reports the shard's own times
        Assert.NotNull(captured);
        Assert.True(captured.SkipTemporaryFailures);
        Assert.Equal(PayRouteAttachMode.IfInFlight, captured.Attach);
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.Equal(offered.ToUnixTimeMilliseconds() * 1_000_000, attempt.AttemptTimeNs);
        Assert.Equal(resolved.ToUnixTimeMilliseconds() * 1_000_000, attempt.ResolveTimeNs);
    }

    [Fact]
    public async Task Given_ARouteWithoutAnMppRecord_When_SendToRouteV2_Then_ItNeverJoinsAPaymentInFlight()
    {
        // Arrange: LND refuses a non-MPP route for a hash in flight (ErrPaymentInFlight); payroute refuses it too
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var hash = new Hash(RandomNumberGenerator.GetBytes(32));
        PayRouteRequest? captured = null;
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) =>
                                     captured = request)
                       .ThrowsAsync(new InvalidOperationException(
                                        $"A payment for payment hash {hash} is already InFlight (being retried)."));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.RouterClient.SendToRouteV2Async(new SendToRouteRequest
            {
                PaymentHash = ByteString.CopyFrom((byte[])hash),
                Route = TwoHopRoute(channel.RemoteNodeId, RealKey())
            }, cancellationToken: Ct));

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(PayRouteAttachMode.Never, captured.Attach);
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        Assert.Contains("already InFlight", exception.Status.Detail);
    }

    [Fact]
    public async Task Given_AShardThatDoesNotFitThePaymentInFlight_When_SendToRouteV2_Then_InvalidArgument()
    {
        // Arrange: payroute refuses the attach (another payment address than the shards in flight)
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var hash = new Hash(RandomNumberGenerator.GetBytes(32));
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new ArgumentException(
                                        "The payment secret differs from the one of the payment in flight.", "call"));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.RouterClient.SendToRouteV2Async(Shard(hash, channel.RemoteNodeId, RealKey()),
                                                             cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("payment secret differs", exception.Status.Detail);
    }

    [Fact]
    public async Task Given_ASettledPayment_When_AnotherShardIsSent_Then_FailedPrecondition()
    {
        // Arrange: the hash's payment already succeeded, so the shard joins nothing
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var hash = new Hash(RandomNumberGenerator.GetBytes(32));
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException(
                                        $"A payment for payment hash {hash} is already Succeeded."));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.RouterClient.SendToRouteV2Async(Shard(hash, channel.RemoteNodeId, RealKey()),
                                                             cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        Assert.Contains("already Succeeded", exception.Status.Detail);
    }

    /// <summary>One 10,000 msat shard of a 20,000 msat payment over our channel 7 (payment address 0x66...).</summary>
    private static SendToRouteRequest Shard(Hash hash, CompactPubKey peer, CompactPubKey destination)
    {
        var route = TwoHopRoute(peer, destination);
        route.Hops[1].MppRecord = new MPPRecord
        {
            PaymentAddr = ByteString.CopyFrom(Enumerable.Repeat((byte)0x66, 32).ToArray()),
            TotalAmtMsat = 20_000
        };
        return new SendToRouteRequest { PaymentHash = ByteString.CopyFrom((byte[])hash), Route = route };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}