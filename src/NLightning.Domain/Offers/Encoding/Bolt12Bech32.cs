using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NLightning.Domain.Offers.Encoding;

/// <summary>
/// The BOLT 12 string format ("Encoding"): <c>hrp || "1" || bech32(data)</c> with no checksum and no length limit,
/// optionally split with <c>+</c> followed by whitespace.
/// </summary>
/// <remarks>
/// <para>Writers use all lowercase (or all uppercase, for QR codes) and never split. Readers accept either case (never
/// mixed) and remove every <c>+</c> followed by zero or more whitespace characters between two bech32 characters; a
/// <c>+</c> at either end, next to another <c>+</c>, or whitespace anywhere else, is refused
/// (<c>format-string-test.json</c>).</para>
/// <para>The data part is BIP-173's 5-bit to 8-bit conversion: the final incomplete group must be 4 bits or less, and
/// all zero (<c>offers-test.json</c> "Bech32 padding exceeds 4-bit limit").</para>
/// </remarks>
public static class Bolt12Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const char Separator = '1';
    private const char Continuation = '+';

    private static readonly sbyte[] s_charsetReverse = BuildReverse();

    /// <summary>
    /// Encodes <paramref name="data"/> under <paramref name="hrp"/>.
    /// </summary>
    /// <param name="hrp">The human-readable part (lowercase ASCII letters and digits, not empty).</param>
    /// <param name="data">The bytes (a TLV stream).</param>
    /// <param name="uppercase">True for the all-uppercase form (QR codes).</param>
    /// <exception cref="ArgumentException"><paramref name="hrp"/> is empty or has other characters.</exception>
    public static string Encode(string hrp, ReadOnlySpan<byte> data, bool uppercase = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(hrp);
        foreach (var c in hrp)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
                throw new ArgumentException("The hrp must be lowercase ASCII letters and digits.", nameof(hrp));

        var builder = new StringBuilder(hrp.Length + 1 + (data.Length * 8 + 4) / 5);
        builder.Append(hrp).Append(Separator);

        var accumulator = 0;
        var bits = 0;
        foreach (var b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Charset[(accumulator >> bits) & 0x1f]);
            }

            accumulator &= (1 << bits) - 1;
        }

        if (bits > 0)
            builder.Append(Charset[(accumulator << (5 - bits)) & 0x1f]);

        var text = builder.ToString();
        return uppercase ? text.ToUpperInvariant() : text;
    }

    /// <summary>
    /// Decodes a BOLT 12 string.
    /// </summary>
    /// <returns>The lowercase hrp and the data bytes.</returns>
    /// <exception cref="FormatException">The string is not a valid BOLT 12 string.</exception>
    public static (string Hrp, byte[] Data) Decode(string text)
    {
        return TryDecode(text, out var hrp, out var data, out var reason)
                   ? (hrp, data)
                   : throw new FormatException(reason);
    }

    /// <summary>
    /// <see cref="Decode"/> without the exception.
    /// </summary>
    /// <param name="text">The string, possibly split with <c>+</c>.</param>
    /// <param name="hrp">The human-readable part, lowercased.</param>
    /// <param name="data">The data bytes.</param>
    /// <param name="reason">Why the string is refused.</param>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out string? hrp, [NotNullWhen(true)] out byte[]? data,
                                 [NotNullWhen(false)] out string? reason) =>
        TryDecode(text, out hrp, out data, out reason, out _);

    /// <summary>
    /// <see cref="TryDecode(string?, out string?, out byte[]?, out string?)"/>, also telling whether the refusal is
    /// a case or <c>+</c> rule (B12-ENC-02) rather than the bech32 data itself (B12-ENC-01).
    /// </summary>
    internal static bool TryDecode(string? text, [NotNullWhen(true)] out string? hrp,
                                   [NotNullWhen(true)] out byte[]? data, [NotNullWhen(false)] out string? reason,
                                   out bool isCaseOrContinuationRule)
    {
        hrp = null;
        data = null;
        isCaseOrContinuationRule = false;

        if (string.IsNullOrEmpty(text))
        {
            reason = "The string is empty.";
            return false;
        }

        if (!TryJoin(text, out var joined, out reason))
        {
            isCaseOrContinuationRule = true;
            return false;
        }

        var hasLower = false;
        var hasUpper = false;
        foreach (var c in joined)
        {
            if (c is < '!' or > '~')
            {
                reason = $"Character U+{(int)c:X4} is not printable ASCII.";
                return false;
            }

            hasLower |= char.IsAsciiLetterLower(c);
            hasUpper |= char.IsAsciiLetterUpper(c);
        }

        if (hasLower && hasUpper)
        {
            reason = "The string mixes lowercase and uppercase.";
            isCaseOrContinuationRule = true;
            return false;
        }

        var lower = hasUpper ? joined.ToLowerInvariant() : joined;
        var separator = lower.LastIndexOf(Separator);
        if (separator < 1)
        {
            reason = "The string has no human-readable part followed by '1'.";
            return false;
        }

        var dataPart = lower.AsSpan(separator + 1);
        var bytes = new byte[dataPart.Length * 5 / 8];
        var accumulator = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in dataPart)
        {
            var value = c < s_charsetReverse.Length ? s_charsetReverse[c] : -1;
            if (value < 0)
            {
                reason = $"'{c}' is not a bech32 character.";
                return false;
            }

            accumulator = (accumulator << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(accumulator >> bits);
            }

            accumulator &= (1 << bits) - 1;
        }

        if (bits >= 5)
        {
            reason = $"The data ends with {bits} padding bits (at most 4 allowed).";
            return false;
        }

        if (accumulator != 0)
        {
            reason = "The data's padding bits are not zero.";
            return false;
        }

        hrp = lower[..separator];
        data = bytes;
        reason = null;
        return true;
    }

    /// <summary>
    /// Removes each <c>+</c> and the whitespace that follows it, when it sits between two non-whitespace, non-<c>+</c>
    /// characters.
    /// </summary>
    private static bool TryJoin(string text, [NotNullWhen(true)] out string? joined,
                                [NotNullWhen(false)] out string? reason)
    {
        joined = null;
        if (!text.Contains(Continuation) && !text.Any(char.IsWhiteSpace))
        {
            joined = text;
            reason = null;
            return true;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                reason = $"Whitespace at index {i} does not follow a '+'.";
                return false;
            }

            if (c != Continuation)
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length == 0 || i == 0 || text[i - 1] == Continuation)
            {
                reason = $"'+' at index {i} does not follow a bech32 character.";
                return false;
            }

            var next = i + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
                next++;

            if (next == text.Length || text[next] == Continuation)
            {
                reason = $"'+' at index {i} is not followed by a bech32 character.";
                return false;
            }

            i = next - 1;
        }

        joined = builder.ToString();
        reason = null;
        return true;
    }

    private static sbyte[] BuildReverse()
    {
        var reverse = new sbyte[128];
        Array.Fill(reverse, (sbyte)-1);
        for (var i = 0; i < Charset.Length; i++)
            reverse[Charset[i]] = (sbyte)i;

        return reverse;
    }
}