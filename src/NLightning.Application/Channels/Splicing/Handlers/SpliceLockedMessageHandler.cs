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
/// Handles the peer's <c>splice_locked</c> (BOLT 2 "Splice Completion", type 77; minimal SP1 version, the SCID switch
/// and the announcements are wave SP2). Under the channel's lock it hands the message to
/// <see cref="SpliceService.HandleSpliceLockedAsync"/>: SP-LK-02 (a <c>splice_txid</c> that matches none of our
/// pending splices is a <c>warning</c> and close), SP-LK-03 (sent and received for the same txid: the funding is
/// locked). Without splicing no splice is pending, so every <c>splice_locked</c> breaks SP-LK-02.
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