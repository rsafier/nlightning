using NBitcoin;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Models;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

public class InvoiceSignetTests
{
    private static readonly uint256 s_paymentHash =
        new("0001020304050607080900010203040506070809000102030405060708090102");

    private static readonly uint256 s_paymentSecret =
        new("1111111111111111111111111111111111111111111111111111111111111111");

    [Theory]
    [InlineData(NetworkConstants.Signet)]
    [InlineData(NetworkConstants.Mutinynet)]
    public void Given_SignetNode_When_InvoiceEncodedWithFallback_Then_ItIsTbsAndDecodesWithTbAddress(string name)
    {
        // Arrange: the node's network, as configuration resolves it (Mutinynet is signet)
        var network = BitcoinNetwork.Resolve(name);
        var key = new Key();
        var fallback = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Bitcoin.Instance.Signet);
        var invoice = new Invoice(LightningMoney.Satoshis(1_000), "signet", s_paymentHash, s_paymentSecret, network)
        {
            FallbackAddresses = [fallback]
        };

        // Act
        var encoded = invoice.Encode(key);
        var decoded = Invoice.Decode(encoded, network);

        // Assert
        Assert.StartsWith("lntbs10u1", encoded);
        Assert.Equal(BitcoinNetwork.Signet, decoded.BitcoinNetwork);
        var decodedFallback = Assert.Single(decoded.FallbackAddresses!);
        Assert.StartsWith("tb1q", decodedFallback.ToString());
        Assert.Equal(fallback.ToString(), decodedFallback.ToString());
    }

    [Fact]
    public void Given_SignetInvoice_When_DecodedForMainnet_Then_ItIsRefused()
    {
        // Arrange
        var key = new Key();
        var encoded = new Invoice(LightningMoney.Satoshis(1_000), "signet", s_paymentHash, s_paymentSecret,
                                  BitcoinNetwork.Signet).Encode(key);

        // Act / Assert
        Assert.ThrowsAny<Exception>(() => Invoice.Decode(encoded, BitcoinNetwork.Mainnet));
    }
}