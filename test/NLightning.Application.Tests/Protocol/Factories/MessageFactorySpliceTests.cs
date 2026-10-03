using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Protocol.Factories;

using Application.Protocol.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Constants;

public class MessageFactorySpliceTests
{
    private static readonly CompactPubKey s_pubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private readonly MessageFactory _messageFactory = new(Options.Create(new NodeOptions()));

    [Fact]
    public void Given_ASpliceOut_When_CreatingSpliceInit_Then_TheNegativeContributionIsKept()
    {
        // Act
        var message = _messageFactory.CreateSpliceInitMessage(s_channelId, -50_000, 2_500, 120, s_pubKey,
                                                              requireConfirmedInputs: true);

        // Assert
        Assert.Equal(MessageTypes.SpliceInit, message.Type);
        Assert.Equal(s_channelId, message.Payload.ChannelId);
        Assert.Equal(-50_000, message.Payload.FundingContributionSatoshis);
        Assert.Equal(2_500u, message.Payload.FundingFeeratePerKw);
        Assert.Equal(120u, message.Payload.Locktime);
        Assert.Equal(s_pubKey, message.Payload.FundingPubKey);
        Assert.NotNull(message.RequireConfirmedInputsTlv);
        Assert.NotNull(message.Extension);
    }

    [Fact]
    public void Given_NoContribution_When_CreatingSpliceAck_Then_ItHasNoTlvs()
    {
        // Act
        var message = _messageFactory.CreateSpliceAckMessage(s_channelId, 0, s_pubKey);

        // Assert
        Assert.Equal(MessageTypes.SpliceAck, message.Type);
        Assert.Equal(0, message.Payload.FundingContributionSatoshis);
        Assert.Null(message.RequireConfirmedInputsTlv);
        Assert.Null(message.Extension);
    }

    [Fact]
    public void Given_ASpliceTxId_When_CreatingSpliceLocked_Then_ItCarriesIt()
    {
        // Arrange
        TxId txId = Enumerable.Repeat((byte)0x07, 32).ToArray();

        // Act
        var message = _messageFactory.CreateSpliceLockedMessage(s_channelId, txId);

        // Assert
        Assert.Equal(MessageTypes.SpliceLocked, message.Type);
        Assert.Equal(txId, message.Payload.SpliceTxId);
    }

    [Fact]
    public void Given_TwoFundings_When_CreatingStartBatch_Then_ItAnnouncesCommitmentSigned()
    {
        // Act
        var message = _messageFactory.CreateStartBatchMessage(s_channelId, 2);

        // Assert
        Assert.Equal(MessageTypes.StartBatch, message.Type);
        Assert.Equal((ushort)2, message.Payload.BatchSize);
        Assert.Equal((ushort)MessageTypes.CommitmentSigned, message.MessageTypeTlv?.MessageType);
        Assert.Equal([0x00, 0x84], message.MessageTypeTlv?.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(21)]
    public void Given_ABatchSizeOutsideTwoToTwenty_When_CreatingStartBatch_Then_Throws(ushort batchSize)
    {
        // Act & Assert (BOLT 2: a sender sets 1 < batch_size <= 20)
        Assert.Throws<ArgumentOutOfRangeException>(() => _messageFactory.CreateStartBatchMessage(s_channelId,
                                                                                                  batchSize));
    }
}