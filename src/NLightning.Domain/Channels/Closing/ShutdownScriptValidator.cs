namespace NLightning.Domain.Channels.Closing;

/// <summary>
/// The <c>scriptpubkey</c> forms BOLT 2 allows in <c>shutdown</c> (B2-SHUT-S10, B2-SHUT-R02) and the BOLT 3 dust
/// thresholds of each output script (B2-CLS-R10, B3-DUST-01).
/// </summary>
/// <remarks>
/// Pure and allocation-free. The dust thresholds are Bitcoin Core's relay thresholds as BOLT 3 "Dust Limits" lists them
/// (computed at 3000 sat/kB), not a function of the current feerate.
/// </remarks>
public static class ShutdownScriptValidator
{
    private const byte Op0 = 0x00;
    private const byte Op1 = 0x51;
    private const byte Op16 = 0x60;
    private const byte OpReturn = 0x6a;
    private const byte OpPushData1 = 0x4c;
    private const byte OpDup = 0x76;
    private const byte OpHash160 = 0xa9;
    private const byte OpEqual = 0x87;
    private const byte OpEqualVerify = 0x88;
    private const byte OpCheckSig = 0xac;

    /// <summary>BOLT 3 dust threshold of a P2PKH output.</summary>
    public const ulong P2PkhDustSat = 546;

    /// <summary>BOLT 3 dust threshold of a P2SH output.</summary>
    public const ulong P2ShDustSat = 540;

    /// <summary>BOLT 3 dust threshold of a P2WPKH output.</summary>
    public const ulong P2WpkhDustSat = 294;

    /// <summary>BOLT 3 dust threshold of a P2WSH output.</summary>
    public const ulong P2WshDustSat = 330;

    /// <summary>BOLT 3 dust threshold of a pay-to-anchor output.</summary>
    public const ulong P2ADustSat = 240;

    /// <summary>BOLT 3 dust threshold of an unknown segwit version output (P2TR included).</summary>
    public const ulong UnknownSegwitDustSat = 354;

    /// <summary>
    /// True when <paramref name="script"/> is a <c>shutdown</c> script BOLT 2 allows: v0 P2WPKH or P2WSH; a segwit
    /// v1-v16 program of 2 to 40 bytes only with <c>option_shutdown_anysegwit</c>; a single-push <c>OP_RETURN</c> of
    /// 6-80 bytes only with <c>option_simple_close</c>.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> script, bool anySegwit, bool simpleClose)
    {
        if (IsP2Wpkh(script) || IsP2Wsh(script))
            return true;

        if (anySegwit && IsSegwitV1To16(script))
            return true;

        return simpleClose && IsStandardOpReturn(script);
    }

    /// <summary>The BOLT 3 dust threshold of an output paying <paramref name="script"/>, in satoshis.</summary>
    /// <remarks>
    /// <c>OP_RETURN</c> outputs are never dust (0). A script of no known standard form gets the P2PKH threshold, the
    /// highest one, so it is never treated as more relayable than it is.
    /// </remarks>
    public static ulong GetDustThresholdSat(ReadOnlySpan<byte> script)
    {
        if (script.Length > 0 && script[0] == OpReturn)
            return 0;
        if (IsP2Wpkh(script))
            return P2WpkhDustSat;
        if (IsP2Wsh(script))
            return P2WshDustSat;
        if (IsP2A(script))
            return P2ADustSat;
        if (IsSegwitV1To16(script))
            return UnknownSegwitDustSat;
        if (IsP2Sh(script))
            return P2ShDustSat;

        return P2PkhDustSat;
    }

    /// <summary><c>OP_0 20 &lt;20 bytes&gt;</c>.</summary>
    public static bool IsP2Wpkh(ReadOnlySpan<byte> script) =>
        script.Length == 22 && script[0] == Op0 && script[1] == 20;

    /// <summary><c>OP_0 32 &lt;32 bytes&gt;</c>.</summary>
    public static bool IsP2Wsh(ReadOnlySpan<byte> script) =>
        script.Length == 34 && script[0] == Op0 && script[1] == 32;

    /// <summary><c>OP_1..OP_16</c> followed by a single push of 2 to 40 bytes (witness versions 1-16).</summary>
    public static bool IsSegwitV1To16(ReadOnlySpan<byte> script) =>
        script.Length >= 4 && script[0] is >= Op1 and <= Op16 && script[1] is >= 2 and <= 40
     && script.Length == script[1] + 2;

    /// <summary><c>OP_DUP OP_HASH160 20 &lt;20 bytes&gt; OP_EQUALVERIFY OP_CHECKSIG</c>.</summary>
    public static bool IsP2Pkh(ReadOnlySpan<byte> script) =>
        script.Length == 25 && script[0] == OpDup && script[1] == OpHash160 && script[2] == 20
     && script[23] == OpEqualVerify && script[24] == OpCheckSig;

    /// <summary>
    /// <c>OP_RETURN</c> followed by one push: <c>6..75</c> and that many bytes, or <c>OP_PUSHDATA1 76..80</c> and that
    /// many bytes (BOLT 2 <c>option_simple_close</c> form).
    /// </summary>
    public static bool IsStandardOpReturn(ReadOnlySpan<byte> script)
    {
        if (script.Length < 2 || script[0] != OpReturn)
            return false;

        if (script[1] is >= 6 and <= 75)
            return script.Length == script[1] + 2;

        return script[1] == OpPushData1 && script.Length >= 3 && script[2] is >= 76 and <= 80
            && script.Length == script[2] + 3;
    }

    private static bool IsP2A(ReadOnlySpan<byte> script) =>
        script.Length == 4 && script[0] == Op1 && script[1] == 2 && script[2] == 0x4e && script[3] == 0x73;

    private static bool IsP2Sh(ReadOnlySpan<byte> script) =>
        script.Length == 23 && script[0] == OpHash160 && script[1] == 20 && script[22] == OpEqual;
}