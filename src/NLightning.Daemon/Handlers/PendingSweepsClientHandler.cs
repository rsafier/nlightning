namespace NLightning.Daemon.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Lists the on-chain resolution of closed channels (ClientCommand 15, BOLT 5 plan O3-T6): every recorded funding spend
/// (<c>ChannelCloses</c>) with its outputs (<c>OutputResolutions</c>) and the channel's state. Channels already
/// <c>Closed</c> are left out unless asked for.
/// </summary>
public sealed class PendingSweepsClientHandler
    : IClientCommandHandler<PendingSweepsClientRequest, PendingSweepsClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IUnitOfWork _unitOfWork;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.PendingSweeps;

    public PendingSweepsClientHandler(IChannelMemoryRepository channelMemoryRepository, IUnitOfWork unitOfWork)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc/>
    public async Task<PendingSweepsClientResponse> HandleAsync(PendingSweepsClientRequest request,
                                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var closes = await _unitOfWork.OnchainResolutionDbRepository.GetClosesAsync();
        var channels = new List<PendingSweepChannelInfo>();
        foreach (var close in closes.OrderBy(c => c.SpentAtHeight))
        {
            ct.ThrowIfCancellationRequested();
            if (request.ChannelId is { } only && close.ChannelId != only)
                continue;

            var state = await GetStateAsync(close.ChannelId);
            if (state == ChannelState.Closed && !request.IncludeClosed)
                continue;

            var outputs = await _unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(close.ChannelId);
            channels.Add(new PendingSweepChannelInfo(close.ChannelId, state, close.Kind, close.CommitmentTransactionId,
                                                     close.CommitmentNumber, close.SpentAtHeight,
                                                     outputs.Select(ToInfo).ToList()));
        }

        return new PendingSweepsClientResponse(channels);
    }

    private async Task<ChannelState> GetStateAsync(Domain.Channels.ValueObjects.ChannelId channelId)
    {
        if (_channelMemoryRepository.TryGetChannelState(channelId, out var state))
            return state;

        // Closed channels are not in memory
        var stored = await _unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        return stored?.State ?? ChannelState.Closed;
    }

    private static PendingSweepOutputInfo ToInfo(OutputResolutionModel output) =>
        new(output.TransactionId, output.OutputIndex, output.Descriptor, output.State,
            OutputDescriptorData.TryDecode(output)?.AmountSat, output.HtlcDirection, output.HtlcId,
            output.ResolvingTransactionId, output.WaitUntilHeight, output.DeadlineHeight, output.ResolvedHeight);
}