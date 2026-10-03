using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Node.Services;

using Domain.Exceptions;
using Exceptions;

/// <summary>
/// How loud a peer connection's failure is in the log (NL-532): a routine disconnection is not a fault of ours, and
/// the 24 h mainnet soak logged both of its peers' disconnections (a closed stream, a missed <c>pong</c>) at Error.
/// </summary>
public static class PeerConnectionFailures
{
    /// <summary>
    /// <see cref="LogLevel.Information"/> when the peer closed the connection (end of stream); <see cref="LogLevel.Warning"/>
    /// for a connection problem (a missed <c>pong</c>, a timeout, a reset or other socket and I/O errors) or a
    /// connection or protocol condition we raised about the peer (<see cref="ErrorException"/>,
    /// <see cref="WarningException"/> at the end of the chain); <see cref="LogLevel.Error"/> for anything else, i.e.
    /// our own failure (a bug) behind the connection.
    /// </summary>
    public static LogLevel GetLogLevel(Exception? exception)
    {
        if (exception is null)
            return LogLevel.Information;

        var chain = GetChain(exception);
        if (chain.Any(e => e is EndOfStreamException or PeerClosedConnectionException))
            return LogLevel.Information;

        if (chain.Any(e => e is ConnectionTimeoutException or TimeoutException or SocketException or IOException))
            return LogLevel.Warning;

        return chain[^1] is ErrorException or WarningException ? LogLevel.Warning : LogLevel.Error;
    }

    /// <summary>Whether <paramref name="exception"/> says the peer closed the connection.</summary>
    public static bool IsClosedByPeer(Exception? exception) =>
        exception is not null && GetLogLevel(exception) == LogLevel.Information;

    private static List<Exception> GetChain(Exception exception)
    {
        // An AggregateException's InnerException is its first one (a faulted task's single exception)
        var chain = new List<Exception>();
        for (var current = exception; current is not null && chain.Count < 16; current = current.InnerException)
            chain.Add(current);

        return chain;
    }
}