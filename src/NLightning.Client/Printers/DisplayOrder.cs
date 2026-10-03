namespace NLightning.Client.Printers;

/// <summary>
/// Txids and block hashes as bitcoind, LND and block explorers show them: the hex of the reversed bytes. A
/// <c>TxId</c> or <c>Hash</c> holds the internal (serialized) order; <c>Hash.ToString()</c> prints it unchanged (NL-303),
/// <c>TxId.ToString()</c> prints the display order since NL-519. A BOLT 2 channel_id is not a txid and stays in its own order.
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