namespace NLightning.Domain.Channels.Reestablish;

using Bitcoin.ValueObjects;

/// <summary>
/// One of the two funding TLVs of <c>channel_reestablish</c> as the planner sees it (BOLT 2 "Message Retransmission";
/// splicing plan SP-RE-01..04, SP2-0): a txid and its <c>retransmit_flags</c>. Used both for the fields we send
/// (<see cref="OwnReestablish"/>) and for the peer's (<see cref="PeerReestablish"/>).
/// </summary>
/// <param name="TxId">
/// <c>next_funding_txid</c> (type 1) or <c>my_current_funding_locked_txid</c> (type 5).
/// </param>
/// <param name="RetransmitFlags">The TLV's <c>retransmit_flags</c> byte.</param>
public sealed record ReestablishFundingField(TxId TxId, byte RetransmitFlags = 0)
{
    /// <summary><c>next_funding.retransmit_flags</c> bit 0: the sender did not receive our <c>commitment_signed</c> for
    /// that transaction (SP-RE-01).</summary>
    public const byte CommitmentSignedFlag = 0x01;

    /// <summary><c>my_current_funding_locked.retransmit_flags</c> bit 0: the sender did not receive our
    /// <c>announcement_signatures</c> for that transaction (SP-RE-02).</summary>
    public const byte AnnouncementSignaturesFlag = 0x01;

    /// <summary>Whether bit 0 is set (its meaning depends on the TLV).</summary>
    public bool IsBit0Set => (RetransmitFlags & 0x01) != 0;
}