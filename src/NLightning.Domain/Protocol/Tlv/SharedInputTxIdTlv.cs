namespace NLightning.Domain.Protocol.Tlv;

using Bitcoin.ValueObjects;
using Constants;
using Crypto.Constants;

/// <summary>
/// Shared Input TxId TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>tx_add_input_tlvs</c> type 0: [<c>sha256</c>:<c>funding_txid</c>] (IT-W-01). It marks the input as the
/// channel's shared funding output being spent by a splice: such a <c>tx_add_input</c> has <c>prevtx_len</c> = 0 and the
/// receiver checks the txid against the current funding (IT-R-01). The txid is written in the same byte order as in
/// <c>funding_created</c> and <see cref="FundingTxIdTlv"/>. The converter is lane IT-C's (IT3-T1).
/// </remarks>
public sealed class SharedInputTxIdTlv : BaseTlv
{
    /// <summary>
    /// The size of the TLV value: a 32-byte txid.
    /// </summary>
    public const int ValueLength = CryptoConstants.Sha256HashLen;

    /// <summary>
    /// The funding transaction the shared input spends.
    /// </summary>
    public TxId FundingTxId { get; }

    public SharedInputTxIdTlv(TxId fundingTxId) : base(InteractiveTxTlvConstants.SharedInputTxId)
    {
        FundingTxId = fundingTxId;

        Value = [.. (byte[])fundingTxId];
        Length = Value.Length;
    }
}