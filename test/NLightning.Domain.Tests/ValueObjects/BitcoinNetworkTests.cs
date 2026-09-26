namespace NLightning.Domain.Tests.ValueObjects;

using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

public class BitcoinNetworkTests
{
    [Fact]
    public void Given_NetworkInstances_When_ComparedForEquality_Then_ReturnsCorrectResult()
    {
        // Given
        var mainNet1 = BitcoinNetwork.Mainnet;
        var mainNet2 = new BitcoinNetwork("mainnet");
        var testNet = BitcoinNetwork.Testnet;

        // When & Then
        Assert.True(mainNet1 == mainNet2);
        Assert.False(mainNet1 == testNet);
        Assert.True(mainNet1.Equals(mainNet2));
        Assert.False(mainNet1.Equals(testNet));
    }

    [Fact]
    public void Given_NetworkInstance_When_ConvertedToString_Then_ReturnsCorrectName()
    {
        // Given
        var network = BitcoinNetwork.Mainnet;

        // When
        string networkName = network;

        // Then
        Assert.Equal("mainnet", networkName);
    }

    [Fact]
    public void Given_String_When_ConvertedToNetwork_Then_ReturnsCorrectNetwork()
    {
        // Given
        const string networkName = "testnet";

        // When
        BitcoinNetwork bitcoinNetwork = networkName;

        // Then
        Assert.Equal(BitcoinNetwork.Testnet, bitcoinNetwork);
    }

    [Theory]
    [InlineData(NetworkConstants.Mainnet)]
    [InlineData(NetworkConstants.Testnet)]
    [InlineData(NetworkConstants.Regtest)]
    [InlineData(NetworkConstants.Signet)]
    public void Given_NetworkInstance_When_ChainHashAccessed_Then_ReturnsCorrectHash(string networkName)
    {
        // Given
        var network = new BitcoinNetwork(networkName);
        var expectedChain = networkName switch
        {
            NetworkConstants.Mainnet => ChainConstants.Main,
            NetworkConstants.Testnet => ChainConstants.Testnet,
            NetworkConstants.Regtest => ChainConstants.Regtest,
            NetworkConstants.Signet => ChainConstants.Signet,
            _ => throw new InvalidOperationException("Chain not supported.")
        };

        // When
        var chainHash = network.ChainHash;

        // Then
        Assert.Equal(expectedChain, chainHash);
    }

    [Fact]
    public void Given_UnsupportedNetworkName_When_ChainHashAccessed_Then_ThrowsException()
    {
        // Given
        var network = new BitcoinNetwork("unsupported");

        // When & Then
        Assert.Throws<InvalidOperationException>(() => network.ChainHash);
    }

    [Fact]
    public void Given_NetworkInstance_When_ToStringCalled_Then_ReturnsCorrectName()
    {
        // Given
        var network = BitcoinNetwork.Mainnet;

        // When
        var networkName = network.ToString();

        // Then
        Assert.Equal("mainnet", networkName);
    }

    [Fact]
    public void Given_TwoEqualNetworkInstances_When_Compared_Then_AreEqual()
    {
        // Given
        var network1 = new BitcoinNetwork("mainnet");
        var network2 = BitcoinNetwork.Mainnet;

        // When & Then
        Assert.True(network1 == network2);
        Assert.False(network1 != network2);
        Assert.True(network1.Equals(network2));
        Assert.True(network2.Equals(network1));
    }

    [Fact]
    public void Given_TwoDifferentNetworkInstances_When_Compared_Then_AreNotEqual()
    {
        // Given
        var network1 = BitcoinNetwork.Mainnet;
        var network2 = BitcoinNetwork.Testnet;

        // When & Then
        Assert.False(network1 == network2);
        Assert.True(network1 != network2);
        Assert.False(network1.Equals(network2));
        Assert.False(network2.Equals(network1));
    }

    [Fact]
    public void Builtin_Networks_ChainHash_Matches()
    {
        Assert.Equal(ChainConstants.Main, BitcoinNetwork.Mainnet.ChainHash);
        Assert.Equal(ChainConstants.Testnet, BitcoinNetwork.Testnet.ChainHash);
        Assert.Equal(ChainConstants.Regtest, BitcoinNetwork.Regtest.ChainHash);
    }

