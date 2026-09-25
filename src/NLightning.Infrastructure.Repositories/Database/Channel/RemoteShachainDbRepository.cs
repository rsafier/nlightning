using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Protocol.Models;
using Persistence.Contexts;
using Persistence.Entities.Channel;

public class RemoteShachainDbRepository : BaseDbRepository<RemoteShachainEntity>, IRemoteShachainDbRepository
{
    public RemoteShachainDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShachainEntry>> GetByChannelIdAsync(ChannelId channelId)
    {
        var entities = await Get(e => e.ChannelId == channelId, orderBy: q => q.OrderBy(e => e.Bucket))
                          .ToListAsync();

        return entities.Select(e => new ShachainEntry(e.Bucket, checked((ulong)e.Index), e.Secret)).ToList();
    }

    /// <inheritdoc />
    public async Task SaveAsync(ChannelId channelId, IReadOnlyList<ShachainEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // The query returns the tracked instance of every stored row (also one an earlier SaveAsync in this unit of
        // work marked Deleted); rows an earlier SaveAsync added are only in DbSet.Local
        var existing = await DbSet.Where(e => e.ChannelId == channelId).ToDictionaryAsync(e => e.Bucket);
        foreach (var added in DbSet.Local.Where(e => e.ChannelId == channelId))
            existing[added.Bucket] = added;

        var buckets = new HashSet<byte>();

        foreach (var entry in entries)
        {
            var bucket = checked((byte)entry.Bucket);
            if (!buckets.Add(bucket))
                throw new ArgumentException($"Bucket {bucket} appears twice", nameof(entries));

            var index = checked((long)entry.Index);
            byte[] secret = entry.Secret;
            if (existing.TryGetValue(bucket, out var entity))
            {
                entity.Index = index;
                entity.Secret = secret.ToArray();

                var tracked = DbSet.Entry(entity);
                if (tracked.State == EntityState.Deleted)
                    tracked.State = EntityState.Modified;
            }
            else
            {
                DbSet.Add(new RemoteShachainEntity
                {
                    ChannelId = channelId,
                    Bucket = bucket,
                    Index = index,
                    Secret = secret.ToArray()
                });
            }
        }

        foreach (var (bucket, entity) in existing)
        {
            if (!buckets.Contains(bucket) && DbSet.Entry(entity).State != EntityState.Deleted)
                DbSet.Remove(entity);
        }
    }
}