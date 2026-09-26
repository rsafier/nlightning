namespace NLightning.Client.Printers;

/// <summary>
/// Txids and block hashes as bitcoind, LND and block explorers show them: the hex of the reversed bytes. A
/// <c>TxId</c> or <c>Hash</c> holds the internal (serialized) order, which its <c>ToString()</c> prints unchanged
/// (NL-303). A BOLT 2 channel_id is not a txid and stays in its own order.
/// </summary>
public static class DisplayOrder
{
    /// <summary>The display-order hex of a txid or block hash given in internal order; "-" for none.</summary>
    public static string ToHex(byte[]? internalOrder)
    {
        if (internalOrder is null || internalOrder.Length == 0)
            return "-";

        var reversed = (byte[])internalOrder.Clone();
        Array.Reverse(reversed);
        return Convert.ToHexStringLower(reversed);
    }
}