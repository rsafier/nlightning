using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Bitcoin.Offers;

using Domain.Crypto.Constants;

/// <summary>
/// The BOLT 12 tagged hash ("Signature Calculation"): <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>,
/// and the check that a tag is a BOLT 12 signature tag.
/// </summary>
internal static class Bolt12TaggedHash
{
    private const string SignatureTagPrefix = "lightning";
    private const string SignatureTagSuffix = "signature";

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
    /// Whether <paramref name="tag"/> has the form of a BOLT 12 signature tag, <c>"lightning" || messagename ||
    /// "signature"</c> (e.g. <c>lightninginvoice_requestsignature</c>, <c>lightninginvoicesignature</c>).
    /// </summary>
    public static bool IsSignatureTag(string? tag) =>
        tag is not null
     && tag.Length > SignatureTagPrefix.Length + SignatureTagSuffix.Length
     && tag.StartsWith(SignatureTagPrefix, StringComparison.Ordinal)
     && tag.EndsWith(SignatureTagSuffix, StringComparison.Ordinal);
}