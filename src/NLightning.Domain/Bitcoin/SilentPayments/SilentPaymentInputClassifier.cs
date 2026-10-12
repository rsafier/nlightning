namespace NLightning.Domain.Bitcoin.SilentPayments;

using Crypto.ValueObjects;

/// <summary>BIP 352 input templates. Curve validity and HASH160 are supplied by the crypto adapter.</summary>
public static class SilentPaymentInputClassifier
{
    private static readonly byte[] s_nums = Convert.FromHexString(
        "50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0");

    /// <summary>A future witness prevout disqualifies the entire transaction, not only this input.</summary>
    public static bool IsFutureWitnessVersion(ReadOnlySpan<byte> script) =>
        script.Length is >= 4 and <= 42 && script[0] is >= 0x52 and <= 0x60 &&
        script[1] is >= 2 and <= 40 && script[1] == script.Length - 2;

    public static bool TryGetInputPublicKey(ReadOnlySpan<byte> prevoutScript, ReadOnlySpan<byte> scriptSig,
                                           IReadOnlyList<byte[]> witness, out CompactPubKey key,
                                           Func<byte[], byte[]>? hash160 = null,
                                           Func<CompactPubKey, bool>? isValidPoint = null)
    {
        ArgumentNullException.ThrowIfNull(witness);
        key = default;
        if (prevoutScript.Length == 34 && prevoutScript[0] == 0x51 && prevoutScript[1] == 0x20)
        {
            var count = witness.Count;
            if (count == 0)
                return false;
            if (count > 1 && witness[count - 1].Length > 0 && witness[count - 1][0] == 0x50)
                count--;
            if (count > 1)
            {
                var control = witness[count - 1];
                if (control.Length < 33 || control.Length > 4129 || (control.Length - 33) % 32 != 0 ||
                    control.AsSpan(1, 32).SequenceEqual(s_nums))
                    return false;
            }
            var compressed = new byte[33];
            compressed[0] = 2;
            prevoutScript[2..].CopyTo(compressed.AsSpan(1));
            return Accept(compressed, isValidPoint, out key);
        }
        var p2wpkh = prevoutScript.Length == 22 && prevoutScript[0] == 0 && prevoutScript[1] == 0x14;
        var wrapped = prevoutScript.Length == 23 && prevoutScript[0] == 0xa9 && prevoutScript[1] == 0x14 &&
                      prevoutScript[22] == 0x87 && scriptSig.Length == 23 && scriptSig[0] == 0x16 &&
                      scriptSig[1] == 0 && scriptSig[2] == 0x14;
        if (p2wpkh || wrapped)
            return witness.Count > 0 && Accept(witness[^1], isValidPoint, out key);
        if (prevoutScript.Length != 25 || prevoutScript[0] != 0x76 || prevoutScript[1] != 0xa9 ||
            prevoutScript[2] != 0x14 || prevoutScript[23] != 0x88 || prevoutScript[24] != 0xac || hash160 is null)
            return false;
        for (var end = scriptSig.Length; end >= 33; end--)
        {
            var candidate = scriptSig.Slice(end - 33, 33);
            if (candidate[0] is not (2 or 3))
                continue;
            var bytes = candidate.ToArray();
            if (hash160(bytes).AsSpan().SequenceEqual(prevoutScript.Slice(3, 20)) &&
                Accept(bytes, isValidPoint, out key))
                return true;
        }
        return false;
    }

    private static bool Accept(byte[] bytes, Func<CompactPubKey, bool>? isValidPoint, out CompactPubKey key)
    {
        key = default;
        if (bytes.Length != 33 || bytes[0] is not (2 or 3))
            return false;
        var candidate = new CompactPubKey(bytes.AsSpan().ToArray());
        if (isValidPoint is not null && !isValidPoint(candidate))
            return false;
        key = candidate;
        return true;
    }
}