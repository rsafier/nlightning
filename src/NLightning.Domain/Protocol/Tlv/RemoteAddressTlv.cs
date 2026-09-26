namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Gossip.Addresses;

/// <summary>
/// The BOLT 1 <c>init</c> <c>remote_addr</c> TLV: one BOLT 7 address descriptor (the address the peer sees us at).
/// </summary>
/// <remarks>
/// <see cref="BaseTlv.Value"/> holds the descriptor's wire bytes (<see cref="AddressDescriptorCodec.Encode"/>), so
/// <see cref="BaseTlv.Length"/> is 7 (IPv4), 19 (IPv6), 13 (Tor v2), 38 (Tor v3) or <c>4 + hostname length</c> (DNS)
/// (NL-008).
/// </remarks>
public class RemoteAddressTlv : BaseTlv
{
    /// <summary>
    /// The descriptor.
    /// </summary>
    public AddressDescriptor Descriptor { get; }

    /// <summary>
    /// The descriptor type byte (1-5).
    /// </summary>
    public byte AddressType => (byte)Descriptor.Type;

    /// <summary>
    /// The host as text (<see cref="AddressDescriptor.Host"/>; Tor addresses as <c>&lt;base32&gt;.onion</c>).
    /// </summary>
    public string Address => Descriptor.Host;

    /// <summary>
    /// The port.
    /// </summary>
    public ushort Port => Descriptor.Port;

    public RemoteAddressTlv(AddressDescriptor descriptor) : base(TlvConstants.RemoteAddress)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        Value = AddressDescriptorCodec.Encode(descriptor);
        Length = Value.Length;
    }

    /// <summary>
    /// Creates the TLV from a type and a host string (see <see cref="AddressDescriptor.FromHost"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The type is not 1-5, or the host does not parse as that type.</exception>
    public RemoteAddressTlv(byte addressType, string address, ushort port)
        : this(AddressDescriptor.IsKnownType(addressType)
                   ? AddressDescriptor.FromHost((AddressDescriptorType)addressType, address, port)
                   : throw new ArgumentException("Invalid address type", nameof(addressType)))
    {
    }
}