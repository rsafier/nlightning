using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.SilentPayments;

using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.SilentPayments;

public sealed class SilentPaymentPreparedScanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_AnImmutablePreparedLabelContext_When_ScanningEitherOutputParity_Then_TheLabeledReceiptMatches(bool odd)
    {
        // Arrange: select a deterministic base spend key with the required final output parity.
        var crypto = new SilentPaymentCrypto();
        var one = Scalar(1);
        var shared = Bip352.IndividualPublicKey(one);
        var outputTweak = Bip352.SharedSecretTweak(shared, 0);
        var labelTweak = Bip352.ComputeLabelTweak(one, 7);
        var labelPoint = new CompactPubKey(Bip352.IndividualPublicKey(labelTweak));
        var context = crypto.PrepareScanContext(new Dictionary<uint, CompactPubKey> { [7] = labelPoint });
        CompactPubKey spend = default;
        byte[]? candidate = null;
        for (byte index = 1; index < 100; index++)
        {
            spend = Bip352.IndividualPublicKey(Scalar(index));
            var baseOutput = Bip352.AddPublicTweak(spend, outputTweak);
            Assert.True(Bip352.TrySumPublicKeys([new CompactPubKey(baseOutput), labelPoint], out var labeled));
            if (((byte[])labeled)[0] != (odd ? 3 : 2)) continue;
            candidate = ((byte[])labeled).AsSpan(1).ToArray();
            break;
        }
        Assert.NotNull(candidate);
        IReadOnlyList<SilentPaymentScanMatch>? found = null;
        try
        {
            // Act
            found = crypto.ScanPrepared(shared, spend, [new SilentPaymentScanCandidate(5, candidate)], context);

            // Assert: both sides of the x-only output can carry a label, exactly as uncached scanning.
            var receipt = Assert.Single(found);
            Assert.Equal(7u, receipt.Label);
            Assert.Equal(5u, receipt.OutputIndex);
            Assert.Equal(outputTweak, receipt.Tweak32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outputTweak);
            CryptographicOperations.ZeroMemory(labelTweak);
            if (found is not null) foreach (var match in found) CryptographicOperations.ZeroMemory(match.Tweak32);
        }
    }

    [Fact]
    public void Given_APreparedContext_When_TheSourceDictionaryAndKeyArrayChange_Then_TheSnapshotRemainsStable()
    {
        // Arrange
        var crypto = new SilentPaymentCrypto();
        var one = Scalar(1);
        var shared = Bip352.IndividualPublicKey(one);
        var outputTweak = Bip352.SharedSecretTweak(shared, 0);
        var labelTweak = Bip352.ComputeLabelTweak(one, 1);
        var labelBytes = Bip352.IndividualPublicKey(labelTweak);
        var labels = new Dictionary<uint, CompactPubKey> { [1] = new(labelBytes) };
        var context = crypto.PrepareScanContext(labels);
        var baseOutput = Bip352.AddPublicTweak(shared, outputTweak);
        Assert.True(Bip352.TrySumPublicKeys([new CompactPubKey(baseOutput), new CompactPubKey(labelBytes)], out var labeled));
        SilentPaymentScanCandidate[] candidates = [new(0, ((byte[])labeled).AsSpan(1).ToArray())];
        labelBytes[0] ^= 1;
        labels.Clear();
        IReadOnlyList<SilentPaymentScanMatch>? found = null;
        try
        {
            // Act / Assert: an existing block keeps its copied labels; a newly prepared block sees the new set.
            found = crypto.ScanPrepared(shared, new CompactPubKey(shared), candidates, context);
            Assert.Equal(1u, Assert.Single(found).Label);
            Assert.Empty(crypto.ScanPrepared(shared, new CompactPubKey(shared), candidates, crypto.PrepareScanContext(labels)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outputTweak);
            CryptographicOperations.ZeroMemory(labelTweak);
            if (found is not null) foreach (var match in found) CryptographicOperations.ZeroMemory(match.Tweak32);
        }
    }

    [Fact]
    public void Given_MalformedOrDuplicateLabelPoints_When_PreparingTheContext_Then_ValidationIsPreserved()
    {
        // Arrange
        var crypto = new SilentPaymentCrypto();
        var point = new CompactPubKey(Bip352.IndividualPublicKey(Scalar(1)));
        var malformed = new CompactPubKey([2, .. Enumerable.Repeat((byte)255, 32)]);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => crypto.PrepareScanContext(new Dictionary<uint, CompactPubKey> { [0] = malformed }));
        Assert.Throws<ArgumentException>(() => crypto.PrepareScanContext(new Dictionary<uint, CompactPubKey> { [0] = point, [1] = point }));
        Assert.Throws<ArgumentException>(() => crypto.ScanPrepared(Bip352.IndividualPublicKey(Scalar(1)), point, [], new ForeignContext()));
    }

    private static byte[] Scalar(byte value)
    {
        var scalar = new byte[32];
        scalar[31] = value;
        return scalar;
    }

    private sealed class ForeignContext : ISilentPaymentScanContext
    {
    }
}