using System.Buffers.Binary;
using System.Text;

namespace NLightning.Domain.Protocol.Payloads;

using Crypto.ValueObjects;
using Gossip.Addresses;
using GossipV2;
using Interfaces;
using Tlvs = GossipV2.GossipV2Constants.NodeAnnouncement2;

/// <summary>
/// The payload of <c>node_announcement_2</c> (taproot gossip, BOLTs PR #1059, type 269): a pure TLV stream of
/// <c>features</c> 0, <c>color</c> 1, <c>block_height</c> 2, <c>alias</c> 3 (UTF-8, at most 32 bytes),
/// <c>node_id</c> 4, the address lists <c>ipv4_addrs</c> 5 (u32 addr || u16 port each), <c>ipv6_addrs</c> 7,
/// <c>tor_v3_addrs</c> 9 (35-byte onion || u16 port) and <c>dns_hostnames</c> 11 (u16 len || hostname || u16 port),
/// and the node's BIP 340 <c>signature</c> 240.
/// </summary>
/// <remarks>
/// The records are kept as received (<see cref="Stream"/>). A list whose length is not a whole number of entries, an
/// alias longer than 32 bytes or invalid UTF-8, or a missing <c>features</c>/<c>block_height</c>/<c>node_id</c>/
/// <c>signature</c> fails the parse; a well-framed address that is not usable (a DNS name with bad characters) is
/// dropped from <see cref="Addresses"/>, and port-0 addresses are kept on the wire but left out of
/// <see cref="Addresses"/> (the draft's "SHOULD ignore that address").
/// </remarks>
public sealed class NodeAnnouncement2Payload : IMessagePayload
{
    private const int Ipv4EntryLength = AddressDescriptor.IPv4AddressLength + sizeof(ushort);
    private const int Ipv6EntryLength = AddressDescriptor.IPv6AddressLength + sizeof(ushort);
    private const int TorV3EntryLength = AddressDescriptor.TorV3AddressLength + sizeof(ushort);

    /// <summary>The length of the <c>color</c> record.</summary>
    public const int ColorLength = 3;

    /// <summary>The record types the message defines (an unknown even type fails it).</summary>
    public static readonly IReadOnlySet<ulong> KnownTypes = new HashSet<ulong>
    {
        Tlvs.Features, Tlvs.Color, Tlvs.BlockHeight, Tlvs.Alias, Tlvs.NodeId, Tlvs.Ipv4Addresses,
        Tlvs.Ipv6Addresses, Tlvs.TorV3Addresses, Tlvs.DnsHostnames, Tlvs.Signature
    };

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    private readonly byte[] _features;
    private readonly byte[]? _color;
    private readonly byte[]? _alias;

    private NodeAnnouncement2Payload(PureTlvStream stream)
    {
        Stream = stream;
        _features = (stream.Get(Tlvs.Features) ?? throw PureTlvFields.Missing(Tlvs.Features)).ToArray();
        _color = PureTlvFields.Fixed(stream, Tlvs.Color, ColorLength)?.ToArray();
        BlockHeight = PureTlvFields.U32(stream, Tlvs.BlockHeight) ?? throw PureTlvFields.Missing(Tlvs.BlockHeight);
        if (stream.Get(Tlvs.Alias) is { } alias)
        {
            if (alias.Length > Tlvs.MaxAliasLength)
                throw new FormatException($"The alias is {alias.Length} bytes, more than {Tlvs.MaxAliasLength}.");
            try
            {
                s_strictUtf8.GetString(alias.Span);
            }
            catch (DecoderFallbackException e)
            {
                throw new FormatException("The alias is not valid UTF-8.", e);
            }

            _alias = alias.ToArray();
        }

        NodeId = PureTlvFields.RequiredPoint(stream, Tlvs.NodeId);
        var addresses = new List<AddressDescriptor>();
        ReadFixedEntries(stream, Tlvs.Ipv4Addresses, AddressDescriptorType.IPv4, Ipv4EntryLength, addresses);
        ReadFixedEntries(stream, Tlvs.Ipv6Addresses, AddressDescriptorType.IPv6, Ipv6EntryLength, addresses);
        ReadFixedEntries(stream, Tlvs.TorV3Addresses, AddressDescriptorType.TorV3, TorV3EntryLength, addresses);
        ReadDnsEntries(stream, addresses);
        AllAddresses = addresses;
        Signature = new CompactSignature(PureTlvFields.RequiredFixed(stream, Tlvs.Signature,
                                                                     GossipV2Constants.SignatureLength).ToArray());
    }

