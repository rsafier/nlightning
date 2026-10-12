using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Networks;

using Bitcoin.Networks;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

public class NBitcoinNetworkResolverTests
{
    public static TheoryData<string> BuiltInNetworks =>
    [
        NetworkConstants.Mainnet, NetworkConstants.Testnet, NetworkConstants.Testnet4, NetworkConstants.Regtest,
        NetworkConstants.Signet
    ];

    [Theory]
    [MemberData(nameof(BuiltInNetworks))]
    public void Given_BuiltInNetwork_When_Resolved_Then_NBitcoinGenesisMatchesOurChainHash(string name)
    {
        // Arrange
        var network = new BitcoinNetwork(name);

        // Act
        var nbitcoinNetwork = network.ToNBitcoinNetwork();

        // Assert: BOLT chain_hash is the genesis hash in wire order, which is uint256.ToBytes()
        Assert.Equal(network.ChainHash.Value, nbitcoinNetwork.GenesisHash.ToBytes());
    }

    [Fact]
    public void Given_Signet_When_Resolved_Then_ItIsNBitcoinSignetAndMatchesGetNetwork()
    {
        // Act
        var resolved = BitcoinNetwork.Signet.ToNBitcoinNetwork();

        // Assert: the builders, the signer and the key manager resolve through this seam (NL-298)
        Assert.Same(NBitcoin.Bitcoin.Instance.Signet, resolved);
        Assert.Same(resolved, Network.GetNetwork(NetworkConstants.Signet));
        Assert.Equal("00000008819873e925422c1ff0f99f7cc9bbb232af63a077a480a3633bee1ef6",
                     resolved.GenesisHash.ToString());
    }

    [Fact]
    public void Given_Testnet4_When_Resolved_Then_ItIsNBitcoinTestnet4WithTheBip94Genesis()
    {
        // Act
        var resolved = NBitcoinNetworkResolver.Resolve(NetworkConstants.Testnet4);

        // Assert (NL-012): its own genesis block, not testnet3's
        Assert.Same(NBitcoin.Bitcoin.Instance.Testnet4, resolved);
        Assert.NotSame(Network.TestNet, resolved);
        Assert.Equal("00000000da84f2bafbbc53dee25a72ae507ff4914b867c565be350b0da8bf043",
                     resolved.GenesisHash.ToString());
    }

    [Fact]
    public void Given_Testnet4_When_DerivingAddresses_Then_TheyAreBech32TbAndValidOnTestnet3()
    {
        // Arrange
        var network = BitcoinNetwork.Testnet4.ToNBitcoinNetwork();
        var pubKey = new Key(Convert.FromHexString("0101010101010101010101010101010101010101010101010101010101010101"))
           .PubKey;

        // Act
        var p2Wpkh = pubKey.GetAddress(ScriptPubKeyType.Segwit, network).ToString();
        var p2Tr = pubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ToString();

        // Assert
        Assert.StartsWith("tb1q", p2Wpkh);
        Assert.StartsWith("tb1p", p2Tr);
        Assert.Equal(p2Wpkh, BitcoinAddress.Create(p2Wpkh, Network.TestNet).ToString());
        Assert.Equal(p2Wpkh, BitcoinAddress.Create(p2Wpkh, network).ToString());
    }

    [Theory]
    [InlineData("mutinynet")]
    [InlineData("MUTINYNET")]
    public void Given_Mutinynet_When_Resolved_Then_ItIsNBitcoinSignet(string name)
    {
        // Act
        var byName = NBitcoinNetworkResolver.Resolve(name);
        var byValue = new BitcoinNetwork(name).ToNBitcoinNetwork();

        // Assert
        Assert.Same(NBitcoin.Bitcoin.Instance.Signet, byName);
        Assert.Same(NBitcoin.Bitcoin.Instance.Signet, byValue);
    }

    [Theory]
    [InlineData("unknown-net")]
    [InlineData("testnet5")]
    [InlineData("")]
    public void Given_UnknownNetwork_When_Resolved_Then_ItThrowsInsteadOfFallingBackToMainnet(string name)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => NBitcoinNetworkResolver.Resolve(name));
        Assert.Throws<ArgumentException>(() => new BitcoinNetwork(name).ToNBitcoinNetwork());
    }

    [Fact]
    public void Given_CustomNonSignetNetwork_When_Resolved_Then_ItThrows()
    {
        // Arrange
        const string name = "resolver-custom-chain";
        BitcoinNetwork.Unregister(name);
        BitcoinNetwork.Register(name, new ChainHash(new byte[32]));

        try
        {
            // Act / Assert: registered for its chain hash, but there are no NBitcoin parameters for it
            Assert.Throws<ArgumentException>(() => NBitcoinNetworkResolver.Resolve(name));
        }
        finally
        {
            BitcoinNetwork.Unregister(name);
        }
    }

    [Theory]
    [InlineData(NetworkConstants.Signet)]
    [InlineData(NetworkConstants.Mutinynet)]
    public void Given_SignetNetwork_When_DerivingAddresses_Then_TheyAreBech32Tb(string name)
    {
        // Arrange
        var network = NBitcoinNetworkResolver.Resolve(name);
        var pubKey = new Key(Convert.FromHexString("0101010101010101010101010101010101010101010101010101010101010101"))
           .PubKey;

        // Act
        var p2Wpkh = pubKey.GetAddress(ScriptPubKeyType.Segwit, network).ToString();
        var p2Tr = pubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ToString();

        // Assert
        Assert.StartsWith("tb1q", p2Wpkh);
        Assert.StartsWith("tb1p", p2Tr);
        Assert.Equal(p2Wpkh, BitcoinAddress.Create(p2Wpkh, Network.TestNet).ToString());
    }
}