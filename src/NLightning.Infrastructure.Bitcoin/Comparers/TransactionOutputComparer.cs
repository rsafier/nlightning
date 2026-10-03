namespace NLightning.Infrastructure.Bitcoin.Comparers;

using Outputs;

public class TransactionOutputComparer : IComparer<BaseOutput>
{
    public static TransactionOutputComparer Instance { get; } = new();

    public int Compare(BaseOutput? x, BaseOutput? y)
    {
        switch (x, y)
        {
            // Deal with nulls
            case (null, null):
                return 0;
            case (null, not null):
                return -1;
            case (not null, null):
                return 1;
        }

        // Compare by value (satoshis)
        var valueComparison = x.Amount.Satoshi.CompareTo(y.Amount.Satoshi);
        if (valueComparison != 0)
        {
            return valueComparison;
        }

        // Compare by scriptPubKey lexicographically
        var scriptComparison = CompareScriptPubKey(x.ScriptPubKey.ToBytes(), y.ScriptPubKey.ToBytes());
        if (scriptComparison != 0)
        {
            return scriptComparison;
        }

        // For HTLC outputs, compare by CLTV expiry (a simple taproot offered HTLC's script does not commit to it)
        if (TryGetCltvExpiry(x, out var xExpiry) && TryGetCltvExpiry(y, out var yExpiry) && xExpiry != yExpiry)
            return xExpiry.CompareTo(yExpiry);

        return 0;
    }

    private static bool TryGetCltvExpiry(BaseOutput output, out ulong cltvExpiry)
    {
        switch (output)
        {
            case OfferedHtlcOutput offered:
                cltvExpiry = offered.CltvExpiry;
                return true;
            case ReceivedHtlcOutput received:
                cltvExpiry = received.CltvExpiry;
                return true;
            case TaprootHtlcOutput taproot:
                cltvExpiry = taproot.CltvExpiry;
                return true;
            default:
                cltvExpiry = 0;
                return false;
        }
    }

    private static int CompareScriptPubKey(ReadOnlySpan<byte> script1, ReadOnlySpan<byte> script2)
    {
        var length = Math.Min(script1.Length, script2.Length);
        for (var i = 0; i < length; i++)
        {
            if (script1[i] != script2[i])
            {
                return script1[i].CompareTo(script2[i]);
            }
        }

        // Compare by length if scripts are identical up to the length of the shorter one
        return script1.Length.CompareTo(script2.Length);
    }
}