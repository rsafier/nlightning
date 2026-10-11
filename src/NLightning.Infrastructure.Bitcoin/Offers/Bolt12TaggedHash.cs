using System.Security.Cryptography;
using System.Text;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Offers;

using Domain.Crypto.Constants;
using Domain.Offers.Constants;
using Domain.Offers.Enums;
using Infrastructure.Crypto.Factories;

/// <summary>
/// The BOLT 12 tagged hash ("Signature Calculation"): <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>,
/// the check that a tag is a BOLT 12 signature tag for a key, and the BIP-340 signature over it.
/// </summary>
internal static class Bolt12TaggedHash
{
    private const int AuxRandomnessLen = 32;

    /// <summary>
    /// <c>SHA256(SHA256(tag) || SHA256(tag) || msg)</c>, with the tag as UTF-8.
    /// </summary>
    public static byte[] Compute(string tag, ReadOnlySpan<byte> message)
    {
        ArgumentNullException.ThrowIfNull(tag);

        Span<byte> tagHash = stackalloc byte[CryptoConstants.Sha256HashLen];
        SHA256.HashData(Encoding.UTF8.GetBytes(tag), tagHash);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(tagHash);
        sha256.AppendData(tagHash);
        sha256.AppendData(message);
        return sha256.GetHashAndReset();
    }

    /// <summary>
    /// Whether <paramref name="tag"/> is a BOLT 12 signature tag: exactly <see cref="Bolt12Constants.InvoiceRequestSignatureTag"/>
    /// or <see cref="Bolt12Constants.InvoiceSignatureTag"/>, the only messages BOLT 12 signs. A made-up name of the
    /// form <c>"lightning" || X || "signature"</c> is not one.
    /// </summary>
    public static bool IsSignatureTag(string? tag) =>
        string.Equals(tag, Bolt12Constants.InvoiceRequestSignatureTag, StringComparison.Ordinal)
     || string.Equals(tag, Bolt12Constants.InvoiceSignatureTag, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="kind"/> may sign under <paramref name="tag"/>: a payer key signs only invoice_requests,
    /// the node key and our blinded keys sign only invoices.
    /// </summary>
    public static bool IsSignatureTagFor(Bolt12SigningKeyKind kind, string? tag) => kind switch
    {
        Bolt12SigningKeyKind.Payer => string.Equals(tag, Bolt12Constants.InvoiceRequestSignatureTag,
                                                    StringComparison.Ordinal),
        Bolt12SigningKeyKind.Node or Bolt12SigningKeyKind.BlindedRecipient =>
            string.Equals(tag, Bolt12Constants.InvoiceSignatureTag, StringComparison.Ordinal),
        _ => false
    };

    /// <summary>
    /// The BIP-340 signature of <c>H(tag, merkleRoot)</c> by <paramref name="key"/>, with 32 bytes of fresh auxiliary
    /// randomness from the crypto provider (BIP-340's recommended side-channel hardening, NL-455). Checks nothing: the
    /// signer checks the tag before it calls this.
    /// </summary>
    public static byte[] SignBip340(ECPrivKey key, string tag, ReadOnlySpan<byte> merkleRoot)
    {
        var auxRandomness = new byte[AuxRandomnessLen];
        try
        {
            using (var cryptoProvider = CryptoFactory.GetCryptoProvider())
                cryptoProvider.RandomBytes(auxRandomness);

            return SignBip340(key, tag, merkleRoot, auxRandomness);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(auxRandomness);
        }
    }

    /// <summary>
    /// The BIP-340 signature of <c>H(tag, merkleRoot)</c> by <paramref name="key"/> with the given 32 bytes of
    /// auxiliary randomness. 32 zero bytes give CLN's deterministic signature (libsecp256k1 with no aux data), which
    /// reproduces the BOLT 12 signature-test.json vector; production signing uses the overload with fresh randomness.
    /// </summary>
    internal static byte[] SignBip340(ECPrivKey key, string tag, ReadOnlySpan<byte> merkleRoot,
                                      byte[] auxRandomness)
    {
        ArgumentNullException.ThrowIfNull(auxRandomness);
        if (auxRandomness.Length != AuxRandomnessLen)
            throw new ArgumentException($"BIP-340 aux randomness must be {AuxRandomnessLen} bytes.",
                                        nameof(auxRandomness));

        var digest = Compute(tag, merkleRoot);
        var signature = key.SignBIP340(digest, auxRandomness);
        var bytes = new byte[CryptoConstants.MaxSignatureSize];
        signature.WriteToSpan(bytes);
        return bytes;
    }
}