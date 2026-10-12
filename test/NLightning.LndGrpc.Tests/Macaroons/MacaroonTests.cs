namespace NLightning.LndGrpc.Tests.Macaroons;

using LndGrpc.Macaroons;

/// <summary>
/// The v2 binary format, the HMAC chain and the bakery v3 identifier byte-exact against the Go libraries LND links
/// (<c>gopkg.in/macaroon.v2</c> v2.0.0, <c>github.com/go-macaroon-bakery/macaroonpb</c>;
/// <c>scripts/lnd-grpc/macaroon-vectors</c>).
/// </summary>
public class MacaroonTests
{
    internal static readonly byte[] RootKey =
        Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] s_nonce = Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf");

    private const string IdHex =
        "030a10a0a1a2a3a4a5a6a7a8a9aaabacadaeaf1201301a0c0a04696e666f1204726561641a170a08696e766f69636573120472656164"
      + "12057772697465";

    internal const string PlainHex =
        "0201036c6e64023d030a10a0a1a2a3a4a5a6a7a8a9aaabacadaeaf1201301a0c0a04696e666f1204726561641a170a08696e766f6963"
      + "65731204726561641205777269746500000620ed2d3b21d550d07423818adca7435631cb958f26cf799d6136b45ac9ec1853a4";

    internal const string TimeBeforeHex =
        "0201036c6e64023d030a10a0a1a2a3a4a5a6a7a8a9aaabacadaeaf1201301a0c0a04696e666f1204726561641a170a08696e766f6963"
      + "65731204726561641205777269746500022074696d652d6265666f726520323033302d30312d30325430333a30343a30355a0000062034"
      + "085f76fd70402c56454aa06484dcf8613c8e3e644e8763072d2939e555bbb3";

    internal const string IpAddrHex =
        "0201036c6e64023d030a10a0a1a2a3a4a5a6a7a8a9aaabacadaeaf1201301a0c0a04696e666f1204726561641a170a08696e766f6963"
      + "65731204726561641205777269746500022074696d652d6265666f726520323033302d30312d30325430333a30343a30355a00021069"
      + "7061646472203132372e302e302e3100000620e788ee2a07617fe1b0e72e850c9b73f590e1a09ae3eec35f0bd5ea701aa6c2b5";

    internal static MacaroonId VectorId() =>
        new(s_nonce, "0"u8.ToArray(),
            [new MacaroonOp("invoices", "write"), new MacaroonOp("info", "read"), new MacaroonOp("invoices", "read")]);

    [Fact]
    public void Given_GoVectorOps_When_IdEncoded_Then_ItIsTheBakeryV3IdByteForByte()
    {
        // Act
        var id = VectorId().Encode();

        // Assert
        Assert.Equal(IdHex, Convert.ToHexStringLower(id));
    }

    [Fact]
    public void Given_GoVectorId_When_Decoded_Then_OpsAndStorageIdComeBack()
    {
        // Act
        var id = MacaroonId.Decode(Convert.FromHexString(IdHex));

        // Assert
        Assert.Equal(s_nonce, id.Nonce);
        Assert.Equal("0"u8.ToArray(), id.StorageId);
        Assert.Equal([new MacaroonOp("info", "read"), new MacaroonOp("invoices", "read"),
                      new MacaroonOp("invoices", "write")], id.Ops);
    }

    [Fact]
    public void Given_RootKeyAndId_When_Created_Then_ItIsGosMacaroonByteForByte()
    {
        // Act
        var macaroon = Macaroon.Create(RootKey, VectorId().Encode(), "lnd");

        // Assert
        Assert.Equal(PlainHex, Convert.ToHexStringLower(macaroon.Serialize()));
    }

    [Fact]
    public void Given_Caveats_When_Added_Then_TheChainIsGosByteForByte()
    {
        // Arrange
        var macaroon = Macaroon.Create(RootKey, VectorId().Encode(), "lnd");

        // Act
        var timeBefore = macaroon.AddFirstPartyCaveat("time-before 2030-01-02T03:04:05Z");
        var ipAddr = timeBefore.AddFirstPartyCaveat("ipaddr 127.0.0.1");

        // Assert
        Assert.Equal(TimeBeforeHex, Convert.ToHexStringLower(timeBefore.Serialize()));
        Assert.Equal(IpAddrHex, Convert.ToHexStringLower(ipAddr.Serialize()));
        Assert.Empty(macaroon.Caveats);
    }

    [Theory]
    [InlineData(PlainHex)]
    [InlineData(TimeBeforeHex)]
    [InlineData(IpAddrHex)]
    public void Given_GoMacaroon_When_Deserialized_Then_ItRoundTripsAndVerifies(string hex)
    {
        // Act
        var macaroon = Macaroon.Deserialize(Convert.FromHexString(hex));

        // Assert
        Assert.Equal("lnd", macaroon.Location);
        Assert.Equal(hex, Convert.ToHexStringLower(macaroon.Serialize()));
        Assert.True(macaroon.VerifySignature(RootKey));
        Assert.False(macaroon.VerifySignature(new byte[32]));
    }

    [Fact]
    public void Given_ATamperedCaveat_When_Verified_Then_TheSignatureFails()
    {
        // Arrange: 2030 -> 2031 in the time-before caveat
        var hex = TimeBeforeHex.Replace("323033302d", "323033312d", StringComparison.Ordinal);

        // Act
        var macaroon = Macaroon.Deserialize(Convert.FromHexString(hex));

        // Assert
        Assert.Equal("time-before 2031-01-02T03:04:05Z", macaroon.Caveats[0].Condition);
        Assert.False(macaroon.VerifySignature(RootKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("0201036c6e64")]
    [InlineData("02020100")]
    [InlineData("0202016100000605aabbccddee")]
    public void Given_MalformedBytes_When_Deserialized_Then_FormatException(string hex)
    {
        Assert.Throws<FormatException>(() => Macaroon.Deserialize(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Given_TrailingBytes_When_Deserialized_Then_FormatException()
    {
        Assert.Throws<FormatException>(() => Macaroon.Deserialize(Convert.FromHexString(PlainHex + "00")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("02")]
    [InlineData("030a00")]
    [InlineData("031201301a060a04696e666f")]
    public void Given_ABadId_When_Decoded_Then_FormatException(string hex)
    {
        Assert.Throws<FormatException>(() => MacaroonId.Decode(Convert.FromHexString(hex)));
    }
}