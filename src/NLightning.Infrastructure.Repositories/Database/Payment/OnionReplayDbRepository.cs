using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the onion replay set (table <c>OnionReplayEntries</c>, NL-078), keyed by packet HMAC.
/// </summary>
public class OnionReplayDbRepository : BaseDbRepository<OnionReplayEntryEntity>, IOnionReplayDbRepository
{
    public OnionReplayDbRepository(NLightningDbContext context) : base(context)
    {
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
    public Task<int> DeleteExpiredAsync(uint blockHeight) =>
        DbSet.Where(e => e.ExpiryHeight < blockHeight).ExecuteDeleteAsync();

    /// <inheritdoc />
    public Task<int> CountAsync() => DbSet.CountAsync();
}