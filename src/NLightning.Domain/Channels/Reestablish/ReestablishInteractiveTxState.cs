namespace NLightning.Domain.Channels.Reestablish;

using Bitcoin.ValueObjects;

/// <summary>
/// The latest interactive funding transaction of a channel (a splice or a dual-funded open) as far as the signing
/// steps go, for the <c>next_funding</c> rules of <c>channel_reestablish</c> (BOLT 2; splicing plan SP-RE-01, SP-RE-03,
/// SP2-0). Built by the Application reestablish code from the <c>InteractiveTxSessions</c> row (lane SP2-A); null in
/// <see cref="ReestablishLocalState.LatestInteractiveTx"/> when there is none that is constructed and not aborted.
/// </summary>
/// <param name="TxId">The constructed transaction's id.</param>
/// <param name="IsSplice">A splice (else a dual-funded open). SP-RE-05 only looks at splice transactions.</param>
/// <param name="CommitmentSignedSent">We sent our <c>commitment_signed</c> for it.</param>
/// <param name="CommitmentSignedReceived">We received the peer's <c>commitment_signed</c> for it.</param>
/// <param name="TxSignaturesSent">We sent our <c>tx_signatures</c> for it.</param>
/// <param name="TxSignaturesReceived">We received the peer's <c>tx_signatures</c> for it.</param>
/// <param name="SendsTxSignaturesFirst">We sign first (BOLT 2 <c>tx_signatures</c> ordering; the shared input counts
/// for the splice initiator, SP-CS-02).</param>
public sealed record ReestablishInteractiveTxState(
    TxId TxId,
    bool IsSplice,
    bool CommitmentSignedSent,
    bool CommitmentSignedReceived,
    bool TxSignaturesSent,
    bool TxSignaturesReceived,
    bool SendsTxSignaturesFirst);