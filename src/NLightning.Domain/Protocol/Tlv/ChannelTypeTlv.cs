namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Node;

/// <summary>
/// Channel Type TLV.
/// </summary>
/// <remarks>
/// The channels type TLV is used in the AcceptChannel2Message to communicate the channel type that should be opened.
/// </remarks>
public class ChannelTypeTlv : BaseTlv
{
    /// <summary>
    /// The channel type
    /// </summary>
    public byte[] ChannelType { get; }

    /// <summary>
    /// The channel type features
    /// </summary>
    public FeatureSet Features { get; }

    /// <summary>
    /// Creates the TLV from a channel type feature set, encoding it big-endian as BOLT 2 requires.
    /// </summary>
    /// <exception cref="ArgumentException">The feature set is empty.</exception>
    public ChannelTypeTlv(FeatureSet channelType)
        : this(channelType.GetWireBytes() ?? throw new ArgumentException("Channel type is empty", nameof(channelType)))
    {
    }

    /// <summary>
    /// Creates the TLV from the big-endian wire bytes of a channel type.
    /// </summary>
    public ChannelTypeTlv(byte[] channelType) : base(TlvConstants.ChannelType)
    {
        ChannelType = channelType;
        Features = FeatureSet.DeserializeFromBytes(channelType);

        Value = channelType;
        Length = Value.Length;
    }
}