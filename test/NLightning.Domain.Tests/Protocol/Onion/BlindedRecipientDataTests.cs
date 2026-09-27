namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Validators;

/// <summary>
/// ONION M5: the <c>payment_relay</c> formulas, the reader rules on <c>encrypted_recipient_data</c> and our
/// <c>path_id</c>.
/// </summary>
public class BlindedRecipientDataTests
{
    private static readonly CompactPubKey s_nodeId =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    private static BlindedRecipientData Relay(ShortChannelId? scid = null, CompactPubKey? nextNodeId = null,
                                              BlindedPaymentRelay? relay = null,
                                              BlindedPaymentConstraints? constraints = null,
                                              byte[]? allowedFeatures = null) =>
        new()
        {
            ShortChannelId = scid,
            NextNodeId = nextNodeId,
            PaymentRelay = relay,
            PaymentConstraints = constraints,
            AllowedFeatures = allowedFeatures is null ? null : (ReadOnlyMemory<byte>?)allowedFeatures
        };

    [Theory]
    // blinded-payment-onion-test.json: Bob (10000, 0), Carol (100, 150) and Dave (0, 100) take 110125 down to 100000
    [InlineData(10_000U, 0U, 110_125UL, 100_125UL)]
    [InlineData(100U, 150U, 100_125UL, 100_010UL)]
    [InlineData(0U, 100U, 100_010UL, 100_000UL)]
    // rounded up: the next hop never gets less than the sender paid for
    [InlineData(1_000U, 100U, 1_001_101UL, 1_000_001UL)]
    public void Given_PaymentRelay_When_ComputingAmountToForward_Then_BoltFormula(uint feeBase, uint feeRate,
                                                                                  ulong incoming, ulong expected)
    {
        // Arrange
        var relay = new BlindedPaymentRelay(40, feeRate, feeBase);

        // Act
        var ok = relay.TryComputeAmountToForward(incoming, out var amount);

        // Assert
        Assert.True(ok);
        Assert.Equal(expected, amount);
    }

    [Fact]
    public void Given_AmountBelowFeeBase_When_ComputingAmountToForward_Then_False()
    {
        // Arrange
        var relay = new BlindedPaymentRelay(40, 0, 1_000);

        // Act & Assert
        Assert.False(relay.TryComputeAmountToForward(999, out _));
    }

    [Fact]
    public void Given_ExpiryBelowDelta_When_ComputingOutgoingCltv_Then_FalseElseSubtracted()
    {
        // Arrange
        var relay = new BlindedPaymentRelay(144, 0, 0);

        // Act & Assert
        Assert.False(relay.TryComputeOutgoingCltvValue(143, out _));
        Assert.True(relay.TryComputeOutgoingCltvValue(1_000, out var outgoing));
        Assert.Equal(856U, outgoing);
    }

    [Fact]
    public void Given_ValidRelayData_When_Validated_Then_Accepted()
    {
        // Arrange
        var data = Relay(new ShortChannelId(1, 2, 3), relay: new BlindedPaymentRelay(40, 1, 1),
                         constraints: new BlindedPaymentConstraints(1_000, 50), allowedFeatures: []);

        // Act & Assert
        Assert.True(BlindedRecipientDataValidator.TryValidate(data, false, 50, 1_000, out _));
    }

    public static TheoryData<string, BlindedRecipientData, bool, ulong?, uint?> RefusedData => new()
    {
        {
            "both", Relay(new ShortChannelId(1, 2, 3), s_nodeId, new BlindedPaymentRelay(40, 1, 1)), false, null,
            null
        },
        { "feature", Relay(new ShortChannelId(1, 2, 3), relay: new(40, 1, 1), allowedFeatures: [0x02]), false, null, null },
        { "feature", new BlindedRecipientData { AllowedFeatures = new byte[] { 0x00, 0x80 } }, true, null, null },
        { "max_cltv_expiry", Relay(new ShortChannelId(1, 2, 3), relay: new(40, 1, 1), constraints: new(1_000, 1)), false, 5, 1_001 },
        { "htlc_minimum_msat", new BlindedRecipientData { PaymentConstraints = new(1_000, 50) }, true, 49, 1_000 },
        { "neither", Relay(relay: new(40, 1, 1)), false, null, null },
        { "payment_relay", Relay(new ShortChannelId(1, 2, 3)), false, null, null }
    };

    [Theory]
    [MemberData(nameof(RefusedData))]
    public void Given_InvalidRecipientData_When_Validated_Then_RefusedWithReason(string expected,
                                                                                 BlindedRecipientData data,
                                                                                 bool isFinal, ulong? amount,
                                                                                 uint? expiry)
    {
        // Act
        var ok = BlindedRecipientDataValidator.TryValidate(data, isFinal, amount, expiry, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void Given_FinalDataWithoutRelay_When_Validated_Then_Accepted()
    {
        // Arrange: the recipient's own hop needs neither a next hop nor payment_relay
        var data = new BlindedRecipientData { PathId = new byte[] { 1 } };

        // Act & Assert
        Assert.True(BlindedRecipientDataValidator.TryValidate(data, true, 1, 1, out _));
    }

    [Fact]
    public void Given_Preimage_When_ComputingPathId_Then_DeterministicAndOnlyMatchesItsOwnPreimage()
    {
        // Arrange
        var preimage = new Secret(Enumerable.Repeat((byte)0x42, 32).ToArray());
        var other = new Secret(Enumerable.Repeat((byte)0x43, 32).ToArray());

        // Act
        var pathId = BlindedPathId.Compute(preimage);

        // Assert
        Assert.Equal(BlindedPathId.Length, pathId.Length);
        Assert.Equal(pathId, BlindedPathId.Compute(preimage));
        Assert.True(BlindedPathId.Matches(pathId, preimage));
        Assert.False(BlindedPathId.Matches(pathId, other));
        Assert.False(BlindedPathId.Matches(pathId[..31], preimage));
        Assert.NotEqual((byte[])preimage, pathId);
    }
}