namespace NLightning.Domain.Protocol.Onion.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// A decrypted route-blinding <c>encrypted_data_tlv</c> stream (BOLT 4 "Inside <c>encrypted_recipient_data</c>"):
/// what the creator of a blinded path tells one hop of it.
/// </summary>
/// <remarks>
/// Pure data: which records a hop requires depends on its position and is checked by
/// <see cref="Validators.BlindedRecipientDataValidator"/>. Unknown odd records are kept verbatim (in ascending type
/// order) so a decode/encode round trip is byte-exact; unknown even records never reach this type (the decoder
/// rejects them, as BOLT 4 requires).
/// </remarks>
public sealed class BlindedRecipientData
{
    /// <summary>padding (type 1), kept verbatim (its content is ignored). Null when absent.</summary>
    public ReadOnlyMemory<byte>? Padding { get; init; }

    /// <summary>short_channel_id (type 2): the outgoing channel.</summary>
    public ShortChannelId? ShortChannelId { get; init; }

    /// <summary>next_node_id (type 4): the next node, when no <see cref="ShortChannelId"/> is given.</summary>
    public CompactPubKey? NextNodeId { get; init; }

    /// <summary>path_id (type 6): the recipient's own data, only in its own (final) hop.</summary>
    public ReadOnlyMemory<byte>? PathId { get; init; }

    /// <summary>next_path_key_override (type 8): the path_key to send to the next hop instead of the derived one.
    /// </summary>
    public CompactPubKey? NextPathKeyOverride { get; init; }

    /// <summary>payment_relay (type 10).</summary>
    public BlindedPaymentRelay? PaymentRelay { get; init; }

    /// <summary>payment_constraints (type 12).</summary>
    public BlindedPaymentConstraints? PaymentConstraints { get; init; }

    /// <summary>allowed_features (type 14): the raw feature bits; null when absent (read as an empty array).</summary>
    public ReadOnlyMemory<byte>? AllowedFeatures { get; init; }

    /// <summary>Unknown odd records (type, value), in ascending type order.</summary>
    public IReadOnlyList<KeyValuePair<ulong, ReadOnlyMemory<byte>>> UnknownOddRecords { get; init; } = [];

    /// <summary>
    /// Whether <see cref="AllowedFeatures"/> sets any bit. No feature is defined for blinded payments yet, so every
    /// set bit is unknown and BOLT 4 requires the hop to fail the payment.
    /// </summary>
    public bool HasAnyAllowedFeature =>
        AllowedFeatures is { } features && features.Span.IndexOfAnyExcept((byte)0) >= 0;
}