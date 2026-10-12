using System.Text;
using NBitcoin;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Crypto.Contexts;

/// <summary>
/// Bitcoin Core's message signatures as LND's walletrpc <c>SignMessageWithAddr</c>/<c>VerifyMessageWithAddr</c> use them
/// (NL-1186): a 65-byte recoverable compact signature over SHA256d of <c>varstr("Bitcoin Signed Message:\n") ||
/// varstr(message)</c>, header byte <c>27 + 4 + recovery id</c> for a compressed key (<c>27 + recovery id</c> for an
/// uncompressed one), then <c>r || s</c>. Signing is <see cref="LocalLightningSigner.SignWalletMessage"/>.
/// </summary>
public static class BitcoinMessageSignature
{
    /// <summary>The prefix Bitcoin Core and LND put before the signed message.</summary>
    public static readonly byte[] Prefix = Encoding.ASCII.GetBytes("Bitcoin Signed Message:\n");

    /// <summary>The 32-byte digest of a message (LND's <c>doubleHashMessage</c>).</summary>
    public static byte[] Digest(ReadOnlySpan<byte> message)
    {
        using var stream = new MemoryStream();
        WriteVarString(stream, Prefix);
        WriteVarString(stream, message);
        return SHA256.HashData(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>
    /// The key that made <paramref name="signature"/> over <paramref name="message"/> (btcec's <c>RecoverCompact</c>:
    /// headers 27-34) and whether the header says compressed, or null when it does not recover.
    /// </summary>
    public static (PubKey Key, bool Compressed)? Recover(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != LightningMessageSignature.Length || signature[0] is < 27 or > 34)
            return null;

        var compressed = signature[0] >= 31;
        var recoveryId = (signature[0] - 27) & 3;
        if (!SecpRecoverableECDSASignature.TryCreateFromCompact(signature[1..], recoveryId, out var recoverable)
         || recoverable is null)
            return null;

        if (!ECPubKey.TryRecover(NLightningCryptoContext.Instance, recoverable, Digest(message), out var publicKey)
         || publicKey is null)
            return null;

        return (new PubKey(publicKey.ToBytes(compressed)), compressed);
    }

    /// <summary>
    /// LND's <c>VerifyMessageWithAddr</c>: whether the key <paramref name="signature"/> recovers to owns
    /// <paramref name="address"/> (P2PKH and P2WPKH by the hash of the key as the header serializes it, nested P2WPKH by
    /// the P2SH of its witness program, P2TR by the key's BIP 86 output key), and the recovered key. Null when the
    /// signature does not recover.
    /// </summary>
    /// <exception cref="ArgumentException">The address type cannot be verified.</exception>
    public static (bool Valid, byte[] PubKey)? Verify(BitcoinAddress address, ReadOnlySpan<byte> message,
                                                       ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Recover(message, signature) is not { } recovered)
            return null;

        var (key, compressed) = recovered;
        var serialized = key.ToBytes();
        var valid = address switch
        {
            BitcoinPubKeyAddress p2pkh => key.Hash == p2pkh.Hash,
            BitcoinWitPubKeyAddress p2wpkh => compressed && key.WitHash == p2wpkh.Hash,
            BitcoinScriptAddress p2sh => compressed && key.WitHash.ScriptPubKey.Hash == p2sh.Hash,
            TaprootAddress p2tr => key.GetTaprootFullPubKey().ToBytes().AsSpan()
                                      .SequenceEqual(p2tr.PubKey.ToBytes()),
            _ => throw new ArgumentException($"unsupported address type {address.GetType().Name}")
        };
        return (valid, serialized);
    }

    private static void WriteVarString(Stream stream, ReadOnlySpan<byte> bytes)
    {
        var length = (ulong)bytes.Length;
        if (length < 0xFD)
        {
            stream.WriteByte((byte)length);
        }
        else if (length <= 0xFFFF)
        {
            stream.WriteByte(0xFD);
            stream.WriteByte((byte)length);
            stream.WriteByte((byte)(length >> 8));
        }
        else
        {
            stream.WriteByte(0xFE);
            for (var i = 0; i < 4; i++)
                stream.WriteByte((byte)(length >> (8 * i)));
        }

        stream.Write(bytes);
    }
}