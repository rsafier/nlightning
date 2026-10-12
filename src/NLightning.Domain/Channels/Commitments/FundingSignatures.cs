namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.ValueObjects;

/// <summary>
/// The signatures of one commitment on a pending splice funding (splicing plan §3.3, SP-OP-01/03): the same number and
/// HTLC set as the commitment on the current funding, only the funding outpoint and the main balances differ.
/// </summary>
/// <param name="FundingTxId">The pending funding the commitment spends.</param>
/// <param name="Signatures">The commitment and HTLC signatures for it.</param>
public sealed record FundingSignatures(TxId FundingTxId, CommitmentSignatures Signatures);

/// <summary>
/// One <c>commitment_signed</c> of a received batch (BOLT 2 "Batching channel messages", SP-OP-05/06), as the engine
/// sees it.
/// </summary>
/// <param name="FundingTxId">The message's <c>funding_txid</c> TLV; null when it is missing (a batch member without it
/// fails the channel).</param>
/// <param name="Signatures">The message's signatures.</param>
public sealed record ReceivedCommitmentSigned(TxId? FundingTxId, CommitmentSignatures Signatures);