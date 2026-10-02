using System.Globalization;
using System.Net.Security;
using Grpc.Core;
using Grpc.Net.Client;
using Invoicesrpc;
using Lnrpc;
using Routerrpc;
using Walletrpc;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

/// <summary>
/// A gRPC connection to one LND node with its generated clients (for the spike, LNUnit.LND's <c>Lnrpc</c> types; the
/// plan generates them from LND's protos later). Unlike LNUnit's <c>LNDNodeConnection</c>, which accepts any server
/// certificate, the server must present exactly the node's own <c>tls.cert</c> (pinned): the pod IP is not in the
/// certificate after a restart, but the certificate itself does not change.
/// </summary>
public sealed class LndGrpcConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    public LndGrpcConnection(string host, int port, LndCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(credentials);

        Endpoint = BuildEndpoint(host, port);
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => credentials.Matches(certificate)
            }
        };
        var macaroonHex = credentials.AdminMacaroonHex;
        var callCredentials = CallCredentials.FromInterceptor((_, metadata) =>
        {
            metadata.Add("macaroon", macaroonHex);
            return Task.CompletedTask;
        });
        _channel = GrpcChannel.ForAddress(Endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            Credentials = ChannelCredentials.Create(new SslCredentials(), callCredentials),
            MaxReceiveMessageSize = 64 * 1024 * 1024
        });
        Lightning = new Lightning.LightningClient(_channel);
        Router = new Router.RouterClient(_channel);
        WalletKit = new WalletKit.WalletKitClient(_channel);
        Invoices = new Invoices.InvoicesClient(_channel);
        State = new State.StateClient(_channel);
    }

    /// <summary>The gRPC address (<c>https://host:port</c>).</summary>
    public Uri Endpoint { get; }

    public Lightning.LightningClient Lightning { get; }

    public Router.RouterClient Router { get; }

    public WalletKit.WalletKitClient WalletKit { get; }

    public Invoices.InvoicesClient Invoices { get; }

    public State.StateClient State { get; }

    public void Dispose() => _channel.Dispose();

    /// <summary>The HTTPS address of <paramref name="host"/> (an IPv6 literal gets brackets).</summary>
    internal static Uri BuildEndpoint(string host, int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var literal = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        return new Uri($"https://{literal}:{port.ToString(CultureInfo.InvariantCulture)}");
    }
}