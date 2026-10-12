namespace NLightning.Domain.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// Represents routing information for a payment
/// </summary>
/// <param name="compactPubKey">The public key of the node</param>
/// <param name="shortChannelId">The short channel id of the channel</param>
/// <param name="feeBaseMsat">The base fee in millisatoshis (u32)</param>
/// <param name="feeProportionalMillionths">The proportional fee in millionths (u32)</param>
/// <param name="cltvExpiryDelta">The CLTV expiry delta (u16)</param>
public sealed class RoutingInfo(
    CompactPubKey compactPubKey,
    ShortChannelId shortChannelId,
    uint feeBaseMsat,
    uint feeProportionalMillionths,
    ushort cltvExpiryDelta)
{
    /// <summary>
    /// The public key of the node
    /// </summary>
    public CompactPubKey CompactPubKey { get; } = compactPubKey;

    /// <summary>
    /// The short channel id of the channel
    /// </summary>
    public ShortChannelId ShortChannelId { get; } = shortChannelId;

    /// <summary>
    /// The base fee in millisatoshis
    /// </summary>
    public uint FeeBaseMsat { get; } = feeBaseMsat;

    /// <summary>
    /// The proportional fee in millionths
    /// </summary>
    public uint FeeProportionalMillionths { get; } = feeProportionalMillionths;

    /// <summary>
    /// The CLTV expiry delta
    /// </summary>
    public ushort CltvExpiryDelta { get; } = cltvExpiryDelta;
}