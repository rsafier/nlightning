namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Protocol.Interfaces;

/// <summary>
/// The outcome of one step of an <see cref="InteractiveTxSession"/>: the next session state and what to send.
/// </summary>
/// <param name="Next">The session after the step (the session is immutable: keep this one).</param>
/// <param name="Outbound">The messages to send to the peer, in order (empty for none). On a failed negotiation it is
/// our <c>tx_abort</c> (BOLT 2: a failed negotiation is answered with <c>tx_abort</c>, never a channel failure).</param>
/// <param name="NegotiationComplete">True on the step that ends the negotiation with two consecutive <c>tx_complete</c>
/// (IT-S-02): <see cref="InteractiveTxSession.Inputs"/> and <see cref="InteractiveTxSession.Outputs"/> are final and the
/// driver builds the transaction.</param>
/// <param name="AbortReason">Why the negotiation ended with <c>tx_abort</c> (sent or received), or null.</param>
/// <param name="RequirementId">The splicing plan requirement a failed check enforces (for example <c>IT-R-01</c>), or
/// null.</param>
public sealed record InteractiveTxStepResult(
    InteractiveTxSession Next,
    IReadOnlyList<IChannelMessage> Outbound,
    bool NegotiationComplete,
    string? AbortReason = null,
    string? RequirementId = null)
{
    /// <summary>Whether the negotiation is over because of <c>tx_abort</c>.</summary>
    public bool Aborted => AbortReason is not null;
}