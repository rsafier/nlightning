namespace NLightning.Daemon.Handlers;

using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Lists the node's channels: every channel loaded in memory (the live state), then every persisted channel that is
/// not loaded (closed or stale ones).
/// </summary>
public class ListChannelsClientHandler : IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IPeerManager _peerManager;
    private readonly IUnitOfWork _unitOfWork;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListChannels;

    public ListChannelsClientHandler(IChannelMemoryRepository channelMemoryRepository, IPeerManager peerManager,
                                     IUnitOfWork unitOfWork)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _peerManager = peerManager;
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
        // The channel model keeps one commitment number for both sides until BOLT2 plan N1-T1 (NL-188) splits it
        var commitmentNumber = channel.CommitmentNumber?.Value ?? 0;

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
            LocalCommitmentNumber = commitmentNumber,
            RemoteCommitmentNumber = commitmentNumber,
            OfferedHtlcCount = channel.LocalOfferedHtlcs?.Count ?? 0,
            ReceivedHtlcCount = channel.RemoteOfferedHtlcs?.Count ?? 0,
            DataLossDetected = false
        };
    }
}