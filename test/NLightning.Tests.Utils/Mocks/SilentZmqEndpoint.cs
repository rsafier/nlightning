using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// Loopback ports for a chain monitor's ZMQ subscriber that nothing ever publishes on (NL-310).
/// </summary>
/// <remarks>
/// A test that starts a real <c>BlockchainMonitorService</c> over a <see cref="FakeBitcoinChain"/> must not point its
/// ZMQ socket at a well-known port: a local bitcoind (the Mutinynet container publishes <c>rawblock</c> on
/// 127.0.0.1:28332, the daemon's signet default) then delivers real blocks into the test's monitor while the test hands
/// in its own. Each port here is held by a listener that never accepts, so no other process can bind it while the
/// monitor runs and no ZMQ handshake ever completes.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class SilentZmqEndpoint : IDisposable
{
    private readonly TcpListener _blockListener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _txListener = new(IPAddress.Loopback, 0);

    public SilentZmqEndpoint()
    {
        _blockListener.Start();
        _txListener.Start();
    }

    public string Host => "127.0.0.1";
    public int BlockPort => ((IPEndPoint)_blockListener.LocalEndpoint).Port;
    public int TxPort => ((IPEndPoint)_txListener.LocalEndpoint).Port;

    public void Dispose()
    {
        _blockListener.Stop();
        _txListener.Stop();
        _blockListener.Dispose();
        _txListener.Dispose();
    }
}