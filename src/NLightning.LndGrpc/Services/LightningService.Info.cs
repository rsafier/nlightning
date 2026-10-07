using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Protocol.Constants;
using Lnrpc;
using Mapping;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>GetInfo</c>: our identity, alias, color, channel and peer counts, chain position and features. <c>version</c>
    /// names the LND API mimicked and ours (<c>0.21.4-beta nlightning-&lt;version&gt;</c>); <c>synced_to_chain</c> is
    /// the chain monitor running with a processed block; <c>best_header_timestamp</c> is when we processed the tip
    /// (we keep no header times).
    /// </summary>
    public override async Task<GetInfoResponse> GetInfo(GetInfoRequest request, ServerCallContext context)
    {
        var nodeId = _signer.GetNodePublicKey();
        var channels = _channels.FindChannels(_ => true);
        var height = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var response = new GetInfoResponse
        {
            Version = $"{LndApiVersion} nlightning-"
                    + (typeof(LightningService).Assembly.GetName().Version?.ToString(3) ?? "0.0.1"),
            IdentityPubkey = nodeId.ToString(),
            Alias = _nodeOptions.Alias,
            Color = "#" + _nodeOptions.Color.TrimStart('#').ToLowerInvariant(),
            NumActiveChannels = (uint)channels.Count(IsActive),
            NumInactiveChannels = (uint)channels.Count(c => c.State == ChannelState.Open && !IsActive(c)),
            NumPendingChannels = (uint)channels.Count(IsPendingOpen),
            NumPeers = (uint)_peerManager.ListPeers().Count,
            BlockHeight = height,
            SyncedToChain = _blockchainMonitor is { IsChainProcessingHalted: false } && height > 0,
            SyncedToGraph = _graphStore?.IsLoaded ?? false,
            Testnet = _nodeOptions.BitcoinNetwork.Name is NetworkConstants.Testnet or NetworkConstants.Testnet4
        };
        response.WalletSynced = response.SyncedToChain;
        response.Chains.Add(new Chain { Chain_ = "bitcoin", Network = LndNetworkName() });
        response.Features.Add(LndFeatures.ToMap(_nodeOptions.Features.GetNodeFeatures())
                                         .ToDictionary(p => p.Key, p => p.Value));
        if (_tcpService is not null)
        {
            foreach (var endpoint in _tcpService.ListeningTo.OfType<IPEndPoint>())
            {
                if (!endpoint.Address.Equals(IPAddress.Any) && !endpoint.Address.Equals(IPAddress.IPv6Any))
                    response.Uris.Add($"{nodeId}@{endpoint}");
            }
        }

        try
        {
            await using var scope = CreateScope();
            if (await UnitOfWork(scope).BlockchainStateDbRepository.GetStateAsync() is { } state)
            {
                response.BlockHash = state.LastProcessedBlockHash.ToString();
                response.BestHeaderTimestamp = new DateTimeOffset(DateTime.SpecifyKind(state.LastProcessedAt,
                                                                      DateTimeKind.Utc)).ToUnixTimeSeconds();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "GetInfo could not read the chain state");
        }

        // lndclient parses block_hash even while waiting for sync. Never return an empty hash.
        if (string.IsNullOrEmpty(response.BlockHash))
        {
            response.BlockHash = new string('0', 64);
            response.SyncedToChain = false;
            response.WalletSynced = false;
        }

        return response;
    }

    /// <summary>
    /// <c>WalletBalance</c> by LND's rule (NL-1236): <c>confirmed_balance</c> is the wallet outputs with at least
    /// <c>min_confs</c> confirmations (1 when unset), <c>total_balance</c> every output, mined or not, and
    /// <c>unconfirmed_balance</c> the difference; the node's own <c>walletbalance</c> and its spend rules keep their
    /// 4-confirmation rule. The anchors reserve is <c>reserved_balance_anchor_chan</c> and the outputs locked to channel
    /// fundings or leased as <c>locked_balance</c>; one account, <c>default</c>, with the same split.
    /// </summary>
    public override async Task<WalletBalanceResponse> WalletBalance(WalletBalanceRequest request,
                                                                    ServerCallContext context)
    {
        if (request.Account is { Length: > 0 } account && account != "default")
            throw NotFound($"account {account} not found");

        // LND: total = outputs with >= 0 confirmations, confirmed = >= min_confs (default 1), unconfirmed = the rest
        var minConfirmations = request.MinConfs > 0 ? (uint)request.MinConfs : 1u;
        var height = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var total = _utxos?.GetBalanceWithConfirmations(height, 0).Satoshi ?? 0;
        var confirmed = _utxos?.GetBalanceWithConfirmations(height, minConfirmations).Satoshi ?? 0;
        var unconfirmed = total - confirmed;
        var reserve = _anchorReserve is null ? null : await _anchorReserve.GetStatusAsync(context.CancellationToken);
        var response = new WalletBalanceResponse
        {
            ConfirmedBalance = confirmed,
            UnconfirmedBalance = unconfirmed,
            TotalBalance = total,
            LockedBalance = _utxos?.GetLockedBalance().Satoshi ?? 0,
            ReservedBalanceAnchorChan = reserve?.RequiredReserve.Satoshi ?? 0
        };
        response.AccountBalance.Add("default", new WalletAccountBalance
        {
            ConfirmedBalance = confirmed,
            UnconfirmedBalance = unconfirmed
        });
        return response;
    }

    /// <summary>
    /// <c>ListPeers</c>: the connected peers with their address and features; <c>inbound</c> is true only for a peer
    /// that can only reach us (we keep no direction per connection); traffic counters stay 0.
    /// </summary>
    public override Task<ListPeersResponse> ListPeers(ListPeersRequest request, ServerCallContext context)
    {
        var response = new ListPeersResponse();
        foreach (var peer in _peerManager.ListPeers())
        {
            var item = new Peer
            {
                PubKey = peer.NodeId.ToString(),
                Address = peer.Host.Contains(':') && !peer.Host.StartsWith('[')
                              ? $"[{peer.Host}]:{peer.Port}"
                              : $"{peer.Host}:{peer.Port}",
                Inbound = peer.IsInboundOnly,
                SyncType = Peer.Types.SyncType.UnknownSync
            };
            item.Features.Add(LndFeatures.ToMap(TryGetFeatures(peer)).ToDictionary(p => p.Key, p => p.Value));
            response.Peers.Add(item);
        }

        return Task.FromResult(response);
    }

    /// <summary>The peer's features, or null before its <c>init</c> (no peer service yet).</summary>
    private static Domain.Node.FeatureSet? TryGetFeatures(Domain.Node.Models.PeerModel peer) =>
        peer.TryGetPeerService(out _) ? peer.Features : null;

    /// <summary>LND's network name (<c>chains[].network</c>): mutinynet is a signet.</summary>
    private string LndNetworkName() => _nodeOptions.BitcoinNetwork.Name switch
    {
        NetworkConstants.Mainnet => "mainnet",
        NetworkConstants.Testnet => "testnet",
        NetworkConstants.Testnet4 => "testnet4",
        NetworkConstants.Regtest => "regtest",
        _ => "signet"
    };

    /// <summary>Open, its peer connected and <c>channel_reestablish</c> done on that connection (LND's active).</summary>
    private bool IsActive(ChannelModel channel) =>
        channel.State == ChannelState.Open
     && _peerManager.GetPeer(channel.RemoteNodeId) is not null
     && (_reestablish?.IsReestablished(channel.ChannelId) ?? true);

    /// <summary>A channel whose funding is not locked yet (LND's pending open channels).</summary>
    private static bool IsPendingOpen(ChannelModel channel) =>
        channel.State is > ChannelState.None and < ChannelState.Open && channel.FundingOutput?.TransactionId is not null;
}