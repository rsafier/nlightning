namespace NLightning.Domain.Payments.Trampoline;

/// <summary>
/// The counts and fees of the trampoline relays matching a <see cref="TrampolineRelayListQuery"/> (its page ignored),
/// for the <c>listforwards</c> totals (NL-981): the relays by status, the failed attempts a payer's retry replaced
/// (NL-899) counted as failed, and the fee earned by the fulfilled ones.
/// </summary>
/// <param name="Collecting">Relays still collecting their incoming parts.</param>
/// <param name="Sending">Relays whose outgoing payment runs.</param>
/// <param name="Fulfilled">Relays fulfilled.</param>
/// <param name="Failed">Relays failed, replaced attempts included.</param>
/// <param name="FulfilledFeesMsat">The fee earned by the fulfilled relays, in msat.</param>
public readonly record struct TrampolineRelayTotals(int Collecting, int Sending, int Fulfilled, int Failed,
                                                    long FulfilledFeesMsat)
{
    /// <summary>Every relay counted.</summary>
    public int Total => Collecting + Sending + Fulfilled + Failed;
}