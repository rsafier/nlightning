namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Crypto.ValueObjects;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 12 (<c>current_path_key</c>), the route-blinding path key for this hop.
/// </summary>
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