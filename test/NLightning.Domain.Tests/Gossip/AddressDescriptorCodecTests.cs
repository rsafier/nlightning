using System.Net;
using System.Text;

namespace NLightning.Domain.Tests.Gossip;

using Domain.Gossip.Addresses;

public class AddressDescriptorCodecTests
{
    // DuckDuckGo's Tor v3 onion service: pubkey(32) || checksum 9164 (SHA3-256 checked when the vector was made) ||
    // version 0x03
    private const string TorV3Host = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    // A historic Tor v2 address (10 bytes)
    private const string TorV2Host = "3g2upl4pq6kufc4m.onion";

    public static TheoryData<string, AddressDescriptorType, string, ushort> WireVectors => new()
    {
        // type || address || port (big-endian)
        { "01" + "7f000001" + "2607", AddressDescriptorType.IPv4, "127.0.0.1", 9735 },
        { "02" + "20010db8000000000000000000000001" + "2607", AddressDescriptorType.IPv6, "2001:db8::1", 9735 },
        { "03" + "d9b547af8f8795428b8c" + "0050", AddressDescriptorType.TorV2, TorV2Host, 80 },
        { "04" + "1d04a1d04a338c6e6ae970bfabee49049d6702250984ca950c01673f4ec034ad916403" + "2607",
          AddressDescriptorType.TorV3, TorV3Host, 9735 },
        { "05" + "0b" + "6578616d706c652e636f6d" + "2607", AddressDescriptorType.Dns, "example.com", 9735 }
    };

    [Theory]
    [MemberData(nameof(WireVectors))]
    public void Given_WireVector_When_DecodingAndEncoding_Then_RoundTripsByteExact(
        string hex, AddressDescriptorType type, string host, ushort port)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var descriptor = AddressDescriptorCodec.DecodeSingle(bytes);
        var encoded = AddressDescriptorCodec.Encode(descriptor);

