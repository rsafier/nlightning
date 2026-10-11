using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Node.Credentials;
using Domain.Node.Fencing;
using Helpers;
using Persistence.Contexts;
using Persistence.Entities.Node;

/// <summary>
/// The LND gRPC macaroon root keys (NL-1389, table <c>MacaroonRootKeys</c>), stored encrypted: this repository never
/// sees a root key in the clear. A new key is staged and written by the unit of work's save (fenced like every save); a
/// deletion is one <c>DELETE</c> under the node write fence (NL-1342).
/// </summary>
public class MacaroonRootKeyDbRepository : IMacaroonRootKeyDbRepository
{
    private readonly NLightningDbContext _context;
    private readonly INodeWriteFence? _writeFence;

    public MacaroonRootKeyDbRepository(NLightningDbContext context, INodeWriteFence? writeFence = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _writeFence = writeFence;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredMacaroonRootKey>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.MacaroonRootKeys.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(e => new StoredMacaroonRootKey(unchecked((ulong)e.RootKeyId), e.Nonce, e.Ciphertext,
                                                              new DateTimeOffset(e.CreatedAt, TimeSpan.Zero)))
                       .ToList();
    }

    /// <inheritdoc />
    public void Add(StoredMacaroonRootKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _context.MacaroonRootKeys.Add(new MacaroonRootKeyEntity
        {
            RootKeyId = unchecked((long)key.RootKeyId),
            Nonce = key.Nonce,
            Ciphertext = key.Ciphertext,
            CreatedAt = key.CreatedAt.UtcTicks
        });
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(ulong rootKeyId, CancellationToken cancellationToken = default)
    {
        var id = unchecked((long)rootKeyId);
        var deleted = await FencedWrite.RunAsync(_context, _writeFence,
                                                 () => _context.MacaroonRootKeys.Where(e => e.RootKeyId == id)
                                                               .ExecuteDeleteAsync(cancellationToken),
                                                 cancellationToken: cancellationToken);
        return deleted > 0;
    }
}