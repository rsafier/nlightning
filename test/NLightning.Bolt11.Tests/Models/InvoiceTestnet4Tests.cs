using NBitcoin;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Models;
using Domain.Money;
using Domain.Protocol.ValueObjects;

public class InvoiceTestnet4Tests
{
    private static readonly uint256 s_paymentHash =
        new("0001020304050607080900010203040506070809000102030405060708090102");

    private static readonly uint256 s_paymentSecret =
        new("1111111111111111111111111111111111111111111111111111111111111111");

    [Fact]
    public void Given_Testnet4Node_When_InvoiceEncodedWithFallback_Then_ItIsLntbAndDecodesAsTestnet4()
    {
        // Arrange: testnet4 shares testnet3's `tb` prefix (LND and CLN do the same, NL-012)
        var network = BitcoinNetwork.Resolve("testnet4");
        var key = new Key();
        var fallback = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Bitcoin.Instance.Testnet4);
        var invoice = new Invoice(LightningMoney.Satoshis(1_000), "testnet4", s_paymentHash, s_paymentSecret, network)
        {
            FallbackAddresses = [fallback]
        };

        // Act
        var encoded = invoice.Encode(key);
        var decoded = Invoice.Decode(encoded, network);

        // Assert
        Assert.StartsWith("lntb10u1", encoded);
        Assert.Equal(BitcoinNetwork.Testnet4, decoded.BitcoinNetwork);
        var decodedFallback = Assert.Single(decoded.FallbackAddresses!);
        Assert.StartsWith("tb1q", decodedFallback.ToString());
        Assert.Equal(fallback.ToString(), decodedFallback.ToString());
    }

    [Fact]
    public void Given_Testnet4Invoice_When_DecodedWithoutAnExpectedNetwork_Then_ItReadsAsTestnet()
    {
        // Arrange
        var key = new Key();
        var encoded = new Invoice(LightningMoney.Satoshis(1_000), "testnet4", s_paymentHash, s_paymentSecret,
                                  BitcoinNetwork.Testnet4).Encode(key);

        // Act
        var decoded = Invoice.Decode(encoded);
        var onTestnet = Invoice.Decode(encoded, BitcoinNetwork.Testnet);

        // Assert: the prefix cannot tell the two apart; only the caller's network can
        Assert.Equal(BitcoinNetwork.Testnet, decoded.BitcoinNetwork);
        Assert.Equal(BitcoinNetwork.Testnet, onTestnet.BitcoinNetwork);
    }

    [Theory]
    [InlineData("mainnet")]
    [InlineData("signet")]
    [InlineData("regtest")]
    public void Given_Testnet4Invoice_When_DecodedForAnotherNetwork_Then_ItIsRefused(string other)
    {
        // Arrange
        var key = new Key();
        var encoded = new Invoice(LightningMoney.Satoshis(1_000), "testnet4", s_paymentHash, s_paymentSecret,
                                  BitcoinNetwork.Testnet4).Encode(key);

        // Act / Assert
        Assert.ThrowsAny<Exception>(() => Invoice.Decode(encoded, BitcoinNetwork.Resolve(other)));
    }

    [Fact]
    public void Given_ASignetInvoice_When_DecodedForTestnet4_Then_ItIsRefused()
    {
        // Arrange
        var key = new Key();
        var encoded = new Invoice(LightningMoney.Satoshis(1_000), "signet", s_paymentHash, s_paymentSecret,
                                  BitcoinNetwork.Signet).Encode(key);

        // Act / Assert
        Assert.ThrowsAny<Exception>(() => Invoice.Decode(encoded, BitcoinNetwork.Testnet4));
    }
}