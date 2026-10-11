namespace NLightning.Domain.Protocol.InteractiveTx.Enums;

/// <summary>
/// Where an interactive-tx negotiation is (BOLT 2 "Interactive Transaction Construction").
/// </summary>
/// <remarks>Persisted (<c>InteractiveTxSessions.State</c>): never renumber.</remarks>
public enum InteractiveTxSessionState : byte
{
    /// <summary>
    /// Adding and removing inputs and outputs, turn by turn, until two consecutive <c>tx_complete</c> (IT-S-02). Never
    /// persisted: an unsigned negotiation is forgotten on disconnection.
    /// </summary>
    Negotiating = 0,

    /// <summary>
    /// The transaction is constructed; the <c>commitment_signed</c> for the new funding is being exchanged. Persisted
    /// from the moment our <c>commitment_signed</c> is sent (BOLT 2: remember the details of the negotiation).
    /// </summary>
    AwaitingCommitmentSigned = 1,

    /// <summary>
    /// The peer's valid <c>commitment_signed</c> was received (IT-SIG-03); <c>tx_signatures</c> are exchanged in the
    /// IT-SIG-01 order.
    /// </summary>
    AwaitingTxSignatures = 2,

    /// <summary>
    /// Our <c>tx_signatures</c> was sent, the peer's is missing. <c>tx_abort</c> can no longer be sent and the
    /// negotiation is kept until an input of the transaction is spent (IT-ABT-01).
    /// </summary>
    TxSignaturesSent = 3,

    /// <summary>Both <c>tx_signatures</c> were exchanged: the transaction is fully signed and may be broadcast.</summary>
    Signed = 4,

    /// <summary><c>tx_abort</c> was sent or received before our <c>tx_signatures</c>: the negotiation is forgotten.</summary>
    Aborted = 5
}