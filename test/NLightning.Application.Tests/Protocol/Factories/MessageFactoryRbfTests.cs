using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Protocol.Factories;

using Application.Protocol.Factories;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;

/// <summary>
/// <c>tx_init_rbf</c>/<c>tx_ack_rbf</c> from the factory: <c>funding_output_contribution</c> is an s64 in satoshis
/// (negative for a splice-out RBF), and locktime and feerate keep their places.
/// </summary>
public class MessageFactoryRbfTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x24, 32).ToArray());

    private readonly MessageFactory _messageFactory = new(Options.Create(new NodeOptions()));

    [Fact]
    public void Given_LocktimeAndFeerate_When_CreatingTxInitRbf_Then_TheyAreNotSwapped()
    {
        // Act (regression: the factory passed them to the (channel_id, feerate, locktime) constructor swapped)
        var message = _messageFactory.CreateTxInitRbfMessage(s_channelId, 800_123, 2_600, 0, false);

        // Assert
        Assert.Equal(800_123U, message.Payload.Locktime);
        Assert.Equal(2_600U, message.Payload.Feerate);
        Assert.Null(message.FundingOutputContributionTlv);
        Assert.Null(message.RequireConfirmedInputsTlv);
    }

    [Theory]
    [InlineData(-50_000L)]
    [InlineData(10L)]
    public void Given_ASignedContribution_When_CreatingTxInitRbf_Then_ItIsKeptInSatoshis(long contribution)
    {
        // Act
        var message = _messageFactory.CreateTxInitRbfMessage(s_channelId, 0, 2_600, contribution, true);

        // Assert
        Assert.NotNull(message.FundingOutputContributionTlv);
        Assert.Equal(contribution, message.FundingOutputContributionTlv.Satoshis);
        Assert.NotNull(message.RequireConfirmedInputsTlv);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(100_000L)]
    public void Given_ASignedContribution_When_CreatingTxAckRbf_Then_ItIsKeptInSatoshis(long contribution)
    {
        // Act
        var message = _messageFactory.CreateTxAckRbfMessage(s_channelId, contribution, false);

        // Assert
        Assert.NotNull(message.FundingOutputContributionTlv);
        Assert.Equal(contribution, message.FundingOutputContributionTlv.Satoshis);
    }

    [Fact]
    public void Given_NoContribution_When_CreatingTxAckRbf_Then_TheTlvIsOmitted()
    {
        // Act (BOLT 2: "If omitted, the sender is not contributing to the funding output.")
        var message = _messageFactory.CreateTxAckRbfMessage(s_channelId, 0, false);

        // Assert
        Assert.Null(message.FundingOutputContributionTlv);
        Assert.Null(message.Extension);
    }
}