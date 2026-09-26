namespace NLightning.Domain.Protocol.Onion.Models;

using Channels.ValueObjects;
using Constants;

/// <summary>
/// One entry of the persistent onion replay set (NL-078): the packet HMAC of an incoming payment onion, the incoming
/// HTLC that first carried it and the height after which it can be forgotten (that HTLC's <c>cltv_expiry</c>).
/// </summary>
public sealed class OnionReplayEntry
{
    private readonly byte[] _hmac;

    /// <summary>The 32-byte packet HMAC.</summary>
    public ReadOnlyMemory<byte> Hmac => _hmac;

    /// <summary>The channel of the incoming HTLC that recorded the HMAC.</summary>
    public ChannelId ChannelId { get; }

    /// <summary>The id of that incoming HTLC on <see cref="ChannelId"/>.</summary>
    public ulong HtlcId { get; }

    /// <summary>The incoming HTLC's <c>cltv_expiry</c>: the entry is kept until the chain passes it.</summary>
    public uint ExpiryHeight { get; }

    /// <exception cref="ArgumentException"><paramref name="hmac"/> is not 32 bytes long.</exception>
    public OnionReplayEntry(ReadOnlySpan<byte> hmac, ChannelId channelId, ulong htlcId, uint expiryHeight)
    {
        if (hmac.Length != OnionConstants.HmacLength)
            throw new ArgumentException($"Onion HMAC must be {OnionConstants.HmacLength} bytes.", nameof(hmac));

        _hmac = hmac.ToArray();
        ChannelId = channelId;
        HtlcId = htlcId;
        ExpiryHeight = expiryHeight;
    }

    /// <summary>
    /// True when the incoming HTLC <paramref name="channelId"/>/<paramref name="htlcId"/> recorded this entry:
    /// re-processing that HTLC is not a replay.
    /// </summary>
    public bool IsOwnedBy(ChannelId channelId, ulong htlcId) => ChannelId == channelId && HtlcId == htlcId;
}