using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NLightning.Tests.Utils;

/// <summary>
/// Hands out listen ports for tests. Every test process gets its own range of <see cref="PoolSize"/> ports, so test
/// processes that run at the same time (the Docker suites in separate processes or containers with
/// <c>--network host</c>) do not listen on the same port. The range starts at <see cref="PortBaseEnvironmentVariable"/>
/// when it is set, otherwise at one of <see cref="Slots"/> slots chosen from the process id and the machine name
/// (a container's host name is its id, and its test process often has the same small pid as another container's).
/// A port some other process listens on anyway is skipped.
/// </summary>
[ExcludeFromCodeCoverage]
public static class PortPoolUtil
{
    /// <summary>Environment variable that fixes the first port of this process's range.</summary>
    public const string PortBaseEnvironmentVariable = "NLTG_TEST_PORT_BASE";

    /// <summary>The number of ports in a process's range.</summary>
    public const int PoolSize = 50;

    /// <summary>The first port of the lowest slot.</summary>
    public const int FirstSlotPort = 20_000;

    /// <summary>
    /// The number of slots: 20000-29999, below the ephemeral ranges of Linux (32768+) and macOS (49152+), so no
    /// outgoing connection takes a pool port.
    /// </summary>
    public const int Slots = 200;

    private static readonly Random s_random = new();
    private static readonly HashSet<int> s_availablePorts = [];
    private static readonly SemaphoreSlim s_semaphore = new(PoolSize, PoolSize);
    private static readonly object s_lock = new();

    /// <summary>The first port of this process's range.</summary>
    public static int BasePort { get; }

    static PortPoolUtil()
    {
        BasePort = ResolveBasePort(Environment.GetEnvironmentVariable(PortBaseEnvironmentVariable),
                                   Environment.ProcessId, Environment.MachineName);
        for (var port = BasePort; port < BasePort + PoolSize; port++)
            s_availablePorts.Add(port);
    }

    /// <summary>
    /// The first port of a process's range: <paramref name="configuredBase"/> when given, otherwise the slot of
    /// <paramref name="processId"/> on <paramref name="machineName"/> (processes of one machine whose ids differ by less
    /// than <see cref="Slots"/> get different slots).
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured base is not a port from 1024 that leaves room for
    /// <see cref="PoolSize"/> ports.</exception>
    public static int ResolveBasePort(string? configuredBase, int processId, string machineName)
    {
        if (!string.IsNullOrWhiteSpace(configuredBase))
        {
            if (!int.TryParse(configuredBase.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                              out var configured)
             || configured < 1024 || configured > IPEndPoint.MaxPort + 1 - PoolSize)
                throw new InvalidOperationException(
                    $"{PortBaseEnvironmentVariable} must be a port from 1024 to {IPEndPoint.MaxPort + 1 - PoolSize}, " +
                    $"got '{configuredBase}'.");

            return configured;
        }

        var slot = (int)(((uint)processId + StableHash(machineName)) % Slots);
        return FirstSlotPort + slot * PoolSize;
    }

    public static async Task<int> GetAvailablePortAsync()
    {
        // Tests should no take more than 10 seconds to get a port.
        if (!await s_semaphore.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Could not get a port in time.");
        }

        lock (s_lock)
        {
            if (s_availablePorts.Count == 0)
            {
                s_semaphore.Release();
                throw new InvalidOperationException("No available ports. Are you returning them?");
            }

            // Random order, skipping ports another process listens on (they stay in the pool for later)
            var candidates = s_availablePorts.OrderBy(_ => s_random.Next()).ToList();
            if (PickFreePort(candidates, IsFree) is { } port)
            {
                s_availablePorts.Remove(port);
                return port;
            }

            s_semaphore.Release();
            throw new InvalidOperationException(
                $"Every free port of this process's range {BasePort}-{BasePort + PoolSize - 1} is in use by another " +
                $"process; set {PortBaseEnvironmentVariable} to another range.");
        }
    }

    public static void ReleasePort(int port)
    {
        lock (s_lock)
        {
            s_availablePorts.Add(port);
        }

        s_semaphore.Release();
    }

    /// <summary>
    /// The first of <paramref name="candidates"/> that <paramref name="isFree"/> accepts, or null when none is. Pure,
    /// so tests check the skipping without draining this process's shared pool.
    /// </summary>
    public static int? PickFreePort(IEnumerable<int> candidates, Func<int, bool> isFree)
    {
        foreach (var port in candidates)
        {
            if (isFree(port))
                return port;
        }

        return null;
    }

    /// <summary>
    /// Whether nothing listens on <paramref name="port"/>: test nodes listen on the loopback address, and another
    /// process may also listen on every address.
    /// </summary>
    public static bool IsFree(int port) => CanListen(IPAddress.Loopback, port) && CanListen(IPAddress.Any, port);

    private static bool CanListen(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // FNV-1a: string.GetHashCode is randomized per process
    private static uint StableHash(string value)
    {
        var hash = 2166136261U;
        foreach (var b in Encoding.UTF8.GetBytes(value))
            hash = (hash ^ b) * 16777619U;

        return hash;
    }
}