using System.Reflection;
using System.Security.Cryptography;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Managers;

using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Musig2;
using Infrastructure.Bitcoin.Managers;

public sealed class SecureKeyManagerSilentPaymentTests : IDisposable
{
    private const string MnemonicWords =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private const string MainnetScan = "024139b0f81042e243a90478e43990c6a27be0e3346f0c71adbbcdd511beaea1e3";
    private const string MainnetSpend = "02fa210b3c4a60b80dd1616f48ae53bbdf0db744b3f9083385108f81be0acb58c6";
    private const string TestScan = "03439fc230182b46ff22032ca7ae2dff32958c9a0d177c7b7096df0d4b7141eb21";
    private const string TestSpend = "02833085c9a716d36b467552c00d6aa8bd42e39adbe98b05bc203110177192f702";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-sp-keys-" + Guid.NewGuid().ToString("N"));

    public SecureKeyManagerSilentPaymentTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("mainnet", MainnetScan, MainnetSpend)]
    [InlineData("testnet", TestScan, TestSpend)]
    [InlineData("testnet4", TestScan, TestSpend)]
    [InlineData("signet", TestScan, TestSpend)]
    [InlineData("mutinynet", TestScan, TestSpend)]
    [InlineData("regtest", TestScan, TestSpend)]
    public void Given_AStandardMnemonic_When_DerivingSilentPaymentKeys_Then_MatchesIndependentBip32Vectors(
        string network, string scan, string spend)
    {
        // Arrange: frozen fixtures computed with Python HMAC-SHA512 and the upstream secp256k1 oracle.
        using var manager = Create(BitcoinNetwork.Resolve(network));

        // Act / Assert: test networks use coin type 1 even though existing deposit paths use coin type 0.
        Assert.Equal(scan, manager.ScanPubKey.ToString());
        Assert.Equal(spend, manager.SpendPubKey.ToString());
        Assert.True(manager.RecoverableElsewhere);
        Assert.NotEqual(manager.GetNodePubKey(), manager.ScanPubKey);
        Assert.NotEqual(manager.SpendPubKey, manager.ScanPubKey);
    }

    [Fact]
    public void Given_PublicKeysOnly_When_ReadingAndMutatingCopies_Then_NoScanSecretIsRetainedAndCacheIsUnchanged()
    {
        // Arrange
        using var manager = Create(BitcoinNetwork.Regtest);

        // Act
        byte[] scan = manager.ScanPubKey;
        byte[] spend = manager.SpendPubKey;
        scan[1] ^= 1;
        spend[1] ^= 1;

        // Assert
        Assert.Equal(TestScan, manager.ScanPubKey.ToString());
        Assert.Equal(TestSpend, manager.SpendPubKey.ToString());
        Assert.Equal(IntPtr.Zero, ScanPointer(manager));
    }

    [Fact]
    public void Given_AReceiveOperation_When_ComputingSharedSecret_Then_ReturnsUnhashedPointAndRetainsLockedScanKey()
    {
        // Arrange: multiplying the generator by the private scan scalar returns its public key.
        using var manager = Create(BitcoinNetwork.Regtest);
        using var generator = new Key(Convert.FromHexString(new string('0', 63) + "1"));
        var point = new byte[33];

        // Act
        manager.ComputeScanSharedSecret(generator.PubKey.ToBytes(), point);

        // Assert: this is the full point, not the BOLT 4 SHA256 ECDH secret.
        Assert.Equal(TestScan, Convert.ToHexStringLower(point));
        Assert.NotEqual(IntPtr.Zero, ScanPointer(manager));
        manager.Dispose();
        Assert.Equal(IntPtr.Zero, ScanPointer(manager));
        Assert.Throws<ObjectDisposedException>(() => manager.ComputeScanSharedSecret(generator.PubKey.ToBytes(), point));
        Assert.Throws<ObjectDisposedException>(() => manager.GetLabelTweak(0, new byte[32]));
        Assert.Throws<ObjectDisposedException>(() => manager.ScanPubKey);
    }

    [Theory]
    [InlineData(0u, "a6df7f176ac2f7b3eb4459e7125b244b222fa0d9082c020144c4536777fe3d1b", "024b1bc6c34f24ae409889caa1cd8f46d03431324e1418e9c98156107f6198dcdf")]
    [InlineData(1u, "b0fbcac7926a3661f22a68e10248f6df1db76c4f0dbaae51eb0278207eeaebc8", "021ea7c1ce70d25a3ce8eb83125be7f48aba2adf5879d3960edc6c76202b9c5117")]
    [InlineData(4294967295u, "cdb9de940b4e9d6ba6684e532028efa74724a780adde4b62a85416c1706c7986", "02a4f89a2cec1bec4ef22104183ac52c92e750e6d4aeab146a322048dd0df1afdd")]
    public void Given_ALabel_When_DerivingTheTweak_Then_HashStateIsWipedAndPointMatchesScalar(uint label, string expectedTweak, string expectedPoint)
    {
        // Arrange
        using var manager = Create(BitcoinNetwork.Regtest);
        var hashers = new List<WipingSha256>();
        WipingSha256.Tracker.Value = hashers;
        var tweak = new byte[32];
        try
        {
            // Act
            manager.GetLabelTweak(label, tweak);
            var point = manager.GetLabelPoint(label);

            // Assert
            using var key = new Key(tweak);
            Assert.Equal(expectedTweak, Convert.ToHexStringLower(tweak));
            Assert.Equal(expectedPoint, point.ToString());
            Assert.Equal(key.PubKey.ToBytes(), (byte[])point);
            Assert.Equal(2, hashers.Count);
            Assert.All(hashers, hasher => Assert.True(hasher.IsWiped));
        }
        finally
        {
            WipingSha256.Tracker.Value = null;
            CryptographicOperations.ZeroMemory(tweak);
        }
    }

    [Fact]
    public void Given_AnOutputTweak_When_DerivingTheSignerKey_Then_RawScalarIsFreshWithoutABip86Tweak()
    {
        // Arrange: independently derived base spend scalar plus one modulo n.
        using var manager = Create(BitcoinNetwork.Regtest);
        var tweak = Convert.FromHexString(new string('0', 63) + "1");
        var first = manager.GetSilentPaymentSpendKey(tweak, null);
        var second = manager.GetSilentPaymentSpendKey(tweak, null);
        try
        {
            // Act / Assert: no label needs no persistent scan secret.
            Assert.Equal("9fd37137e760930c7208fa905e991c78c522689d237a220b2820c3ddb4c745a9",
                         Convert.ToHexStringLower(first));
            using var rawKey = new Key(second);
            Assert.Equal("03d55ab22f551d448795254addd7b01cc2c1ba8e2fcf6e44f46dc609be2fb07a3c", rawKey.PubKey.ToHex());
            Assert.Equal(first, second);
            Assert.NotSame(first, second);
            Assert.Equal(IntPtr.Zero, ScanPointer(manager));
            CryptographicOperations.ZeroMemory(first);
            Assert.Contains(second, value => value != 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Theory]
    [InlineData(0u, "0249d7f0d6e9349aedb8b0b1579bf50d1859d5d1472aa85469efeed0c1fdee1a4e")]
    [InlineData(1u, "02d66f7cc104f76b204f638ecd42d8b89030688b46d90b63c146c8a541c7c9c7e7")]
    [InlineData(4294967295u, "03308e7ede809850d494d01653c83bd96086ce67636ea92504d78a11fd12398c6b")]
    public void Given_ALabeledOutput_When_DerivingTheSignerKey_Then_MatchesTheIndependentRawOutputPoint(uint label, string expected)
    {
        // Arrange: the vectors include both odd and even Y; the signer applies BIP340 parity once.
        using var manager = Create(BitcoinNetwork.Regtest);
        var tweak = Convert.FromHexString(new string('0', 63) + "1");
        var scalar = manager.GetSilentPaymentSpendKey(tweak, label);
        try
        {
            // Act / Assert
            using var key = new Key(scalar);
            Assert.Equal(expected, key.PubKey.ToHex());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    [Fact]
    public void Given_InvalidSecretInputs_When_DerivingOrScanning_Then_TheyAreRejectedBeforeReceiveInitialization()
    {
        // Arrange
        using var manager = Create(BitcoinNetwork.Regtest);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => manager.GetSilentPaymentSpendKey(new byte[32], null));
        Assert.Throws<ArgumentException>(() => manager.GetSilentPaymentSpendKey(Enumerable.Repeat((byte)255, 32).ToArray(), null));
        Assert.Throws<ArgumentException>(() => manager.ComputeScanSharedSecret(new byte[33], new byte[33]));
        Assert.Throws<ArgumentException>(() => manager.GetLabelTweak(0, new byte[31]));
        Assert.Equal(IntPtr.Zero, ScanPointer(manager));
    }

    [Fact]
    public void Given_AStoredStandardWallet_When_Reloading_Then_ReceiveKeysAndRawSignerKeysAreStable()
    {
        // Arrange
        var path = Path.Combine(_directory, "keys.json");
        using var manager = Create(BitcoinNetwork.Regtest, path);
        manager.SaveToFile("test password");
        var tweak = Convert.FromHexString(new string('0', 63) + "1");
        var original = manager.GetSilentPaymentSpendKey(tweak, 0);
        using var loaded = SecureKeyManager.FromFilePath(path, BitcoinNetwork.Regtest, "test password");
        var restored = loaded.GetSilentPaymentSpendKey(tweak, 0);
        try
        {
            // Act / Assert
            Assert.Equal(manager.ScanPubKey, loaded.ScanPubKey);
            Assert.Equal(manager.SpendPubKey, loaded.SpendPubKey);
            Assert.Equal(original, restored);
            Assert.True(loaded.RecoverableElsewhere);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(restored);
        }
    }

    private SecureKeyManager Create(BitcoinNetwork network, string? path = null) =>
        SecureKeyManager.FromMnemonic(MnemonicWords, string.Empty, network, path ?? Path.Combine(_directory, "keys.json"));

    private static IntPtr ScanPointer(SecureKeyManager manager) =>
        (IntPtr)typeof(SecureKeyManager).GetField("_secureScanKeyPtr", BindingFlags.NonPublic | BindingFlags.Instance)!
                                      .GetValue(manager)!;

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}