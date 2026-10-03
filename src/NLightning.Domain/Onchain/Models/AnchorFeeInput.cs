namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;

/// <summary>
/// One wallet output that pays the fee of a zero-fee anchors HTLC transaction (BOLT 5 §Generation of HTLC
/// Transactions, B5-HTX-02, plan O7-T3). The wallet selected it and signs it; the HTLC transaction builder only adds it
/// as an input after the HTLC input.
/// </summary>
/// <param name="TxId">The transaction that holds the output.</param>
/// <param name="Vout">The output index.</param>
/// <param name="AmountSat">The output amount in satoshis.</param>
/// <param name="ScriptPubKey">The output's script (what its witness spends).</param>
/// <param name="InputWeight">The weight the input adds once signed: <c>4 * 41</c> non-witness bytes plus its
/// witness (272-273 for a P2WPKH input, see <see cref="P2WpkhInputWeight"/>).</param>
public sealed record AnchorFeeInput(TxId TxId, uint Vout, ulong AmountSat, byte[] ScriptPubKey, long InputWeight)
{
    /// <summary>
    /// A P2WPKH input with a worst-case 73-byte signature: <c>164 + (1 + 1 + 73 + 1 + 33)</c>.
    /// </summary>
    public const long P2WpkhInputWeight = 273;
}