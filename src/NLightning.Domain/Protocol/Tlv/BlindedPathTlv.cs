namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Blinded Path TLV.
/// </summary>
/// <remarks>
/// The blinded path TLV is used in the UpdateAddHtlcMessage to communicate the blinded path key.
/// The key is only checked for length and a 0x02/0x03 prefix; it is NOT validated as a point on the curve.
/// Consumers MUST validate it through <see cref="Crypto.Interfaces.ISecp256K1Math"/> before use and fail the HTLC
/// with <c>invalid_onion_blinding</c> (BOLT 4) when it is invalid.
/// </remarks>
public class BlindedPathTlv : BaseTlv
{
    /// <summary>
    /// The blinded path key
    /// </summary>
    public CompactPubKey PathKey { get; }

    public BlindedPathTlv(CompactPubKey pathKey) : base(TlvConstants.BlindedPath)
    {
        PathKey = pathKey;

        Value = PathKey;
        Length = Value.Length;
    }
}