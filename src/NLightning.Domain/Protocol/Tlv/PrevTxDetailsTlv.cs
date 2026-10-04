using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Tlv;

using Bitcoin.ValueObjects;
using Constants;
using Crypto.Constants;
using ValueObjects;

/// <summary>
/// Prev Tx Details TLV.
/// </summary>
/// <remarks>
/// BOLTs PR #1324 <c>tx_add_input_tlvs</c> type 2 <c>prevtx_details</c>: [<c>sha256</c>:<c>prevtx_txid</c>]
/// [<c>u64</c>:<c>amount_satoshis</c>] [<c>...*byte</c>:<c>scriptpubkey</c>]. A taproot input may be sent with
/// <c>prevtx_len</c> = 0 and its spent output described here instead: every taproot signature commits to the amount and
/// script of every spent output, so the transaction cannot be malleated as long as every input is taproot. Eclair 0.14.3
/// numbers it 1111 (<see cref="InteractiveTxTlvConstants.PrevTxDetailsEclair"/>), with the same encoding; both are read,
/// the type it was read as is kept in <see cref="BaseTlv.Type"/>. The txid is in the same byte order as
/// <see cref="SharedInputTxIdTlv"/>. Converted by <c>PrevTxDetailsTlvConverter</c> (NL-957).
/// </remarks>
public sealed class PrevTxDetailsTlv : BaseTlv
{
    /// <summary>
    /// The size of the fixed part of the value: a 32-byte txid and a u64 amount.
    /// </summary>
    public const int FixedLength = CryptoConstants.Sha256HashLen + sizeof(ulong);

    /// <summary>
    /// The txid of the transaction holding the spent output.
    /// </summary>
    public TxId PrevTxId { get; }

    /// <summary>
    /// The value of the spent output, in satoshis (not range-checked here; the interactive-tx rules refuse more than
    /// <c>MAX_MONEY</c>).
    /// </summary>
    public ulong AmountSatoshis { get; }

    /// <summary>
    /// The scriptPubKey of the spent output.
    /// </summary>
    public BitcoinScript ScriptPubKey { get; }

    /// <param name="prevTxId">The txid of the spent output.</param>
    /// <param name="amountSatoshis">The value of the spent output in satoshis.</param>
    /// <param name="scriptPubKey">The scriptPubKey of the spent output.</param>
    /// <param name="type"><see cref="InteractiveTxTlvConstants.PrevTxDetails"/> (the default) or
    /// <see cref="InteractiveTxTlvConstants.PrevTxDetailsEclair"/>.</param>
    /// <exception cref="ArgumentException">Another type.</exception>
    public PrevTxDetailsTlv(TxId prevTxId, ulong amountSatoshis, BitcoinScript scriptPubKey, BigSize? type = null)
        : base(type ?? InteractiveTxTlvConstants.PrevTxDetails)
    {
        if (Type != InteractiveTxTlvConstants.PrevTxDetails && Type != InteractiveTxTlvConstants.PrevTxDetailsEclair)
            throw new ArgumentException($"prevtx_details is type 2 or 1111, not {Type}", nameof(type));

        PrevTxId = prevTxId;
        AmountSatoshis = amountSatoshis;
        ScriptPubKey = scriptPubKey;

        var script = (byte[])scriptPubKey;
        var value = new byte[FixedLength + script.Length];
        ((byte[])prevTxId).CopyTo(value, 0);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(CryptoConstants.Sha256HashLen), amountSatoshis);
        script.CopyTo(value, FixedLength);

        Value = value;
        Length = value.Length;
    }
}