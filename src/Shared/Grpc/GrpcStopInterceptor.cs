using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NLightning.Shared.Grpc;

using Domain.Node.Fencing;

/// <summary>
/// Ends the calls of one of the node's gRPC servers when it stops, and answers a fenced node with a status of its own
/// (NL-1381).
/// </summary>
/// <remarks>
/// <para>A streaming call (server, client or bidirectional) runs with a <see cref="ServerCallContext"/> whose
/// <see cref="ServerCallContext.CancellationToken"/> also fires at <see cref="GrpcServerStopping"/>: every handler
/// already ends when its caller leaves, so it ends the same way, through the same cleanup (an HTLC interceptor's held
/// forwards are resumed, a channel acceptor's pending opens rejected), and the caller gets
/// <see cref="StatusCode.Unavailable"/> "node is shutting down" however the handler ended. A status the handler chose
/// itself (other than <see cref="StatusCode.Cancelled"/>) is kept. A call that arrives while the server stops gets the
/// same status before anything runs. Unary calls keep the caller's token: the host's bounded graceful stop
/// (<see cref="GrpcServerStopping.GracefulStopTimeout"/>) covers them.</para>
/// <para>A <see cref="NodeFencedException"/> anywhere in a call's exception (inner exceptions included) is answered
/// <see cref="StatusCode.FailedPrecondition"/> "node is fenced", not <see cref="StatusCode.Unknown"/>: this instance may
/// no longer write, and retrying it will not help.</para>
/// </remarks>
internal sealed class GrpcStopInterceptor : Interceptor
{
    /// <summary>The status detail of a call ended by the stop.</summary>
    public const string ShuttingDownDetail = "node is shutting down";

    /// <summary>The status detail of a call refused by the node's write fence.</summary>
    public const string FencedDetail = "node is fenced";

    private readonly GrpcServerStopping _stopping;

    public GrpcStopInterceptor(GrpcServerStopping stopping)
    {
        _stopping = stopping;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (_stopping.IsStopping)
            throw ShuttingDown();
        try
        {
            return await continuation(request, context);
        }
        catch (Exception e) when (FindFenced(e) is not null)
        {
            throw Fenced();
        }
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(context, async stoppable =>
        {
            await continuation(request, responseStream, stoppable);
            return true;
        });

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(context, stoppable => continuation(requestStream, stoppable));

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(context, async stoppable =>
        {
            await continuation(requestStream, responseStream, stoppable);
            return true;
        });

    private async Task<T> RunAsync<T>(ServerCallContext context, Func<ServerCallContext, Task<T>> call)
    {
        if (_stopping.IsStopping)
            throw ShuttingDown();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken,
                                                                           _stopping.Token);
        T result;
        try
        {
            result = await call(new StoppableCallContext(context, linked.Token));
        }
        catch (Exception e) when (FindFenced(e) is not null)
        {
            throw Fenced();
        }
        catch (Exception e) when (EndedByStop(context) && e is not RpcException { StatusCode: not StatusCode.Cancelled })
        {
            throw ShuttingDown();
        }

        // A handler that treats the cancellation as its caller leaving returns normally: the caller is still there
        if (EndedByStop(context))
            throw ShuttingDown();
        return result;
    }

    private bool EndedByStop(ServerCallContext context) =>
        _stopping.IsStopping && !context.CancellationToken.IsCancellationRequested;

    /// <summary>The <see cref="NodeFencedException"/> in <paramref name="exception"/> or its inner exceptions.</summary>
    internal static NodeFencedException? FindFenced(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 16; depth++)
        {
            switch (exception)
            {
                case NodeFencedException fenced:
                    return fenced;
                case RpcException:
                    return null;
                case AggregateException aggregate:
                    foreach (var inner in aggregate.InnerExceptions)
                        if (FindFenced(inner) is { } found)
                            return found;
                    return null;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    internal static RpcException ShuttingDown() => new(new Status(StatusCode.Unavailable, ShuttingDownDetail));

    internal static RpcException Fenced() => new(new Status(StatusCode.FailedPrecondition, FencedDetail));

    /// <summary>The call's own context with a token that also fires at the server's stop.</summary>
    private sealed class StoppableCallContext : ServerCallContext
    {
        /// <summary>Where <c>GetHttpContext</c> looks for the request in a context it did not create.</summary>
        private const string HttpContextKey = "__HttpContext";

        private readonly ServerCallContext _inner;
        private readonly CancellationToken _cancellationToken;

        public StoppableCallContext(ServerCallContext inner, CancellationToken cancellationToken)
        {
            _inner = inner;
            _cancellationToken = cancellationToken;
            // ASP.NET Core's stream readers and writers reach the request through the context they are handed
            inner.UserState.TryAdd(HttpContextKey, inner.GetHttpContext());
        }

        protected override string MethodCore => _inner.Method;
        protected override string HostCore => _inner.Host;
        protected override string PeerCore => _inner.Peer;
        protected override DateTime DeadlineCore => _inner.Deadline;
        protected override Metadata RequestHeadersCore => _inner.RequestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore => _inner.ResponseTrailers;

        protected override Status StatusCore
        {
            get => _inner.Status;
            set => _inner.Status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => _inner.WriteOptions;
            set => _inner.WriteOptions = value;
        }

        protected override AuthContext AuthContextCore => _inner.AuthContext;
        protected override IDictionary<object, object> UserStateCore => _inner.UserState;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            _inner.CreatePropagationToken(options);

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            _inner.WriteResponseHeadersAsync(responseHeaders);
    }
}