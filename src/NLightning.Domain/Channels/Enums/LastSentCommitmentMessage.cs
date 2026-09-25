namespace NLightning.Domain.Channels.Enums;

/// <summary>
/// Which of <c>commitment_signed</c> and <c>revoke_and_ack</c> we sent last, persisted per channel so that both are
/// retransmitted in their original order on <c>channel_reestablish</c> (BOLT 2 §Message Retransmission; plan §3.11
/// step 6, <c>LastSentOrder</c>).
/// </summary>
public enum LastSentCommitmentMessage : byte
{
    /// <summary>Neither has been sent since the channel opened.</summary>
    None = 0,

    /// <summary>The last one sent was a <c>commitment_signed</c>.</summary>
    CommitmentSigned = 1,

    /// <summary>The last one sent was a <c>revoke_and_ack</c>.</summary>
    RevokeAndAck = 2,
}