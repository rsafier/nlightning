using System.Globalization;

namespace NLightning.Testing.Cluster.Reach;

using Nodes;

/// <summary>
/// One TCP socket of a pod's network namespace, from <c>/proc/net/tcp</c> or <c>/proc/net/tcp6</c>.
/// </summary>
/// <param name="LocalPort">The local port.</param>
/// <param name="RemotePort">The remote port (0 for a listener).</param>
/// <param name="State">The kernel's state code (<c>01</c> = ESTABLISHED, <c>0A</c> = LISTEN, ...).</param>
public sealed record TcpSocketEntry(int LocalPort, int RemotePort, string State)
{
    /// <summary>The kernel's code of ESTABLISHED.</summary>
    public const string Established = "01";

    public bool IsEstablished => State == Established;
}

/// <summary>
/// The TCP sockets a node's pod holds (test harness phase 4), read from <c>/proc/net/tcp{,6}</c> through an exec: no
/// tool in the image needed (<c>ss</c> and <c>netstat</c> are often missing), every container of the pod sees the same
/// network namespace. Lets a fault test assert that a connection really is gone (e.g. no subscriber on bitcoind's ZMQ
/// port) or back, which a NetworkPolicy alone does not say: it cuts new connections only.
/// </summary>
public static class TcpConnectionTable
{
    /// <summary>
    /// The exec command over <see cref="Script"/> for <c>/proc/net/tcp</c>: both tables; a kernel without IPv6
    /// (<c>ipv6.disable=1</c>, no <c>/proc/net/tcp6</c>) is fine, a failure on <c>/proc/net/tcp</c> fails the exec.
    /// </summary>
    public static IReadOnlyList<string> Command { get; } = ["sh", "-c", Script("/proc/net/tcp")];

    /// <summary>
    /// The script that prints <paramref name="tcp"/> and, when it is readable, <paramref name="tcp"/><c>6</c>; it exits
    /// non-zero only when <paramref name="tcp"/> itself cannot be read (<c>cat a b</c> fails when <c>b</c> is missing
    /// whatever <c>2&gt;/dev/null</c> hides).
    /// </summary>
    public static string Script(string tcp) =>
        $"cat {tcp} && {{ [ ! -e {tcp}6 ] || cat {tcp}6; }}";

    /// <summary>The sockets of <c>/proc/net/tcp</c>-formatted <paramref name="text"/> (header lines skipped).</summary>
    public static IReadOnlyList<TcpSocketEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<TcpSocketEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // sl local_address rem_address st ...: "0: 0100007F:6EAC 0100007F:C350 01 ..."
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !fields[0].EndsWith(':') || !TryPort(fields[1], out var local)
             || !TryPort(fields[2], out var remote))
                continue;

            entries.Add(new TcpSocketEntry(local, remote, fields[3].ToUpperInvariant()));
        }

        return entries;
    }

    /// <summary>How many ESTABLISHED sockets of <paramref name="entries"/> have <paramref name="localPort"/>.</summary>
    public static int CountEstablished(IEnumerable<TcpSocketEntry> entries, int localPort)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Count(e => e.IsEstablished && e.LocalPort == localPort);
    }

    /// <summary>Reads the sockets of <paramref name="node"/>'s pod.</summary>
    public static async Task<IReadOnlyList<TcpSocketEntry>> ReadAsync(INodeHandle node,
                                                                      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        var result = await node.ExecAsync(Command, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"reading the TCP table of {node}");
        return Parse(result.StdOutText);
    }

    /// <summary>How many ESTABLISHED connections <paramref name="node"/>'s pod has on its <paramref name="localPort"/>.</summary>
    public static async Task<int> CountEstablishedAsync(INodeHandle node, int localPort,
                                                        CancellationToken cancellationToken) =>
        CountEstablished(await ReadAsync(node, cancellationToken).ConfigureAwait(false), localPort);

    private static bool TryPort(string address, out int port)
    {
        port = 0;
        var colon = address.LastIndexOf(':');
        return colon > 0
            && int.TryParse(address.AsSpan(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                            out port);
    }
}