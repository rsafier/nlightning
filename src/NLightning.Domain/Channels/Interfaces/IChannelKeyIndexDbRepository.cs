namespace NLightning.Domain.Channels.Interfaces;

/// <summary>
/// The database's channel key index high-water mark (NL-1388, table <c>ChannelKeyIndexHighWater</c>), reached through
/// <c>IUnitOfWork.ChannelKeyIndexDbRepository</c>: the highest channel key index any process over this database ever
/// handed out. Like the signer guard its writes are not staged: each one is committed when it returns, independent of
/// the unit of work's save, because an index is claimed before it leaves the signer.
/// </summary>
public interface IChannelKeyIndexDbRepository
{
    /// <summary>The stored high-water mark, or null when none was stored yet.</summary>
    Task<uint?> GetHighWaterAsync();

    /// <summary>
    /// Claims <paramref name="index"/>: raises the high-water mark to it when it is above the stored one, atomically
    /// against other writers, and commits. True when this call raised it; false when the stored mark was already at or
    /// above <paramref name="index"/> (another process, or this one, handed it out before: it must not be used).
    /// </summary>
    Task<bool> TryClaimAsync(uint index);

    /// <summary>Raises the high-water mark to at least <paramref name="index"/> (never lowers it) and commits.</summary>
    Task RaiseAsync(uint index);
}