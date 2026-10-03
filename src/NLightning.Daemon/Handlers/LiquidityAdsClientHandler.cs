namespace NLightning.Daemon.Handlers;

using Application.Gossip.Graph.Interfaces;
using Application.LiquidityAds;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Interfaces;
using Domain.Node.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// <c>liquidityads rates|sellers|purchases</c> (ClientCommand 46, liquidity ads NL-771).
/// </summary>
/// <remarks>
/// <para><c>rates</c>: our rates (<c>Node:LiquidityAds:FundingRates</c>; none means we do not sell), the lease and the
/// sale negotiations in progress.</para>
/// <para><c>sellers</c>: every node that advertises rates, one row per node: a connected peer's <c>init</c>
/// (<see cref="IPeerService.LiquidityRates"/>, the freshest) wins over its <c>node_announcement</c> in the graph
/// (<see cref="NodeAnnouncementRates.TryReadFromAnnouncement"/>); connected sellers first, then by node id. Our own node
/// is left out.</para>
/// <para><c>purchases</c>: the liquidity we bought and sold, newest first, paged like the other lists, with the chain
/// height the lease status is computed at.</para>
/// <para>Every collaborator is optional: a node without liquidity ads answers <see cref="ErrorCodes.InvalidOperation"/>
/// "not available"; without a graph only the peers' <c>init</c> are read; without a unit of work that stores purchases
/// the purchase list is empty.</para>
/// </remarks>
public sealed class LiquidityAdsClientHandler
    : IClientCommandHandler<LiquidityAdsClientRequest, LiquidityAdsClientResponse>
{
    private readonly LiquidityAdsService? _liquidityAdsService;
    private readonly IPeerManager? _peerManager;
    private readonly IGraphStore? _graphStore;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly ISecureKeyManager? _secureKeyManager;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.LiquidityAds;

    public LiquidityAdsClientHandler(LiquidityAdsService? liquidityAdsService = null, IPeerManager? peerManager = null,
                                     IGraphStore? graphStore = null, IUnitOfWork? unitOfWork = null,
                                     IBlockchainMonitor? blockchainMonitor = null,
                                     ISecureKeyManager? secureKeyManager = null)
    {
        _liquidityAdsService = liquidityAdsService;
        _peerManager = peerManager;
        _graphStore = graphStore;
        _unitOfWork = unitOfWork;
        _blockchainMonitor = blockchainMonitor;
        _secureKeyManager = secureKeyManager;
    }

    /// <inheritdoc/>
    public async Task<LiquidityAdsClientResponse> HandleAsync(LiquidityAdsClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_liquidityAdsService is null)
            throw new ClientException(ErrorCodes.InvalidOperation, "Liquidity ads are not available on this node");

        var options = _liquidityAdsService.Options;
        return request.Action switch
        {
            LiquidityAdsAction.Rates => new LiquidityAdsClientResponse(request.Action)
            {
                OurRates = _liquidityAdsService.OurRates,
                LeaseBlocks = options.LeaseBlocks,
                SalesInProgress = _liquidityAdsService.SalesInProgress
            },
            LiquidityAdsAction.Sellers => new LiquidityAdsClientResponse(request.Action)
            {
                Sellers = ListSellers(),
                LeaseBlocks = options.LeaseBlocks
            },
            LiquidityAdsAction.Purchases => await ListPurchasesAsync(request, options.LeaseBlocks),
            _ => throw new ClientException(ErrorCodes.InvalidOperation,
                                           $"Unknown liquidityads action {(int)request.Action}")
        };
    }

    /// <summary>The sellers: connected peers' <c>init</c> first, then the graph's node announcements.</summary>
    internal IReadOnlyList<LiquiditySellerInfo> ListSellers()
    {
        var ourNodeId = TryGetOurNodeId();
        var snapshot = _graphStore?.GetSnapshot();
        var sellers = new Dictionary<CompactPubKey, LiquiditySellerInfo>();

        foreach (var peer in _peerManager?.ListPeers() ?? [])
        {
            if (peer.NodeId == ourNodeId || !peer.TryGetPeerService(out var service)
                                         || service.LiquidityRates is not { } rates)
                continue;

            var alias = snapshot is not null && snapshot.TryGetNode(peer.NodeId, out var node) ? node.AliasText : null;
            sellers[peer.NodeId] = new LiquiditySellerInfo(peer.NodeId, LiquiditySellerSource.Init, rates, true,
                                                           NullIfEmpty(alias));
        }

        if (snapshot is not null)
        {
            foreach (var node in snapshot.Nodes)
            {
                if (node.NodeId == ourNodeId || sellers.ContainsKey(node.NodeId)
                 || !NodeAnnouncementRates.TryReadFromAnnouncement(node.RawAnnouncement.Span, out var rates))
                    continue;

                sellers[node.NodeId] = new LiquiditySellerInfo(node.NodeId, LiquiditySellerSource.NodeAnnouncement,
                                                               rates, _peerManager?.GetPeer(node.NodeId) is not null,
                                                               NullIfEmpty(node.AliasText));
            }
        }

        return sellers.Values
                      .OrderBy(s => s.IsConnected ? 0 : 1)
                      .ThenBy(s => Convert.ToHexString(s.NodeId), StringComparer.Ordinal)
                      .ToList();
    }

    private async Task<LiquidityAdsClientResponse> ListPurchasesAsync(LiquidityAdsClientRequest request,
                                                                      uint leaseBlocks)
    {
        ClientRequestGuards.ThrowIfInvalidPage(request.Skip, request.Take);
        if (request.Role is { } role && !Enum.IsDefined(role))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown purchase role {(int)role}");
        if (request.Status is { } status && !Enum.IsDefined(status))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown purchase status {(int)status}");

        var repository = TryGetRepository();
        var purchases = repository is null
                            ? []
                            : await repository.ListAsync(request.Role, request.Status, request.Skip, request.Take);
        return new LiquidityAdsClientResponse(request.Action)
        {
            Purchases = purchases,
            LeaseBlocks = leaseBlocks,
            CurrentHeight = _blockchainMonitor?.LastProcessedBlockHeight ?? 0
        };
    }

    private ILiquidityPurchaseDbRepository? TryGetRepository()
    {
        try
        {
            return _unitOfWork?.LiquidityPurchaseDbRepository;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private CompactPubKey? TryGetOurNodeId()
    {
        try
        {
            return _secureKeyManager?.GetNodePubKey();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}