using System.Security.Cryptography;
using System.Text;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Offers;

using Domain.Crypto.Constants;
using Domain.Offers.Constants;
using Domain.Offers.Enums;

/// <summary>
/// The BOLT 12 tagged hash ("Signature Calculation"): <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>,
/// the check that a tag is a BOLT 12 signature tag for a key, and the BIP-340 signature over it.
/// </summary>
internal static class Bolt12TaggedHash
{
    // BIP-340 auxiliary randomness: 32 zero bytes, as CLN (libsecp256k1 with no aux data), so a signature is
    // deterministic and reproduces the BOLT 12 signature-test.json vector
    private static readonly ReadOnlyMemory<byte> s_zeroAuxRandomness = new byte[32];

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
    /// The BIP-340 signature of <c>H(tag, merkleRoot)</c> by <paramref name="key"/>, with 32 zero bytes of auxiliary
    /// randomness (deterministic, as CLN). Checks nothing: the signer checks the tag before it calls this.
    /// </summary>
    public static byte[] SignBip340(ECPrivKey key, string tag, ReadOnlySpan<byte> merkleRoot)
    {
        var digest = Compute(tag, merkleRoot);
        var signature = key.SignBIP340(digest, s_zeroAuxRandomness);
        var bytes = new byte[CryptoConstants.MaxSignatureSize];
        signature.WriteToSpan(bytes);
        return bytes;
    }
}