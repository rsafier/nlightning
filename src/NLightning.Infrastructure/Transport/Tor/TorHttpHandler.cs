using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Node.Options;

/// <summary>
/// The HTTP handler of the node's own outbound HTTP clients (fee estimation, Esplora, the accounting price source):
/// with Tor off a plain <see cref="SocketsHttpHandler"/>; with Tor on, a handler whose connections are routed per host
/// (<see cref="RoutesThroughTor"/>): a loopback or private-network IP address and <c>localhost</c> directly (Tor refuses
/// them and they never leave this host or its LAN, as for peers, NL-588), a <c>.onion</c> host through
/// <see cref="ITorSocksDialer"/>, and every other host through Tor in <c>TorOnly</c>, or in any Tor mode for a client
/// created with <c>throughTorWhenEnabled</c> (the price source, NL-677), else directly. A host name that goes through
/// Tor is resolved by Tor; TLS still runs end to end on top. A mode that needs Tor without a dialer is a composition
/// error, never a silent clearnet handler (NL-580).
/// </summary>
public static class TorHttpHandler
{
    private const string OnionSuffix = ".onion";
    private const string LocalhostName = "localhost";

    /// <summary>
    /// A handler for <paramref name="serviceProvider"/>'s <c>Node:Tor</c> settings.
    /// </summary>
    /// <param name="serviceProvider">Resolves <see cref="NodeOptions"/> and <see cref="ITorSocksDialer"/>.</param>
    /// <param name="pooledConnectionLifetime">How long a pooled connection is reused.</param>
    /// <param name="throughTorWhenEnabled">Send every non-local host through Tor whenever Tor is on
    /// (<c>Hybrid</c> included), not only in <c>TorOnly</c> (NL-677: the price source, whose request times mark when
    /// the node moved money).</param>
    /// <exception cref="InvalidOperationException">Clearnet hosts must go through Tor and no
    /// <see cref="ITorSocksDialer"/> is registered.</exception>
    public static SocketsHttpHandler Create(IServiceProvider serviceProvider, TimeSpan pooledConnectionLifetime,
                                            bool throughTorWhenEnabled = false)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = pooledConnectionLifetime };
        var torOptions = serviceProvider.GetService<IOptions<NodeOptions>>()?.Value.Tor;
        if (torOptions is not { IsEnabled: true })
            return handler;

        var allTraffic = torOptions.IsTorOnly || throughTorWhenEnabled;
        var dialer = serviceProvider.GetService<ITorSocksDialer>();
        if (dialer is null && allTraffic)
            throw new InvalidOperationException($"Node:Tor:Mode is {torOptions.Mode} and this client's requests must "
                                              + "go through Tor, but no ITorSocksDialer is registered: refusing to "
                                              + "make HTTP requests without Tor");

        handler.UseProxy = false;
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            if (!RoutesThroughTor(host, allTraffic))
                return await ConnectDirectAsync(context.DnsEndPoint, cancellationToken);

            if (dialer is null)
                throw new HttpRequestException($"{host} is an onion service, but no ITorSocksDialer is registered");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(torOptions.ConnectTimeout);
            var client = await dialer.ConnectAsync(host, context.DnsEndPoint.Port, timeout.Token);
            return new NetworkStream(client.Client, ownsSocket: true);
        };

        return handler;
    }

    /// <summary>
    /// Whether a request to <paramref name="host"/> goes through Tor while Tor is on: never for a loopback or
    /// private-network IP address (<see cref="TorOptions.IsLocalNetworkAddress"/>) or <c>localhost</c>, always for a
    /// <c>.onion</c> name, and for anything else when <paramref name="allTraffic"/> (<c>TorOnly</c>, or a client that
    /// asked for Tor whenever it is on).
    /// </summary>
    public static bool RoutesThroughTor(string host, bool allTraffic)
    {
        var name = (host ?? string.Empty).Trim().TrimEnd('.');
        if (name.StartsWith('[') && name.EndsWith(']'))
            name = name[1..^1];

        if (IPAddress.TryParse(name, out var address))
            return allTraffic && !TorOptions.IsLocalNetworkAddress(address);
        if (name.Equals(LocalhostName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.EndsWith(OnionSuffix, StringComparison.OrdinalIgnoreCase))
            return true;

        return allTraffic;
    }

    private static async ValueTask<Stream> ConnectDirectAsync(DnsEndPoint endPoint,
                                                              CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}