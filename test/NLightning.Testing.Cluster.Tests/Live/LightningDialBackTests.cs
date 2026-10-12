using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Nodes;
using Cluster.Nodes.Cln;
using Cluster.Nodes.Lnd;
using Cluster.Reach;
using Cluster.Run;
using Cluster.Topology;

/// <summary>
/// Spike check 1 with real Lightning implementations: LND and CLN in their pods dial a listener in the host test
/// process (where the in-process NLightning node listens) at <see cref="HostEndpoints.ForPods"/>, and the listener
/// receives each one's BOLT 8 act one (50 bytes, version 0, a compressed ephemeral key). The listener answers nothing,
/// so the handshakes then time out; the transport path is what this proves. Explicit (see
/// <see cref="ClusterSmokeTests"/> for how to run them).
/// </summary>
[Trait("Category", "Cluster")]
public class LightningDialBackTests
{
    /// <summary>The secp256k1 generator, a valid node id nobody owns.</summary>
    private const string FakeNodeId = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private const int ActOneLength = 50;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_LndAndClnInPods_When_TheyDialTheHostProcess_Then_TheListenerReceivesTheirBolt8ActOne()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var options = TestRunOptions.FromEnvironment("dial-back") with { Quota = NamespaceQuota.Spike, Log = Log };
        var run = await TestRun.StartAsync(options, ct);
        var ns = run.Namespace;
        var hostName = HostEndpoints.ForPods();
        await using var listener = ActOneListener.Start(HostEndpoints.BindAddressFor(hostName));
        try
        {
            using var topology = await new TopologyBuilder { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4) }
                                      .AddBitcoinCore("miner")
                                      .AddLnd("alice")
                                      .AddCln("bob")
                                      .BuildAsync(run, ct);
            var target = new TestPeerAddress(FakeNodeId, hostName, listener.Port);

            // Act
            var lnd = await DialAsync(topology.Node<LndNode>("alice"), listener, target, ct);
            var cln = await DialAsync(topology.Node<ClnTestPeer>("bob"), listener, target, ct);

            // Assert
            foreach (var (who, received) in new[] { ("LND", lnd), ("CLN", cln) })
            {
                Assert.Equal(ActOneLength, received.Bytes.Length);
                Assert.Equal(0, received.Bytes[0]);
                Assert.Contains(received.Bytes[1], new byte[] { 2, 3 });
                Log($"{ns}: {who} reached {hostName}:{listener.Port} (bound to {listener.LocalEndPoint.Address}) "
                  + $"in {received.After.TotalMilliseconds:F0} ms; the listener saw {received.Remote}, act one "
                  + $"{Convert.ToHexString(received.Bytes.AsSpan(0, 4)).ToLowerInvariant()}... "
                  + $"({received.Bytes.Length} bytes)");
            }
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    private static async Task<ActOne> DialAsync(ILightningTestPeer node, ActOneListener listener,
                                                TestPeerAddress target, CancellationToken ct)
    {
        var before = listener.Received.Count;
        var watch = Stopwatch.StartNew();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(10));
        var dial = Task.Run(async () =>
        {
            try
            {
                await node.ConnectAsync(target, attempt.Token);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Expected: nobody answers act one, so the handshake fails or is cancelled
            }
        }, CancellationToken.None);
        var received = await Poll.ForAsync(_ => Task.FromResult(listener.Received.Skip(before).FirstOrDefault()),
                                           TimeSpan.FromSeconds(30), Poll.DefaultInterval, $"{node.Alias}'s act one",
                                           ct);
        await attempt.CancelAsync();
        await dial;
        return received with { After = watch.Elapsed };
    }

    private sealed record ActOne(IPEndPoint Remote, byte[] Bytes, TimeSpan After);

    /// <summary>Accepts connections and records the first <see cref="ActOneLength"/> bytes each one sends.</summary>
    private sealed class ActOneListener : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<ActOne> _received = new();
        private readonly Task _loop;

        private ActOneListener(TcpListener listener)
        {
            _listener = listener;
            _loop = AcceptLoopAsync();
        }

        public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public int Port => LocalEndPoint.Port;

        public IReadOnlyCollection<ActOne> Received => _received.ToArray();

        public static ActOneListener Start(IPAddress address)
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            return new ActOneListener(listener);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException
                                              or SocketException)
                {
                    return;
                }

                _ = ReadActOneAsync(socket);
            }
        }

        private async Task ReadActOneAsync(Socket socket)
        {
            using (socket)
            {
                var buffer = new byte[ActOneLength];
                var read = 0;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    while (read < buffer.Length)
                    {
                        var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, timeout.Token);
                        if (n == 0)
                            break;
                        read += n;
                    }
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    // Keep what arrived
                }

                if (read > 0 && socket.RemoteEndPoint is IPEndPoint remote)
                    _received.Enqueue(new ActOne(remote, buffer[..read], TimeSpan.Zero));
            }
        }
    }
}