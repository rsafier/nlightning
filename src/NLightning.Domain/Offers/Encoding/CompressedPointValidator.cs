using System.Numerics;

namespace NLightning.Domain.Offers.Encoding;

/// <summary>
/// Whether 33 bytes are a compressed secp256k1 point: prefix 02 or 03, x below the field prime, and
/// <c>x^3 + 7</c> a square modulo p (so a y exists). BCL only, for the BOLT 12 readers, which must refuse a
/// <c>point</c> field that is not on the curve (<c>offers-test.json</c> "invalid offer_issuer_id", "bad path_key").
/// </summary>
public static class CompressedPointValidator
{
    private static readonly BigInteger s_fieldPrime =
        BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F",
                         System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger s_legendreExponent = (s_fieldPrime - 1) / 2;

    /// <summary>
    /// True when <paramref name="point"/> is a valid compressed point.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> point)
    {
        if (point.Length != 33 || (point[0] != 0x02 && point[0] != 0x03))
            return false;

        var x = new BigInteger(point[1..], isUnsigned: true, isBigEndian: true);
        if (x >= s_fieldPrime)
            return false;

        var rhs = (BigInteger.ModPow(x, 3, s_fieldPrime) + 7) % s_fieldPrime;
        return rhs.IsZero || BigInteger.ModPow(rhs, s_legendreExponent, s_fieldPrime).IsOne;
    }
}