using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Testing.Lnd.Tests.TestUtils;

/// <summary>
/// An in-process HTTP/2 TLS endpoint on loopback that answers unary gRPC calls like LND: each path
/// (<c>/lnrpc.State/GetState</c>) maps to a canned reply, every call is recorded with its metadata, and an unknown
/// path answers UNIMPLEMENTED. Only server-side framing is written by hand; the replies are the generated messages.
/// </summary>
internal sealed class FakeLndServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Func<IMessage>> _replies = new(StringComparer.Ordinal);

    private FakeLndServer(WebApplication app, string endpoint)
    {
        _app = app;
        GrpcEndpoint = endpoint;
    }

    /// <summary>The server's <c>https://127.0.0.1:port</c>.</summary>
    public string GrpcEndpoint { get; }

    /// <summary>Every call received: its path and its <c>macaroon</c> header (null when absent).</summary>
    public ConcurrentQueue<(string Path, string? Macaroon)> Calls { get; } = new();

    public static async Task<FakeLndServer> StartAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(certificate);
        }));

        var app = builder.Build();
        FakeLndServer? server = null;
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                         .Addresses.Single();
        var port = new Uri(address).Port;
        server = new FakeLndServer(app, $"https://127.0.0.1:{port}");
        return server;
    }

    /// <summary>Answers calls to <paramref name="path"/> (e.g. <c>/lnrpc.Lightning/GetInfo</c>) with <paramref name="reply"/>.</summary>
    public void Reply(string path, Func<IMessage> reply) => _replies[path] = reply;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var macaroon = context.Request.Headers.TryGetValue("macaroon", out var values) ? values.ToString() : null;
        Calls.Enqueue((path, macaroon));

        // Drain the request frame; the replies do not depend on it.
        await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/grpc";
        if (!_replies.TryGetValue(path, out var reply))
        {
            await context.Response.StartAsync(context.RequestAborted);
            context.Response.AppendTrailer("grpc-status", "12");
            context.Response.AppendTrailer("grpc-message", $"unknown method {path}");
            return;
        }

        var payload = reply().ToByteArray();
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame, 5);
        await context.Response.Body.WriteAsync(frame, context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }
}