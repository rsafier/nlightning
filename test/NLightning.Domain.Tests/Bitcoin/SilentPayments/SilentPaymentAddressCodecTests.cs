namespace NLightning.Domain.Tests.Bitcoin.SilentPayments;

using Domain.Bitcoin.SilentPayments;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;

public class SilentPaymentAddressCodecTests
{
    private const string VectorAddress = "sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xc9pkqwv";
    private static readonly CompactPubKey s_scan = new(Convert.FromHexString(
        "0220bcfac5b99e04ad1a06ddfb016ee13582609d60b6291e98d01a9bc9a16c96d4"));
    private static readonly CompactPubKey s_spend = new(Convert.FromHexString(
        "025cc9856d6f8375350e123978daac200c260cb5b5ae83106cab90484dcd8fcf36"));

    [Fact]
    public void Given_OfficialAddress_When_DecodedAndEncoded_Then_KeysAndBytesMatch()
    {
        // Arrange
        var expected = new SilentPaymentAddress(0, s_scan, s_spend, "sp");
        // Act
        var decoded = SilentPaymentAddressCodec.Decode(VectorAddress, BitcoinNetwork.Mainnet);
        // Assert
        Assert.Equal(expected, decoded);
        Assert.Equal(VectorAddress, SilentPaymentAddressCodec.Encode(decoded));
        Assert.Equal(expected, SilentPaymentAddressCodec.Decode(VectorAddress.ToUpperInvariant(), BitcoinNetwork.Mainnet));
    }

    [Theory]
    [InlineData("mainnet", "sp")]
    [InlineData("testnet", "tsp")]
    [InlineData("testnet4", "tsp")]
    [InlineData("signet", "tsp")]
    [InlineData("mutinynet", "tsp")]
    [InlineData("regtest", "sprt")]
    public void Given_Network_When_Encoded_Then_NetworkHrpIsUsed(string network, string hrp)
    {
        // Arrange
        var chain = new BitcoinNetwork(network);
        // Act
        var address = SilentPaymentAddressCodec.Encode(s_scan, s_spend, chain);
        // Assert
        Assert.StartsWith(hrp + "1q", address);
        Assert.Equal(s_scan, SilentPaymentAddressCodec.Decode(address, chain).ScanKey);
    }

    [Fact]
    public void Given_FutureVersion_When_Decoded_Then_RequiresExplicitOptIn()
    {
        // Arrange
        var future = SilentPaymentAddressCodec.Encode(new SilentPaymentAddress(1, s_scan, s_spend, "sp"));
        // Act
        var supported = SilentPaymentAddressCodec.TryDecode(future, BitcoinNetwork.Mainnet, out _, out var reason);
        // Assert
        Assert.False(supported);
        Assert.Contains("unsupported", reason);
        Assert.Equal((byte)1, SilentPaymentAddressCodec.Decode(future, BitcoinNetwork.Mainnet, true).Version);
    }

    [Fact]
    public void Given_FutureVersionWithExtension_When_OptedIn_Then_FirstTwoKeysAreDecoded()
    {
        // Arrange
        const string address = "sp17qgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xc4quzzltf";
        // Act
        var decoded = SilentPaymentAddressCodec.Decode(address, BitcoinNetwork.Mainnet, true);
        // Assert
        Assert.Equal((byte)30, decoded.Version);
        Assert.Equal(s_scan, decoded.ScanKey);
        Assert.Equal(s_spend, decoded.SpendKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sp1q")]
    [InlineData("sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xc9pkqwx")]
    [InlineData("sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xcsaxvtw")]
    [InlineData("sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70kll9qy")]
    [InlineData("sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xcqqvv86g7")]
    [InlineData("sp1lqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xc4wndsd")]
    [InlineData("sp1qqsste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xcuskmf6")]
    [InlineData("sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xpnzwpcs")]
    public void Given_MalformedAddress_When_Decoded_Then_Refused(string address)
    {
        // Arrange / Act
        var accepted = SilentPaymentAddressCodec.TryDecode(address, BitcoinNetwork.Mainnet, out _, out _);
        // Assert
        Assert.False(accepted);
    }

    [Fact]
    public void Given_MixedCaseWrongNetworkOrOversizedAddress_When_Decoded_Then_Refused()
    {
        // Arrange / Act / Assert
        Assert.False(SilentPaymentAddressCodec.TryDecode("S" + VectorAddress[1..], BitcoinNetwork.Mainnet, out _, out _));
        Assert.False(SilentPaymentAddressCodec.TryDecode(VectorAddress, BitcoinNetwork.Regtest, out _, out var reason));
        Assert.Contains("sprt", reason);
        Assert.False(SilentPaymentAddressCodec.TryDecode(new string('q', 1024), BitcoinNetwork.Mainnet, out _, out _));
    }
}