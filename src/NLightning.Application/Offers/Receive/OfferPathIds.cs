using System.Security.Cryptography;

namespace NLightning.Application.Offers.Receive;

using Domain.Protocol.Interfaces;

/// <summary>
/// The <c>path_id</c> of our hop in an offer's <c>offer_paths</c> (BOLT 4: the recipient MAY put a secret in
/// <c>path_id</c>; BOLT 12: an invoice_request for an offer with paths MUST be ignored unless it came through one of
/// them, B12-IRQ-03).
/// </summary>
/// <remarks>
/// <c>path_id = HMAC-SHA256(secret, "nltg_bolt12_offer_path" || offer_metadata)</c> with
/// <c>secret = HMAC-SHA256(node_key, "nltg_bolt12_offer_paths")</c>. The offer's random metadata is known before its
/// bytes (which contain the paths), the node secret makes the id unguessable although the metadata is public, and the
/// same key gives the same ids after a restart, so nothing is stored per path. Thread-safe.
/// </remarks>
public sealed class OfferPathIds
{
    /// <summary>The <c>path_id</c> length.</summary>
    public const int Length = 32;

    private readonly ISecureKeyManager _secureKeyManager;

    public OfferPathIds(ISecureKeyManager secureKeyManager)
    {
        ArgumentNullException.ThrowIfNull(secureKeyManager);
        _secureKeyManager = secureKeyManager;
    }

    /// <summary>
    /// The <c>path_id</c> of the paths of the offer whose <c>offer_metadata</c> is <paramref name="offerMetadata"/>.
    /// </summary>
    public byte[] Compute(ReadOnlySpan<byte> offerMetadata)
    {
        return _secureKeyManager.ComputeOfferPathId(offerMetadata.ToArray());
    }

    /// <summary>
    /// Whether <paramref name="pathId"/> is the <c>path_id</c> of that offer's paths (constant time).
    /// </summary>
    public bool Matches(ReadOnlySpan<byte> pathId, ReadOnlySpan<byte> offerMetadata) =>
        pathId.Length == Length && CryptographicOperations.FixedTimeEquals(pathId, Compute(offerMetadata));
}