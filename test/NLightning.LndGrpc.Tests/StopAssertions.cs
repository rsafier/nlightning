using System.Diagnostics;
using Grpc.Core;

namespace NLightning.LndGrpc.Tests;

using Shared.Grpc;

/// <summary>NL-1381: a stream open while the host stops ends at once with the defined status.</summary>
internal static class StopAssertions
{
    /// <summary>
    /// Starts reading <paramref name="stream"/> to its end, stops <paramref name="host"/>, and asserts that the stop
    /// took well under the server's own graceful stop timeout (the stream ended, nothing waited for an abort) and
    /// that the reader saw <c>UNAVAILABLE</c> "node is shutting down".
    /// </summary>
    public static async Task AssertStopEndsStreamAsync<T>(LndGrpcHost host, IAsyncStreamReader<T> stream)
    {
        var reading = DrainAsync(stream);
        // The read is pending on the server's next message before the stop
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(reading.IsCompleted, "the stream ended before the stop");

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        var error = await Assert.ThrowsAsync<RpcException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10),
                                                                                   TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Equal(GrpcStopInterceptor.ShuttingDownDetail, error.Status.Detail);
        Assert.True(stopwatch.Elapsed < GrpcServerStopping.GracefulStopTimeout,
                    $"the stop took {stopwatch.Elapsed}, the stream did not end at the stop");
    }

    private static async Task DrainAsync<T>(IAsyncStreamReader<T> stream)
    {
        while (await stream.MoveNext(CancellationToken.None))
        {
        }
    }
}