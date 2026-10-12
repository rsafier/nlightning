namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Node;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 21 (<c>recipient_features</c>, <c>...*byte</c>): the invoice features of a recipient reached
/// through <see cref="RecipientBlindedPathsTlv"/>, in the big-endian wire format of BOLT 9 feature fields (BOLTs
/// PR 836).
/// </summary>
public class RecipientFeaturesTlv : BaseTlv
{
    /// <summary>
    /// The feature bits as on the wire (big-endian: the last byte holds bits 0-7).
    /// </summary>
    public ReadOnlyMemory<byte> Features { get; }

    /// <summary>
    /// Creates the TLV from big-endian wire bytes (copied; empty is allowed).
    /// </summary>
    public RecipientFeaturesTlv(ReadOnlySpan<byte> features) : base(OnionPayloadTlvTypes.RecipientFeatures)
    {
        var value = features.ToArray();

        Value = value;
        Length = value.Length;
        Features = value;
    }

    /// <summary>
    /// Creates the TLV from a feature set, encoded big-endian (<see cref="FeatureSet.GetWireBytes"/>; no bit set gives
    /// an empty value).
    /// </summary>
    public RecipientFeaturesTlv(FeatureSet features)
        : this((features ?? throw new ArgumentNullException(nameof(features))).GetWireBytes() ?? [])
    { }

    /// <summary>
    /// A new <see cref="FeatureSet"/> holding exactly these bits (<see cref="FeatureSet.DeserializeFromBytes"/>).
    /// </summary>
    public FeatureSet GetFeatureSet() => FeatureSet.DeserializeFromBytes(Features.ToArray());
}