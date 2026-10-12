namespace NLightning.Application.InteractiveTx.Models;

using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Interfaces;

/// <summary>
/// The outcome of one step of an <see cref="IInteractiveTxNegotiation"/>: the application-side twin of
/// <see cref="InteractiveTxStepResult"/> (same members, the next state behind the engine seam).
/// </summary>
/// <param name="Next">The negotiation after the step.</param>
/// <param name="Outbound">The messages to send to the peer, in order.</param>
/// <param name="NegotiationComplete">True on the step that ends the negotiation with two consecutive
/// <c>tx_complete</c>.</param>
/// <param name="AbortReason">Why the negotiation ended with <c>tx_abort</c> (sent or received), or null.</param>
/// <param name="RequirementId">The requirement a failed check enforces, or null.</param>
public sealed record InteractiveTxNegotiationStep(
    IInteractiveTxNegotiation Next,
    IReadOnlyList<IChannelMessage> Outbound,
    bool NegotiationComplete,
    string? AbortReason = null,
    string? RequirementId = null)
{
    /// <summary>Whether the negotiation is over because of <c>tx_abort</c>.</summary>
    public bool Aborted => AbortReason is not null;
}