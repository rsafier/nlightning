using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Tests.Protocol.Services;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Enums;
using Domain.Protocol.Models;
using Infrastructure.Protocol.Services;

public class SecretStorageServiceTests
{
    private const ulong MaxIndex = Bolt3AppendixDVectors.StorageIndexMax;

    [Fact]
    public void Given_LowerIndexStored_When_InsertingAHigherIndex_Then_Rejected()
    {
        // Arrange - secrets are revealed in descending index order
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, MaxIndex - 1));

        // Act
        var result = storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_IndexStored_When_InsertingTheSameIndexAgain_Then_Rejected()
    {
        // Arrange
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex));

        // Act
        var result = storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_IndexAbove48Bits_When_Inserting_Then_Rejected()
    {
        // Arrange
        using var storage = new SecretStorageService();

        // Act
        var result = storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex + 1);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_EmptyStorage_When_Export_Then_Empty()
    {
        // Arrange
        using var storage = new SecretStorageService();

        // Act / Assert
        Assert.Empty(storage.Export());
    }

    [Fact]
    public void Given_ThreeSecrets_When_Export_Then_OneEntryPerOccupiedBucket()
    {
        // Arrange - indices ...FF (bucket 0), ...FE (bucket 1), ...FD (bucket 0 again, replaces ...FF)
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex));
        Assert.True(storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, MaxIndex - 1));
        Assert.True(storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, MaxIndex - 2));

        // Act
        var entries = storage.Export();

        // Assert
        Assert.Equal(
        [
            new ShachainEntry(0, MaxIndex - 2, Bolt3AppendixDVectors.StorageExpectedSecret2),
            new ShachainEntry(1, MaxIndex - 1, Bolt3AppendixDVectors.StorageExpectedSecret1)
        ], entries);
    }

    [Fact]
    public void Given_ExportedEntries_When_LoadedIntoNewStorage_Then_OrderRuleAndDerivationSurvive()
    {
        // Arrange
        using var original = new SecretStorageService();
        Assert.True(original.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex));
        Assert.True(original.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, MaxIndex - 1));
        using var reloaded = new SecretStorageService();

        // Act
        reloaded.Load(original.Export());

        // Assert
        Assert.Equal(new Secret(Bolt3AppendixDVectors.StorageExpectedSecret0), reloaded.DeriveOldSecret(MaxIndex));
        Assert.False(reloaded.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, MaxIndex - 1));
        Assert.True(reloaded.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, MaxIndex - 2));
    }

    [Fact]
    public void Given_EntryInWrongBucket_When_Load_Then_ThrowsAndKeepsCurrentSecrets()
    {
        // Arrange
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, MaxIndex));

        // Act / Assert - index ...FE has one trailing zero, so it belongs in bucket 1
        Assert.Throws<ArgumentException>(
            () => storage.Load([new ShachainEntry(0, MaxIndex - 1, Bolt3AppendixDVectors.StorageExpectedSecret1)]));
        Assert.Equal(new Secret(Bolt3AppendixDVectors.StorageExpectedSecret0), storage.DeriveOldSecret(MaxIndex));
    }

    [Fact]
    public void Given_DuplicateBucket_When_Load_Then_Throws()
    {
        // Arrange
        using var storage = new SecretStorageService();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => storage.Load(
        [
            new ShachainEntry(0, MaxIndex, Bolt3AppendixDVectors.StorageExpectedSecret0),
            new ShachainEntry(0, MaxIndex - 2, Bolt3AppendixDVectors.StorageExpectedSecret2)
        ]));
    }

    [Fact]
    public void Given_InconsistentBuckets_When_Load_Then_Throws()
    {
        // Arrange - Appendix D "#1 incorrect": secret 8 (other seed) at ...FF cannot be derived from secret 1 at ...FE
        using var storage = new SecretStorageService();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => storage.Load(
        [
            new ShachainEntry(0, MaxIndex, Bolt3AppendixDVectors.StorageExpectedSecret8),
            new ShachainEntry(1, MaxIndex - 1, Bolt3AppendixDVectors.StorageExpectedSecret1)
        ]));
    }

    [Fact]
    public void Given_StoredBasepointKey_When_GetBasepointPrivateKey_Then_ReturnsIt()
    {
        // Arrange - regression (NL-066): GetBasepointPrivateKey threw NotImplementedException
        using var storage = new SecretStorageService();
        var key = new PrivKey(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        storage.StoreBasepointPrivateKey(BasepointType.Htlc, key);

        // Act
        var result = storage.GetBasepointPrivateKey(BasepointType.Htlc);

        // Assert
        Assert.Equal(key, result);
        Assert.Throws<InvalidOperationException>(() => storage.GetBasepointPrivateKey(BasepointType.Payment));
    }
}