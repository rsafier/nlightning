using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Wallet.Interfaces;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public sealed class ImportedTapscriptDbRepository : BaseDbRepository<ImportedTapscriptEntity>, IImportedTapscriptDbRepository
{
    private readonly NLightningDbContext _context;
    public ImportedTapscriptDbRepository(NLightningDbContext context) : base(context) { _context = context; }
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
    public async Task<ImportedWatchIndex?> GetIndexAsync()
    {
        var entity = await _context.ImportedWatchIndexes.AsNoTracking().SingleOrDefaultAsync(e => e.Id == 1);
        return entity is null ? null : new ImportedWatchIndex(entity.Height, entity.BlockHash, entity.ScriptSet, entity.History);
    }

    public async Task SetIndexAsync(ImportedWatchIndex index)
    {
        var entity = await _context.ImportedWatchIndexes.SingleOrDefaultAsync(e => e.Id == 1);
        if (entity is null)
        {
            entity = new ImportedWatchIndexEntity { Id = 1 };
            _context.ImportedWatchIndexes.Add(entity);
        }
        entity.Height = index.Height;
        entity.BlockHash = index.BlockHash;
        entity.ScriptSet = index.ScriptSet;
        entity.History = index.History;
    }

    private static ImportedTapscript Map(ImportedTapscriptEntity entity) =>
        new(entity.Script, entity.InternalKey, entity.Definition, entity.CreatedHeight);
}