using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Entities.Bitcoin;
using Infrastructure.Repositories.Database;
using Infrastructure.Repositories.Database.Bitcoin;

public class BaseDbRepositoryTests
{
    [Fact]
    public async Task Given_OrderByAndPaging_When_Get_Then_ReturnsTheRequestedPageOfTheOrderedSet()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        await using (var context = database.CreateContext())
        {
            new WalletAddressesDbRepository(context).AddRange([walletAddress]);
            var utxoRepository = new UtxoDbRepository(context);
            for (uint i = 1; i <= 5; i++)
                utxoRepository.Add(SqliteTestDatabase.CreateUtxo(walletAddress, index: i));

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = database.CreateContext();
        var repository = new TestUtxoRepository(readContext);

        // Act
        var firstPage = await repository.GetIndexesPage(q => q.OrderByDescending(x => x.Index), 2, 1)
                                        .ToListAsync(TestContext.Current.CancellationToken);
        var secondPage = await repository.GetIndexesPage(q => q.OrderByDescending(x => x.Index), 2, 2)
                                         .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([5u, 4u], firstPage);
        Assert.Equal([3u, 2u], secondPage);
    }

    private sealed class TestUtxoRepository(NLightningDbContext context) : BaseDbRepository<UtxoEntity>(context)
    {
        public IQueryable<uint> GetIndexesPage(Func<IQueryable<UtxoEntity>, IOrderedQueryable<UtxoEntity>> orderBy,
                                               int perPage, int pageNumber)
        {
            return Get(orderBy: orderBy, perPage: perPage, pageNumber: pageNumber).Select(x => x.Index);
        }
    }
}