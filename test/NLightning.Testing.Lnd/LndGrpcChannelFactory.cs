// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDNodeConnection.CreateGrpcConnection), Copyright (c)
// 2024-2025 nbd, MIT License; the full text is in LICENSE-LNUnit.txt next to this file. Changed: the server
// certificate is pinned (or checked by a callback) instead of accepted blindly, SocketsHttpHandler with HTTP/2
// keep-alive pings instead of HttpClientHandler, no GRPC_SSL_CIPHER_SUITES (a Grpc.Core setting Grpc.Net ignores),
// and the TLS certificate is no longer offered as a client certificate.

using System.Net.Security;
using Grpc.Core;
using Grpc.Net.Client;

namespace NLightning.Testing.Lnd;

/// <summary>Opens the gRPC channel to an LND node: TLS with the certificate check of the settings, plus the macaroon.</summary>
public static class LndGrpcChannelFactory
{
    /// <summary>A new channel; the caller owns it (dispose it to close the HTTP/2 connection).</summary>
    public static GrpcChannel Create(LndSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var handler = CreateHttpHandler(settings);
        try
        {
            var credentials = settings.Macaroon is { } macaroon
                ? ChannelCredentials.Create(ChannelCredentials.SecureSsl, LndMacaroonCredentials.Create(macaroon))
                : ChannelCredentials.SecureSsl;

            return GrpcChannel.ForAddress(settings.GrpcEndpoint!, new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true,
                Credentials = credentials,
                MaxReceiveMessageSize = settings.MaxReceiveMessageSize,
                MaxSendMessageSize = settings.MaxSendMessageSize
            });
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    internal static SocketsHttpHandler CreateHttpHandler(LndSettings settings) => new()
    {
        EnableMultipleHttp2Connections = true,
        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = LndCertificatePinning.CreateCallback(settings)
        }
    };
}