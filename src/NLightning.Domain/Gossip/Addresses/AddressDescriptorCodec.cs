using System.Buffers.Binary;

namespace NLightning.Domain.Gossip.Addresses;

/// <summary>
/// The strict BOLT 7 <c>address descriptor</c> codec, shared by <c>node_announcement.addresses</c> and the BOLT 1
/// <c>init</c> <c>remote_addr</c> TLV (NL-008).
/// </summary>
/// <remarks>
/// Wire lengths including the type byte: IPv4 7, IPv6 19, Tor v2 13, Tor v3 38 (35 address bytes + 2 port bytes),
/// DNS <c>1 + 1 + len + 2</c>. Ports are big-endian.
/// </remarks>
public static class AddressDescriptorCodec
{
    /// <summary>
    /// Writes one descriptor (type byte first).
    /// </summary>
    public static byte[] Encode(AddressDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var buffer = new byte[descriptor.EncodedLength];
        Write(descriptor, buffer);
        return buffer;
    }

    /// <summary>
    /// Writes a <c>node_announcement.addresses</c> field under the origin rules of BOLT 7 (B7-NA-02): descriptors in
    /// ascending type order, no port 0, at most one DNS hostname, no Tor v2.
    /// </summary>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    public static byte[] EncodeList(IEnumerable<AddressDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var list = descriptors.ToList();
        var dnsCount = 0;
        AddressDescriptorType? previous = null;
        foreach (var descriptor in list)
        {
            ArgumentNullException.ThrowIfNull(descriptor, nameof(descriptors));
            if (previous is not null && descriptor.Type < previous)
                throw new ArgumentException("Address descriptors must be in ascending type order.",
                                            nameof(descriptors));
            if (descriptor.Port == 0)
                throw new ArgumentException($"Address {descriptor} has port 0.", nameof(descriptors));
            if (descriptor.Type == AddressDescriptorType.TorV2)
                throw new ArgumentException("Tor v2 onion services must not be announced.", nameof(descriptors));
            if (descriptor.Type == AddressDescriptorType.Dns && ++dnsCount > 1)
                throw new ArgumentException("At most one DNS hostname may be announced.", nameof(descriptors));

            previous = descriptor.Type;
        }

        var total = list.Sum(d => d.EncodedLength);
        if (total > ushort.MaxValue)
            throw new ArgumentException("The address list does not fit a u16 length.", nameof(descriptors));

        var buffer = new byte[total];
        var offset = 0;
        foreach (var descriptor in list)
        {
            Write(descriptor, buffer.AsSpan(offset));
            offset += descriptor.EncodedLength;
        }

