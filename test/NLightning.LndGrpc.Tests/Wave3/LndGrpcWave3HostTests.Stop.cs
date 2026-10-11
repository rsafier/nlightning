namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Channels.Acceptance;
using Domain.Payments.Interception;
using LndGrpc.Macaroons;

/// <summary>
/// NL-1381: a node stop ends the HTLC interceptor and channel acceptor streams at once with <c>UNAVAILABLE</c>, through
/// the same path as a client that leaves: held forwards resume, pending opens are rejected.
/// </summary>
public sealed partial class LndGrpcWave3HostTests
{
    [Fact]
    public async Task Given_AnInterceptorHoldingAForward_When_TheNodeStops_Then_TheStreamEndsAndTheForwardResumes()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);
        var resolved = new TaskCompletionSource<ForwardInterceptResolution>();
        _hub.Intercept(CreateForward(11), 100, false, r =>
        {
            resolved.TrySetResult(r);
            return Task.CompletedTask;
        });
        Assert.True(await stream.ResponseStream.MoveNext(Ct));

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, stream.ResponseStream);
        var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ForwardInterceptAction.Resume, resolution.Action);
        Assert.False(_hub.IsActive);
        Assert.Equal(0, _hub.HeldCount);
    }

    [Fact]
    public async Task Given_AnAcceptorWithAPendingOpen_When_TheNodeStops_Then_TheStreamEndsAndTheOpenIsRejected()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);
        var decision = _gate.DecideAsync(CreateOpenRequest(12), Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));

        // Act and assert
        await StopAssertions.AssertStopEndsStreamAsync(_host!, stream.ResponseStream);
        var result = await decision.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(result.Accept);
        Assert.Equal(ChannelOpenDecision.GenericRejection, result.Error);
        Assert.False(_gate.HasDeciders);
    }
}