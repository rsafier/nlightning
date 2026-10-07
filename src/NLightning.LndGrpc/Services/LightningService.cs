using System.Buffers.Binary;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Services;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Payments.Routing.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Reestablish;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Transport.Interfaces;
using Macaroons;

/// <summary>
/// LND's <c>lnrpc.Lightning</c> service over this node (<c>docs/agents/LND_GRPC_PLAN.md</c> §3, NL-1161): the wave 1
/// read surface (node, wallet, channels, peers, graph, invoices, payments, forwards), live subscriptions, <c>AddInvoice</c>,
/// <c>DecodePayReq</c> and LND's message signatures. Every method not overridden answers <c>UNIMPLEMENTED</c>.
/// Callers are authorized before they get here (<see cref="Macaroons.MacaroonAuthInterceptor"/>).
/// </summary>
/// <remarks>
/// The services read the node directly (memory repositories for the live channel state, a scoped
/// <see cref="IUnitOfWork"/> per call for stored rows), not through the IPC handlers. The field-by-field mapping and
/// the semantic differences from LND are in the plan; the important ones are noted on each method.
/// </remarks>
public sealed partial class LightningService : Lnrpc.Lightning.LightningBase
{
    /// <summary>The label on the invoices made through <c>AddInvoice</c> (accounting, <c>listinvoices</c>).</summary>
    public const string InvoiceLabel = "lnd-grpc";

    /// <summary>The LND API version this server mimics; clients parse the leading version of <c>GetInfo.version</c>.</summary>
    public const string LndApiVersion = "0.21.4-beta";

    private readonly IChannelMemoryRepository _channels;
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<LightningService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly LndGrpcOptions _options;
    private readonly IPeerManager _peerManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILightningSigner _signer;
    private readonly TimeProvider _timeProvider;
    private readonly IAnchorReserveService? _anchorReserve;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly GossipGraphOptions? _graphOptions;
    private readonly IGraphStore? _graphStore;
    private readonly IReestablishTracker? _reestablish;
    private readonly ITcpService? _tcpService;
    private readonly IUtxoMemoryRepository? _utxos;
    private readonly INodeCommandDispatcher? _dispatcher;
    private readonly LndRootKeyStore? _rootKeys;
    private readonly IPaymentEventSource? _paymentEvents;
    private readonly IChannelPolicyService? _channelPolicyService;
    private readonly IRouteQueryService? _routeQuery;
    private readonly SemaphoreSlim _globalPolicyGate = new(1, 1);

    public LightningService(ILightningSigner signer, IOptions<NodeOptions> nodeOptions,
                            IServiceScopeFactory scopeFactory, IChannelMemoryRepository channels,
                            IPeerManager peerManager, IInvoiceService invoiceService,
                            ILogger<LightningService> logger, IOptions<LndGrpcOptions>? options = null,
                            TimeProvider? timeProvider = null, IBlockchainMonitor? blockchainMonitor = null,
                            IUtxoMemoryRepository? utxos = null, IAnchorReserveService? anchorReserve = null,
                            IReestablishTracker? reestablish = null, IGraphStore? graphStore = null,
                            IOptions<GossipGraphOptions>? graphOptions = null, ITcpService? tcpService = null,
                            INodeCommandDispatcher? dispatcher = null, LndRootKeyStore? rootKeys = null,
                            IPaymentEventSource? paymentEvents = null, IChannelPolicyService? channelPolicyService = null,
                            IRouteQueryService? routeQuery = null)
    {
        _routeQuery = routeQuery;
        _channelPolicyService = channelPolicyService;
        _paymentEvents = paymentEvents;
        _dispatcher = dispatcher;
        _rootKeys = rootKeys;
        _signer = signer;
        _nodeOptions = nodeOptions.Value;
        _scopeFactory = scopeFactory;
        _channels = channels;
        _peerManager = peerManager;
        _invoiceService = invoiceService;
        _logger = logger;
        _options = options?.Value ?? new LndGrpcOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _blockchainMonitor = blockchainMonitor;
        _utxos = utxos;
        _anchorReserve = anchorReserve;
        _reestablish = reestablish;
        _graphStore = graphStore;
        _graphOptions = graphOptions?.Value;
        _tcpService = tcpService;
    }

    /// <summary>The graph snapshot, or null when the node keeps no gossip graph.</summary>
    private IGraphView? Graph =>
        _graphStore is not null && (_graphOptions?.IsEnabledFor(_nodeOptions.BitcoinNetwork) ?? true)
            ? _graphStore.GetSnapshot()
            : null;

    /// <summary>LND's uint64 <c>chan_id</c>: the BOLT 7 short channel id as a big-endian integer (0 when unset).</summary>
    internal static ulong ToChanId(ShortChannelId scid) =>
        scid.BlockHeight == 0 ? 0 : BinaryPrimitives.ReadUInt64BigEndian((byte[])scid);

    /// <summary>A scope for one call (its <see cref="IUnitOfWork"/>).</summary>
    private AsyncServiceScope CreateScope() => _scopeFactory.CreateAsyncScope();

    private static IUnitOfWork UnitOfWork(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException NotFound(string message) => new(new Status(StatusCode.NotFound, message));

    private static RpcException Unimplemented(string message) => new(new Status(StatusCode.Unimplemented, message));

    /// <summary>
    /// A block hash as LND (and bitcoind) print it: the hex of the reversed bytes. The node stores block hashes in
    /// internal (serialized) order, whose <see cref="Domain.Crypto.ValueObjects.Hash.ToString"/> is not the display
    /// form (NL-1244).
    /// </summary>
    internal static string DisplayHex(Domain.Crypto.ValueObjects.Hash hash)
    {
        var bytes = ((byte[])hash).ToArray();
        Array.Reverse(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Unix nanoseconds (LND's <c>*_ns</c> fields).</summary>
    internal static long UnixNanos(DateTimeOffset time) =>
        (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
}