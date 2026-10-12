using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.PeerTransport;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures.Tor;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Interfaces;
using Utils;

/// <summary>
/// NL-1369 end to end: two full nodes (the daemon's service graph, SQLite, a regtest bitcoind) that reach each other
/// only through a tiny in-test TCP relay and the host seams. Bob runs with no listener of his own
/// (<c>Node:ListenEnabled</c> false) and takes every connection from the relay through his
/// <see cref="IPeerInboundAcceptor"/>, marked proxied; Alice dials through her
/// <see cref="IPeerConnectionDialer"/>, which connects to the relay whatever address she dials. Alice opens a channel
/// to Bob, both pay each other, Alice restarts and redials through the dialer, and both pay again.
/// </summary>
/// <remarks>
/// Explicit (<c>Category=PeerTransport</c>): it needs a running Docker for its bitcoind (the Tor fixture's standalone
/// chain recipe). Run it with
/// <c>dotnet test test/NLightning.Integration.Tests -c Release -f net10.0
/// --filter "FullyQualifiedName~RelayedPeerFlowTests" -- xUnit.Explicit=only</c>.
/// </remarks>
public sealed class RelayedPeerFlowTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan s_usableTimeout = TimeSpan.FromMinutes(2);

    private readonly TorChainHost _chain = new(TorChainHost.CreateClient(), $"nltg-relay-{Guid.NewGuid():N}",
                                               $"nltg-relay-{Guid.NewGuid():N}");
    private NLightningTestNode _alice = null!;
    private NLightningTestNode _bob = null!;
    private InTestRelay _relay = null!;
    private RelayDialer _dialer = null!;

    public async ValueTask InitializeAsync()
    {
        await _chain.StartAsync();
        _alice = await NLightningTestNode.CreateAsync(_chain.Bitcoin, "relay-alice");
        _bob = await NLightningTestNode.CreateAsync(_chain.Bitcoin, "relay-bob",
                                                    configureNodeOptions: o => o.ListenEnabled = false);

        // The relay hands every connection to Bob's acceptor (resolved per connection: a restart rebuilds his graph)
        _relay = new InTestRelay(() => _bob.Services.GetRequiredService<IPeerInboundAcceptor>());
        _dialer = new RelayDialer(_relay.EndPoint);
        _alice.ConfigureServices = services => services.AddSingleton<IPeerConnectionDialer>(_dialer);

        await _alice.StartAsync(CancellationToken.None);
        await _bob.StartAsync(CancellationToken.None);
        await _alice.FundWalletAsync(LightningMoney.Satoshis(10_000_000), AddressType.P2Wpkh, CancellationToken.None);
        // The accepter of an anchors channel keeps the anchors reserve on chain
        await _bob.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, CancellationToken.None);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "PeerTransport")]
    public async Task Given_TwoNodesBehindARelay_When_TheyOpenAChannel_Then_BothPayEachOtherAcrossARestart()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // Act 1: connect and open, only through the relay (bob listens nowhere)
            await _alice.ConnectToAsync(_bob, ct);
            Assert.Empty(_bob.Services.GetRequiredService<ITcpService>().ListeningTo);
            var opened = await _alice.OpenChannelAsync(new OpenChannelClientRequest(_bob.Address,
                                                                                    LightningMoney.Satoshis(1_000_000))
            {
                PushAmount = LightningMoney.Satoshis(200_000)
            }, ct);
            await WaitUsableAsync(opened.ChannelId, ct);

            // Act 2: payments both ways
            await PayAsync(_alice, _bob, 21_000, ct);
            await PayAsync(_bob, _alice, 12_000, ct);

            // Act 3: alice restarts and redials bob through the dialer (her startup dial of the stored peer)
            await _alice.StopAsync();
            await _alice.StartAsync(ct);
            await Poll.UntilAsync(() => _alice.IsConnectedTo(_bob.NodeId) && _bob.IsConnectedTo(_alice.NodeId),
                                  TimeSpan.FromSeconds(30), "alice redialed bob after her restart", ct);
            await WaitUsableAsync(opened.ChannelId, ct);
            await PayAsync(_alice, _bob, 5_000, ct);
            await PayAsync(_bob, _alice, 3_000, ct);

            // Assert: every connection went through the dialer, and bob kept alice inbound-only (never at the relay)
            Assert.True(_dialer.Dials.Count >= 2, $"{_dialer.Dials.Count} dial(s)");
            Assert.All(_dialer.Dials, d => Assert.Equal(_bob.NodeId, d));
            Assert.True(_relay.Accepted >= 2, $"{_relay.Accepted} relayed connection(s)");
            var aliceSeenByBob = _bob.PeerManager.GetPeer(_alice.NodeId);
            Assert.NotNull(aliceSeenByBob);
            Assert.True(aliceSeenByBob.IsInboundOnly);
        }
        catch
        {
            foreach (var node in new[] { _alice, _bob })
                output.WriteLine($"--- {node.Name} log tail ---\n" + string.Join("\n", node.NodeLog.TakeLast(80)));
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        await _relay.DisposeAsync();
        await _chain.RemoveAsync();
    }

    private async Task WaitUsableAsync(Domain.Channels.ValueObjects.ChannelId channelId, CancellationToken ct)
    {
        await Poll.UntilAsync(async () => (await _alice.GetChannelAsync(channelId, ct)).IsUsable()
                                       && (await _bob.GetChannelAsync(channelId, ct)).IsUsable(),
                              s_usableTimeout, "the channel usable on both ends", ct);
    }

    private static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, long satoshis,
                                       CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(satoshis), "relayed", ct);
        var payment = await payer.PayInvoiceAsync(invoice.Bolt11!, ct);
        Assert.True(payment.Status == PaymentStatus.Succeeded,
                    $"{payer.Name} -> {payee.Name}: {payment.Status} {payment.FailureReason}");
    }

    /// <summary>
    /// A tiny TCP relay, as a host in front of a node would run: it accepts on loopback and hands each connection
    /// to the node's acceptor as a proxied stream.
    /// </summary>
    private sealed class InTestRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _accepted;

        public InTestRelay(Func<IPeerInboundAcceptor> target)
        {
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(target);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public int Accepted => Volatile.Read(ref _accepted);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
                // Stopped
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(Func<IPeerInboundAcceptor> target)
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                socket.NoDelay = true;
                Interlocked.Increment(ref _accepted);
                var stream = new NetworkStream(socket, ownsSocket: true);
                try
                {
                    await target().AcceptInboundAsync(stream, socket.RemoteEndPoint, isProxied: true, _cts.Token);
                }
                catch (InvalidOperationException)
                {
                    // The node is not accepting (restarting): the client sees the connection close and redials
                    await stream.DisposeAsync();
                }
            }
        }
    }

    /// <summary>Dials the relay, whatever address the node asks for, and records the node ids.</summary>
    private sealed class RelayDialer(IPEndPoint relay) : IPeerConnectionDialer
    {
        private readonly ConcurrentQueue<CompactPubKey> _dials = new();

        public IReadOnlyList<CompactPubKey> Dials => [.. _dials];

        public async ValueTask<Stream?> DialAsync(CompactPubKey nodeId, PeerAddress address,
                                                  CancellationToken cancellationToken)
        {
            _dials.Enqueue(nodeId);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(relay, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}