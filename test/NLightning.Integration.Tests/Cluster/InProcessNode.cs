using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Cluster;

using Daemon.Interfaces;
using Docker.Abcd;
using Docker.Utils;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Domain.Payments.Enums;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Testing.Cluster.Nodes;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// Our node, running in the test process (<see cref="NLightningTestNode"/>, the daemon's own composition), as a
/// Lightning node of a cluster topology: the <see cref="ITopologyLightningNode"/> facade over the daemon's client
/// command handlers, plus what only our node offers (the open mode, cooperative close, restart).
/// </summary>
/// <remarks>
/// <para>Reachability (plan "Spike check 1 record"): the node listens on loopback (or on all interfaces when
/// <c>NLTG_HOST_ADDRESS</c> names another host) and the pods reach it at <see cref="PodFacingHost"/>
/// (<c>host.orb.internal</c> on OrbStack). OrbStack forwards that name to the Mac's loopback, so every pod peer that
/// dials us shows up as 127.0.0.1 and is saved inbound-only without an address (NL-497): our node then never redials
/// it. So <b>our node dials out</b> (<see cref="ConnectAsync"/>) for every channel test; only a test that needs the
/// other direction lets a pod dial <see cref="GetAddressAsync"/>.</para>
/// <para>A peer address with a bare alias (LND's <c>GetAddressAsync</c>, which pods resolve inside the namespace) is
/// dialled at the alias's headless Service name (<see cref="ResolvePeerHost"/>), which the host resolves too and which
/// follows the pod after a restart; CLN's stable ClusterIP name is used as given.</para>
/// </remarks>
public sealed class InProcessNode : ITopologyLightningNode
{
    /// <summary>How long a <c>payinvoice</c> waits for the outcome before it reads the payment again.</summary>
    public const uint PayTimeoutSeconds = 60;

    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(250);

    private readonly RunIdentity? _run;

    /// <param name="node">The in-process node (started or not).</param>
    /// <param name="run">The run whose namespace qualifies bare peer aliases; null to dial peer hosts as given.</param>
    /// <param name="podFacingHost">The host pods dial to reach the node (<c>HostEndpoints.ForPods()</c>).</param>
    public InProcessNode(NLightningTestNode node, RunIdentity? run, string podFacingHost)
    {
        TestNode = node ?? throw new ArgumentNullException(nameof(node));
        ArgumentException.ThrowIfNullOrWhiteSpace(podFacingHost);
        _run = run;
        PodFacingHost = podFacingHost;
    }

    /// <summary>The node itself, for everything the facade does not cover (services, logs, crash).</summary>
    public NLightningTestNode TestNode { get; }

    /// <summary>The host the pods dial to reach this node.</summary>
    public string PodFacingHost { get; }

    /// <summary>The open <see cref="OpenChannelAsync(TestOpenChannelRequest, CancellationToken)"/> uses.</summary>
    public InProcessOpenMode DefaultOpenMode { get; set; } = InProcessOpenMode.V1;

    /// <summary>In-process: there is no pod.</summary>
    public INodeHandle? Node => null;

    public NodeKind Kind => NodeKind.NLightning;

    public string Alias => TestNode.Name;

    public Task<string> GetNodeIdAsync(CancellationToken cancellationToken) => Task.FromResult(TestNode.NodeIdHex);

