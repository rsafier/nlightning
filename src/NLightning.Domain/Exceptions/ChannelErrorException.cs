using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Channels.ValueObjects;

/// <summary>
/// Represents an exception that is thrown when a channel error occurs.
/// </summary>
/// <remarks>
/// We usually want to close the connection when this exception is thrown.
/// </remarks>
[ExcludeFromCodeCoverage]
public class ChannelErrorException : ErrorException
{
    public ChannelId? ChannelId { get; }
    public string? PeerMessage { get; }

    /// <summary>
    /// Send the <c>error</c> for the channel and keep the connection (default false: disconnect after it). For a
    /// refused open (a temporary channel id) whose refusal must not drop the peer's other channels (NL-1392).
    /// </summary>
    public bool KeepConnection { get; init; }

    public ChannelErrorException(string message, string? peerMessage = null) : base(message)
    {
        PeerMessage = peerMessage;
    }

    public ChannelErrorException(string message, Exception innerException, string? peerMessage = null)
        : base(message, innerException)
    {
        PeerMessage = peerMessage;
    }

    public ChannelErrorException(string message, ChannelId? channelId, string? peerMessage = null) : base(message)
    {
        ChannelId = channelId;
        PeerMessage = peerMessage;
    }

    public ChannelErrorException(string message, ChannelId? channelId, Exception innerException,
                                 string? peerMessage = null)
        : base(message, innerException)
    {
        ChannelId = channelId;
        PeerMessage = peerMessage;
    }
}