        // Assert
        Assert.Equal(type, descriptor.Type);
        Assert.Equal(host, descriptor.Host);
        Assert.Equal(port, descriptor.Port);
        Assert.Equal(bytes, encoded);
        Assert.Equal(bytes.Length, descriptor.EncodedLength);
    }

    [Fact]
    public void Given_TorV3Host_When_Encoding_Then_Is38BytesWith35AddressBytesAndVersion3()
    {
        // Arrange
        var descriptor = AddressDescriptor.FromHost(AddressDescriptorType.TorV3, TorV3Host, 9735);

        // Act
        var encoded = AddressDescriptorCodec.Encode(descriptor);
        var decoded = AddressDescriptorCodec.DecodeSingle(encoded);

        // Assert
        Assert.Equal(38, encoded.Length);
        Assert.Equal(4, encoded[0]);
        Assert.Equal(0x03, encoded[35]); // the onion version, last of the 35 address bytes
        Assert.Equal([0x26, 0x07], encoded[36..]);
        Assert.Equal(TorV3Host, decoded.Host);
        Assert.Equal(descriptor, decoded);
    }

    [Fact]
    public void Given_TorV3HexHost_When_Parsing_Then_EqualsTheBase32Form()
    {
        // Arrange
        var base32 = AddressDescriptor.FromHost(AddressDescriptorType.TorV3, TorV3Host, 1);
        var hex = Convert.ToHexString(base32.Address);

        // Act
        var fromHex = AddressDescriptor.FromHost(AddressDescriptorType.TorV3, hex, 1);

        // Assert
        Assert.Equal(base32, fromHex);
    }

    [Theory]
    [InlineData("01" + "7f0000" + "2607")] // IPv4 one byte short
    [InlineData("01" + "7f000001" + "2607" + "00")] // IPv4 one byte long
    [InlineData("02" + "20010db8000000000000000000000001" + "26")] // IPv6 short port
    [InlineData("04" + "00")] // Tor v3 truncated
    [InlineData("05" + "0b" + "6578616d706c652e636f6d" + "26")] // DNS short port
    [InlineData("05" + "0c" + "6578616d706c652e636f6d" + "2607")] // DNS length one too long
    [InlineData("05" + "00" + "2607")] // DNS empty hostname
    [InlineData("05" + "02" + "c3a9" + "2607")] // DNS non-ASCII
    [InlineData("05" + "03" + "610062" + "2607")] // DNS with NUL
    [InlineData("05" + "03" + "610a62" + "2607")] // DNS with a line feed
    [InlineData("05" + "03" + "612062" + "2607")] // DNS with a space
    [InlineData("05" + "03" + "612f62" + "2607")] // DNS with '/'
    [InlineData("05" + "03" + "613a62" + "2607")] // DNS with ':'
    [InlineData("00" + "2607")] // type 0
    [InlineData("06" + "0102")] // unknown type
    [InlineData("")] // empty
    public void Given_BadDescriptor_When_DecodingSingle_Then_Throws(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act / Assert
        Assert.Throws<FormatException>(() => AddressDescriptorCodec.DecodeSingle(bytes));
    }

    [Fact]
    public void Given_DnsDescriptor_When_Encoding_Then_LengthIsFourPlusHostname()
    {
        // Arrange
        var descriptor = AddressDescriptor.FromDnsHostname("node.example.org", 9735);

        // Act
        var encoded = AddressDescriptorCodec.Encode(descriptor);

        // Assert
        Assert.Equal(4 + 16, encoded.Length);
        Assert.Equal(5, encoded[0]);
        Assert.Equal(16, encoded[1]);
        Assert.Equal("node.example.org"u8.ToArray(), encoded[2..18]);
        Assert.Equal([0x26, 0x07], encoded[18..]);
    }

    [Fact]
    public void Given_InternationalHostname_When_Creating_Then_UsesPunycode()
    {
        // Act
        var descriptor = AddressDescriptor.FromDnsHostname("bücher.example", 1);

        // Assert
        Assert.Equal("xn--bcher-kva.example", descriptor.Host);
    }

    [Fact]
    public void Given_Hostname256Bytes_When_Creating_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AddressDescriptor.FromDnsHostname(new string('a', 256), 1));
    }

    [Fact]
    public void Given_AllKnownTypes_When_DecodingList_Then_KeepsOrderAndDropsTorV2()
    {
        // Arrange
        var bytes = Convert.FromHexString("01" + "7f000001" + "2607"
                                        + "02" + "20010db8000000000000000000000001" + "2607"
                                        + "03" + "d9b547af8f8795428b8c" + "0050"
                                        + "05" + "0b" + "6578616d706c652e636f6d" + "2607");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.False(result.IsMalformed);
        Assert.False(result.StoppedAtUnknownType);
        Assert.Equal(1, result.IgnoredTorV2);
        Assert.Equal([AddressDescriptorType.IPv4, AddressDescriptorType.IPv6, AddressDescriptorType.Dns],
                     result.Addresses.Select(a => a.Type));
    }

    [Fact]
    public void Given_UnknownType_When_DecodingList_Then_IgnoresItAndEverythingAfter()
    {
        // Arrange
        var bytes = Convert.FromHexString("01" + "7f000001" + "2607" + "07" + "ffff" + "01" + "0a000001" + "2607");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.True(result.StoppedAtUnknownType);
        Assert.False(result.IsMalformed);
        Assert.Single(result.Addresses);
        Assert.Equal("127.0.0.1", result.Addresses[0].Host);
    }

    [Fact]
    public void Given_ZeroTypePadding_When_DecodingList_Then_StopsThere()
    {
        // Arrange
        var bytes = Convert.FromHexString("01" + "7f000001" + "2607" + "0000");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.True(result.StoppedAtUnknownType);
        Assert.Single(result.Addresses);
    }

    [Fact]
    public void Given_PortZero_When_DecodingList_Then_DropsThatDescriptorOnly()
    {
        // Arrange
        var bytes = Convert.FromHexString("01" + "7f000001" + "0000" + "01" + "0a000001" + "2607");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.Equal(1, result.IgnoredPortZero);
        Assert.Single(result.Addresses);
        Assert.Equal("10.0.0.1", result.Addresses[0].Host);
    }

    [Fact]
    public void Given_TruncatedKnownDescriptor_When_DecodingList_Then_IsMalformedAndKeepsEarlierOnes()
    {
        // Arrange
        var bytes = Convert.FromHexString("01" + "7f000001" + "2607" + "02" + "2001");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.True(result.IsMalformed);
        Assert.Single(result.Addresses);
    }

    [Fact]
    public void Given_ControlCharacterHostname_When_DecodingList_Then_DroppedAsInvalidAndTheRestKept()
    {
        // Arrange: an IPv4 descriptor, then a DNS descriptor "a\r\nb"
        var bytes = Convert.FromHexString("01" + "7f000001" + "2607" + "05" + "04" + "610d0a62" + "2607");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.False(result.IsMalformed);
        Assert.Equal(1, result.IgnoredInvalid);
        Assert.Single(result.Addresses);
        Assert.Equal(AddressDescriptorType.IPv4, result.Addresses[0].Type);
    }

    [Theory]
    [InlineData("node-1.example.org")]
    [InlineData("_service.example")]
    [InlineData("XN--BCHER-KVA.EXAMPLE")]
    public void Given_LdhHostname_When_Validating_Then_Accepted(string hostname)
    {
        // Act
        var valid = AddressDescriptor.TryValidate(AddressDescriptorType.Dns, Encoding.ASCII.GetBytes(hostname),
                                                  out var error);

        // Assert
        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public void Given_TwoDnsDescriptors_When_DecodingList_Then_KeepsTheFirstAndFlagsIt()
    {
        // Arrange
        var bytes = Convert.FromHexString("05" + "01" + "61" + "2607" + "05" + "01" + "62" + "2607");

        // Act
        var result = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.True(result.HasMultipleDns);
        Assert.Single(result.Addresses);
        Assert.Equal("a", result.Addresses[0].Host);
    }

    [Fact]
    public void Given_ValidList_When_EncodingList_Then_RoundTrips()
    {
        // Arrange
        AddressDescriptor[] list =
        [
            AddressDescriptor.FromIpAddress(IPAddress.Parse("203.0.113.5"), 9735),
            AddressDescriptor.FromIpAddress(IPAddress.Parse("2001:db8::5"), 9735),
            AddressDescriptor.FromHost(AddressDescriptorType.TorV3, TorV3Host, 9735),
            AddressDescriptor.FromDnsHostname("node.example.org", 9735)
        ];

        // Act
        var bytes = AddressDescriptorCodec.EncodeList(list);
        var decoded = AddressDescriptorCodec.DecodeList(bytes);

        // Assert
        Assert.Equal(7 + 19 + 38 + 20, bytes.Length);
        Assert.Equal(list, decoded.Addresses);
    }

    [Fact]
    public void Given_DescendingTypes_When_EncodingList_Then_Throws()
    {
        // Arrange
        AddressDescriptor[] list =
        [
            AddressDescriptor.FromIpAddress(IPAddress.Parse("2001:db8::5"), 9735),
            AddressDescriptor.FromIpAddress(IPAddress.Parse("203.0.113.5"), 9735)
        ];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AddressDescriptorCodec.EncodeList(list));
    }

    [Fact]
    public void Given_PortZero_When_EncodingList_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AddressDescriptorCodec.EncodeList(
                                             [AddressDescriptor.FromIpAddress(IPAddress.Loopback, 0)]));
    }

    [Fact]
    public void Given_TwoDnsNames_When_EncodingList_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AddressDescriptorCodec.EncodeList(
                                             [
                                                 AddressDescriptor.FromDnsHostname("a.example", 1),
                                                 AddressDescriptor.FromDnsHostname("b.example", 1)
                                             ]));
    }

    [Fact]
    public void Given_TorV2_When_EncodingList_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AddressDescriptorCodec.EncodeList(
                                             [AddressDescriptor.FromHost(AddressDescriptorType.TorV2, TorV2Host, 80)]));
    }

    [Theory]
    [InlineData(AddressDescriptorType.IPv4, 3)]
    [InlineData(AddressDescriptorType.IPv6, 15)]
    [InlineData(AddressDescriptorType.TorV2, 11)]
    [InlineData(AddressDescriptorType.TorV3, 36)]
    [InlineData(AddressDescriptorType.Dns, 0)]
    public void Given_WrongAddressLength_When_Constructing_Then_Throws(AddressDescriptorType type, int length)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new AddressDescriptor(type, new byte[length], 1));
    }

    [Theory]
    [InlineData(AddressDescriptorType.IPv4, "2001:db8::1")]
    [InlineData(AddressDescriptorType.IPv6, "127.0.0.1")]
    [InlineData(AddressDescriptorType.TorV3, "not-an-onion.onion")]
    [InlineData(AddressDescriptorType.IPv4, "example.com")]
    public void Given_HostOfWrongType_When_Parsing_Then_Throws(AddressDescriptorType type, string host)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AddressDescriptor.FromHost(type, host, 1));
    }

    [Fact]
    public void Given_IPv6Descriptor_When_ToString_Then_BracketsTheHost()
    {
        // Act
        var text = AddressDescriptor.FromIpAddress(IPAddress.Parse("2001:db8::1"), 9735).ToString();

        // Assert
        Assert.Equal("[2001:db8::1]:9735", text);
    }
}