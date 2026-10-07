using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace NLightning.LndGrpc.Tests.Wave3;

using Application.Payments.Interception;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Interception;
using Domain.Payments.Interfaces;
using LndGrpc.Services;
using Routerrpc;

public class InterceptorReliabilityTests
{
    [Fact]
    public async Task Given_AFailedOutboundStream_When_TheReaderIsIdle_Then_TheClientDisconnectsAndHoldsResume()
    {
        using var hub = new HtlcInterceptorHub(NullLogger<HtlcInterceptorHub>.Instance);
        var service = new RouterService(Mock.Of<IPaymentService>(), Mock.Of<IChannelMemoryRepository>(),
            Options.Create(new NodeOptions()), NullLogger<RouterService>.Instance, hub);
        var context = new Mock<ServerCallContext>();
        context.Protected().SetupGet<CancellationToken>("CancellationTokenCore")
            .Returns(TestContext.Current.CancellationToken);
        var reader = new Mock<IAsyncStreamReader<ForwardHtlcInterceptResponse>>();
        reader.Setup(r => r.MoveNext(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return false;
        });
        var writer = new Mock<IServerStreamWriter<ForwardHtlcInterceptRequest>>();
        writer.Setup(w => w.WriteAsync(It.IsAny<ForwardHtlcInterceptRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("broken response stream"));
        var stream = service.HtlcInterceptor(reader.Object, writer.Object, context.Object);
        Assert.True(hub.IsActive);
        var resumed = new TaskCompletionSource<ForwardInterceptResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forward = new InterceptedForward(new ChannelId(new byte[32]), 1, new ShortChannelId(1, 1, 0),
            new ShortChannelId(2, 1, 0), null, new Hash(new byte[32]), LightningMoney.MilliSatoshis(1000),
            LightningMoney.MilliSatoshis(900), 500, 450, 0, new byte[1366], []);
        Assert.Equal(ForwardInterceptOutcome.Held, hub.Intercept(forward, 100, false, resolution =>
        {
            resumed.TrySetResult(resolution);
            return Task.CompletedTask;
        }));

        await Assert.ThrowsAsync<IOException>(() => stream.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(hub.IsActive);
        Assert.Equal(ForwardInterceptAction.Resume,
            (await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Action);
    }
}