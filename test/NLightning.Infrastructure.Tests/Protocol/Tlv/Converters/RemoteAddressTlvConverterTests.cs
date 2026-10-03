namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Gossip.Addresses;
using Domain.Protocol.Constants;
using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters;

/// <summary>
/// NL-008: <c>remote_addr</c> through the strict BOLT 7 address descriptor codec, for all five types.
/// </summary>
public class RemoteAddressTlvConverterTests
{
    private const string TorV3Host = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    [Theory]
    [InlineData(1, "192.168.0.1", 9735, "01" + "c0a80001" + "2607")]
    [InlineData(2, "2001:db8::1", 9735, "02" + "20010db8000000000000000000000001" + "2607")]
    [InlineData(3, "3g2upl4pq6kufc4m.onion", 80, "03" + "d9b547af8f8795428b8c" + "0050")]
    [InlineData(4, TorV3Host, 9735,
                "04" + "1d04a1d04a338c6e6ae970bfabee49049d6702250984ca950c01673f4ec034ad916403" + "2607")]
    [InlineData(5, "node.example.org", 9735, "05" + "10" + "6e6f64652e6578616d706c652e6f7267" + "2607")]
    public void Given_EveryAddressType_When_Converting_Then_WireBytesAreExactAndRoundTrip(
        byte type, string host, ushort port, string expectedHex)
    {
        // Arrange
        var converter = new RemoteAddressTlvConverter();
        var tlv = new RemoteAddressTlv(type, host, port);

        // Act
        var baseTlv = converter.ConvertToBase(tlv);
        var back = converter.ConvertFromBase(baseTlv);

        // Assert
        Assert.Equal(TlvConstants.RemoteAddress, baseTlv.Type);
        Assert.Equal(expectedHex, Convert.ToHexStringLower(baseTlv.Value));
        Assert.Equal(expectedHex.Length / 2, (int)baseTlv.Length);
        Assert.Equal(type, back.AddressType);
        Assert.Equal(host, back.Address);
        Assert.Equal(port, back.Port);
        Assert.Equal(tlv, back);
    }

    [Fact]
    public void Given_TorV3Value_When_Decoding_Then_Reads35AddressBytes()
    {
        // Arrange: NL-008 read 36 bytes (Value[1..37]) and so swallowed the port's first byte
        var value = Convert.FromHexString(
            "04" + "1d04a1d04a338c6e6ae970bfabee49049d6702250984ca950c01673f4ec034ad916403" + "2607");

        // Act
        var tlv = new RemoteAddressTlvConverter().ConvertFromBase(new BaseTlv(TlvConstants.RemoteAddress, value));

        // Assert
        Assert.Equal(AddressDescriptorType.TorV3, tlv.Descriptor.Type);
        Assert.Equal(35, tlv.Descriptor.Address.Length);
        Assert.Equal(9735, tlv.Port);
    }

    [Fact]
    public void Given_DnsValue_When_Decoding_Then_LengthIsFourPlusHostname()
    {
        // Arrange: NL-008 expected length + 3 and rejected this spec-correct value
        var value = Convert.FromHexString("05" + "0b" + "6578616d706c652e636f6d" + "2607");

        // Act
        var tlv = new RemoteAddressTlvConverter().ConvertFromBase(new BaseTlv(TlvConstants.RemoteAddress, value));

        // Assert
        Assert.Equal("example.com", tlv.Address);
        Assert.Equal(9735, tlv.Port);
        Assert.Equal(15, (int)tlv.Length);
    }

    [Theory]
    [InlineData("01" + "c0a800" + "2607")] // IPv4 short
    [InlineData("04" + "1d04a1d04a338c6e6ae970bfabee49049d6702250984ca950c01673f4ec034ad9164" + "2607")] // Tor 34
    [InlineData("05" + "0a" + "6578616d706c652e636f6d" + "2607")] // DNS length one short
    [InlineData("06" + "00")] // unknown type
    public void Given_MalformedValue_When_Decoding_Then_InvalidCast(string hex)
    {
        // Arrange
        var baseTlv = new BaseTlv(TlvConstants.RemoteAddress, Convert.FromHexString(hex));

        // Act / Assert
        Assert.Throws<InvalidCastException>(() => new RemoteAddressTlvConverter().ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongTlvType_When_Decoding_Then_InvalidCast()
    {
        // Act / Assert
        Assert.Throws<InvalidCastException>(() => new RemoteAddressTlvConverter().ConvertFromBase(
                                                new BaseTlv(TlvConstants.Networks, [0x01])));
    }

    [Fact]
    public void Given_UnknownTypeByte_When_Constructing_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new RemoteAddressTlv(6, "x", 1));
    }
}