namespace NLightning.Shared.Grpc;

/// <summary>
/// Signalled when one of the node's gRPC servers starts stopping (its host's <c>StopAsync</c>, or the node's
/// <c>ApplicationStopping</c>): <see cref="GrpcStopInterceptor"/> ends every streaming call then, so a client holding
/// a stream open never holds the node's stop (NL-1381). This file is linked into each gRPC host project.
/// </summary>
internal sealed class GrpcServerStopping : IDisposable
{
    /// <summary>
    /// How long the server's own graceful stop waits for calls still running after the signal (unary calls; a stream
    /// ends at the signal) before Kestrel aborts their connections.
    /// </summary>
    public static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Cancelled once the server stops.</summary>
    public CancellationToken Token => _stopping.Token;

    /// <summary>Whether the server is stopping.</summary>
    public bool IsStopping => _stopping.IsCancellationRequested;

    /// <summary>Starts the stop: every streaming call ends. Idempotent, never throws.</summary>
    public async Task SignalAsync()
    {
        try
        {
            await _stopping.CancelAsync();
        }
        catch (AggregateException)
        {
            // A handler's cancellation callback failed: its call ends anyway
        }
        catch (ObjectDisposedException)
        {
            // Stopped and disposed already
        }
    }

    /// <summary><see cref="SignalAsync"/> for a cancellation callback (the node's <c>ApplicationStopping</c>).</summary>
    public void Signal() => _ = SignalAsync();

    public void Dispose() => _stopping.Dispose();
}