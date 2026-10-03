using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.LiquidityAds;

using Constants;
using Models;
using Protocol.Payloads;
using Protocol.Tlv;

/// <summary>
/// Liquidity ads rates in a <c>node_announcement</c> (BOLT PR #1153 <c>option_will_fund</c>, NL-771). Eclair 0.14.3
/// writes a TLV stream after the addresses (<see cref="NodeAnnouncementPayload.ExtraData"/>, covered by the signature)
/// whose record <see cref="LiquidityAdsConstants.TlvType"/> holds the seller's <c>will_fund_rates</c>.
/// </summary>
public static class NodeAnnouncementRates
{
    /// <summary>
    /// The extra data of our <c>node_announcement</c> that announces <paramref name="rates"/>: the one record
    /// <c>fd053b || bigsize length || will_fund_rates</c>; empty for null (we do not sell).
    /// </summary>
    public static byte[] EncodeExtraData(WillFundRates? rates)
    {
        if (rates is null)
            return [];

        var value = LiquidityAdsCodec.EncodeWillFundRates(rates);
        var typeLength = BigSizeCodec.GetLength(LiquidityAdsConstants.TlvType);
        var lengthLength = BigSizeCodec.GetLength((ulong)value.Length);
        var extraData = new byte[typeLength + lengthLength + value.Length];
        var offset = BigSizeCodec.Write(LiquidityAdsConstants.TlvType, extraData);
        offset += BigSizeCodec.Write((ulong)value.Length, extraData.AsSpan(offset));
        value.CopyTo(extraData, offset);
        return extraData;
    }

    /// <summary>
    /// Reads a seller's rates from the extra data of its <c>node_announcement</c> (a TLV stream). Quiet: anything that
    /// is not a valid stream (types not strictly increasing, a bigsize that is not minimal, a length past the end, an
    /// unknown even type) or a rates record that does not decode gives false; unknown odd records are skipped.
    /// </summary>
    /// <returns>True with the rates when the stream is valid and carries them; false otherwise.</returns>
    public static bool TryRead(ReadOnlySpan<byte> extraData, [NotNullWhen(true)] out WillFundRates? rates)
    {
        rates = null;
        WillFundRates? found = null;
        ulong? previousType = null;
        while (!extraData.IsEmpty)
        {
            if (!BigSizeCodec.TryRead(extraData, out var type, out var typeLength))
                return false;

            extraData = extraData[typeLength..];
            if (!BigSizeCodec.TryRead(extraData, out var length, out var lengthLength))
                return false;

            extraData = extraData[lengthLength..];
            if (length > (ulong)extraData.Length || (previousType is { } previous && type <= previous))
                return false;

            var value = extraData[..(int)length];
            extraData = extraData[(int)length..];
            previousType = type;

            if (type == LiquidityAdsConstants.TlvType)
            {
                if (!LiquidityAdsCodec.TryDecodeWillFundRates(value, out found))
                    return false;
            }
            else if (type % 2 == 0)
            {
                return false;
            }
        }

        rates = found;
        return rates is not null;
    }

    /// <summary>
    /// Reads a seller's rates from a whole <c>node_announcement</c> payload (without the message type, as
    /// <c>GraphNode.RawAnnouncement</c> stores it); false when it does not parse or carries no valid rates.
    /// </summary>
    public static bool TryReadFromAnnouncement(ReadOnlySpan<byte> rawAnnouncement,
                                               [NotNullWhen(true)] out WillFundRates? rates)
    {
        rates = null;
        return NodeAnnouncementPayload.TryParse(rawAnnouncement, out var announcement)
            && TryRead(announcement.ExtraData.Span, out rates);
    }
}