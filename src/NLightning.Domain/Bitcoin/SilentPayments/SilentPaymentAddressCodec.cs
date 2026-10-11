using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NLightning.Domain.Bitcoin.SilentPayments;

using Crypto.ValueObjects;
using Protocol.ValueObjects;

/// <summary>BIP 352 Bech32m address encoding with the extended 1023-character limit.</summary>
public static class SilentPaymentAddressCodec
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32m = 0x2bc830a3;
    private static readonly uint[] s_generators = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    public static string GetHrp(BitcoinNetwork network) => network.Name switch
    {
        "mainnet" => "sp",
        "regtest" => "sprt",
        "testnet" or "testnet4" or "signet" => "tsp",
        _ => throw new ArgumentException($"Unsupported silent payment network '{network}'.", nameof(network))
    };

    public static string Encode(CompactPubKey scanKey, CompactPubKey spendKey, BitcoinNetwork network) =>
        Encode(new SilentPaymentAddress(0, scanKey, spendKey, GetHrp(network)));

    public static string Encode(SilentPaymentAddress address)
    {
        if (address.Version > 30 || address.Hrp is not ("sp" or "tsp" or "sprt"))
            throw new ArgumentException("Invalid silent payment version or HRP.", nameof(address));
        ReadOnlySpan<byte> scan = address.ScanKey;
        ReadOnlySpan<byte> spend = address.SpendKey;
        if (scan.Length != 33 || spend.Length != 33)
            throw new ArgumentException("Both silent payment keys must be compressed public keys.", nameof(address));
        var data = new List<byte> { address.Version };
        var accumulator = 0;
        var bits = 0;
        foreach (var b in scan.ToArray().Concat(spend.ToArray()))
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                data.Add((byte)((accumulator >> bits) & 31));
            }
            accumulator &= (1 << bits) - 1;
        }
        if (bits > 0)
            data.Add((byte)(accumulator << (5 - bits)));
        var checksum = ExpandHrp(address.Hrp);
        foreach (var value in data)
            checksum = Step(checksum, value);
        for (var i = 0; i < 6; i++)
            checksum = Step(checksum, 0);
        checksum ^= Bech32m;
        var result = new StringBuilder(address.Hrp).Append('1');
        foreach (var value in data)
            result.Append(Charset[value]);
        for (var i = 5; i >= 0; i--)
            result.Append(Charset[(int)((checksum >> (5 * i)) & 31)]);
        return result.ToString();
    }

    public static SilentPaymentAddress Decode(string text, BitcoinNetwork network, bool allowFutureVersions = false) =>
        TryDecode(text, network, out var address, out var reason, allowFutureVersions)
            ? address
            : throw new FormatException(reason);

    public static bool TryDecode(string? text, BitcoinNetwork network, out SilentPaymentAddress address,
                                 [NotNullWhen(false)] out string? reason, bool allowFutureVersions = false)
    {
        address = default;
        reason = "Invalid silent payment address.";
        var expectedHrp = GetHrp(network);
        if (text is null || text.Length > 1023 || text.Length < 8 ||
            text.Any(c => c is < '!' or > '~') ||
            (text.Any(char.IsLower) && text.Any(char.IsUpper)))
            return false;
        text = text.ToLowerInvariant();
        var separator = text.LastIndexOf('1');
        if (separator < 1 || separator + 7 >= text.Length)
            return false;
        var hrp = text[..separator];
        if (hrp != expectedHrp)
        {
            reason = $"Silent payment address requires HRP '{expectedHrp}' for network '{network}'.";
            return false;
        }
        var symbols = new byte[text.Length - separator - 1];
        var checksum = ExpandHrp(hrp);
        for (var i = 0; i < symbols.Length; i++)
        {
            var value = Charset.IndexOf(text[separator + 1 + i]);
            if (value < 0)
                return false;
            symbols[i] = (byte)value;
            checksum = Step(checksum, (byte)value);
        }
        if (checksum != Bech32m)
            return false;
        var version = symbols[0];
        if (version == 31 || (version != 0 && !allowFutureVersions))
        {
            reason = $"Silent payment address version {version} is unsupported.";
            return false;
        }
        var bytes = new List<byte>();
        var accumulator = 0;
        var bits = 0;
        for (var i = 1; i < symbols.Length - 6; i++)
        {
            accumulator = (accumulator << 5) | symbols[i];
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(accumulator >> bits));
            }
            accumulator &= (1 << bits) - 1;
        }
        if (bits >= 5 || accumulator != 0 || bytes.Count < 66 || (version == 0 && bytes.Count != 66) ||
            bytes[0] is not (2 or 3) || bytes[33] is not (2 or 3))
            return false;
        address = new SilentPaymentAddress(version, new CompactPubKey(bytes.GetRange(0, 33).ToArray()),
                                           new CompactPubKey(bytes.GetRange(33, 33).ToArray()), hrp);
        reason = null;
        return true;
    }

    private static uint ExpandHrp(string hrp)
    {
        uint checksum = 1;
        foreach (var c in hrp)
            checksum = Step(checksum, (byte)(c >> 5));
        checksum = Step(checksum, 0);
        foreach (var c in hrp)
            checksum = Step(checksum, (byte)(c & 31));
        return checksum;
    }

    private static uint Step(uint checksum, byte value)
    {
        var top = checksum >> 25;
        checksum = ((checksum & 0x1ffffff) << 5) ^ value;
        for (var i = 0; i < 5; i++)
            if (((top >> i) & 1) != 0)
                checksum ^= s_generators[i];
        return checksum;
    }
}