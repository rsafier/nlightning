using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Crypto.KeyRing;
using Domain.Crypto.ValueObjects;
using Persistence.Contexts;
using Persistence.Entities.Node;

public sealed class KeyRingDbRepository : BaseDbRepository<KeyRingKeyEntity>, IKeyRingDbRepository
{
    public KeyRingDbRepository(NLightningDbContext context) : base(context) { }

    public Task<int?> LastIndexAsync(int family) =>
        DbSet.Where(k => k.Family == family).MaxAsync(k => (int?)k.Index);

    public async Task<KeyRingKey?> GetAsync(KeyRingLocator locator) =>
        Map(await DbSet.AsNoTracking().FirstOrDefaultAsync(k => k.Family == locator.Family && k.Index == locator.Index));

    public async Task<KeyRingKey?> FindAsync(CompactPubKey publicKey)
    {
        var bytes = (byte[])publicKey;
        return Map(await DbSet.AsNoTracking().FirstOrDefaultAsync(k => k.PublicKey.SequenceEqual(bytes)));
    }

    public void Add(KeyRingKey key) => Insert(new KeyRingKeyEntity
    {

        Family = key.Locator.Family,
        Index = key.Locator.Index,
        PublicKey = key.PublicKey,
        CreatedAt = key.CreatedAt.UtcTicks
    });

    private static KeyRingKey? Map(KeyRingKeyEntity? key) => key is null ? null
        : new KeyRingKey(new KeyRingLocator(key.Family, key.Index), key.PublicKey,
                         new DateTimeOffset(key.CreatedAt, TimeSpan.Zero));
}