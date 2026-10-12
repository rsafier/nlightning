namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Crypto.ValueObjects;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 12 (<c>current_path_key</c>), the route-blinding path key for this hop.
/// </summary>
/// <remarks>
/// The value is only checked for length and a 0x02/0x03 prefix; it is NOT validated as a point on the curve.
/// Consumers (the onion peeler / route-blinding logic) MUST validate it through
/// <see cref="Crypto.Interfaces.ISecp256K1Math"/> before use and fail the hop with <c>invalid_onion_blinding</c>
/// (BOLT 4) when it is invalid.
/// </remarks>
public class CurrentPathKeyTlv : BaseTlv
{
    /// <summary>
    /// The current path key.
    /// </summary>
    public CompactPubKey PathKey { get; }

    public CurrentPathKeyTlv(CompactPubKey pathKey) : base(OnionPayloadTlvTypes.CurrentPathKey)
    {
        PathKey = pathKey;

        Value = pathKey;
        Length = Value.Length;
    }
}