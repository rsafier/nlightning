namespace NLightning.Domain.Channels.Interfaces;

using ValueObjects;

/// <summary>
/// The local signer's durable safety state per channel (NL-1345, table <c>ChannelSignerGuards</c>), reached through
/// <c>IUnitOfWork.ChannelSignerGuardDbRepository</c>. Unlike the other repositories its raise is not staged: it is
/// written and committed when it returns, independent of the unit of work's save, because the signer raises it before
/// a secret or signature leaves the signer.
/// </summary>
public interface IChannelSignerGuardDbRepository
{
    /// <summary>The persisted guard of <paramref name="channelId"/>, or null when none was persisted yet.</summary>
    Task<ChannelSignerGuard?> GetAsync(ChannelId channelId);

    /// <summary>
    /// Raises the persisted guard of <paramref name="channelId"/> to at least <paramref name="guard"/>, field by field
    /// as <see cref="ChannelSignerGuard.Merge"/> and atomically against other writers (a stale writer never lowers it),
    /// and commits it.
    /// </summary>
    Task RaiseAsync(ChannelId channelId, ChannelSignerGuard guard);
}