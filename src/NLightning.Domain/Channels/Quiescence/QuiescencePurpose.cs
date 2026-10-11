namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// Why we ask a channel to become quiescent (BOLT 2 "Channel Quiescence"): the dependent protocol that will run once
/// the channel is quiescent and that must end it (Q-R-06).
/// </summary>
/// <remarks>
/// Quiescence is never persisted (splicing plan D1), so the numeric values are only stable for logs.
/// </remarks>
public enum QuiescencePurpose : byte
{
    /// <summary>
    /// A splice we initiate (BOLT 2 "Channel Splicing", <c>splice_init</c>). Ended by the exchange of
    /// <c>tx_signatures</c> or by <c>tx_abort</c> (SP-Q-01).
    /// </summary>
    Splice = 1,

    /// <summary>
    /// An RBF of a pending splice (<c>tx_init_rbf</c> on a splice, wave SPR). Ended like <see cref="Splice"/>.
    /// </summary>
    SpliceRbf = 2,

    /// <summary>
    /// No dependent protocol: a test or operator probe (Proof Q (b)). We end it ourselves with <c>tx_abort</c> once the
    /// channel is quiescent (splicing plan D2), since BOLT 2 defines no other exit than disconnection.
    /// </summary>
    Probe = 100
}