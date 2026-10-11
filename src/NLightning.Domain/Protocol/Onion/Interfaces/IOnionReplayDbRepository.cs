namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Models;

/// <summary>
/// Persistence port of the onion replay set (table <c>OnionReplayEntries</c>, NL-078), keyed by packet HMAC. Reached
/// through <c>IUnitOfWork.OnionReplayDbRepository</c>; used by the persistent <see cref="IOnionReplayStore"/>.
/// </summary>
/// <remarks>
/// <see cref="Add"/> is staged (committed by <c>IUnitOfWork.SaveChangesAsync</c>); <see cref="DeleteExpiredAsync"/>
/// runs at once, outside the unit of work's save.
/// </remarks>
public interface IOnionReplayDbRepository
{
    /// <summary>The entry recorded for <paramref name="hmac"/>, or null.</summary>
    /// <exception cref="ArgumentException"><paramref name="hmac"/> is not 32 bytes long.</exception>
    Task<OnionReplayEntry?> GetByHmacAsync(ReadOnlyMemory<byte> hmac);

    /// <summary>Stages a new entry. Its HMAC must not be stored yet.</summary>
    void Add(OnionReplayEntry entry);

    /// <summary>
    /// Deletes every entry whose <c>ExpiryHeight</c> is below <paramref name="blockHeight"/> (the chain passed it),
    /// immediately (not staged).
    /// </summary>
    /// <returns>The number of entries deleted.</returns>
    Task<int> DeleteExpiredAsync(uint blockHeight);

    /// <summary>The number of stored entries.</summary>
    Task<int> CountAsync();
}