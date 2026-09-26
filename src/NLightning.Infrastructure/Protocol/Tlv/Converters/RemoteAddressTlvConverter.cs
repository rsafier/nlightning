using System.Diagnostics.CodeAnalysis;
using NLightning.Domain.Gossip.Addresses;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Tlv;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

/// <summary>
/// Converts the <c>init</c> <c>remote_addr</c> TLV with the strict BOLT 7 address descriptor codec
/// (<see cref="AddressDescriptorCodec"/>, NL-008): the value is exactly one descriptor of type 1-5 with its exact
/// length (Tor v3 35 + 2, DNS <c>1 + len + 2</c> after the type byte).
/// </summary>
public class RemoteAddressTlvConverter : ITlvConverter<RemoteAddressTlv>
{
    public BaseTlv ConvertToBase(RemoteAddressTlv tlv)
    {
        ArgumentNullException.ThrowIfNull(tlv);
        return new BaseTlv(tlv.Type, AddressDescriptorCodec.Encode(tlv.Descriptor));
    }

    public RemoteAddressTlv ConvertFromBase(BaseTlv baseTlv)
    {
        ArgumentNullException.ThrowIfNull(baseTlv);
        if (baseTlv.Type != TlvConstants.RemoteAddress)
            throw new InvalidCastException("Invalid TLV type");

        try
        {
            return new RemoteAddressTlv(AddressDescriptorCodec.DecodeSingle(baseTlv.Value));
        }
        catch (FormatException e)
        {
            throw new InvalidCastException($"Invalid remote_addr: {e.Message}", e);
        }
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as RemoteAddressTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {nameof(RemoteAddressTlv)}"));
    }
}