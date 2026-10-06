using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Wallet.Interfaces;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public sealed class ImportedTapscriptDbRepository : BaseDbRepository<ImportedTapscriptEntity>, IImportedTapscriptDbRepository
{
    public ImportedTapscriptDbRepository(NLightningDbContext context) : base(context) { }
    public async Task<IReadOnlyList<ImportedTapscript>> ListAsync() =>
        (await DbSet.AsNoTracking().ToListAsync()).Select(Map).ToList();
    public async Task<ImportedTapscript?> GetAsync(byte[] script)
    {
        var entity = await DbSet.AsNoTracking().FirstOrDefaultAsync(s => s.Script.SequenceEqual(script));
        return entity is null ? null : Map(entity);
    }
    public void Add(ImportedTapscript script) => Insert(new ImportedTapscriptEntity
    {

        Script = script.Script,
        InternalKey = script.InternalKey,
        Definition = script.Definition,
        CreatedHeight = script.CreatedHeight
    });
    private static ImportedTapscript Map(ImportedTapscriptEntity entity) =>
        new(entity.Script, entity.InternalKey, entity.Definition, entity.CreatedHeight);
}