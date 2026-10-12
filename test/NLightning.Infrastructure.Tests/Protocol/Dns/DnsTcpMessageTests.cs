using System.Buffers.Binary;
using System.Net;

namespace NLightning.Infrastructure.Tests.Protocol.Dns;

using Infrastructure.Protocol.Dns;

public class DnsTcpMessageTests
{
    [Fact]
    public void Given_AName_When_AQueryIsBuilt_Then_ItIsAWellFormedQuestion()
    {
        // Act
        var query = DnsTcpMessage.BuildQuery(0x1234, "a.b", DnsRecordKind.Srv);

        // Assert: header (id, recursion desired, one question), the labels, SRV, IN
        Assert.Equal("1234 0100 0001 0000 0000 0000 0161 0162 00 0021 0001".Replace(" ", ""),
                     Convert.ToHexString(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".a")]
    [InlineData("a..b")]
    public void Given_ABadName_When_AQueryIsBuilt_Then_ItIsRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => DnsTcpMessage.BuildQuery(1, name, DnsRecordKind.A));
    }

    [Fact]
    public void Given_ANameWithALongLabel_When_AQueryIsBuilt_Then_ItIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DnsTcpMessage.BuildQuery(1, new string('a', 64), DnsRecordKind.A));
    }

    [Fact]
    public void Given_ANameOutsideAscii_When_AQueryIsBuilt_Then_ItIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DnsTcpMessage.BuildQuery(1, "ä.b", DnsRecordKind.A));
    }

    [Fact]
    public void Given_AResponseWithACompressedSrvTargetAndGlue_When_Parsed_Then_AllOfItComesBack()
    {
        // Arrange: the question echoed as a compression pointer in the answer's owner name; the target plain; one
        // A record in the additional section
        var message = new MemoryStream();
        message.Write([0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 1]); // id, flags, counts
        message.Write([0x01, (byte)'a', 0x01, (byte)'b', 0x00]);         // a.b.
        message.Write([0x00, 0x21, 0x00, 0x01]);                         // SRV IN
        message.Write([0xC0, 0x0C]);                                     // answer: pointer to the question name
        message.Write([0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C]); // SRV IN, ttl 300
        message.Write([0x00, 0x14]);                                     // rdlength 20
        message.Write([0x00, 0x0A, 0x00, 0x0A, 0x26, 0x07]);             // priority 10, weight 10, port 9735
        message.Write([0x05, (byte)'l', (byte)'n', (byte)'1', (byte)'q', (byte)'z',
                       0x06, (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', 0x00]); // ln1qz.target
        message.Write([0x05, (byte)'l', (byte)'n', (byte)'1', (byte)'q', (byte)'z',
                       0x06, (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', 0x00]); // additional
        message.Write([0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, 8, 8, 8, 8]);

        // Act
        var ok = DnsTcpMessage.TryParseResponse(message.ToArray(), 0x1234, DnsRecordKind.Srv, out var response);

        // Assert
        Assert.True(ok);
        Assert.Equal(DnsLookupStatus.NoError, response.Status);
        var srv = Assert.Single(response.Srv);
        Assert.Equal((ushort)10, srv.Priority);
        Assert.Equal((ushort)10, srv.Weight);
        Assert.Equal((ushort)9735, srv.Port);
        Assert.Equal("ln1qz.target", srv.Target);
        Assert.Empty(response.Addresses);
        var glue = Assert.Single(response.Additional);
        Assert.Equal("ln1qz.target", glue.Name);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), glue.Address);
    }

    [Fact]
    public void Given_AnAnswerToAnAQuery_When_Parsed_Then_TheAddressesComeBack()
    {
        // Arrange
        var message = new MemoryStream();
        message.Write([0x00, 0x02, 0x84, 0x00, 0, 1, 0, 1, 0, 0, 0, 0]); // no recursion available, rcode 0
        message.Write([0x01, (byte)'a', 0x01, (byte)'b', 0x00, 0x00, 0x01, 0x00, 0x01]);
        message.Write([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, 1, 2, 3, 4]);

        // Act
        var ok = DnsTcpMessage.TryParseResponse(message.ToArray(), 2, DnsRecordKind.A, out var response);

        // Assert
        Assert.True(ok);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), Assert.Single(response.Addresses));
        Assert.Empty(response.Additional);
    }

    [Fact]
    public void Given_AnAnswerWithAnIpv6Record_When_Parsed_Then_ItIsAnAddressToo()
    {
        // Arrange
        var message = new MemoryStream();
        message.Write([0x00, 0x03, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0]);
        message.Write([0x01, (byte)'a', 0x01, (byte)'b', 0x00, 0x00, 0x1C, 0x00, 0x01]);
        message.Write([0xC0, 0x0C, 0x00, 0x1C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x10]);
        message.Write(new byte[16]);

        // Act
        var ok = DnsTcpMessage.TryParseResponse(message.ToArray(), 3, DnsRecordKind.Aaaa, out var response);

        // Assert
        Assert.True(ok);
        Assert.Equal(IPAddress.IPv6Any, Assert.Single(response.Addresses));
    }

    [Theory]
    [InlineData(2, DnsLookupStatus.ServFail)]
    [InlineData(3, DnsLookupStatus.NxDomain)]
    [InlineData(5, DnsLookupStatus.Refused)]
    [InlineData(15, DnsLookupStatus.Other)]
    public void Given_AnRcode_When_Parsed_Then_TheStatusMaps(ushort rcode, DnsLookupStatus expected)
    {
        // Arrange: header only, no records, QR set
        var message = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(0x8000 | rcode));

        // Act
        var ok = DnsTcpMessage.TryParseResponse(message, 0, DnsRecordKind.Srv, out var response);

        // Assert
        Assert.True(ok);
        Assert.Equal(expected, response.Status);
    }

    [Fact]
    public void Given_AnotherQuerysReply_When_Parsed_Then_ItIsRefused()
    {
        // Arrange
        var message = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(message, 7);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x8000);

        // Act & Assert
        Assert.False(DnsTcpMessage.TryParseResponse(message, 8, DnsRecordKind.Srv, out _));
    }

    [Fact]
    public void Given_AQueryInsteadOfAResponse_When_Parsed_Then_ItIsRefused()
    {
        // Arrange: QR clear
        var message = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x0100);

        // Act & Assert
        Assert.False(DnsTcpMessage.TryParseResponse(message, 0, DnsRecordKind.Srv, out _));
    }

    [Fact]
    public void Given_ATruncatedRecord_When_Parsed_Then_ItIsRefused()
    {
        // Arrange: an SRV record whose rdlength promises more than the message holds
        var message = new MemoryStream();
        message.Write([0x00, 0x01, 0x81, 0x80, 0, 0, 0, 1, 0, 0, 0, 0]);
        message.Write([0x01, (byte)'a', 0x00, 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00]);

        // Act & Assert
        Assert.False(DnsTcpMessage.TryParseResponse(message.ToArray(), 1, DnsRecordKind.Srv, out _));
    }

    [Fact]
    public void Given_AnEndlessPointerChain_When_Parsed_Then_ItIsRefused()
    {
        // Arrange: a name that points at itself
        var message = new MemoryStream();
        message.Write([0x00, 0x01, 0x81, 0x80, 0, 0, 0, 1, 0, 0, 0, 0]);
        message.Write([0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xC0, 0x0C]);

        // Act & Assert
        Assert.False(DnsTcpMessage.TryParseResponse(message.ToArray(), 1, DnsRecordKind.Srv, out _));
    }
}