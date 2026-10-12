using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.Interfaces;
using Domain.Node.Fencing;
using Helpers;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The channel key index high-water mark (NL-1388, table <c>ChannelKeyIndexHighWater</c>, one row with id 1).
/// </summary>
/// <remarks>
/// A claim or a raise is one conditional <c>UPDATE</c> (only upwards), atomic per row in every provider, so of two
/// processes claiming the same index only one sees its update applied; the first write of a database inserts the row and
/// falls back to the update when a concurrent insert won. Each write commits on its own, under the node write fence
/// (NL-1342, <see cref="FencedWrite"/>): a fenced process cannot claim an index, so its signer refuses new channels.
/// Use it from a scope that stages nothing else (<c>ChannelKeyIndexStore</c> opens one per call).
/// </remarks>
public class ChannelKeyIndexDbRepository : IChannelKeyIndexDbRepository
{
    private const int RowId = 1;

    private readonly NLightningDbContext _context;
    private readonly INodeWriteFence? _writeFence;

    public ChannelKeyIndexDbRepository(NLightningDbContext context, INodeWriteFence? writeFence = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _writeFence = writeFence;
    }

    /// <inheritdoc />
    public async Task<uint?> GetHighWaterAsync()
    {
        var value = await _context.ChannelKeyIndexHighWater.AsNoTracking()
                                  .Where(e => e.Id == RowId)
                                  .Select(e => (long?)e.HighWater)
                                  .SingleOrDefaultAsync();
        return value is { } v ? checked((uint)v) : null;
    }

    /// <inheritdoc />
    public Task<bool> TryClaimAsync(uint index) =>
        FencedWrite.RunAsync(_context, _writeFence, () => WriteAsync(index));

    /// <inheritdoc />
    public Task RaiseAsync(uint index) =>
        FencedWrite.RunAsync(_context, _writeFence, () => WriteAsync(index));

    /// <summary>Raises the mark to <paramref name="index"/> when it is below; true when this call raised it.</summary>
    /// <remarks>Inside a transaction (with a fence) the insert's save runs under a savepoint EF creates, so a lost insert
    /// race rolls back to it and the update that follows still runs in the transaction.</remarks>
    private async Task<bool> WriteAsync(uint index)
    {
        if (await UpdateAsync(index) > 0)
            return true;
        if (await _context.ChannelKeyIndexHighWater.AsNoTracking().AnyAsync(e => e.Id == RowId))
            return false;

        var entity = new ChannelKeyIndexHighWaterEntity { Id = RowId, HighWater = index };
        _context.ChannelKeyIndexHighWater.Add(entity);
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Another writer inserted the row first: its value decides
            _context.Entry(entity).State = EntityState.Detached;
            return await UpdateAsync(index) > 0;
        }
        finally
        {
            if (_context.Entry(entity).State != EntityState.Detached)
                _context.Entry(entity).State = EntityState.Detached;
        }
    }

    private Task<int> UpdateAsync(uint index)
    {
        long value = index;
        return _context.ChannelKeyIndexHighWater
                       .Where(e => e.Id == RowId && e.HighWater < value)
                       .ExecuteUpdateAsync(s => s.SetProperty(e => e.HighWater, value));
    }
}