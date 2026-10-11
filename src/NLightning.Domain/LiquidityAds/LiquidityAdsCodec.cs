using System.Buffers.Binary;

namespace NLightning.Domain.LiquidityAds;

using Constants;
using Crypto.ValueObjects;
using Models;
using Protocol.Tlv;

/// <summary>
/// Byte-exact codec of the liquidity ads records, as Eclair 0.14.3 writes them (<c>LiquidityAds.Codecs</c>):
/// <list type="bullet">
/// <item><c>funding_rate</c>: u32 min, u32 max, u16 funding_weight, u16 fee_basis, u32 fee_base, u32
/// channel_creation_fee.</item>
/// <item><c>request_funds</c>: u64 requested sats, funding_rate, payment_details (a TLV record: bigsize type, bigsize
/// length, value).</item>
/// <item><c>will_fund</c>: funding_rate, u16 script length, script, 64-byte signature.</item>
/// <item><c>will_fund_rates</c>: u16 count, the rates, u16 payment-types length, the payment-types bitfield.</item>
/// </list>
/// Decoding is strict: every byte of the TLV value must be consumed and bigsizes must be minimal.
/// </summary>
public static class LiquidityAdsCodec
{
    public static byte[] EncodeFundingRate(FundingRate rate)
    {
        var buffer = new byte[LiquidityAdsConstants.FundingRateLength];
        WriteFundingRate(rate, buffer);
        return buffer;
    }

    public static byte[] EncodeRequestFunding(RequestFunding request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = request.PaymentDetails;
        var typeLength = BigSizeCodec.GetLength(details.Type);
        var lengthLength = BigSizeCodec.GetLength((ulong)details.Value.Length);
        var buffer = new byte[8 + LiquidityAdsConstants.FundingRateLength + typeLength + lengthLength
                            + details.Value.Length];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, request.RequestedSat);
        var offset = 8;
        WriteFundingRate(request.Rate, buffer.AsSpan(offset));
        offset += LiquidityAdsConstants.FundingRateLength;
        offset += BigSizeCodec.Write(details.Type, buffer.AsSpan(offset));
        offset += BigSizeCodec.Write((ulong)details.Value.Length, buffer.AsSpan(offset));
        details.Value.CopyTo(buffer, offset);
        return buffer;
    }

    public static byte[] EncodeWillFund(WillFund willFund)
    {
        ArgumentNullException.ThrowIfNull(willFund);
        var buffer = new byte[LiquidityAdsConstants.FundingRateLength + 2 + willFund.FundingScript.Length
                            + LiquidityAdsConstants.SignatureLength];
        WriteFundingRate(willFund.Rate, buffer);
        var offset = LiquidityAdsConstants.FundingRateLength;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)willFund.FundingScript.Length);
        offset += 2;
        willFund.FundingScript.CopyTo(buffer, offset);
        offset += willFund.FundingScript.Length;
        ((ReadOnlySpan<byte>)willFund.Signature).CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    public static byte[] EncodeWillFundRates(WillFundRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var buffer = new byte[2 + rates.Rates.Count * LiquidityAdsConstants.FundingRateLength + 2
                            + rates.EncodedPaymentTypes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)rates.Rates.Count);
        var offset = 2;
        foreach (var rate in rates.Rates)
        {
            WriteFundingRate(rate, buffer.AsSpan(offset));
            offset += LiquidityAdsConstants.FundingRateLength;
        }

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)rates.EncodedPaymentTypes.Length);
        offset += 2;
        rates.EncodedPaymentTypes.CopyTo(buffer, offset);
        return buffer;
    }

    public static bool TryDecodeRequestFunding(ReadOnlySpan<byte> data, out RequestFunding? request)
    {
        request = null;
        if (data.Length < 8 + LiquidityAdsConstants.FundingRateLength)
            return false;

        var requested = BinaryPrimitives.ReadUInt64BigEndian(data);
        var rate = ReadFundingRate(data[8..]);
        var rest = data[(8 + LiquidityAdsConstants.FundingRateLength)..];
        if (!BigSizeCodec.TryRead(rest, out var type, out var typeLength))
            return false;

        rest = rest[typeLength..];
        if (!BigSizeCodec.TryRead(rest, out var length, out var lengthLength))
            return false;

        rest = rest[lengthLength..];
        if (length != (ulong)rest.Length)
            return false;

        request = new RequestFunding(requested, rate, LiquidityPaymentDetails.Create(type, rest.ToArray()));
        return true;
    }

    public static bool TryDecodeWillFund(ReadOnlySpan<byte> data, out WillFund? willFund)
    {
        willFund = null;
        if (data.Length < LiquidityAdsConstants.FundingRateLength + 2)
            return false;

        var rate = ReadFundingRate(data);
        var offset = LiquidityAdsConstants.FundingRateLength;
        var scriptLength = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (data.Length != offset + scriptLength + LiquidityAdsConstants.SignatureLength)
            return false;

        var script = data.Slice(offset, scriptLength).ToArray();
        var signature = new CompactSignature(data[(offset + scriptLength)..].ToArray());
        willFund = new WillFund(rate, script, signature);
        return true;
    }

    public static bool TryDecodeWillFundRates(ReadOnlySpan<byte> data, out WillFundRates? rates)
    {
        rates = null;
        if (data.Length < 2)
            return false;

        var count = BinaryPrimitives.ReadUInt16BigEndian(data);
        var offset = 2;
        if (data.Length < offset + count * LiquidityAdsConstants.FundingRateLength + 2)
            return false;

        var list = new FundingRate[count];
        for (var i = 0; i < count; i++)
        {
            list[i] = ReadFundingRate(data[offset..]);
            offset += LiquidityAdsConstants.FundingRateLength;
        }

        var typesLength = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (data.Length != offset + typesLength)
            return false;

        rates = new WillFundRates(list, data[offset..].ToArray());
        return true;
    }

    private static void WriteFundingRate(FundingRate rate, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, rate.MinAmountSat);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], rate.MaxAmountSat);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], rate.FundingWeight);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], rate.FeeBasis);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], rate.FeeBaseSat);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], rate.ChannelCreationFeeSat);
    }

    private static FundingRate ReadFundingRate(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(source),
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[10..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]));
}