    /// <summary>The records as received or built, in wire order.</summary>
    public PureTlvStream Stream { get; }

    /// <summary>The node's feature bits (possibly empty).</summary>
    public ReadOnlyMemory<byte> Features => _features;

    /// <summary>The RGB color, or null when the record is absent.</summary>
    public ReadOnlyMemory<byte>? Color => _color is null ? null : (ReadOnlyMemory<byte>?)_color;

    /// <summary>The announcement's timestamp: a block height.</summary>
    public uint BlockHeight { get; }

    /// <summary>The alias bytes (valid UTF-8, at most 32 bytes), or null when the record is absent.</summary>
    public ReadOnlyMemory<byte>? Alias => _alias is null ? null : (ReadOnlyMemory<byte>?)_alias;

    /// <summary>The announcing node.</summary>
    public CompactPubKey NodeId { get; }

    /// <summary>Every well-formed address on the wire, port 0 included.</summary>
    public IReadOnlyList<AddressDescriptor> AllAddresses { get; }

    /// <summary>The usable addresses: <see cref="AllAddresses"/> without port 0.</summary>
    public IEnumerable<AddressDescriptor> Addresses => AllAddresses.Where(a => a.Port != 0);

    /// <summary>The node's BIP 340 signature.</summary>
    public CompactSignature Signature { get; }

    /// <summary>The bytes the signature covers.</summary>
    public byte[] GetSignedData() => Stream.GetSignedBytes();

    /// <summary>The BIP 340 message: <c>MsgHash("node_announcement_2", "signature", m)</c>.</summary>
    public Hash GetSignatureHash() =>
        GossipV2MsgHash.ComputeSignatureHash(GossipV2Constants.NodeAnnouncement2Name, Stream);

    /// <summary>The wire bytes of the payload (without the message type).</summary>
    public byte[] GetBytes() => Stream.GetBytes();

    /// <summary>Returns a copy with <paramref name="signature"/>; the rest keeps its bytes.</summary>
    public NodeAnnouncement2Payload WithSignature(CompactSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != GossipV2Constants.SignatureLength)
            throw new ArgumentException($"A BIP 340 signature is {GossipV2Constants.SignatureLength} bytes.",
                                        nameof(signature));

