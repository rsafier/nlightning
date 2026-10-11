namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Protocol.Factories;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;

public class RecoveryChannelsTests
{
    [Fact]
    public void Given_AnUnknownKeyRecoveryFunding_When_Created_Then_TheUnknownStateReachesFundingAndSigningViews()
    {
        var data = new BackupTestData();
        var entry = ChannelBackupService.CreateEntry(data.AddChannel(3, simpleTaproot: true), data.Peers[0])
                    with
        { FundingKeysUnknown = true };
        using var sha256 = new Sha256();
        var channel = RecoveryChannels.Create(entry, data.Signer.Object.GetChannelBasepoints(3), sha256);

        Assert.True(RecoveryChannels.HasSpliceFundings(entry));
        Assert.True(RecoveryChannels.CreateFundings(entry).Current.FundingKeysUnknown);
        Assert.True(channel.GetSigningInfo().FundingKeysUnknown);
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
        Assert.True(ChannelBackupService.CreateEntry(channel, data.Peers[0]).FundingKeysUnknown);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Given_BackedUpChannel_When_Created_Then_FailedWithDataLossAndTheBackedUpKeysAndOutpoint(
        bool anchors, bool initiator)
    {
        // Arrange
        var data = new BackupTestData();
        var original = data.AddChannel(3, anchors: anchors, initiator: initiator,
                                       scid: new ShortChannelId(700_000, 5, 1));
        var entry = ChannelBackupService.CreateEntry(original, data.Peers[0]);
        using var sha256 = new Sha256();

        // Act
        var channel = RecoveryChannels.Create(entry, data.Signer.Object.GetChannelBasepoints(entry.KeyIndex), sha256);

        // Assert
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
        Assert.Equal(ChannelState.Failed, channel.State);
        Assert.True(channel.DataLossDetected);
        Assert.Null(channel.Commitments);
        Assert.Equal(original.ChannelId, channel.ChannelId);
        Assert.Equal(original.RemoteNodeId, channel.RemoteNodeId);
        Assert.Equal(initiator, channel.IsInitiator);
        Assert.Equal(anchors, channel.ChannelParams.OptionAnchorOutputs);
        Assert.Equal(original.FundingOutput!.TransactionId, channel.FundingOutput!.TransactionId);
        Assert.Equal(original.FundingOutput.Index, channel.FundingOutput.Index);
        Assert.Equal(original.FundingOutput.Amount, channel.FundingOutput.Amount);
        Assert.Equal(original.ShortChannelId, channel.ShortChannelId);
        Assert.Equal(original.LocalKeySet.KeyIndex, channel.LocalKeySet.KeyIndex);
        Assert.Equal(original.LocalKeySet.PaymentCompactBasepoint, channel.LocalKeySet.PaymentCompactBasepoint);
        Assert.Equal(original.RemoteKeySet!.PaymentCompactBasepoint, channel.RemoteKeySet!.PaymentCompactBasepoint);
        Assert.Equal(original.RemoteKeySet.RevocationCompactBasepoint, channel.RemoteKeySet.RevocationCompactBasepoint);
        Assert.Equal(original.ChannelParams.Local, channel.ChannelParams.Local);
        Assert.Equal(original.ChannelParams.Remote, channel.ChannelParams.Remote);

        // The obscuring factor of the original channel: the peer's commitment numbers decode the same way
        var (opener, accepter) = initiator
                                     ? (original.LocalKeySet.PaymentCompactBasepoint,
                                        original.RemoteKeySet.PaymentCompactBasepoint)
                                     : (original.RemoteKeySet.PaymentCompactBasepoint,
                                        original.LocalKeySet.PaymentCompactBasepoint);
        var expected = new CommitmentNumber(opener, accepter, sha256);
        Assert.Equal(expected.ObscuringFactor, channel.CommitmentNumber!.ObscuringFactor);

        // The peer's "current point" never passes for the peer's commitment 0
        Assert.Equal(RecoveryChannels.UnknownPerCommitmentIndex, channel.RemoteKeySet.CurrentPerCommitmentIndex);
        Assert.NotEqual(PerCommitmentIndex.From(0), channel.RemoteKeySet.CurrentPerCommitmentIndex);
    }

    [Fact]
    public void Given_BasepointsOfAnotherKeyIndex_When_Created_Then_Refused()
    {
        // Arrange
        var data = new BackupTestData();
        var entry = ChannelBackupService.CreateEntry(data.AddChannel(3), data.Peers[0]);
        using var sha256 = new Sha256();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => RecoveryChannels.Create(
                                             entry, data.Signer.Object.GetChannelBasepoints(4), sha256));
    }

    [Fact]
    public void Given_AFailedChannelWithDataLossThatExchangedSignatures_When_Checked_Then_NotARecoveryChannel()
    {
        // Arrange: data loss proven by channel_reestablish on a channel that went through funding
        var data = new BackupTestData();
        var channel = data.AddChannel(3, state: ChannelState.Failed);
        channel.MarkDataLossDetected();
        channel.UpdateLastReceivedSignature(new CompactSignature(new byte[64]));
        var open = data.AddChannel(4);

        // Act / Assert
        Assert.False(RecoveryChannels.IsRecoveryChannel(channel));
        Assert.False(RecoveryChannels.IsRecoveryChannel(open));
    }

    [Fact]
    public void Given_RecoveryChannel_When_DataLossReestablishCreated_Then_BothNumbersZeroAndSecretZero()
    {
        // Arrange (BOLT 2 B2-RE-14: next_commitment_number 0 makes the peer fail the channel and broadcast)
        var factory = new MessageFactory(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));
        var channelId = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        var point = BackupTestData.Key(0x02, 9, 9);

        // Act
        var message = RecoveryChannels.CreateDataLossReestablish(factory, channelId, point);

        // Assert
        Assert.Equal(channelId, message.Payload.ChannelId);
        Assert.Equal(0UL, message.Payload.NextCommitmentNumber);
        Assert.Equal(0UL, message.Payload.NextRevocationNumber);
        Assert.Equal(new byte[32], message.Payload.YourLastPerCommitmentSecret.ToArray());
        Assert.Equal(point, message.Payload.MyCurrentPerCommitmentPoint);
    }
}