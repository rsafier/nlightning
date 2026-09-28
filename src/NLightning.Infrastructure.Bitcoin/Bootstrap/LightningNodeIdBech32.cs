using System.Text;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace NLightning.Infrastructure.Bitcoin.Bootstrap;

using Domain.Crypto.ValueObjects;

/// <summary>
/// The bech32 node id of a BOLT 10 virtual host label: HRP <c>ln</c>, the 33-byte compressed public key, a bech32
/// (not bech32m) checksum, 62 characters (NL-113, D-B10-4: NBitcoin's encoder, no bech32 code of our own).
/// </summary>
internal static class LightningNodeIdBech32
{
    /// <summary>The human-readable part of a node id label.</summary>
    public const string Hrp = "ln";

    /// <summary>The length of a node id label: <c>ln</c>, <c>1</c>, 53 data and 6 checksum characters.</summary>
    public const int LabelLength = 62;

    private const int PubKeyLength = 33;

    private static readonly Codec s_codec = new();

    /// <summary>
    /// Decodes a virtual host label into a node id. Upper case is accepted (resolvers may randomize the case of
    /// names, 0x20 encoding).
    /// </summary>
    /// <param name="label">The label, e.g. <c>ln1qfzcdxeg...</c>.</param>
    /// <param name="key">The node id when the label decodes.</param>
    /// <param name="reason">Why the label was refused; empty when it decodes.</param>
    public static bool TryDecode(string? label, out CompactPubKey key, out string reason)
    {
        key = default;
        if (string.IsNullOrEmpty(label))
        {
            reason = "empty label";
            return false;
        }

        label = label.ToLowerInvariant();
        if (label.Length != LabelLength)
        {
            reason = $"length {label.Length}, expected {LabelLength}";
            return false;
        }

        // The separator is the last '1' (the data charset has none)
        if (label.LastIndexOf('1') != Hrp.Length || !label.StartsWith(Hrp, StringComparison.Ordinal))
        {
            reason = "not the ln human-readable part";
            return false;
        }

        byte[] data;
        try
        {
            data = s_codec.DecodeRaw(label, out var encodingType);
            if (encodingType != Bech32EncodingType.BECH32)
            {
                reason = "bech32m checksum, expected bech32";
                return false;
            }
        }
        catch (FormatException e)
        {
            reason = $"bad bech32: {e.Message}";
            return false;
        }

        if (!TryConvertFiveToEight(data, out var bytes, out reason))
            return false;

        if (bytes.Length != PubKeyLength)
        {
            reason = $"{bytes.Length}-byte payload, expected {PubKeyLength}";
            return false;
        }

        if (bytes[0] is not (0x02 or 0x03))
        {
            reason = $"key prefix 0x{bytes[0]:x2}, expected 0x02 or 0x03";
            return false;
        }

        if (!PubKey.TryCreatePubKey(bytes, out _))
        {
            reason = "not a point on the curve";
            return false;
        }

        key = new CompactPubKey(bytes);
        reason = string.Empty;
        return true;
    }

    /// <summary>The label of <paramref name="key"/> (<c>ln1...</c>, 62 characters).</summary>
    public static string Encode(CompactPubKey key)
    {
        byte[] bytes = key;
        return EncodeRaw(ConvertEightToFive(bytes), bech32M: false);
    }

    /// <summary>Encodes 5-bit <paramref name="fiveBitData"/> under <c>ln</c> with a checksum (tests).</summary>
    internal static string EncodeRaw(byte[] fiveBitData, bool bech32M) =>
        s_codec.EncodeFiveBit(fiveBitData, bech32M ? Bech32EncodingType.BECH32M : Bech32EncodingType.BECH32);

    /// <summary>Regroups bytes into 5-bit groups, the last one zero padded.</summary>
    internal static byte[] ConvertEightToFive(ReadOnlySpan<byte> bytes)
    {
        var result = new List<byte>((bytes.Length * 8 + 4) / 5);
        int accumulator = 0, bits = 0;
        foreach (var b in bytes)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Add((byte)((accumulator >> bits) & 0x1f));
            }
        }

        if (bits > 0)
            result.Add((byte)((accumulator << (5 - bits)) & 0x1f));
        return [.. result];
    }

    /// <summary>
    /// Regroups 5-bit groups into bytes without padding: fewer than 5 bits may be left over, and they must be zero.
    /// </summary>
    private static bool TryConvertFiveToEight(byte[] data, out byte[] bytes, out string reason)
    {
        var result = new List<byte>(data.Length * 5 / 8);
        int accumulator = 0, bits = 0;
        foreach (var value in data)
        {
            if (value > 31)
            {
                bytes = [];
                reason = "invalid 5-bit value";
                return false;
            }

            accumulator = ((accumulator << 5) | value) & 0xfff;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result.Add((byte)((accumulator >> bits) & 0xff));
            }
        }

        if (bits >= 5 || (accumulator & ((1 << bits) - 1)) != 0)
        {
            bytes = [];
            reason = "non-zero padding";
            return false;
        }

        bytes = [.. result];
        reason = string.Empty;
        return true;
    }

    /// <summary>NBitcoin's bech32 codec under the <c>ln</c> HRP.</summary>
    private sealed class Codec() : NBitcoin.DataEncoders.Bech32Encoder(Encoding.ASCII.GetBytes(Hrp))
    {
        public byte[] DecodeRaw(string label, out Bech32EncodingType encodingType)
        {
            StrictLength = false;
            return DecodeDataRaw(label, out encodingType);
        }

        public string EncodeFiveBit(byte[] fiveBitData, Bech32EncodingType encodingType) =>
            EncodeData(fiveBitData, encodingType);
    }
}