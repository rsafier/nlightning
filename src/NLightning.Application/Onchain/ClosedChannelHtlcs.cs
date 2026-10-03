namespace NLightning.Application.Onchain;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// NL-320: the stored record of an HTLC of a channel that closed on chain and is no longer loaded (the HTLC switch's
/// replay fallback and the HTLC expiry monitor read it to tell whether a forward over it can still resolve).
/// </summary>
internal static class ClosedChannelHtlcs
{
    /// <summary>
    /// The record of HTLC <paramref name="key"/> of the channel <paramref name="channelId"/> when that channel is
    /// <c>Closed</c> in the database (live in its stored snapshot, else archived; null when neither holds it).
    /// <c>Closed</c> is false when the channel is unknown or not closed. The caller checks first that the channel is not
    /// loaded.
    /// </summary>
    public static async Task<(bool Closed, HtlcRecord? Record)> FindAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                         HtlcKey key)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        if (channel is not { State: ChannelState.Closed })
            return (false, null);

        if (channel.Commitments is not { } commitments)
            return (true, null);

        if (commitments.GetHtlc(key.Direction, key.Id) is { } live)
            return (true, live);

        var persisted = await unitOfWork.ChannelStateDbRepository.LoadAsync(channelId, commitments.Params);
        return (true, persisted?.SettledHtlcs.FirstOrDefault(h => h.Key == key));
    }
}