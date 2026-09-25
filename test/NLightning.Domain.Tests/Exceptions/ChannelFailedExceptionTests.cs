namespace NLightning.Domain.Tests.Exceptions;

using Domain.Channels.ValueObjects;
using Domain.Exceptions;

public class ChannelFailedExceptionTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)9, 32).ToArray());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Given_NoPeerMessage_When_Created_Then_GenericPeerMessageNotLocalMessage(string? peerMessage)
    {
        // Act
        var exception = new ChannelFailedException(s_channelId, "local detail: key material mismatch", peerMessage);
        var withInner = new ChannelFailedException(s_channelId, "local detail", new InvalidOperationException(),
                                                   peerMessage);

        // Assert
        Assert.Equal(ChannelFailedException.DefaultPeerMessage, exception.PeerMessage);
        Assert.Equal(ChannelFailedException.DefaultPeerMessage, withInner.PeerMessage);
        Assert.Equal(s_channelId, exception.FailedChannelId);
        Assert.Equal(s_channelId, exception.ChannelId);
    }

    [Fact]
    public void Given_PeerMessage_When_Created_Then_Kept()
    {
        // Act
        var exception = new ChannelFailedException(s_channelId, "local", "invalid commitment signature");

        // Assert
        Assert.Equal("invalid commitment signature", exception.PeerMessage);
    }
}