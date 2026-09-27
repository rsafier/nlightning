using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.InteractiveTx.Handlers;

using Channels.Handlers.Interfaces;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;

/// <summary>
/// The shared body of the nine interactive-tx message handlers (types 66-74, splicing plan IT4-T2): the message goes to
/// the <see cref="IInteractiveTxDriver"/> under the channel's lock (<c>ChannelManager</c> holds it), with this
/// message's unit of work. A node without a driver answers as if no negotiation were in progress: <c>tx_abort</c> for
/// 66-73, an echo for <c>tx_abort</c>. A negotiation failure is a <c>tx_abort</c>, never a channel failure.
/// </summary>
/// <typeparam name="TMessage">The interactive-tx message type.</typeparam>
public abstract class InteractiveTxMessageHandler<TMessage> : IChannelMessageHandler<TMessage>
    where TMessage : IChannelMessage
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUnitOfWork _unitOfWork;

    protected InteractiveTxMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    {
        _serviceProvider = serviceProvider;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(TMessage message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Resolved per message so a node built without the interactive-tx services (no driver registered) still
        // answers: the handlers are registered by reflection, the driver by AddInteractiveTxServices
        var driver = _serviceProvider.GetService<IInteractiveTxDriver>();
        if (driver is not null)
            return driver.ReceiveAsync(message, peerPubKey, _unitOfWork);

        IReadOnlyList<IChannelMessage> reply = message is TxAbortMessage
                                                   ? [InteractiveTxDriver.CreateTxAbort(message.Payload.ChannelId,
                                                                                      "tx_abort acknowledged")]
                                                   : [InteractiveTxDriver.CreateTxAbort(message.Payload.ChannelId,
                                                                                      "interactive-tx not supported")];
        return Task.FromResult(reply);
    }
}