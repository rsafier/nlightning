namespace NLightning.Domain.Protocol.Tlv;

using Bitcoin.ValueObjects;
using Constants;
using Crypto.Constants;

/// <summary>
/// My Current Funding Locked TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>channel_reestablish_tlvs</c> type 5: [<c>sha256</c>:<c>my_current_funding_locked_txid</c>]
/// [<c>byte</c>:<c>retransmit_flags</c>] (SP-RE-02, SP-RE-04). Bit 0 of the flags asks the peer to retransmit
/// <c>announcement_signatures</c> (<see cref="AnnouncementSignaturesFlag"/>). The txid is written in the same byte
/// order as in <c>funding_created</c>. The converter and the strict known set {1, 5} of the
/// <c>channel_reestablish</c> serializer are lane SP1-A's (SP1-A-T2).
/// </remarks>
public sealed class MyCurrentFundingLockedTlv : BaseTlv
{
    /// <summary>
    /// The size of the TLV value: a 32-byte txid followed by a 1-byte retransmit_flags.
    /// </summary>
    public const int ValueLength = CryptoConstants.Sha256HashLen + 1;

    /// <summary><c>retransmit_flags</c> bit 0: <c>announcement_signatures</c>.</summary>
    public const byte AnnouncementSignaturesFlag = 0x01;

    /// <summary>The funding (or splice) transaction the sender considers locked.</summary>
    public TxId FundingTxId { get; }

    /// <summary>The retransmit flags.</summary>
    public byte RetransmitFlags { get; }

    public MyCurrentFundingLockedTlv(TxId fundingTxId, byte retransmitFlags = 0)
        : base(TlvConstants.MyCurrentFundingLocked)
    {
        FundingTxId = fundingTxId;
        RetransmitFlags = retransmitFlags;

        Value = [.. (byte[])fundingTxId, retransmitFlags];
        Length = Value.Length;
    }
}