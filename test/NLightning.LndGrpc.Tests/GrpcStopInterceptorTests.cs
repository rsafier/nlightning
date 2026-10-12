using Grpc.Core;

namespace NLightning.LndGrpc.Tests;

using Domain.Node.Fencing;
using Shared.Grpc;

public sealed class GrpcStopInterceptorTests
{
    [Fact]
    public void Given_AFencedExceptionWrapped_When_Searched_Then_ItIsFoundInInnerAndAggregateExceptions()
    {
        // Arrange
        var fenced = new NodeFencedException("lease lost");
        var nested = new InvalidOperationException("save refused", new IOException("io", fenced));
        var aggregate = new AggregateException(new TimeoutException(), new InvalidOperationException("x", fenced));

        // Act and assert
        Assert.Same(fenced, GrpcStopInterceptor.FindFenced(fenced));
        Assert.Same(fenced, GrpcStopInterceptor.FindFenced(nested));
        Assert.Same(fenced, GrpcStopInterceptor.FindFenced(aggregate));
        Assert.Null(GrpcStopInterceptor.FindFenced(new InvalidOperationException("other")));
        Assert.Null(GrpcStopInterceptor.FindFenced(null));
    }

    [Fact]
    public void Given_AStatusAHandlerChose_When_Searched_Then_ItIsNotRewrittenAsFenced()
    {
        // Arrange: an RpcException the handler threw itself keeps its own status
        var chosen = new RpcException(new Status(StatusCode.NotFound, "x"), "x");

        // Act and assert
        Assert.Null(GrpcStopInterceptor.FindFenced(chosen));
    }

    [Fact]
    public void Given_TheStatuses_When_Built_Then_ShutdownIsUnavailableAndFencedIsFailedPrecondition()
    {
        // Act
        var shuttingDown = GrpcStopInterceptor.ShuttingDown();
        var fenced = GrpcStopInterceptor.Fenced();

        // Assert
        Assert.Equal(StatusCode.Unavailable, shuttingDown.StatusCode);
        Assert.Equal("node is shutting down", shuttingDown.Status.Detail);
        Assert.Equal(StatusCode.FailedPrecondition, fenced.StatusCode);
        Assert.Equal("node is fenced", fenced.Status.Detail);
    }

    [Fact]
    public async Task Given_ASignal_When_SignalledTwiceAndAfterDispose_Then_ItNeverThrows()
    {
        // Arrange
        var stopping = new GrpcServerStopping();

        // Act
        await stopping.SignalAsync();
        await stopping.SignalAsync();
        stopping.Dispose();
        await stopping.SignalAsync();
        stopping.Signal();

        // Assert
        Assert.True(GrpcServerStopping.GracefulStopTimeout < TimeSpan.FromSeconds(30));
    }
}