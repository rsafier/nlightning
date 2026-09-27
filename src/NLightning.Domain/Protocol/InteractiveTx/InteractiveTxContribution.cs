namespace NLightning.Domain.Protocol.InteractiveTx;

using Models;

/// <summary>
/// What we add to an interactive-tx negotiation (BOLT 2 "Interactive Transaction Construction"), chosen by
/// <see cref="Interfaces.IInteractiveTxContributor"/> before the session starts. The session sends one
/// <c>tx_add_input</c>/<c>tx_add_output</c> per item on our turns, then <c>tx_complete</c>.
/// </summary>
/// <remarks>
/// The shared funding input and output of a splice are not part of it: the session adds them from
/// <see cref="SharedFundingSpec"/> when we are the initiator. Our inputs pay for themselves and our outputs at the
/// agreed feerate, and as initiator also the common fields and the shared input/output (IT-S-03); the contributor sizes
/// the change accordingly.
/// </remarks>
/// <param name="Inputs">Our wallet inputs, in the order they are sent.</param>
/// <param name="Outputs">Our outputs (change, splice-out destination), in the order they are sent.</param>
/// <param name="ReservationId">The wallet reservation holding <paramref name="Inputs"/>
/// (<c>FeeInputReservation.Id</c>): released on <c>tx_abort</c> before our <c>tx_signatures</c>, kept after them until
/// an input of the transaction is spent (IT-ABT-01). Null when we add no wallet input.</param>
public sealed record InteractiveTxContribution(
    IReadOnlyList<ContributedInput> Inputs,
    IReadOnlyList<ContributedOutput> Outputs,
    Guid? ReservationId)
{
    /// <summary>We add nothing (as splice acceptor in SP1, splicing plan D10).</summary>
    public static InteractiveTxContribution Empty { get; } = new([], [], null);
}