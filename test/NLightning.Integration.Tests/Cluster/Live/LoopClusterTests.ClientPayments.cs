using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Routerrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Application.Payments.Events;
using Docker.Utils;
using Domain.Payments.Keysend;
using LndGrpc;

public partial class LoopClusterTests
{
    private static async Task AssertClientPaymentGapsAsync(LndNodeConnection alice, NLightningTestNode node,
        LndGrpcHost host, string grpc, CancellationToken ct)
    {
        using var channel = LndGrpcChannelFactory.Create(LndSettings.FromFiles($"https://127.0.0.1:{host.BoundPort}",
            Path.Combine(grpc, "tls.cert"), Path.Combine(grpc, "admin.macaroon")));
        var router = new Router.RouterClient(channel);
        var hub = node.Services.GetRequiredService<PaymentEventHub>();
        var before = hub.SubscriberCount;
        using var tracking = router.TrackPayments(new TrackPaymentsRequest(), cancellationToken: ct);
        var firstTracked = tracking.ResponseStream.MoveNext(ct);
        await Testing.Cluster.Poll.UntilAsync(_ => Task.FromResult(hub.SubscriberCount > before), s_timeout,
            Testing.Cluster.Poll.DefaultInterval, "real TrackPayments subscription", ct);
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);
        var request = new SendPaymentRequest
        {
            Dest = ByteString.CopyFrom(Convert.FromHexString(alice.LocalNodePubKey)),
            Amt = 1_000,
            PaymentHash = ByteString.CopyFrom(hash),
            TimeoutSeconds = 60,
            FeeLimitMsat = 5_000
        };
        request.DestCustomRecords.Add(CustomRecordCodec.KeysendPreimageType, ByteString.CopyFrom(preimage));
        request.DestCustomRecords.Add(7629169, ByteString.CopyFromUtf8("lnd-p2-proof"));
        using var payment = router.SendPaymentV2(request, cancellationToken: ct);
        Payment? final = null;
        while (await payment.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct))
            final = payment.ResponseStream.Current;
        Assert.NotNull(final);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, final.Status);
        Assert.Equal(Convert.ToHexStringLower(hash), final.PaymentHash);
        Assert.Equal(Convert.ToHexStringLower(preimage), final.PaymentPreimage);
        Assert.True(await firstTracked.WaitAsync(s_timeout, ct));
        var started = tracking.ResponseStream.Current;
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, started.Status);
        Assert.Equal(final.PaymentHash, started.PaymentHash);
        Assert.True(started.PaymentIndex > 0);
        Assert.Equal(final.PaymentIndex, started.PaymentIndex);
        Assert.Empty(started.PaymentPreimage);
        Assert.True(await tracking.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, tracking.ResponseStream.Current.Status);
        var received = await alice.LightningClient.LookupInvoiceAsync(new PaymentHash
        { RHash = ByteString.CopyFrom(hash) }, cancellationToken: ct);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, received.State);
        Log("Real SendPaymentV2 keysend preserved the caller hash and TrackPayments emitted IN_FLIGHT then SUCCEEDED");
    }
}