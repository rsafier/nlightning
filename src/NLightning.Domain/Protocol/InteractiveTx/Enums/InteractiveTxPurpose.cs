namespace NLightning.Domain.Protocol.InteractiveTx.Enums;

/// <summary>
/// The protocol an interactive-tx negotiation serves (splicing plan §3.8, <c>InteractiveTxSessions.Purpose</c>).
/// </summary>
/// <remarks>Persisted: never renumber.</remarks>
public enum InteractiveTxPurpose : byte
{
    /// <summary>A splice (BOLT 2 "Channel Splicing", <c>splice_init</c>/<c>splice_ack</c>).</summary>
    Splice = 1,

    /// <summary>A dual-funded open (BOLT 2 <c>open_channel2</c>/<c>accept_channel2</c>, wave DF).</summary>
    DualFund = 2,

    /// <summary>An RBF of a pending splice (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c>, wave SPR).</summary>
    SpliceRbf = 3,

    /// <summary>An RBF of an unconfirmed dual-funded open (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c>, wave DF).</summary>
    DualFundRbf = 4
}