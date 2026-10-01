using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;

/// <summary>
/// Request for ListForwards (ClientCommand 40, NL-597): a page of our forwarded payments, newest first, with optional
/// filters. Every filter key is nullable; null (or 0 for the times) means the filter is off, so an older client
/// sending only the page keeps working.
/// </summary>
[MessagePackObject]
public sealed class ListForwardsIpcRequest
{
    /// <summary>How many of the newest forwards to skip.</summary>
    [Key(0)] public int Skip { get; init; }

    /// <summary>The most forwards to return.</summary>
    [Key(1)] public int Take { get; set; } = 100;

    /// <summary>Only forwards created at or after this Unix time (seconds), or null for no lower bound.</summary>
    [Key(2)] public long? SinceUnixSeconds { get; init; }

    /// <summary>Only forwards created at or before this Unix time (seconds), or null for no upper bound.</summary>
    [Key(3)] public long? UntilUnixSeconds { get; init; }

    /// <summary>Only forwards in this <c>ForwardCircuitStatus</c> (0 pending, 1 offered, 2 fulfilled, 3 failed), or
    /// null for all.</summary>
    [Key(4)] public byte? Status { get; init; }

    /// <summary>
    /// Only forwards whose incoming or outgoing channel is this one: a 64-hex-character channel id, or a
    /// <c>short_channel_id</c> as <c>block x tx x output</c> (or its decimal form), which also matches the outgoing
    /// side's requested scid.
    /// </summary>
    [Key(5)] public string? Channel { get; init; }

    public ListForwardsClientRequest ToClientRequest()
    {
        ChannelId? channelId = null;
        ShortChannelId? channelScid = null;
        if (!string.IsNullOrWhiteSpace(Channel))
        {
            // A channel id is 64 hex characters; a short channel id is block x tx x output (or its decimal form)
            if (Channel.Length == 64)
            {
                try
                {
                    channelId = new ChannelId(Convert.FromHexString(Channel));
                }
                catch (FormatException)
                {
                    throw InvalidChannel();
                }
            }
            else if (ulong.TryParse(Channel, out var scidValue))
            {
                channelScid = new ShortChannelId(scidValue);
            }
            else
            {
                try
                {
                    channelScid = ShortChannelId.Parse(Channel);
                }
                catch (FormatException)
                {
                    throw InvalidChannel();
                }
            }
        }

        return new ListForwardsClientRequest
        {
            Skip = Skip,
            Take = Take,
            Since = SinceUnixSeconds is { } since
                        ? DateTimeOffset.FromUnixTimeSeconds(since)
                        : null,
            Until = UntilUnixSeconds is { } until
                        ? DateTimeOffset.FromUnixTimeSeconds(until)
                        : null,
            Status = Status is { } status ? (Domain.Payments.Enums.ForwardCircuitStatus)status : null,
            ChannelId = channelId,
            ChannelScid = channelScid
        };
    }

    private static ClientException InvalidChannel() =>
        new(ErrorCodes.InvalidOperation,
            "Invalid channel: expected a 64-hex channel id or a short_channel_id like 800000x1234x0.");
}