    [Fact]
    public void Unregistered_CustomNetwork_Throws()
    {
        var net = new BitcoinNetwork("myinvelidnet");
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = net.ChainHash;
        });
    }

    [Fact]
    public void RegisterCustomNetwork_ReturnsExpectedChainHash()
    {
        var customChainHash = DummyChainHash(0x42);
        // Register
        BitcoinNetwork.Register("mycustomnet", customChainHash);

        var net = new BitcoinNetwork("mycustomnet");
        Assert.Equal(customChainHash, net.ChainHash);
    }

    [Fact]
    public void Register_IsCaseInsensitive()
    {
        var customChainHash = DummyChainHash(0xDD);
        var lower = "lowercase";
        var upper = "LOWERCASE";

        BitcoinNetwork.Register(upper, customChainHash);

        var net1 = new BitcoinNetwork(lower);
        var net2 = new BitcoinNetwork(upper);
        Assert.Equal(customChainHash, net1.ChainHash);
        Assert.Equal(customChainHash, net2.ChainHash);
    }

    [Fact]
    public void Unregister_RemovesChainHash()
    {
        var customChainHash = DummyChainHash(0xAB);
        var name = "toRemove";
        BitcoinNetwork.Register(name, customChainHash);

        var useNet = new BitcoinNetwork(name);
        Assert.Equal(customChainHash, useNet.ChainHash);

        BitcoinNetwork.Unregister(name);

        var afterRemove = new BitcoinNetwork(name);
        Assert.Throws<InvalidOperationException>(() =>
        {
            var _ = afterRemove.ChainHash;
        });
    }

    [Fact]
    public void ToString_And_Conversions_Work_For_Custom()
    {
        var customChainHash = DummyChainHash(0x5A);
        var name = "foo_bar";
        var bnet = new BitcoinNetwork(name);
        BitcoinNetwork.Register(name, customChainHash);

        Assert.Equal(name, bnet.ToString());

        string str = bnet;
        Assert.Equal(name.ToLowerInvariant(), str);

        BitcoinNetwork net2 = name;
        Assert.Equal(name, net2.Name);
        Assert.Equal(customChainHash, net2.ChainHash);
    }

    [Fact]
    public void Register_Throws_For_AlreadyExistingCustomNetwork()
    {
        var chainHash1 = DummyChainHash(0x13);
        var chainHash2 = DummyChainHash(0x53);
        var name = "networkdup";

        // Ensure clean slate
        BitcoinNetwork.Unregister(name);

        BitcoinNetwork.Register(name, chainHash1);

        // Attempting to add again (any value) must throw
        var ex = Assert.Throws<InvalidOperationException>(() => BitcoinNetwork.Register(name, chainHash2));
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Clean up for other tests
        BitcoinNetwork.Unregister(name);
    }

    [Fact]
    public void Register_Throws_For_Null_Or_Whitespace_Name()
    {
        var ch = DummyChainHash(0x77);

        Assert.Throws<ArgumentNullException>(() => BitcoinNetwork.Register(null!, ch));
        Assert.Throws<ArgumentNullException>(() => BitcoinNetwork.Register("", ch));
        Assert.Throws<ArgumentNullException>(() => BitcoinNetwork.Register("   ", ch));
    }

    [Fact]
    public void Given_Signet_When_ChainHashAccessed_Then_ItIsTheSignetGenesisHashInWireOrder()
    {
        // Arrange: the signet genesis block hash as block explorers show it (big-endian)
        const string genesisHashHex = "00000008819873e925422c1ff0f99f7cc9bbb232af63a077a480a3633bee1ef6";
        var expected = Convert.FromHexString(genesisHashHex).Reverse().ToArray();

        // Act
        var chainHash = BitcoinNetwork.Signet.ChainHash;

        // Assert: BOLT chain_hash is the genesis hash in the byte order of the block header (little-endian)
        Assert.Equal(expected, chainHash.Value);
        Assert.Equal(ChainConstants.Signet, chainHash);
    }

    [Theory]
    [InlineData(NetworkConstants.Mainnet, "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f")]
    [InlineData(NetworkConstants.Testnet, "000000000933ea01ad0ee984209779baaec3ced90fa3f408719526f8d77f4943")]
    [InlineData(NetworkConstants.Regtest, "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206")]
    [InlineData(NetworkConstants.Signet, "00000008819873e925422c1ff0f99f7cc9bbb232af63a077a480a3633bee1ef6")]
    public void Given_BuiltInNetwork_When_ChainHashAccessed_Then_ItIsTheReversedGenesisHash(string name,
        string genesisHashHex)
    {
        // Arrange (the testnet constant had 4 bytes garbled before this check)
        var expected = Convert.FromHexString(genesisHashHex).Reverse().ToArray();

        // Act
        var chainHash = new BitcoinNetwork(name).ChainHash;

        // Assert
        Assert.Equal(expected, chainHash.Value);
    }

    [Theory]
    [InlineData("mutinynet")]
    [InlineData("MutinyNet")]
    [InlineData(" mutinynet ")]
    public void Given_Mutinynet_When_Resolved_Then_ItIsSignet(string name)
    {
        // Act
        var network = BitcoinNetwork.Resolve(name);

        // Assert
        Assert.Equal(BitcoinNetwork.Signet, network);
        Assert.Equal(NetworkConstants.Signet, network.Name);
        Assert.Equal(ChainConstants.Signet, network.ChainHash);
        Assert.True(BitcoinNetwork.IsCustomSignet(name));
        Assert.True(new BitcoinNetwork(name).IsSignet);
        Assert.Equal(BitcoinNetwork.Signet, new BitcoinNetwork(name)); // e.g. the daemon's Node:Network binding
        Assert.Equal(ChainConstants.Signet, new BitcoinNetwork(name).ChainHash);
    }

    [Fact]
    public void Given_CustomSignetFromConfiguration_When_Registered_Then_ItResolvesToSignet()
    {
        // Arrange
        const string name = "my-test-signet";
        BitcoinNetwork.Unregister(name);
        Assert.Throws<ArgumentException>(() => BitcoinNetwork.Resolve(name));
        var options = new CustomSignetOptions { Name = name };

        try
        {
            // Act
            options.Register();
            options.Register(); // idempotent

            // Assert
            Assert.Equal(BitcoinNetwork.Signet, BitcoinNetwork.Resolve(name));
            Assert.Empty(options.GetValidationErrors(BitcoinNetwork.Signet));
        }
        finally
        {
            BitcoinNetwork.Unregister(name);
        }
    }

    [Theory]
    [InlineData(NetworkConstants.Mainnet)]
    [InlineData(NetworkConstants.Testnet)]
    [InlineData(NetworkConstants.Regtest)]
    [InlineData(NetworkConstants.Signet)]
    public void Given_BuiltInName_When_Resolved_Then_ItIsThatNetwork(string name)
    {
        // Act
        var network = BitcoinNetwork.Resolve(name.ToUpperInvariant());

        // Assert
        Assert.Equal(new BitcoinNetwork(name), network);
        Assert.Equal(name, network.Name);
    }

    [Theory]
    [InlineData("bitcoin-unknown")]
    [InlineData("testnet4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Given_UnknownName_When_Resolved_Then_ItFailsInsteadOfFallingBack(string? name)
    {
        // Act / Assert
        var exception = Assert.Throws<ArgumentException>(() => BitcoinNetwork.Resolve(name));
        Assert.DoesNotContain("Chain hash", exception.Message);
    }

    [Fact]
    public void Given_CustomSignetName_When_RegisteredWithAnotherChainHash_Then_RegisterCustomSignetThrows()
    {
        // Arrange
        const string name = "not-a-signet";
        BitcoinNetwork.Unregister(name);
        BitcoinNetwork.Register(name, DummyChainHash(0x31));

        try
        {
            // Act / Assert
            Assert.Throws<InvalidOperationException>(() => BitcoinNetwork.RegisterCustomSignet(name));
            Assert.False(BitcoinNetwork.IsCustomSignet(name));
            Assert.Equal(name, BitcoinNetwork.Resolve(name).Name);
        }
        finally
        {
            BitcoinNetwork.Unregister(name);
        }
    }

    [Theory]
    [InlineData(NetworkConstants.Signet)]
    [InlineData(NetworkConstants.Regtest)]
    public void Given_BuiltInName_When_RegisteredAsCustom_Then_Throws(string name)
    {
        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => BitcoinNetwork.RegisterCustomSignet(name));
        Assert.Throws<InvalidOperationException>(() => BitcoinNetwork.Register(name, DummyChainHash(0x01)));
    }

    [Fact]
    public void Given_UnknownNetwork_When_ComparedAsObject_Then_ItDoesNotThrow()
    {
        // Arrange
        object unknown = new BitcoinNetwork("unknown-net");

        // Act / Assert: Equals(object) compares names, it no longer reads ChainHash (which throws for unknown names)
        Assert.False(BitcoinNetwork.Mainnet.Equals(unknown));
        Assert.True(new BitcoinNetwork("UNKNOWN-NET").Equals(unknown));
    }

    [Fact]
    public void Given_DefaultNetwork_When_Read_Then_NameIsEmptyAndResolveFails()
    {
        // Arrange
        var network = default(BitcoinNetwork);

        // Act / Assert
        Assert.Equal(string.Empty, network.Name);
        Assert.False(network.IsSignet);
        Assert.Throws<InvalidOperationException>(() => network.ChainHash);
        Assert.Throws<ArgumentException>(() => BitcoinNetwork.Resolve(network.Name));
    }

    [Fact]
    public void Given_CustomSignetOptions_When_NetworkIsNotSignet_Then_ValidationFails()
    {
        // Arrange
        var options = new CustomSignetOptions { Name = NetworkConstants.Mutinynet };
        var builtIn = new CustomSignetOptions { Name = NetworkConstants.Regtest };

        // Act
        var onRegtest = options.GetValidationErrors(BitcoinNetwork.Regtest);
        var onSignet = options.GetValidationErrors(BitcoinNetwork.Signet);
        var builtInErrors = builtIn.GetValidationErrors(BitcoinNetwork.Signet);

        // Assert
        Assert.Single(onRegtest);
        Assert.Empty(onSignet);
        Assert.Single(builtInErrors);
        Assert.Empty(new CustomSignetOptions().GetValidationErrors(BitcoinNetwork.Mainnet));
    }

    private static ChainHash DummyChainHash(byte fill)
    {
        // Create a 32-byte chainhash with a pattern to differentiate
        return new ChainHash(new[]
        {
            fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill,
            fill, fill,
            fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill,
            fill, fill
        });
    }
}