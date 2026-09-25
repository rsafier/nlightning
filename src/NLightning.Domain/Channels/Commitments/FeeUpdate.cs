namespace NLightning.Domain.Channels.Commitments;

using Enums;

/// <summary>
/// A feerate change (<c>update_fee</c>), or the channel's opening feerate (sequence 0, already final).
/// </summary>
/// <remarks>
/// Fee updates move through the add half of <see cref="HtlcStateTable"/>, owned by the funder: 10-14 when we are the
/// funder, 30-34 when the peer is. A commitment's feerate is the last fee update (highest <see cref="Sequence"/>) that
/// is in that commitment (B2-FEE-X01: a fee update is replaced, never removed). Once one is final the earlier ones are
/// dropped.
/// </remarks>
/// <param name="Sequence">Strictly increasing per channel; 0 is the opening feerate.</param>
/// <param name="FeeratePerKw">The feerate in satoshi per 1000 weight units.</param>
/// <param name="State">The state (add half of the HTLC machine).</param>
public sealed record FeeUpdate(ulong Sequence, uint FeeratePerKw, HtlcState State)
{
    public bool IsInCommit(CommitmentSide side) => HtlcStateTable.IsInCommit(State, side);

    public bool IsFinal => HtlcStateTable.IsFeeFinal(State);

    public HtlcDirection Owner => HtlcStateTable.Owner(State);
}