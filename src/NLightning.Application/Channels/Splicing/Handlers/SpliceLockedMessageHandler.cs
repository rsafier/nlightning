using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Splicing.Handlers;

using Channels.Handlers.Interfaces;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Handles the peer's <c>splice_locked</c> (BOLT 2 "Splice Completion", type 77). Under the channel's lock it hands the
/// message to <see cref="SpliceService.HandleSpliceLockedAsync"/>: SP-LK-02 (a <c>splice_txid</c> that matches none of
/// our pending splices is a <c>warning</c> and close; a duplicate or the current funding's txid is ignored), SP-LK-03
/// (sent and received for the same txid: the funding is locked, its RBF siblings discarded, the short channel id
/// switched with the old one retired for 72 blocks, and a public channel's announcement restarted on the splice; the
/// replies are our <c>announcement_signatures</c> when already due; another RBF candidate than ours: remembered,
/// never failed, D11). Without splicing no splice is pending, so every <c>splice_locked</c> breaks SP-LK-02.
/// </summary>
public sealed class SpliceLockedMessageHandler : IChannelMessageHandler<SpliceLockedMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUnitOfWork _unitOfWork;

    public SpliceLockedMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    {
        _serviceProvider = serviceProvider;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(SpliceLockedMessage message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_serviceProvider.GetService<SpliceService>() is { } spliceService)
            return spliceService.HandleSpliceLockedAsync(message, peerPubKey, _unitOfWork);

        throw new ChannelWarningException("[SP-LK-02] splice_locked without a pending splice",
                                          message.Payload.ChannelId, "splice_locked for an unknown splice transaction")
        {
            CloseConnection = true
        };
    }
}