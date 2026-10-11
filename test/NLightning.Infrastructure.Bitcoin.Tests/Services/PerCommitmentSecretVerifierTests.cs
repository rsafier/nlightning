using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Protocol.Services;

public class PerCommitmentSecretVerifierTests
{
    // BOLT 3 Appendix E: per_commitment_secret and per_commitment_point
    private static readonly Secret s_appendixESecret =
        Convert.FromHexString("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");

    private static readonly CompactPubKey s_appendixEPoint =
        Convert.FromHexString("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");

    [Fact]
    public void Given_AppendixESecret_When_Verify_Then_ItGeneratesThePoint()
    {
        // Arrange
        var verifier = new PerCommitmentSecretVerifier();

        // Act
        var result = verifier.Verify(s_appendixESecret, s_appendixEPoint);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Given_WrongSecret_When_Verify_Then_False()
    {
        // Arrange
        var verifier = new PerCommitmentSecretVerifier();
        var wrongSecret = ((byte[])s_appendixESecret).ToArray();
        wrongSecret[31] ^= 0x01;

        // Act
        var result = verifier.Verify(wrongSecret, s_appendixEPoint);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_ZeroOrOverflowingSecret_When_Verify_Then_False()
    {
        // Arrange - neither zero nor a value >= the curve order is a valid scalar
        var verifier = new PerCommitmentSecretVerifier();
        var overflowing = Enumerable.Repeat((byte)0xFF, CryptoConstants.SecretLen).ToArray();

        // Act / Assert
        Assert.False(verifier.Verify(Secret.Empty, s_appendixEPoint));
        Assert.False(verifier.Verify(overflowing, s_appendixEPoint));
    }

    [Fact]
    public void Given_PeerSecretsInOrder_When_VerifyAndStore_Then_StoredAndDerivable()
    {
        // Arrange - the peer's secrets come from one seed (Appendix D), revealed for commitments 0, 1, 2
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var verifier = new PerCommitmentSecretVerifier();
        using var shachain = new SecretStorageService();

        // Act
        var results = Enumerable.Range(0, 3)
                                .Select(n => verifier.VerifyAndStore(PeerSecret(keyDerivationService, (ulong)n),
                                                                     (ulong)n,
                                                                     PeerPoint(keyDerivationService, (ulong)n),
                                                                     shachain))
                                .ToList();

        // Assert
        Assert.All(results, Assert.True);
        Assert.Equal(PeerSecret(keyDerivationService, 0), shachain.DeriveOldSecret(PerCommitmentIndex.From(0)));
        Assert.Equal(PeerSecret(keyDerivationService, 1), shachain.DeriveOldSecret(PerCommitmentIndex.From(1)));
    }

    [Fact]
    public void Given_SecretNotMatchingThePoint_When_VerifyAndStore_Then_FalseAndShachainUntouched()
    {
        // Arrange
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var verifier = new PerCommitmentSecretVerifier();
        using var shachain = new SecretStorageService();

        // Act - commitment 0's secret offered for commitment 1's point
        var result = verifier.VerifyAndStore(PeerSecret(keyDerivationService, 0), 0,
                                             PeerPoint(keyDerivationService, 1), shachain);

        // Assert
        Assert.False(result);
        Assert.Throws<InvalidOperationException>(() => shachain.DeriveOldSecret(PerCommitmentIndex.From(0)));
    }

    [Fact]
    public void Given_SecretFromAnotherSeed_When_VerifyAndStore_Then_ShachainRejectsIt()
    {
        // Arrange - the second secret opens its point but was not generated from the same seed as the first
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var verifier = new PerCommitmentSecretVerifier();
        using var shachain = new SecretStorageService();
        Assert.True(verifier.VerifyAndStore(PeerSecret(keyDerivationService, 0), 0, PeerPoint(keyDerivationService, 0),
                                            shachain));
        var otherSecret = keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.Seed0FinalNode,
                                                                           PerCommitmentIndex.From(1));
        using var otherKey = new Key(otherSecret);

        // Act
        var result = verifier.VerifyAndStore(otherSecret, 1, otherKey.PubKey.ToBytes(), shachain);

        // Assert
        Assert.False(result);
    }

    private static Secret PeerSecret(KeyDerivationService keyDerivationService, ulong commitmentNumber) =>
        keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.StorageCorrectSeed,
                                                         PerCommitmentIndex.From(commitmentNumber));

    private static CompactPubKey PeerPoint(KeyDerivationService keyDerivationService, ulong commitmentNumber)
    {
        using var key = new Key(PeerSecret(keyDerivationService, commitmentNumber));
        return key.PubKey.ToBytes();
    }
}