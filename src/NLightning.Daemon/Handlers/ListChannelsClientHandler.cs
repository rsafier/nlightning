using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Lists the node's channels: every channel loaded in memory (the live state), then every persisted channel that is
/// not loaded (closed or stale ones).
/// </summary>
/// <remarks>
/// Pending HTLC counts come from the commitment snapshot (<see cref="ChannelModel.Commitments"/>): every HTLC whose
/// state is not final, per direction (NL-241). The legacy <c>LocalOfferedHtlcs</c>/<c>RemoteOfferedHtlcs</c>
/// collections are only used for a channel without a snapshot. The fee policy is the node's
/// <see cref="RoutingOptions"/> (one policy for every channel).
/// </remarks>
public class ListChannelsClientHandler : IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IPeerManager _peerManager;
    private readonly IReestablishTracker _reestablishTracker;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoutingOptions _routingOptions;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListChannels;

    public ListChannelsClientHandler(IChannelMemoryRepository channelMemoryRepository, IPeerManager peerManager,
                                     IReestablishTracker reestablishTracker, IUnitOfWork unitOfWork,
                                     IOptions<NodeOptions> nodeOptions)
    {
        _routingOptions = nodeOptions.Value.Routing;
        _channelMemoryRepository = channelMemoryRepository;
        _peerManager = peerManager;
        _reestablishTracker = reestablishTracker;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc/>
    public async Task<ListChannelsClientResponse> HandleAsync(ListChannelsClientRequest request,
                                                              CancellationToken ct)
    {
        var channels = new List<ChannelModel>();
        var listed = new HashSet<ChannelId>();

        foreach (var channel in _channelMemoryRepository.FindChannels(c => IsRequested(c, request)))
        {
            if (listed.Add(channel.ChannelId))
                channels.Add(channel);
        }

        foreach (var channel in await _unitOfWork.ChannelDbRepository.GetAllAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (IsRequested(channel, request) && listed.Add(channel.ChannelId))
                channels.Add(channel);
        }

        return new ListChannelsClientResponse(channels.Select(ToChannelInfo).ToList());
    }

    private static bool IsRequested(ChannelModel channel, ListChannelsClientRequest request) =>
        request.PeerId is null || channel.RemoteNodeId == request.PeerId.Value;

    private ChannelInfoClientResponse ToChannelInfo(ChannelModel channel)
    {
        return new ChannelInfoClientResponse
        {
            ChannelId = channel.ChannelId,
            PeerId = channel.RemoteNodeId,
            State = channel.State,
            IsInitiator = channel.IsInitiator,
            IsPeerConnected = _peerManager.GetPeer(channel.RemoteNodeId) is not null,
            // A default ShortChannelId has no bytes; block 0 never holds a funding transaction
            ShortChannelId = channel.ShortChannelId.BlockHeight == 0
                                 ? (ShortChannelId?)null
                                 : channel.ShortChannelId,
            FundingTxId = channel.FundingOutput?.TransactionId,
            FundingOutputIndex = channel.FundingOutput?.Index,
            Capacity = channel.FundingOutput?.Amount ?? LightningMoney.Zero,
            LocalBalance = channel.LocalBalance,
            RemoteBalance = channel.RemoteBalance,
            LocalCommitmentNumber = channel.LocalCommitmentNumber,
            RemoteCommitmentNumber = channel.RemoteCommitmentNumber,
            OfferedHtlcCount = CountPendingHtlcs(channel, HtlcDirection.Outgoing),
            ReceivedHtlcCount = CountPendingHtlcs(channel, HtlcDirection.Incoming),
            DataLossDetected = channel.DataLossDetected,
            // True once channel_reestablish was exchanged on the peer's current connection (BOLT2 plan N7)
            IsReestablished = _reestablishTracker.IsReestablished(channel.ChannelId),
            FeeBaseMsat = _routingOptions.FeeBaseMsat,
            FeePpm = _routingOptions.FeeProportionalMillionths
        };
    }

    /// <summary>
    /// HTLCs of <paramref name="direction"/> that are not fully resolved: offered (or received) and not yet removed
    /// from both commitments with the removal irrevocably committed.
    /// </summary>
    private static int CountPendingHtlcs(ChannelModel channel, HtlcDirection direction)
    {
        if (channel.Commitments is { } commitments)
            return commitments.Htlcs.Values.Count(h => h.Direction == direction && !HtlcStateTable.IsFinal(h.State));

        // No snapshot yet (legacy or still opening): only the in-memory legacy collections can hold HTLCs
        var legacy = direction == HtlcDirection.Outgoing ? channel.LocalOfferedHtlcs : channel.RemoteOfferedHtlcs;
        return legacy?.Count ?? 0;
    }
}