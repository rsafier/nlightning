namespace NLightning.Domain.Protocol.Tlv;

using Bitcoin.ValueObjects;
using Constants;
using Crypto.Constants;

/// <summary>
/// Funding TxId TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>commitment_signed_tlvs</c> type 1: [<c>sha256</c>:<c>funding_txid</c>], the funding transaction spent by
/// the signed commitment transaction. The txid is written in the same byte order as in <c>funding_created</c>.
/// </remarks>
public class FundingTxIdTlv : BaseTlv
{
    /// <summary>
    /// The size of the TLV value: a 32-byte txid.
    /// </summary>
    public const int ValueLength = CryptoConstants.Sha256HashLen;

    /// <summary>
    /// The funding transaction id.
    /// </summary>
    public TxId FundingTxId { get; }

    public FundingTxIdTlv(TxId fundingTxId) : base(TlvConstants.FundingTxId)
    {
        FundingTxId = fundingTxId;

        Value = [.. (byte[])fundingTxId];
        Length = Value.Length;
    }
}