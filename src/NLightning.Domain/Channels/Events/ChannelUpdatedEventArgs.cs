namespace NLightning.Domain.Channels.Events;

using Models;

public class ChannelUpdatedEventArgs
{
    public ChannelModel Channel { get; }

    /// <summary>False for progress staged in an interactive-tx round that has not committed yet.</summary>
    public bool IsPersisted { get; }

    public ChannelUpdatedEventArgs(ChannelModel channel, bool isPersisted = true)
    {
        Channel = channel;
        IsPersisted = isPersisted;
    }
}