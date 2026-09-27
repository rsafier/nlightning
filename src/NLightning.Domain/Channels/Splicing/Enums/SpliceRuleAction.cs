namespace NLightning.Domain.Channels.Splicing.Enums;

/// <summary>
/// What BOLT 2 prescribes when a splice rule does not hold.
/// </summary>
public enum SpliceRuleAction : byte
{
    /// <summary>Our own attempt is refused locally; nothing is sent.</summary>
    Refuse = 0,

    /// <summary>Answer with <c>tx_abort</c> (an unacceptable feerate, a rejected splice, SP-TX-02/05).</summary>
    TxAbort = 1,

    /// <summary>Send a <c>warning</c> and close the connection (the spec's first option; we never fail the channel for
    /// these, as for quiescence).</summary>
    WarningAndClose = 2,

    /// <summary>Send an <c>error</c> and fail the channel (SP-SIG-01, SP-OP-05).</summary>
    ErrorAndFail = 3
}