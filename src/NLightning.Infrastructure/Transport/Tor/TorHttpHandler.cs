using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Node.Options;

/// <summary>
/// The HTTP handler of the node's own outbound HTTP clients (fee estimation, Esplora): in Tor-only mode every
/// connection goes through <see cref="ITorSocksDialer"/>, the host name resolved by Tor (TLS still runs end to end on
/// top); otherwise a plain <see cref="SocketsHttpHandler"/>. Tor-only mode without a dialer is a composition error,
/// never a silent clearnet handler (NL-580).
/// </summary>
public static class TorHttpHandler
{
    /// <summary>
    /// A handler for <paramref name="serviceProvider"/>'s <c>Node:Tor</c> settings.
    /// </summary>
    /// <param name="serviceProvider">Resolves <see cref="NodeOptions"/> and <see cref="ITorSocksDialer"/>.</param>
    /// <param name="pooledConnectionLifetime">How long a pooled connection is reused.</param>
    /// <exception cref="InvalidOperationException">Tor-only mode and no <see cref="ITorSocksDialer"/>.</exception>
    public static SocketsHttpHandler Create(IServiceProvider serviceProvider, TimeSpan pooledConnectionLifetime)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = pooledConnectionLifetime };
        var torOptions = serviceProvider.GetService<IOptions<NodeOptions>>()?.Value.Tor;
        if (torOptions is { IsTorOnly: true })
        {
            var dialer = serviceProvider.GetService<ITorSocksDialer>()
                      ?? throw new InvalidOperationException("Node:Tor:Mode is TorOnly but no ITorSocksDialer is "
                                                           + "registered: refusing to make HTTP requests without Tor");

            handler.UseProxy = false;
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(torOptions.ConnectTimeout);
                var client = await dialer.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port,
                                                       timeout.Token);
                return new NetworkStream(client.Client, ownsSocket: true);
            };
        }

        return handler;
    }
}