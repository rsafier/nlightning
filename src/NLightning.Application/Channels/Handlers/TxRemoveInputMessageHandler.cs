namespace NLightning.Application.Channels.Handlers;

using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using InteractiveTx.Handlers;

/// <summary>
/// Receives <c>tx_remove_input</c> (BOLT 2 "Interactive Transaction Construction", type 68; splicing plan IT4-T2) and hands
/// it to the interactive-tx driver (<see cref="InteractiveTx.Handlers.InteractiveTxMessageHandler{TMessage}"/>).
/// </summary>
public sealed class TxRemoveInputMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    : InteractiveTxMessageHandler<TxRemoveInputMessage>(serviceProvider, unitOfWork);