        return new NodeAnnouncement2Payload(Stream.With(new PureTlvRecord(Tlvs.Signature, signature.Value)));
    }

    /// <summary>
    /// Builds an announcement (our own) with an all-zero signature, to be signed with <see cref="WithSignature"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The alias is too long or the color is not 3 bytes.</exception>
    public static NodeAnnouncement2Payload Create(ReadOnlySpan<byte> features, uint blockHeight, CompactPubKey nodeId,
                                                  ReadOnlySpan<byte> color, ReadOnlySpan<byte> alias,
                                                  IEnumerable<AddressDescriptor> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (!color.IsEmpty && color.Length != ColorLength)
            throw new ArgumentException("The color is 3 bytes.", nameof(color));
        if (alias.Length > Tlvs.MaxAliasLength)
            throw new ArgumentException($"The alias is at most {Tlvs.MaxAliasLength} bytes.", nameof(alias));

        var records = new List<PureTlvRecord> { new(Tlvs.Features, features) };
        if (!color.IsEmpty)
            records.Add(new PureTlvRecord(Tlvs.Color, color));
        records.Add(PureTlvFields.U32Record(Tlvs.BlockHeight, blockHeight));
        if (!alias.IsEmpty)
            records.Add(new PureTlvRecord(Tlvs.Alias, alias));
        records.Add(new PureTlvRecord(Tlvs.NodeId, nodeId));

        var list = addresses.Where(a => a.Port != 0).ToList();
        AddFixedEntries(records, Tlvs.Ipv4Addresses, list.Where(a => a.Type == AddressDescriptorType.IPv4));
        AddFixedEntries(records, Tlvs.Ipv6Addresses, list.Where(a => a.Type == AddressDescriptorType.IPv6));
        AddFixedEntries(records, Tlvs.TorV3Addresses, list.Where(a => a.Type == AddressDescriptorType.TorV3));
        var dns = list.Where(a => a.Type == AddressDescriptorType.Dns).ToList();
        if (dns.Count > 0)
        {
            var bytes = new List<byte>();
            foreach (var address in dns)
            {
                var host = address.Address;
                var length = new byte[sizeof(ushort)];
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)host.Length);
                bytes.AddRange(length);
                bytes.AddRange(host);
                bytes.AddRange(PortBytes(address.Port));
            }

            records.Add(new PureTlvRecord(Tlvs.DnsHostnames, bytes.ToArray()));
        }

        records.Add(new PureTlvRecord(Tlvs.Signature, new byte[GossipV2Constants.SignatureLength]));
        return new NodeAnnouncement2Payload(new PureTlvStream(records));
    }

    /// <summary>Builds the payload from parsed records.</summary>
    /// <exception cref="FormatException">A required record is missing or a known one is malformed.</exception>
    public static NodeAnnouncement2Payload FromStream(PureTlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new NodeAnnouncement2Payload(stream);
    }

    /// <summary>Parses the payload (without the message type).</summary>
    /// <exception cref="FormatException">The stream or a known record is malformed, or a required record is missing.</exception>
    public static NodeAnnouncement2Payload Parse(ReadOnlySpan<byte> payload) =>
        new(PureTlvStream.Parse(payload, KnownTypes));

    private static void ReadFixedEntries(PureTlvStream stream, ulong type, AddressDescriptorType addressType,
                                         int entryLength, List<AddressDescriptor> addresses)
    {
        if (stream.Get(type) is not { } value)
            return;
        if (value.Length % entryLength != 0)
            throw new FormatException($"TLV type {type} is not a whole number of {entryLength}-byte entries.");

        var span = value.Span;
        for (var offset = 0; offset < span.Length; offset += entryLength)
        {
            var addressLength = entryLength - sizeof(ushort);
            var port = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + addressLength)..]);
            addresses.Add(new AddressDescriptor(addressType, span.Slice(offset, addressLength), port));
        }
    }

    private static void ReadDnsEntries(PureTlvStream stream, List<AddressDescriptor> addresses)
    {
        if (stream.Get(Tlvs.DnsHostnames) is not { } value)
            return;

        var span = value.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            if (span.Length - offset < sizeof(ushort))
                throw new FormatException("A dns_hostname entry is truncated.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            offset += sizeof(ushort);
            if (span.Length - offset < length + sizeof(ushort))
                throw new FormatException("A dns_hostname entry is truncated.");

            var host = span.Slice(offset, length);
            offset += length;
            var port = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            offset += sizeof(ushort);

            // A well-framed but unusable name (empty, too long, not a hostname) is ignored, not fatal
            if (AddressDescriptor.TryValidate(AddressDescriptorType.Dns, host, out _))
                addresses.Add(new AddressDescriptor(AddressDescriptorType.Dns, host, port));
        }
    }

    private static void AddFixedEntries(List<PureTlvRecord> records, ulong type,
                                        IEnumerable<AddressDescriptor> addresses)
    {
        var bytes = new List<byte>();
        foreach (var address in addresses)
        {
            bytes.AddRange(address.Address);
            bytes.AddRange(PortBytes(address.Port));
        }

        if (bytes.Count > 0)
            records.Add(new PureTlvRecord(type, bytes.ToArray()));
    }

    private static byte[] PortBytes(ushort port)
    {
        var bytes = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, port);
        return bytes;
    }
}