namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;

public class KeyDerivationServiceTests
{
    // BOLT 3 Appendix E: Key Derivation Test Vectors
    private static readonly byte[] s_baseSecret =
        Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] s_perCommitmentSecret =
        Convert.FromHexString("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");

    private static readonly byte[] s_expectedRevocationPrivKey =
        Convert.FromHexString("d09ffff62ddb2297ab000cc85bcb4283fdeb6aa052affbc9dddcf33b61078110");

    [Fact]
    public void Given_Bolt3Secrets_When_DeriveRevocationPrivKey_Then_MatchesVectorAndWipesIntermediateTerms()
    {
        // Arrange
        var math = new RecordingSecp256K1Math();
        var service = new KeyDerivationService(math);

        // Act
        var result = service.DeriveRevocationPrivKey(s_baseSecret.ToArray(), s_perCommitmentSecret.ToArray());

        // Assert
        Assert.Equal(s_expectedRevocationPrivKey, result.Value);
        Assert.Equal(2, math.MultipliedPrivKeys.Count);
        Assert.All(math.MultipliedPrivKeys, term => Assert.All(term, b => Assert.Equal(0, b)));
    }

    /// <summary>
    /// Delegates to the real <see cref="Secp256K1Math"/> and keeps the arrays returned by MultiplyPrivKey so the
    /// test can check they are wiped. (Moq cannot mock ReadOnlySpan parameters.)
    /// </summary>
    private sealed class RecordingSecp256K1Math : ISecp256K1Math
    {
        private readonly Secp256K1Math _inner = new();

        public List<byte[]> MultipliedPrivKeys { get; } = [];

        public CompactPubKey MultiplyPubKey(CompactPubKey pubKey, ReadOnlySpan<byte> scalar) =>
            _inner.MultiplyPubKey(pubKey, scalar);

        public PrivKey MultiplyPrivKey(PrivKey privKey, ReadOnlySpan<byte> scalar)
        {
            var result = _inner.MultiplyPrivKey(privKey, scalar);
            MultipliedPrivKeys.Add(result.Value);
            return result;
        }

        public CompactPubKey AddPubKeys(CompactPubKey pubKey1, CompactPubKey pubKey2) =>
            _inner.AddPubKeys(pubKey1, pubKey2);

        public PrivKey AddPrivKeys(PrivKey privKey1, PrivKey privKey2) => _inner.AddPrivKeys(privKey1, privKey2);
    }
}