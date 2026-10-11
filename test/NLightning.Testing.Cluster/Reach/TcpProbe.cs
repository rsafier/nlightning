using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NLightning.Testing.Cluster.Reach;

/// <summary>
/// One TCP connect attempt from the test process: resolve the host, connect within a timeout and, optionally, read
/// the first line the server sends (the echo node's <c>pong</c>), so a probe proves the far end answered and not only
/// that some proxy accepted the connection.
/// </summary>
public static class TcpProbe
{
    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/>. Never throws for a network failure: the result
    /// says what happened.
    /// </summary>
    /// <param name="host">An IP address or a DNS name.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="timeout">For the resolution, the connect and the banner read together.</param>
    /// <param name="expectedBanner">Text the server's first line must contain, or null to only connect.</param>
    /// <param name="cancellationToken">Cancels the probe (the result is not returned then).</param>
    public static async Task<ProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, string? expectedBanner,
                                                     CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var target = $"{host}:{port}";
        var watch = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        string? resolved = null;
        try
        {
            var addresses = IPAddress.TryParse(host, out var literal)
                                        ? [literal]
                                        : await Dns.GetHostAddressesAsync(host, timeoutSource.Token)
                                                   .ConfigureAwait(false);
            if (addresses.Length == 0)
                return ProbeResult.Failed(ProbeDirection.HostToPod, target, watch.Elapsed, "no address");

            using var client = new TcpClient(addresses[0].AddressFamily);
            await client.ConnectAsync(addresses[0], port, timeoutSource.Token).ConfigureAwait(false);
            resolved = addresses[0].ToString();
            string? banner = null;
            if (expectedBanner is not null)
            {
                banner = await ReadLineAsync(client.GetStream(), timeoutSource.Token).ConfigureAwait(false);
                if (!banner.Contains(expectedBanner, StringComparison.Ordinal))
                    return ProbeResult.Failed(ProbeDirection.HostToPod, target, watch.Elapsed,
                                              $"banner '{banner.Trim()}' lacks '{expectedBanner}'", resolved);
            }

            return new ProbeResult(ProbeDirection.HostToPod, target, true, watch.Elapsed, null, resolved,
                                   banner?.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Failed(ProbeDirection.HostToPod, target, watch.Elapsed,
                                      $"timed out after {timeout.TotalSeconds:F0} s", resolved);
        }
        catch (SocketException e)
        {
            return ProbeResult.Failed(ProbeDirection.HostToPod, target, watch.Elapsed,
                                      $"{e.SocketErrorCode}: {e.Message}", resolved);
        }
        catch (IOException e)
        {
            return ProbeResult.Failed(ProbeDirection.HostToPod, target, watch.Elapsed, e.Message, resolved);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0)
                break;
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }
}