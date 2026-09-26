namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interpreters;
using Domain.Protocol.Onion.Models;

public class FailureInterpreterTests
{
    private const int RouteLength = 4;
    private static readonly byte[] s_sha256OfOnion = new byte[32];

    private static DecryptedFailure Decrypted(int hop, FailureMessage message)
    {
        return new DecryptedFailure(hop, Array.Empty<byte>(), message);
    }

    private static byte[] ChannelUpdatePayload()
    {
        var payload = new byte[FailureChannelUpdateFactory.MinPayloadLength];
        payload.AsSpan().Fill(0x33);
        return payload;
    }

    [Fact]
    public void Given_NoHopMatched_When_Interpreting_Then_UnattributedAndRetryWithoutPenalty()
    {
        // Act
        var result = FailureInterpreter.Interpret(null, RouteLength);

        // Assert
        Assert.False(result.IsAttributed);
        Assert.True(result.ShouldRetry);
        Assert.Null(result.ExcludedNodeHopIndex);
        Assert.Null(result.FailedChannelHopIndex);
        Assert.Null(result.Code);
    }

    [Fact]
    public void Given_FinalNodePermFailure_When_Interpreting_Then_PaymentFails()
    {
        // Arrange
        var failure = Decrypted(RouteLength - 1,
                                FailureMessage.IncorrectOrUnknownPaymentDetails(
                                    LightningMoney.MilliSatoshis(100UL), 800_000));

        // Act
        var result = FailureInterpreter.Interpret(failure, RouteLength);

        // Assert
        Assert.True(result.IsFinalNode);
        Assert.True(result.IsPermanent);
        Assert.False(result.ShouldRetry);
        Assert.Null(result.ExcludedNodeHopIndex);
        Assert.Null(result.FailedChannelHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Code);
    }

    [Theory]
    [InlineData(FailureCode.MppTimeout)]
    [InlineData(FailureCode.TemporaryNodeFailure)]
    [InlineData(FailureCode.FinalIncorrectCltvExpiry)]
    public void Given_FinalNodeTransientKnownFailure_When_Interpreting_Then_MayRetry(FailureCode code)
    {
        // Arrange
        var message = code switch
        {
            FailureCode.MppTimeout => FailureMessage.MppTimeout(),
            FailureCode.TemporaryNodeFailure => FailureMessage.TemporaryNodeFailure(),
            _ => FailureMessage.FinalIncorrectCltvExpiry(144)
        };

        // Act
        var result = FailureInterpreter.Interpret(Decrypted(RouteLength - 1, message), RouteLength);

        // Assert
        Assert.True(result.IsFinalNode);
        Assert.False(result.IsPermanent);
        Assert.True(result.ShouldRetry);
    }

    [Fact]
    public void Given_FinalNodeLegacyFinalExpiryTooSoon_When_Interpreting_Then_MayRetry()
    {
        // Arrange: BOLT 4 "Receiving Failure Codes": final_expiry_too_soon (17) can occur if the block height changed
        var failure = Decrypted(RouteLength - 1, FailureMessage.FinalExpiryTooSoon());

        // Act
        var result = FailureInterpreter.Interpret(failure, RouteLength);

        // Assert
        Assert.True(result.IsFinalNode);
        Assert.False(result.IsPermanent);
        Assert.True(result.ShouldRetry);
        Assert.Equal(FailureCode.FinalExpiryTooSoon, result.Code);
    }

    [Fact]
    public void Given_FinalNodeLegacyIncorrectPaymentAmount_When_Interpreting_Then_PaymentFails()
    {
        // Arrange
        var failure = Decrypted(RouteLength - 1, FailureMessage.IncorrectPaymentAmount());

        // Act
        var result = FailureInterpreter.Interpret(failure, RouteLength);

        // Assert
        Assert.True(result.IsPermanent);
        Assert.False(result.ShouldRetry);
    }

    [Fact]
    public void Given_FinalNodeUnknownTransientCode_When_Interpreting_Then_PaymentFails()
    {
        // Arrange: not understood, so the MAY-retry clause does not apply
        var failure = Decrypted(RouteLength - 1, new FailureMessage((FailureCode)0x0063, Array.Empty<byte>()));

        // Act
        var result = FailureInterpreter.Interpret(failure, RouteLength);

        // Assert
        Assert.False(result.ShouldRetry);
    }

    [Fact]
    public void Given_FinalNodeUnparsableMessage_When_Interpreting_Then_PaymentFails()
    {
        // Arrange: MppTimeout code in the raw bytes, but no parsed message
        var failure = new DecryptedFailure(RouteLength - 1, new byte[] { 0x00, 0x17 }, null);

        // Act
        var result = FailureInterpreter.Interpret(failure, RouteLength);

        // Assert
        Assert.Equal(FailureCode.MppTimeout, result.Code);
        Assert.False(result.ShouldRetry);
    }

