namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;

/// <summary>
/// A short channel id a channel stopped using at a splice lock and that still resolves in the switch until
/// <see cref="ExpiresAtHeight"/> (splicing plan D12, SP2-0; filled by lane SP2-D from <c>IRetiredScidMap</c>).
/// </summary>
public sealed class RetiredScidInfoClientResponse
{
    public required ShortChannelId ShortChannelId { get; init; }
    public uint RetiredAtHeight { get; init; }
    public uint ExpiresAtHeight { get; init; }
}