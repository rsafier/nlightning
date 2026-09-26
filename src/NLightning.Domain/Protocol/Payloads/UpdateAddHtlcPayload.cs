namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Interfaces;
using Money;
using Onion.Constants;

/// <summary>
/// Represents the payload for the update_add_htlc message.
/// </summary>
/// <remarks>
/// Initializes a new instance of the TxAckRbfPayload class.
/// </remarks>
/// <param name="channelId">The channel ID.</param>
/// <param name="onionRoutingPacket">The raw BOLT 4 onion packet; must be exactly 1366 bytes.</param>
/// <exception cref="ArgumentException">The onion routing packet is not exactly 1366 bytes.</exception>
public class UpdateAddHtlcPayload(
    LightningMoney amount,
    ChannelId channelId,
    uint cltvExpiry,
    ulong id,
    ReadOnlyMemory<byte> paymentHash,
    ReadOnlyMemory<byte> onionRoutingPacket)
    : IChannelMessagePayload
{
    /// <summary>
    /// Gets the channel ID.
    /// </summary>
    public ChannelId ChannelId { get; } = channelId;

    /// <summary>
    /// Offer Id
    /// </summary>
    /// <remarks>
    ///  This should be 0 for the first offer for the channel and must be incremented by 1 for each successive offer
    /// </remarks>
    public ulong Id { get; } = id;

    /// <summary>
    /// AmountSats offered for this Htlc
    /// </summary>
    public LightningMoney Amount { get; } = amount;

    /// <summary>
    /// The payment hash
    /// </summary>
    public ReadOnlyMemory<byte> PaymentHash { get; } = paymentHash;

    /// <summary>
    /// The Cltv Expiration
    /// </summary>
    public uint CltvExpiry { get; } = cltvExpiry;

    /// <summary>
    /// The raw onion routing packet (always exactly <see cref="OnionConstants.PacketLength"/> bytes).
    /// </summary>
    public ReadOnlyMemory<byte> OnionRoutingPacket { get; } = onionRoutingPacket.Length == OnionConstants.PacketLength
                                                                ? onionRoutingPacket
                                                                : throw new ArgumentException(
                                                                      $"Onion routing packet must be exactly {OnionConstants.PacketLength} bytes, got {onionRoutingPacket.Length}",
                                                                      nameof(onionRoutingPacket));
}