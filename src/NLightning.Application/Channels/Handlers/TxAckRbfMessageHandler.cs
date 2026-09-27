namespace NLightning.Application.Channels.Handlers;

using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using InteractiveTx.Handlers;

/// <summary>
/// Receives <c>tx_ack_rbf</c> (BOLT 2 "Interactive Transaction Construction", type 73; splicing plan IT4-T2) and hands
/// it to the interactive-tx driver (<see cref="InteractiveTx.Handlers.InteractiveTxMessageHandler{TMessage}"/>).
/// </summary>
public sealed class TxAckRbfMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    : InteractiveTxMessageHandler<TxAckRbfMessage>(serviceProvider, unitOfWork);