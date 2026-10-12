using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Money;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;
using LndGrpc.Macaroons;
using Testing.Lnd.Routerrpc;

/// <summary>
/// NL-1182 over the <c>HtlcInterceptor</c> stream: <c>RESUME_MODIFIED</c> reaches the switch with its amounts and
/// records, the incoming wire records are offered, and a forward held on chain takes a settle only (a fail ends the
/// stream as LND's <c>ErrCannotFailOnChain</c> does, the hold stays for the next client).
/// </summary>
public sealed partial class LndGrpcWave3HostTests
{
    [Fact]
    public async Task Given_AnInterceptor_When_ItResumesModified_Then_TheSwitchGetsTheAmountsAndRecords()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);
        var resolved = new TaskCompletionSource<ForwardInterceptResolution>();
        _hub.Intercept(CreateForward(11) with { InWireCustomRecords = [new CustomRecord(65_541, [7])] }, 100, false,
                       r =>
                       {
                           resolved.TrySetResult(r);
                           return Task.CompletedTask;
                       });
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        var request = stream.ResponseStream.Current;

        // Act
        await stream.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = request.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.ResumeModified,
            InAmountMsat = 10_200,
            OutAmountMsat = 0,
            OutWireCustomRecords = { [65_537] = ByteString.CopyFrom(1, 2) }
        }, Ct);

        // Assert: the incoming add's records were offered; a zero amount means unchanged (LND)
        Assert.Equal([7], request.InWireCustomRecords[65_541].ToByteArray());
        var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ForwardInterceptAction.ResumeModified, resolution.Action);
        Assert.Equal(LightningMoney.MilliSatoshis(10_200), resolution.InAmount);
        Assert.Null(resolution.OutAmount);
        var record = Assert.Single(resolution.OutWireCustomRecords!);
        Assert.Equal(65_537UL, record.Type);
        Assert.Equal([1, 2], record.Value.ToArray());
        await WaitUntilAsync(() => _hub.HeldCount == 0);
    }

    [Fact]
    public async Task Given_AForwardHeldOnChain_When_TheInterceptorFailsIt_Then_TheStreamEndsAndTheHoldStays()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);
        var calls = 0;
        Assert.True(_hub.InterceptOnChain(CreateForward(12), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }));
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        var request = stream.ResponseStream.Current;

        // Act
        await stream.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = request.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Fail
        }, Ct);
        var end = await Assert.ThrowsAsync<RpcException>(async () => await stream.ResponseStream.MoveNext(Ct));

        // Assert: offered with the settle deadline (the incoming expiry); the fail refused, nothing resolved, still held
        Assert.Equal(500, request.AutoFailHeight);
        Assert.Equal(StatusCode.Unknown, end.StatusCode);
        Assert.Equal("cannot fail held htlc in the on-chain flow", end.Status.Detail);
        await WaitUntilAsync(() => !_hub.IsActive);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(1, _hub.HeldCount);
    }

    [Fact]
    public async Task Given_AForwardHeldOnChain_When_ItsClientLeavesAndAnotherSettlesIt_Then_TheSwitchGetsThePreimage()
    {
        // Arrange: held on chain while a first client is connected, which then leaves
        var preimage = Enumerable.Repeat((byte)6, 32).ToArray();
        var hash = new Domain.Crypto.ValueObjects.Hash(System.Security.Cryptography.SHA256.HashData(preimage));
        var resolved = new TaskCompletionSource<ForwardInterceptResolution>();
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using (var first = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct))
        {
            await WaitUntilAsync(() => _hub.IsActive);
            _hub.InterceptOnChain(CreateForward(13) with { PaymentHash = hash }, r =>
            {
                resolved.TrySetResult(r);
                return Task.CompletedTask;
            });
            Assert.True(await first.ResponseStream.MoveNext(Ct));
        }

        await WaitUntilAsync(() => !_hub.IsActive);
        var heldAfterLeaving = _hub.HeldCount;

        // Act: the next client is offered it again and settles it
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        await stream.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = stream.ResponseStream.Current.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Settle,
            Preimage = ByteString.CopyFrom(preimage)
        }, Ct);

        // Assert: an on-chain hold is never resumed when its client leaves
        Assert.Equal(1, heldAfterLeaving);
        var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ForwardInterceptAction.Settle, resolution.Action);
        Assert.Equal(preimage, (byte[])resolution.Preimage!.Value);
        await WaitUntilAsync(() => _hub.HeldCount == 0);
    }
}