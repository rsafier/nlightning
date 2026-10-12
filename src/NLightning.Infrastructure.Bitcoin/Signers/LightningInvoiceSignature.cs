using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Utils;

/// <summary>BOLT 11 message construction and recoverable node-key signing, used inside signer processes.</summary>
public static class LightningInvoiceSignature
{
    public static byte[] Sign(ReadOnlySpan<byte> nodePrivateKey, string humanReadablePart, byte[] dataU5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(humanReadablePart);
        ArgumentNullException.ThrowIfNull(dataU5);
        if (!humanReadablePart.StartsWith("ln", StringComparison.Ordinal)
         || humanReadablePart.Any(c => c is < '!' or > '~') || dataU5.Length < 7)
            throw new ArgumentException("Invalid BOLT 11 signing message.");
        using var writer = new BitWriter(checked(dataU5.Length * 5));
        foreach (var word in dataU5)
        {
            if (word > 31)
                throw new ArgumentException("BOLT 11 data must contain five-bit words.", nameof(dataU5));
            writer.WriteByteAsBits(word, 5);
        }
        var hrp = Encoding.UTF8.GetBytes(humanReadablePart);
        var data = writer.ToArray();
        var message = new byte[hrp.Length + data.Length];
        hrp.CopyTo(message, 0);
        data.CopyTo(message, hrp.Length);
        var privateKey = nodePrivateKey.ToArray();
        try
        {
            using var key = new Key(privateKey);
            var signature = key.SignCompact(new uint256(SHA256.HashData(message)), false);
            var result = new byte[65];
            signature.Signature.CopyTo(result, 0);
            result[64] = (byte)signature.RecoveryId;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}