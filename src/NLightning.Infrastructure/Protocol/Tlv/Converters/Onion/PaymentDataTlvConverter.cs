using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Infrastructure.Converters;

public class PaymentDataTlvConverter : ITlvConverter<PaymentDataTlv>
{
    private const int MinLength = CryptoConstants.SecretLen;
    private const int MaxLength = CryptoConstants.SecretLen + TruncatedInt.MaxTu64Length;

    public BaseTlv ConvertToBase(PaymentDataTlv tlv)
    {
        byte[] secret = tlv.PaymentSecret;
        var total = TruncatedInt.EncodeTu64(tlv.TotalMsat.MilliSatoshi);

        var value = new byte[CryptoConstants.SecretLen + total.Length];
        secret.AsSpan(0, CryptoConstants.SecretLen).CopyTo(value);
        total.CopyTo(value, CryptoConstants.SecretLen);

        return new BaseTlv(tlv.Type, value);
    }

    public PaymentDataTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.PaymentData)
            throw new InvalidCastException("Invalid TLV type");

        var value = baseTlv.Value;
        if (baseTlv.Length != (ulong)value.Length || value.Length < MinLength || value.Length > MaxLength)
            throw new InvalidCastException("Invalid length");

        if (!TruncatedInt.TryDecodeTu64(value.AsSpan(CryptoConstants.SecretLen), out var totalMsat))
            throw new InvalidCastException("Invalid total_msat encoding");

        return new PaymentDataTlv(new Secret(value[..CryptoConstants.SecretLen]),
                                  LightningMoney.MilliSatoshis(totalMsat));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as PaymentDataTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {nameof(PaymentDataTlv)}"));
    }
}