    [Theory]
    [InlineData(FailureCode.TemporaryNodeFailure, false)]
    [InlineData(FailureCode.PermanentNodeFailure, true)]
    [InlineData(FailureCode.RequiredNodeFeatureMissing, true)]
    public void Given_IntermediateNodeFailure_When_Interpreting_Then_NodeIsExcluded(FailureCode code,
                                                                                    bool isPermanent)
    {
        // Arrange
        var message = code switch
        {
            FailureCode.TemporaryNodeFailure => FailureMessage.TemporaryNodeFailure(),
            FailureCode.PermanentNodeFailure => FailureMessage.PermanentNodeFailure(),
            _ => FailureMessage.RequiredNodeFeatureMissing()
        };

        // Act
        var result = FailureInterpreter.Interpret(Decrypted(1, message), RouteLength);

        // Assert
        Assert.False(result.IsFinalNode);
        Assert.True(result.IsNodeFailure);
        Assert.Equal(isPermanent, result.IsPermanent);
        Assert.Equal(1, result.ExcludedNodeHopIndex);
        Assert.Null(result.FailedChannelHopIndex);
        Assert.True(result.ShouldRetry);
    }

    [Theory]
    [InlineData(0, FailureCode.PermanentChannelFailure, true)]
    [InlineData(1, FailureCode.UnknownNextPeer, true)]
    [InlineData(2, FailureCode.TemporaryChannelFailure, false)]
    [InlineData(1, FailureCode.InvalidOnionHmac, true)]
    [InlineData(0, FailureCode.ExpiryTooFar, false)]
    public void Given_IntermediateChannelFailure_When_Interpreting_Then_OutgoingChannelIsPenalized(
        int erringHop, FailureCode code, bool isPermanent)
    {
        // Arrange
        var message = code switch
        {
            FailureCode.PermanentChannelFailure => FailureMessage.PermanentChannelFailure(),
            FailureCode.UnknownNextPeer => FailureMessage.UnknownNextPeer(),
            FailureCode.TemporaryChannelFailure => FailureMessage.TemporaryChannelFailure(),
            FailureCode.InvalidOnionHmac => FailureMessage.FromMalformed(code, s_sha256OfOnion),
            _ => FailureMessage.ExpiryTooFar()
        };

        // Act
        var result = FailureInterpreter.Interpret(Decrypted(erringHop, message), RouteLength);

        // Assert
        Assert.False(result.IsNodeFailure);
        Assert.Equal(isPermanent, result.IsPermanent);
        Assert.Equal(erringHop + 1, result.FailedChannelHopIndex);
        Assert.Null(result.ExcludedNodeHopIndex);
        Assert.True(result.ShouldRetry);
        Assert.Null(result.ChannelUpdate);
    }

    [Fact]
    public void Given_IntermediateUpdateFailureWithChannelUpdate_When_Interpreting_Then_PayloadIsExposed()
    {
        // Arrange
        var payload = ChannelUpdatePayload();
        var message = FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(5000UL),
                                                     FailureChannelUpdateFactory.Encode(payload));

        // Act
        var result = FailureInterpreter.Interpret(Decrypted(2, message), RouteLength);

        // Assert
        Assert.Equal(3, result.FailedChannelHopIndex);
        Assert.NotNull(result.ChannelUpdate);
        Assert.Equal(payload, result.ChannelUpdate.Value.ToArray());
    }

    [Fact]
    public void Given_IntermediateUpdateFailureWithEmptyChannelUpdate_When_Interpreting_Then_AcceptedWithoutUpdate()
    {
        // Act: BOLT 4 lets nodes set len = 0
        var result = FailureInterpreter.Interpret(Decrypted(0, FailureMessage.ChannelDisabled()), RouteLength);

        // Assert
        Assert.Equal(1, result.FailedChannelHopIndex);
        Assert.Null(result.ChannelUpdate);
        Assert.True(result.ShouldRetry);
    }

    [Fact]
    public void Given_IntermediateUnreadableFailure_When_Interpreting_Then_ErringNodeIsExcluded()
    {
        // Arrange: bad failure_len framing, so no code at all
        var failure = new DecryptedFailure(1, ReadOnlyMemory<byte>.Empty, null);

        // Act
        var result = FailureInterpreter.Interpret(failure, RouteLength);

        // Assert
        Assert.Null(result.Code);
        Assert.True(result.IsNodeFailure);
        Assert.False(result.IsPermanent);
        Assert.Equal(1, result.ExcludedNodeHopIndex);
        Assert.True(result.ShouldRetry);
    }

    [Fact]
    public void Given_ErringHopOutsideRoute_When_Interpreting_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FailureInterpreter.Interpret(Decrypted(RouteLength, FailureMessage.MppTimeout()), RouteLength));
    }

    [Fact]
    public void Given_EmptyRoute_When_Interpreting_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => FailureInterpreter.Interpret(null, 0));
    }
}