using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;
using OnionMessages;

/// <summary>
/// A <c>payment_blinded_path</c> as it appears on the wire (BOLTs PR 836, the elements of the trampoline payload's
/// <c>recipient_blinded_paths</c>): a <c>blinded_path</c> followed by its <c>blinded_payinfo</c>.
/// </summary>
/// <remarks>
/// The introduction node is a <see cref="SciddirOrPubkey"/>, which may be an unresolved SCID and direction; use
/// <see cref="TryToBlindedPaymentPath"/> or <see cref="ToBlindedPaymentPath"/> for M5's <see cref="BlindedPaymentPath"/>.
/// Equality is by reference for the hops and features: compare encodings
/// (<see cref="Codecs.PaymentBlindedPathCodec"/>).
/// </remarks>
/// <param name="Path">The blinded path, introduction node first, recipient last.</param>
/// <param name="PayInfo">What the path costs (<c>payment_info</c>).</param>
public sealed record WireBlindedPaymentPath(WireBlindedPath Path, BlindedPayInfo PayInfo)
{
    /// <summary>
    /// The wire form of a payment path whose introduction node id is known.
    /// </summary>
    public static WireBlindedPaymentPath FromBlindedPaymentPath(BlindedPaymentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new WireBlindedPaymentPath(WireBlindedPath.FromBlindedPath(path.Path), path.PayInfo);
    }

    /// <summary>
    /// The M5 <see cref="BlindedPaymentPath"/> of this path when its <c>first_node_id</c> is a node id.
    /// </summary>
    /// <param name="paymentPath">The M5 path, or null when the introduction node is a SCID and direction.</param>
    public bool TryToBlindedPaymentPath([NotNullWhen(true)] out BlindedPaymentPath? paymentPath)
    {
        paymentPath = BlindedPathCodec.TryToBlindedPath(Path, out var blindedPath)
                          ? new BlindedPaymentPath(blindedPath, PayInfo)
                          : null;
        return paymentPath is not null;
    }

    /// <summary>
    /// The M5 <see cref="BlindedPaymentPath"/> of this path, with its introduction node resolved to
    /// <paramref name="firstNodeId"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><c>first_node_id</c> is a node id other than
    /// <paramref name="firstNodeId"/>.</exception>
    public BlindedPaymentPath ToBlindedPaymentPath(CompactPubKey firstNodeId) =>
        new(BlindedPathCodec.ToBlindedPath(Path, firstNodeId), PayInfo);
}