        return buffer;
    }

    /// <summary>
    /// Reads exactly one descriptor that fills <paramref name="data"/> (the <c>remote_addr</c> TLV value).
    /// </summary>
    /// <exception cref="FormatException">The type is unknown, the length does not match the type exactly, or the
    /// address is invalid for its type (e.g. a non-ASCII hostname).</exception>
    public static AddressDescriptor DecodeSingle(ReadOnlySpan<byte> data)
    {
        var status = TryRead(data, out var descriptor, out var consumed, out var error);
        if (status != ReadStatus.Ok)
            throw new FormatException(error);
        if (consumed != data.Length)
            throw new FormatException($"{data.Length - consumed} unexpected bytes after the {descriptor!.Type} "
                                    + "address descriptor.");

        return descriptor!;
    }

    /// <summary>
    /// Reads a <c>node_announcement.addresses</c> field under the receiver rules of BOLT 7 (B7-NA-03):
    /// <list type="bullet">
    ///   <item>the first descriptor of an unknown type (including 0) and everything after it are ignored
    ///   (<see cref="AddressListDecodeResult.StoppedAtUnknownType"/>);</item>
    ///   <item>a known type whose bytes do not fit (<c>addrlen</c> too short) stops the parse and sets
    ///   <see cref="AddressListDecodeResult.IsMalformed"/> (the receiver SHOULD send a <c>warning</c>);</item>
    ///   <item>port-0 descriptors, Tor v2 descriptors and a hostname that is not ASCII letters, digits, '-', '_' and '.'
    ///   are dropped;</item>
    ///   <item>DNS descriptors after the first are dropped and
    ///   <see cref="AddressListDecodeResult.HasMultipleDns"/> is set (the announcement MUST NOT be forwarded).</item>
    /// </list>
    /// </summary>
    public static AddressListDecodeResult DecodeList(ReadOnlySpan<byte> data)
    {
        var addresses = new List<AddressDescriptor>();
        var offset = 0;
        var stoppedAtUnknownType = false;
        var malformed = false;
        var ignoredPortZero = 0;
        var ignoredTorV2 = 0;
        var ignoredInvalid = 0;
        var dnsCount = 0;

        while (offset < data.Length)
        {
            var status = TryRead(data[offset..], out var descriptor, out var consumed, out _);
            if (status == ReadStatus.UnknownType)
            {
                stoppedAtUnknownType = true;
                break;
            }

            if (status == ReadStatus.Truncated)
            {
                malformed = true;
                break;
            }

            offset += consumed;
            if (status == ReadStatus.InvalidAddress)
            {
                ignoredInvalid++;
                continue;
            }

            if (descriptor!.Port == 0)
            {
                ignoredPortZero++;
                continue;
            }

            if (descriptor.Type == AddressDescriptorType.TorV2)
            {
                ignoredTorV2++;
                continue;
            }

            if (descriptor.Type == AddressDescriptorType.Dns && ++dnsCount > 1)
                continue;

            addresses.Add(descriptor);
        }

        return new AddressListDecodeResult(addresses, malformed, stoppedAtUnknownType, dnsCount > 1,
                                           ignoredPortZero, ignoredTorV2, ignoredInvalid);
    }

    private static void Write(AddressDescriptor descriptor, Span<byte> destination)
    {
        var address = descriptor.Address;
        destination[0] = (byte)descriptor.Type;
        var offset = 1;
        if (descriptor.Type == AddressDescriptorType.Dns)
            destination[offset++] = (byte)address.Length;

        address.CopyTo(destination[offset..]);
        offset += address.Length;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], descriptor.Port);
    }

    private enum ReadStatus
    {
        Ok,
        UnknownType,
        Truncated,
        InvalidAddress
    }

    private static ReadStatus TryRead(ReadOnlySpan<byte> data, out AddressDescriptor? descriptor, out int consumed,
                                      out string error)
    {
        descriptor = null;
        consumed = 0;
        error = string.Empty;
        if (data.IsEmpty)
        {
            error = "Empty address descriptor.";
            return ReadStatus.Truncated;
        }

        var typeByte = data[0];
        if (!AddressDescriptor.IsKnownType(typeByte))
        {
            error = $"Unknown address descriptor type {typeByte}.";
            return ReadStatus.UnknownType;
        }

        var type = (AddressDescriptorType)typeByte;
        var offset = 1;
        int addressLength;
        switch (type)
        {
            case AddressDescriptorType.IPv4:
                addressLength = AddressDescriptor.IPv4AddressLength;
                break;
            case AddressDescriptorType.IPv6:
                addressLength = AddressDescriptor.IPv6AddressLength;
                break;
            case AddressDescriptorType.TorV2:
                addressLength = AddressDescriptor.TorV2AddressLength;
                break;
            case AddressDescriptorType.TorV3:
                addressLength = AddressDescriptor.TorV3AddressLength;
                break;
            default:
                if (data.Length < 2)
                {
                    error = "The DNS address descriptor has no hostname length.";
                    return ReadStatus.Truncated;
                }

                addressLength = data[1];
                offset = 2;
                break;
        }

        var total = offset + addressLength + 2;
        if (data.Length < total)
        {
            error = $"The {type} address descriptor needs {total} bytes, {data.Length} left.";
            return ReadStatus.Truncated;
        }

        consumed = total;
        var address = data.Slice(offset, addressLength);
        if (!AddressDescriptor.TryValidate(type, address, out var validationError))
        {
            error = validationError!;
            return ReadStatus.InvalidAddress;
        }

        descriptor = new AddressDescriptor(type, address,
                                           BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + addressLength, 2)));
        return ReadStatus.Ok;
    }
}