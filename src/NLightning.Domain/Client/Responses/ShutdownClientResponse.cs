namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The answer to <c>shutdown</c> (<c>ClientCommand.Shutdown</c>, NL-591): the node is stopping, with the channels that
/// were not closed (none of them had an HTLC in flight).
/// </summary>
public sealed class ShutdownClientResponse
{
    public ShutdownClientResponse(int channelCount)
    {
        ChannelCount = channelCount;
    }

    /// <summary>The channels that are not Closed or Stale; they reestablish when the node starts again.</summary>
    public int ChannelCount { get; }
}