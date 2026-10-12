namespace NLightning.LndGrpc.Mapping;

/// <summary>
/// z-base-32 (Phil Zimmermann's human-oriented base 32), the encoding of LND's and CLN's message signatures:
/// alphabet <c>ybndrfg8ejkmcpqxot1uwisza345h769</c>, most significant bit first, no padding; a trailing partial group
/// is padded with zero bits (<c>github.com/tv42/zbase32</c>, which LND uses).
/// </summary>
public static class ZBase32
{
    private const string Alphabet = "ybndrfg8ejkmcpqxot1uwisza345h769";

    /// <summary>Encodes <paramref name="data"/>.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output[index++] = Alphabet[(buffer >> bits) & 31];
            }
        }

        if (bits > 0)
            output[index] = Alphabet[(buffer << (5 - bits)) & 31];
        return new string(output);
    }

    /// <summary>Decodes <paramref name="text"/> (whole bytes only; the trailing pad bits are dropped).</summary>
    /// <exception cref="FormatException">A character outside the alphabet.</exception>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new byte[text.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"'{c}' is not a z-base-32 character");

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (index < output.Length)
                    output[index++] = (byte)(buffer >> bits);
            }

            buffer &= (1 << bits) - 1;
        }

        return output;
    }
}