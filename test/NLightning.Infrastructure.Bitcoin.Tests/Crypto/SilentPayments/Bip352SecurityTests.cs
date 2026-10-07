using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.SilentPayments;

using Domain.Bitcoin.SilentPayments.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Musig2;
using Infrastructure.Bitcoin.Crypto.SilentPayments;

public sealed class Bip352SecurityTests
{
    private const string Order = "fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141";
    private static byte[] ScalarOne() => Convert.FromHexString(new string('0', 63) + "1");

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", 2)]
    [InlineData(Order, 2)]
    [InlineData("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364142", 3)]
    public void Given_AnIntegerLabelTweak_When_DerivingASpendKey_Then_ItIsReducedModuloTheCurveOrder(string label, int expected)
    {
        // Arrange: label tweaks are integer addends; only input_hash and t_k must be valid nonzero scalars.
        var one = ScalarOne();

        // Act
        var derived = Bip352.DeriveSpendPrivateKey(one, one, Convert.FromHexString(label));

        // Assert: 2G and 3G have even Y, so BIP340 parity keeps these tiny scalars unchanged.
        try
        {
            var wanted = new byte[32];
            wanted[31] = (byte)expected;
            Assert.Equal(wanted, derived);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(Order)]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Given_AnInvalidSharedSecretOutputTweak_When_DerivingASpendKey_Then_ItFails(string tweak)
    {
        // Arrange / Act / Assert: the output tweak has stricter rules than an optional label.
        Assert.Throws<ArgumentException>(() => Bip352.DeriveSpendPrivateKey(ScalarOne(), Convert.FromHexString(tweak)));
    }

    [Fact]
    public void Given_AnInputErrorAfterOneValidSecret_When_Aggregating_Then_TheDestinationStaysZero()
    {
        // Arrange
        var destination = Enumerable.Repeat((byte)255, 32).ToArray();
        SilentPaymentSenderInput[] inputs = [new(new byte[36], ScalarOne(), false), new(new byte[36], new byte[32], false)];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => Bip352.AggregateSenderSecret(inputs, destination));
        Assert.All(destination, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Given_AScanThatFailsAfterAMatch_When_TheNextPointIsInfinity_Then_AllTaggedHashStatesAreWiped()
    {
        // Arrange: B_spend = -t_1*G, so k=0 matches but k=1 is invalid. This enters partial-result cleanup.
        var shared = Bip352.IndividualPublicKey(ScalarOne());
        var tweak0 = Bip352.SharedSecretTweak(shared, 0);
        var tweak1 = Bip352.SharedSecretTweak(shared, 1);
        var spend = Bip352.IndividualPublicKey(tweak1);
        spend[0] ^= 1;
        var first = Bip352.AddPublicTweak(spend, tweak0).AsSpan(1).ToArray();
        var dummy = shared.AsSpan(1).ToArray();
        var tracked = new List<WipingSha256>();
        WipingSha256.Tracker.Value = tracked;
        try
        {
            // Act / Assert
            Assert.Throws<ArgumentException>(() => Bip352.Scan(shared, new CompactPubKey(spend),
                [new SilentPaymentScanCandidate(0, first), new SilentPaymentScanCandidate(1, dummy)]));
            Assert.All(tracked, hash => Assert.True(hash.IsWiped));
            Assert.Equal(2, tracked.Count);
        }
        finally
        {
            WipingSha256.Tracker.Value = null;
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(tweak0);
            CryptographicOperations.ZeroMemory(tweak1);
        }
    }
}