    /// <summary>The address pods dial: <see cref="PodFacingHost"/> and the node's port.</summary>
    public Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new TestPeerAddress(TestNode.NodeIdHex, PodFacingHost, TestNode.Port));

    /// <summary>
    /// Dials <paramref name="peer"/> (its host qualified by <see cref="ResolvePeerHost"/>) and returns once the peer
    /// manager lists the connection. Already connected: returns at once.
    /// </summary>
    public async Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var peerId = ToPubKey(peer.NodeId);
        if (TestNode.IsConnectedTo(peerId))
            return;

        var address = DialAddress(peer);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(s_connectTimeout);
        try
        {
            await TestNode.PeerManager.DialPeerAsync(new PeerAddressInfo(address), attempt.Token);
        }
        catch (InvalidOperationException) when (TestNode.IsConnectedTo(peerId))
        {
            // The peer connected to us in the meantime
        }

        await ClusterPoll.UntilAsync(_ => Task.FromResult(TestNode.IsConnectedTo(peerId)), s_connectTimeout,
                                     s_pollInterval, $"{Alias} connected to {address}", cancellationToken);
    }

    public async Task DisconnectAsync(string nodeId, CancellationToken cancellationToken)
    {
        var peerId = ToPubKey(nodeId);
        TestNode.PeerManager.DisconnectPeer(peerId);
        await ClusterPoll.UntilAsync(_ => Task.FromResult(!TestNode.IsConnectedTo(peerId)), s_connectTimeout,
                                     s_pollInterval, $"{Alias} disconnected from {nodeId}", cancellationToken);
    }

    /// <summary>A fresh P2WPKH address of the node's wallet (reserved, NL-280).</summary>
    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken)
    {
        using var scope = TestNode.Services.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        return (await wallet.GetUnusedAddressAsync(AddressType.P2Wpkh, false)).Address;
    }

    /// <summary>Opens with <see cref="DefaultOpenMode"/>; see <see cref="OpenChannelAsync(TestOpenChannelRequest, InProcessOpenMode, CancellationToken)"/>.</summary>
    public async Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request,
                                                        CancellationToken cancellationToken) =>
        (await OpenChannelAsync(request, DefaultOpenMode, cancellationToken)).Open;

    /// <summary>
    /// Opens a channel to a connected peer through the daemon's <c>openchannel</c> handler and returns once the
    /// funding transaction is in bitcoind's mempool (it still needs blocks): the channel id and the funding outpoint.
    /// </summary>
    public async Task<InProcessChannelOpen> OpenChannelAsync(TestOpenChannelRequest request, InProcessOpenMode mode,
                                                             CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var open = BuildOpenRequest(request, mode);
        var response = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(open, cancellationToken)
                          .WaitAsync(s_openStepTimeout, cancellationToken);

        // The long-poll answers once the funding is published (a dual-funded attempt once its broadcast row is saved)
        var state = await HandleAsync<OpenChannelClientSubscriptionRequest, OpenChannelClientSubscriptionResponse>(
                            new OpenChannelClientSubscriptionRequest(response.ChannelId) { ReportFundingChanges = true },
                            cancellationToken)
                       .WaitAsync(s_openStepTimeout, cancellationToken);
        var txId = state.TxId ?? response.FundingTxId
                ?? throw new InvalidOperationException(
                       $"{Alias}: the open of {response.ChannelId} answered {state.ChannelState} without a funding txid");
        var index = state.Index ?? response.FundingOutputIndex;
        var fundingTxId = txId.ToString();
        await WaitPublishedAsync(fundingTxId, cancellationToken);
        return new InProcessChannelOpen(response.ChannelId, new TestChannelOpen(fundingTxId, (int?)index));
    }

    public async Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken) =>
        (await TestNode.ListChannelsAsync(cancellationToken)).Channels.Select(ToTestChannel).ToList();

    public async Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description,
                                                      CancellationToken cancellationToken)
    {
        var request = new CreateInvoiceClientRequest
        {
            Amount = amountMsat is { } msat ? LightningMoney.MilliSatoshis(msat) : null,
            Description = description
        };
        var invoice = (await HandleAsync<CreateInvoiceClientRequest, CreateInvoiceClientResponse>(
                           request, cancellationToken)).Invoice;
        return new TestInvoice(invoice.Bolt11 ?? throw new InvalidOperationException($"{Alias}: invoice without bolt11"),
                               Convert.ToHexStringLower((byte[])invoice.PaymentHash));
    }

    /// <summary>
    /// <c>payinvoice</c> (<see cref="PayTimeoutSeconds"/>), then the payment's final state from <c>listpayments</c> if it
    /// was still in flight; a failed payment is a result.
    /// </summary>
    public async Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);
        PaymentInfoClientResponse payment;
        try
        {
            payment = (await HandleAsync<PayInvoiceClientRequest, PayInvoiceClientResponse>(
                           new PayInvoiceClientRequest(bolt11) { TimeoutSeconds = PayTimeoutSeconds },
                           cancellationToken)).Payment;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new TestPaymentResult(false, null, e.Message);
        }

        if (payment.Status == PaymentStatus.InFlight)
        {
            var hash = payment.PaymentHash;
            payment = await ClusterPoll.ForAsync(async ct =>
                                                 {
                                                     var p = await TestNode.GetPaymentAsync(hash, ct);
                                                     return p is { Status: not PaymentStatus.InFlight } ? p : null;
                                                 }, TimeSpan.FromSeconds(PayTimeoutSeconds), s_pollInterval,
                                                 $"{Alias}: payment {hash} settled or failed", cancellationToken);
        }

        return ToPaymentResult(payment);
    }

    /// <summary>The height the chain monitor processed; -1 while the node is stopped (a wait treats it as behind).</summary>
    public Task<long> GetBlockHeightAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TestNode.IsRunning ? (long)TestNode.BlockchainMonitor.LastProcessedBlockHeight : -1);

    /// <summary>The wallet's confirmed balance (<c>walletbalance</c>'s confirmed figure).</summary>
    public Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken)
    {
        if (!TestNode.IsRunning)
            return Task.FromResult(0L);

        var height = TestNode.BlockchainMonitor.LastProcessedBlockHeight;
        var balance = TestNode.Services.GetRequiredService<IUtxoMemoryRepository>().GetConfirmedBalance(height);
        return Task.FromResult((long)balance.Satoshi);
    }

    /// <summary>The channel funded by <paramref name="fundingTxId"/> (display hex) as <c>listchannels</c> shows it.</summary>
    public async Task<ChannelInfoClientResponse?> FindChannelAsync(string fundingTxId,
                                                                    CancellationToken cancellationToken) =>
        (await TestNode.ListChannelsAsync(cancellationToken)).Channels
                                                             .FirstOrDefault(c => c.FundingTxId?.ToString()
                                                                               == fundingTxId);

    /// <summary>
    /// <c>closechannel</c>: a cooperative close of <paramref name="channelId"/>, waiting up to
    /// <paramref name="waitSeconds"/> for the negotiation; returns the closing txid (display hex) once agreed.
    /// </summary>
    public async Task<string> CloseChannelAsync(ChannelId channelId, CancellationToken cancellationToken,
                                                uint waitSeconds = 120)
    {
        var closed = await HandleAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                         new CloseChannelClientRequest(channelId) { WaitSeconds = waitSeconds }, cancellationToken);
        return closed.ClosingTxId?.ToString()
            ?? throw new InvalidOperationException(
                   $"{Alias}: the close of {channelId} answered {closed.State} without a closing transaction");
    }

    /// <summary>
    /// A process restart: the service graph is stopped and disposed, then built and started again on the same key,
    /// database and port. Stored peers are dialled again at start (by the DNS names we dialled them at).
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await TestNode.StopAsync();
        await TestNode.StartAsync(cancellationToken);
    }

    public override string ToString() => $"nltg {TestNode}";

    /// <summary>
    /// The host our node dials for a peer's <paramref name="host"/>: an IP address or a dotted name as given; a bare
    /// alias (resolved by pods inside the run's namespace) as its headless Service name in <paramref name="run"/>.
    /// </summary>
    public static string ResolvePeerHost(string host, RunIdentity? run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (run is null || IPAddress.TryParse(host, out _) || host.Contains('.', StringComparison.Ordinal))
            return host;

        return run.ServiceDnsName(host);
    }

    /// <summary>A channel of <c>listchannels</c> on the facade (txid display hex, SCID <c>BxTxO</c>).</summary>
    public static TestChannel ToTestChannel(ChannelInfoClientResponse channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new TestChannel(Convert.ToHexStringLower((byte[])channel.PeerId), channel.FundingTxId?.ToString() ?? string.Empty,
                               channel.FundingOutputIndex, channel.ShortChannelId?.ToString(),
                               (long)channel.Capacity.Satoshi, (long)channel.LocalBalance.MilliSatoshi,
                               IsActive(channel));
    }

    /// <summary>Usable for payments: Open, the peer connected and <c>channel_reestablish</c> exchanged.</summary>
    public static bool IsActive(ChannelInfoClientResponse channel) =>
        channel is { State: ChannelState.Open, IsPeerConnected: true, IsReestablished: true };

    /// <summary>A payment of <c>listpayments</c> as a facade result.</summary>
    public static TestPaymentResult ToPaymentResult(PaymentInfoClientResponse payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return payment.Status switch
        {
            PaymentStatus.Succeeded => new TestPaymentResult(
                true, payment.Preimage is { } preimage ? Convert.ToHexStringLower((byte[])preimage) : null, null),
            PaymentStatus.Failed => new TestPaymentResult(false, null,
                                                         payment.FailureReason
                                                      ?? payment.FailureCode?.ToString() ?? "failed"),
            _ => new TestPaymentResult(false, null, $"still {payment.Status}")
        };
    }

    /// <summary>The daemon request for <paramref name="request"/> opened with <paramref name="mode"/>.</summary>
    public static OpenChannelClientRequest BuildOpenRequest(TestOpenChannelRequest request, InProcessOpenMode mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (mode == InProcessOpenMode.DualFund && request.PushMsat > 0)
            throw new ArgumentException("A dual-funded open has no push amount", nameof(request));

        return new OpenChannelClientRequest(request.RemoteNodeId, LightningMoney.Satoshis(request.CapacitySat))
        {
            PushAmount = request.PushMsat > 0 ? LightningMoney.MilliSatoshis(request.PushMsat) : null,
            IsPublic = request.Announce,
            IsDualFunded = mode == InProcessOpenMode.DualFund,
            ForceV1 = mode == InProcessOpenMode.V1
        };
    }

    private string DialAddress(TestPeerAddress peer) =>
        $"{peer.NodeId}@{ResolvePeerHost(peer.Host, _run)}:{peer.Port}";

    /// <summary>Until the funding is in bitcoind's mempool or already mined (the chain runs with <c>-txindex</c>).</summary>
    private Task WaitPublishedAsync(string fundingTxId, CancellationToken cancellationToken) =>
        ClusterPoll.UntilAsync(async ct =>
                               {
                                   var mempool = await TestNode.Bitcoin.GetRawMempoolAsync(ct);
                                   if (mempool.Any(t => t.ToString() == fundingTxId))
                                       return true;

                                   try
                                   {
                                       await TestNode.Bitcoin.GetRawTransactionAsync(
                                           NBitcoin.uint256.Parse(fundingTxId), true, ct);
                                       return true;
                                   }
                                   catch (NBitcoin.RPC.RPCException)
                                   {
                                       return false;
                                   }
                               }, s_openStepTimeout, s_pollInterval, $"{Alias}: funding {fundingTxId} published",
                               cancellationToken);

    private async Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request,
                                                                   CancellationToken cancellationToken)
    {
        using var scope = TestNode.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, cancellationToken);
    }

    private static CompactPubKey ToPubKey(string nodeIdHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeIdHex);
        return new CompactPubKey(Convert.FromHexString(nodeIdHex));
    }
}

/// <summary>A channel our in-process node opened: its channel id and the facade's funding outpoint.</summary>
public sealed record InProcessChannelOpen(ChannelId ChannelId, TestChannelOpen Open);