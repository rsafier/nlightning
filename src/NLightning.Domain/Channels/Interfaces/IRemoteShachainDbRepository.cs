namespace NLightning.Domain.Channels.Interfaces;

using Protocol.Models;
using ValueObjects;

/// <summary>
/// Persists the peer's shachain (the per-commitment secrets it revealed, in BOLT 3 compact form) per channel.
/// </summary>
/// <remarks>
/// Round trip: <c>ISecretStorageService.Export()</c> → <see cref="SaveAsync"/> (staged; commit with
/// <c>IUnitOfWork.SaveChangesAsync</c> together with the rest of the revoke_and_ack transition) →
/// <see cref="GetByChannelIdAsync"/> → <c>ISecretStorageService.Load</c>.
/// </remarks>
public interface IRemoteShachainDbRepository
{
    /// <summary>
    /// The stored buckets of a channel, ordered by bucket (empty when the peer has revealed nothing yet).
    /// </summary>
    Task<IReadOnlyList<ShachainEntry>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// Stages the channel's shachain so it equals <paramref name="entries"/>: buckets are upserted by
    /// (channel, bucket) and buckets not in <paramref name="entries"/> are removed.
    /// </summary>
    Task SaveAsync(ChannelId channelId, IReadOnlyList<ShachainEntry> entries);
}