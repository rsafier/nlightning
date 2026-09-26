namespace NLightning.Domain.Protocol.Tlv;

using Constants;

/// <summary>
/// Next Funding TLV.
/// </summary>
/// <remarks>
/// The next funding TLV is used in the ChannelReestablishMessage. BOLT 2 <c>channel_reestablish_tlvs</c> type 1:
/// [<c>sha256</c>:<c>next_funding_txid</c>] [<c>byte</c>:<c>retransmit_flags</c>].
/// </remarks>
public class NextFundingTlv : BaseTlv
{
    /// <summary>
    /// The size of the TLV value: a 32-byte txid followed by a 1-byte retransmit_flags.
    /// </summary>
    public const int ValueLength = 33;

    /// <summary>
    /// The next funding transaction id
    /// </summary>
    public byte[] NextFundingTxId { get; }

    /// <summary>
    /// The retransmit flags
    /// </summary>
    public byte RetransmitFlags { get; }

    public NextFundingTlv(byte[] nextFundingTxId, byte retransmitFlags = 0) : base(TlvConstants.NextFunding)
    {
        NextFundingTxId = nextFundingTxId;
        RetransmitFlags = retransmitFlags;

        Value = [.. NextFundingTxId, RetransmitFlags];
        Length = Value.Length;
    }
}