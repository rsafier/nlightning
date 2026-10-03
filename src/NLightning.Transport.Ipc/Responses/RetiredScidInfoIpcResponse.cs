using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// A retired short channel id of a channel in a <see cref="ChannelInfoIpcResponse"/> (splicing plan D12, SP2-0; lane
/// SP2-D).
/// </summary>
[MessagePackObject]
public sealed class RetiredScidInfoIpcResponse
{
    /// <summary>The short channel id as a BOLT 7 uint64.</summary>
    [Key(0)] public ulong ShortChannelId { get; init; }

    [Key(1)] public uint RetiredAtHeight { get; init; }
    [Key(2)] public uint ExpiresAtHeight { get; init; }

    public static RetiredScidInfoIpcResponse FromClientResponse(RetiredScidInfoClientResponse retired)
    {
        ArgumentNullException.ThrowIfNull(retired);
        return new RetiredScidInfoIpcResponse
        {
            ShortChannelId = ToUInt64(retired.ShortChannelId),
            RetiredAtHeight = retired.RetiredAtHeight,
            ExpiresAtHeight = retired.ExpiresAtHeight
        };
    }

    /// <summary>block &lt;&lt; 40 | tx index &lt;&lt; 16 | output, as <see cref="ChannelInfoIpcResponse.ShortChannelId"/>.
    /// </summary>
    internal static ulong ToUInt64(ShortChannelId scid) =>
        ((ulong)scid.BlockHeight << 40) | ((ulong)scid.TransactionIndex << 16) | scid.OutputIndex;
}