namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed class SilentPaymentCursorRecoveryTests
{
    [Fact]
    public async Task Given_ARewindOrRescanBaselineWithoutItsHash_When_SavedAndRestarted_Then_TheNextBlockCanBeFetchedFromItsHeight()
    {
        // Arrange: a requested from-height or rewind has a baseline height before its hash is fetched.
        using var database = new SqliteTestDatabase();
        var state = new SilentPaymentScanState(100, 200, 99, null, 199, "GetBlock", 150, null, 50);

        // Act
        await using (var context = database.CreateContext())
        {
            await new SilentPaymentDbRepository(context).SetScanStateAsync(state, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert: both cursors and the label recovery scope survive a restart, without pretending to know a hash.
        await using var read = database.CreateContext();
        Assert.Equal(state, await new SilentPaymentDbRepository(read).GetScanStateAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ACursorHashWithoutItsHeight_When_Staged_Then_TheOrphanHashIsRejected(bool live)
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var hash = new Hash(Enumerable.Repeat((byte)1, 32).ToArray());
        var state = live
            ? new SilentPaymentScanState(100, 100, LiveCursorHash: hash)
            : new SilentPaymentScanState(100, 100, RescanCursorHash: hash);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => new SilentPaymentDbRepository(context)
            .SetScanStateAsync(state, TestContext.Current.CancellationToken));
    }
}