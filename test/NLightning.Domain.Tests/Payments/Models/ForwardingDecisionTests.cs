namespace NLightning.Domain.Tests.Payments.Models;

using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;

public class ForwardingDecisionTests
{
    [Fact]
    public void Given_Forward_When_Read_Then_NoFailure()
    {
        // Act
        var decision = ForwardingDecision.Forward;

        // Assert
        Assert.True(decision.IsForward);
        Assert.Null(decision.FailureCode);
    }

    [Fact]
    public void Given_FeeInsufficient_When_Created_Then_ReportsIncomingAmount()
    {
        // Act
        var decision = ForwardingDecision.FeeInsufficient(5_010_197);

        // Assert
        Assert.False(decision.IsForward);
        Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
        Assert.Equal(5_010_197UL, decision.HtlcMsat);
        Assert.Null(decision.CltvExpiry);
    }

    [Fact]
    public void Given_AmountBelowMinimum_When_Created_Then_ReportsOutgoingAmount()
    {
        // Act
        var decision = ForwardingDecision.AmountBelowMinimum(999);

        // Assert
        Assert.Equal(FailureCode.AmountBelowMinimum, decision.FailureCode);
        Assert.Equal(999UL, decision.HtlcMsat);
    }

    [Fact]
    public void Given_IncorrectCltvExpiry_When_Created_Then_ReportsOutgoingCltv()
    {
        // Act
        var decision = ForwardingDecision.IncorrectCltvExpiry(1_100);

        // Assert
        Assert.Equal(FailureCode.IncorrectCltvExpiry, decision.FailureCode);
        Assert.Equal(1_100U, decision.CltvExpiry);
        Assert.Null(decision.HtlcMsat);
    }

    [Fact]
    public void Given_PlainFailure_When_Created_Then_OnlyCodeSet()
    {
        // Act
        var decision = ForwardingDecision.Fail(FailureCode.UnknownNextPeer);

        // Assert
        Assert.Equal(FailureCode.UnknownNextPeer, decision.FailureCode);
        Assert.Null(decision.HtlcMsat);
        Assert.Null(decision.CltvExpiry);
    }
}