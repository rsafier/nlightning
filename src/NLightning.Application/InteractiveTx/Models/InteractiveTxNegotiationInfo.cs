namespace NLightning.Application.InteractiveTx.Models;

using Domain.Channels.ValueObjects;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// A read-only view of the driver's state for a channel (diagnostics and tests).
/// </summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="SessionId">The current attempt's id, or null when no attempt is in progress.</param>
/// <param name="State">The current attempt's state, or null when no attempt is in progress.</param>
/// <param name="IsPersisted">Whether the current attempt is stored (from "commitment_signed sent" on).</param>
/// <param name="AwaitingAbortEcho">We sent a <c>tx_abort</c> the peer has not echoed yet.</param>
/// <param name="RbfRequested">We sent <c>tx_init_rbf</c> and wait for <c>tx_ack_rbf</c>.</param>
/// <param name="CompletedAttempts">The fully signed attempts, oldest first (the transactions an RBF must
/// double-spend).</param>
/// <param name="Inputs">The current attempt's inputs, empty when none.</param>
/// <param name="Outputs">The current attempt's outputs, empty when none.</param>
public sealed record InteractiveTxNegotiationInfo(
    ChannelId ChannelId,
    Guid? SessionId,
    InteractiveTxSessionState? State,
    bool IsPersisted,
    bool AwaitingAbortEcho,
    bool RbfRequested,
    IReadOnlyList<ConstructedInteractiveTx> CompletedAttempts,
    IReadOnlyList<InteractiveTxInput> Inputs,
    IReadOnlyList<InteractiveTxOutput> Outputs);