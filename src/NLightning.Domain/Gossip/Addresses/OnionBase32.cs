namespace NLightning.Domain.Gossip.Addresses;

/// <summary>
/// RFC 4648 base32 (lower-case alphabet, no padding), the encoding of Tor onion hostnames.
/// </summary>
internal static class OnionBase32
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var chars = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[index++] = Alphabet[(buffer >> (bits - 5)) & 0x1F];
                bits -= 5;
            }
        }

        if (bits > 0)
            chars[index++] = Alphabet[(buffer << (5 - bits)) & 0x1F];

        return new string(chars, 0, index);
    }

    /// <summary>
    /// Decodes <paramref name="text"/> (case-insensitive) into exactly <paramref name="expectedLength"/> bytes.
    /// Returns null when a character is outside the alphabet, the length does not match, or the unused trailing bits
    /// are not zero (non-canonical).
    /// </summary>
    public static byte[]? TryDecode(string text, int expectedLength)
    {
        if (text.Length != (expectedLength * 8 + 4) / 5)
            return null;

        var result = new byte[expectedLength];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(char.ToLowerInvariant(c));
            if (value < 0)
                return null;

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8)
                continue;

            if (index == expectedLength)
                return null;

            result[index++] = (byte)(buffer >> (bits - 8));
            bits -= 8;
            buffer &= (1 << bits) - 1;
        }

        return index == expectedLength && buffer == 0 ? result : null;
    }
}