using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Node.Fencing;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Helpers;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the onion replay set (table <c>OnionReplayEntries</c>, NL-078), keyed by packet HMAC.
/// </summary>
public class OnionReplayDbRepository : BaseDbRepository<OnionReplayEntryEntity>, IOnionReplayDbRepository
{
    private readonly INodeWriteFence? _writeFence;

    /// <param name="context">The unit of work's context.</param>
    /// <param name="writeFence">The node write fence the prune is checked against (NL-1342); null checks nothing.</param>
    public OnionReplayDbRepository(NLightningDbContext context, INodeWriteFence? writeFence = null) : base(context)
    {
        _writeFence = writeFence;
    }

    /// <inheritdoc />
    public async Task<OnionReplayEntry?> GetByHmacAsync(ReadOnlyMemory<byte> hmac)
    {
        if (hmac.Length != OnionConstants.HmacLength)
            throw new ArgumentException($"Onion HMAC must be {OnionConstants.HmacLength} bytes.", nameof(hmac));

        var key = hmac.ToArray();
        var entity = await DbSet.AsNoTracking().FirstOrDefaultAsync(e => e.Hmac == key);
        return entity is null
                   ? null
                   : new OnionReplayEntry(entity.Hmac, entity.ChannelId, entity.HtlcId, entity.ExpiryHeight);
    }

    /// <inheritdoc />
    public void Add(OnionReplayEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Insert(new OnionReplayEntryEntity
        {
            Hmac = entry.Hmac.ToArray(),
            ChannelId = entry.ChannelId,
            HtlcId = entry.HtlcId,
            ExpiryHeight = entry.ExpiryHeight
        });
    }

    /// <inheritdoc />
    /// <remarks>Runs at once (a bulk delete), checked by the node write fence inside its transaction when one is
    /// registered (<see cref="FencedWrite"/>, NL-1342).</remarks>
    public Task<int> DeleteExpiredAsync(uint blockHeight) =>
        FencedWrite.RunAsync(Context, _writeFence,
                             () => DbSet.Where(e => e.ExpiryHeight < blockHeight).ExecuteDeleteAsync());

    /// <inheritdoc />
    public Task<int> CountAsync() => DbSet.CountAsync();
}