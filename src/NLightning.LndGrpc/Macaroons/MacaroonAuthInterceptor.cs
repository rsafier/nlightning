using System.Net;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The macaroon gate in front of every call (LND's <c>macaroons.Service.ValidateMacaroon</c>): exactly one
/// <c>macaroon</c> metadata value, hex, checked by <see cref="MacaroonVerifier"/> against the operations
/// <see cref="LndPermissions"/> lists for the method. A method LND does not list is refused before anything runs.
/// </summary>
/// <remarks>
/// No usable macaroon answers <see cref="StatusCode.Unauthenticated"/>, a macaroon without the method's operations
/// <see cref="StatusCode.PermissionDenied"/> (LND answers both <c>Unknown</c> with the bakery's text). With
/// <c>LndGrpc:AllowNoMacaroons</c> (loopback only) the verifier is null and every listed method is allowed.
/// </remarks>
internal sealed class MacaroonAuthInterceptor : Interceptor
{
    private readonly MacaroonVerifier? _verifier;
    private readonly ILogger<MacaroonAuthInterceptor> _logger;

    public MacaroonAuthInterceptor(LndGrpcRuntime runtime, ILogger<MacaroonAuthInterceptor> logger)
    {
        _verifier = runtime.Verifier;
        _logger = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var required = LndPermissions.ForMethod(context.Method)
                    ?? throw new RpcException(new Status(StatusCode.PermissionDenied,
                                                         "unknown permissions required for method"));
        if (_verifier is null)
            return;

        var values = context.RequestHeaders.Where(h => h.Key == "macaroon" && !h.IsBinary).ToList();
        if (values.Count != 1)
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                                              $"expected 1 macaroon, got {values.Count}"));

        byte[] macaroon;
        try
        {
            macaroon = Convert.FromHexString(values[0].Value);
        }
        catch (FormatException)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "the macaroon is not hex"));
        }

        var result = _verifier.Check(macaroon, required, context.Method, PeerAddress(context));
        switch (result.Outcome)
        {
            case MacaroonCheck.Allowed:
                return;
            case MacaroonCheck.PermissionDenied:
                throw new RpcException(new Status(StatusCode.PermissionDenied, result.Reason ?? "permission denied"));
            default:
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("LND gRPC {Method} refused: {Reason}", context.Method, result.Reason);
                throw new RpcException(new Status(StatusCode.Unauthenticated, result.Reason ?? "invalid macaroon"));
        }
    }

    private static IPAddress? PeerAddress(ServerCallContext context) =>
        context.GetHttpContext().Connection.RemoteIpAddress;
}