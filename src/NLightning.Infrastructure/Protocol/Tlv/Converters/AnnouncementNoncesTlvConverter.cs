using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>channel_reestablish_tlvs</c> type 7 (<c>announcement_nonces</c>, taproot gossip, BOLTs PR #1059):
/// [<c>66*byte</c>:<c>announcement_node_pubnonce</c>] [<c>66*byte</c>:<c>announcement_bitcoin_pubnonce</c>].
/// </summary>
public sealed class AnnouncementNoncesTlvConverter : ITlvConverter<AnnouncementNoncesTlv>
{
    public BaseTlv ConvertToBase(AnnouncementNoncesTlv tlv)
    {
        return tlv;
    }

    public AnnouncementNoncesTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TaprootTlvConstants.AnnouncementNonces)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != AnnouncementNoncesTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
            throw new InvalidCastException(
                $"Invalid length: announcement_nonces holds {AnnouncementNoncesTlv.ValueLength} bytes, not "
              + $"{baseTlv.Value.Length}");

        return new AnnouncementNoncesTlv(new MusigPublicNonce(baseTlv.Value[..MusigConstants.PublicNonceLen]),
                                         new MusigPublicNonce(baseTlv.Value[MusigConstants.PublicNonceLen..]));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as AnnouncementNoncesTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(AnnouncementNoncesTlv)}"));
    }
}